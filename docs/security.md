# Security guide for Runic applications

This guide is for authors of applications built with Runic. It explains which
defaults Runic applies, which options relax them, and which decisions stay with
the application. Package READMEs have the details; this page links to them. To
report a vulnerability in Runic itself, follow [`SECURITY.md`](../SECURITY.md).

## The trust model

A Runic application has two halves:

- **The .NET process** runs with the user's rights. It owns state, files,
  platform services and privileged actions.
- **The page** renders that state in a WebView or a browser. It is your code,
  but it also runs npm dependencies and renders data that can come from
  anywhere. Treat it as semi-trusted: script that runs in the page, through a
  cross-site scripting bug or a compromised dependency, can call every Bridge
  member that the window exposes.

The Bridge is therefore the boundary. Runic decides who may connect to it and
bounds what arrives over it. Your ViewModels decide what a connected page may do.

## Validate every Bridge input in .NET

- **Settable state and commands are your API.** A public setter or a command is
  callable from the page with any value its type can hold, not only the values
  your UI offers. Give state that the page only reads a private setter
  ([read-only and settable state](../packages/dotnet/Runic.Application.Views/README.md#read-only-and-settable-state)).
  Check ranges, lengths, identifiers and formats in the setter or command, and
  report problems with
  [structured validation](../packages/dotnet/Runic.Application.Views/README.md#structured-validation).
  The generated TypeScript types do not validate anything at run time.
- **Input size is bounded.** Each window decodes a Bridge input up to one limit,
  1 MiB in UTF-8 by default, and answers a larger one as invalid input before it
  reaches the ViewModel. Exact number strings are at most 4 096 characters, and a
  window runs at most 64 awaited command calls at once. Lower the limit for a
  window that only takes small values; raise it only for one that needs it
  ([Bridge input limit](../packages/dotnet/Runic.Application.Views/README.md#bridge-input-limit)).
- **Privileged steps need a decision the page cannot make.** A command that
  deletes, overwrites, sends or elevates should act on what the user chose in
  native UI, such as a file from `IFileDialogs`, or ask for confirmation from
  .NET. Do not take a file path, a program or an argument for a privileged action
  from the page. Privileged actions run only programs declared at build time;
  check page-derived arguments with `PrivilegedActionDeclaration.AcceptsArguments`
  first ([privileged actions](../packages/dotnet/Runic.Platform/README.md#privileged-actions)).
- **Links and files from the page.** `IDesktopFileLauncher.LaunchUriAsync`
  accepts only absolute `http`, `https`, `mailto` and `ms-settings` URIs, and
  refuses `mailto` attachments. Still check a link the page supplies before you
  open it. `LaunchAsync` with `DesktopFileOperation.Open` refuses programs,
  scripts, installers and similar types on Windows and macOS, but those lists are
  best effort: for a downloaded file or a name the page supplies, use `Reveal` or
  `ChooseApplication`
  ([opening links](../packages/dotnet/Runic.Platform/README.md#opening-links)).
- **Activations are untrusted.** A URL opened with your application's scheme, a
  file opened with it and a notification activation can come from any web page or
  local process. Check them against the application's own state before acting
  ([opening files and URLs](../packages/dotnet/Runic.Platform/README.md#opening-files-and-urls)).

## Embedded windows and installed browsers

Runic Desktop opens a window in the platform's embedded WebView by default:
WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux. An installed browser
is used only when the application asks for one, or as an explicit
`EmbeddedThenBrowser` fallback. The two differ in what Runic can enforce.

| | Embedded window | Installed browser |
| --- | --- | --- |
| Who receives the session credential | Only documents of the surface's origin and its `AdditionalOrigins`, through a document-start script | Only the browser that Runic launched: a one-time launch code binds the surface to it with an `HttpOnly` cookie |
| Navigation to other origins, new windows, downloads, developer tools | Runic's web content policy: external links open in the default browser, downloads are refused, developer tools only in Development | The browser's own behaviour; Runic cannot apply the policy |
| Camera and microphone | Granted only with `AllowedPermissions`, and only to the presented origin | Not granted by Runic; the browser asks the user, or a fallback opens without the grant |
| Surfaces of one listener | Isolated from each other | Share an origin, so one surface's page can read another's credential |

In an installed browser, other users of a Linux machine cannot use the launch
code: Runic refuses a request from another user's process. On macOS that check
is not available, so another local process that reads the browser's command line
can use the code first. The browser then shows an error and opening the window
fails visibly, rather than the surface being taken over silently.

For a page that shows less-trusted content, or an application that runs on
machines shared with other users, prefer embedded windows. If such an
application can run in a browser, give the less-trusted surface its own listener
and origin with `DesktopSurfaceOptions.UseIsolatedListener`. The
[Runic Desktop security notes](../packages/dotnet/Runic.Desktop/README.md#security-notes)
describe each case, including the remaining risks.

## Surfaces and admission

A Runic Desktop surface defaults to a loopback listener, same-origin admission,
a 256-bit random session credential per surface, one client at a time, redacted
failures and non-cacheable bootstrap content. Each of these
`DesktopSecurityPolicy` and host options relaxes a default; treat each one as a
security decision:

- `NetworkExposure = AllInterfaces` makes the surface reachable from the network.
- `AdditionalOrigins` admits pages of other origins, such as a development
  server.
- `AllowMissingOrigin` admits clients that send no `Origin`, such as tools other
  than a browser.
- `ClientAdmission = Multiple` admits several clients, and in an installed
  browser keeps the surface address on the browser's command line.
- `RequireSessionCredential = false` admits any client that passes the other
  checks.

The credential is the surface's secret: never log it, store it or show it to page
content. A `DesktopSurfaceOptions.Resources` handler receives decoded request
paths that can contain `..`; map them to known files, never to the file system
unchecked.

## Blobs and uploads

A `RunicBlob` crosses as a capability URL and a `RunicUpload` as an upload over
HTTP (Views feature `blobs/1`, `specs/application/README.md`, "Blobs and
uploads"):

- A blob URL carries 192 random bits, is served only for its window and only
  while a state member, the View or the window holds it. On a Desktop surface it
  is also served only to the window's browser client (its cookie, and the launch
  code of an installed browser); CS-WebUI serves it from a loopback listener of
  the window that cannot see WebUI's clients, so there it is a bearer URL of the
  window. Do not put a blob URL where other pages or logs can read it.
- A blob response is `nosniff` and carries a sandboxing
  `Content-Security-Policy` beside the host's own (Runic Desktop's
  `frame-ancestors` stays in force), so serving untrusted HTML or SVG as a blob
  does not run it as the application. Cross-site loads (`Sec-Fetch-Site:
  cross-site`) are refused. CS-WebUI's endpoint also serves loopback peers only,
  which matters on Windows, where http.sys routes requests by their `Host`;
  outside Windows, a relayed connection is checked for its client's address.
  Its URLs name `localhost`, and it holds both loopback addresses of
  `localhost` on its port (`::1` and `127.0.0.1`). After it starts, it connects
  to each address and gives up the port unless its own listener answers that
  connection, so it does not use a port where another process listens on, or
  passes on connections from, either address. On Linux, no other process can
  listen on either address while it does. On macOS, sockets on the wildcard
  addresses can listen beside it but receive no connections to either loopback
  address. On Linux and macOS, a blob load or an upload sent to the endpoint
  therefore does not reach another process. On Windows the endpoint checks this
  when it starts, and http.sys keeps it so afterwards for processes without
  administrator rights: it refuses every such process, of the same user or
  another, a URL under the endpoint's. That holds only where no URL reservation
  (`netsh http add urlacl`) names the port. The endpoint reads the reservations
  when it starts and does not use a port that one names (event 2104); a
  reservation that an administrator adds after that is not covered.
- An upload needs the page's origin and a token that only the connection's
  `hello` received; it is limited per upload and per window, dropped after 30
  seconds without data, and decodes only for that connection, once. An upload
  that fails, also one whose client goes away, frees its slot, its space and its
  partial file. Its bytes, media type and file name are untrusted: validate the
  content before you use it, and never use `FileName` as a path.
- Uploads are stored in a new folder of each window with mode 0700 and a random
  name. Its base is the temporary directory only when no other user can change
  entries of it (owned by the user, or sticky as `/tmp`); otherwise a private
  folder of the user's cache. The host creates each file exclusively and reads
  it through that handle, never through its path again, so a symbolic link put
  in its place is never followed. Folders left by a crash are swept by a later
  run after a day without changes.

## Framing and Content-Security-Policy

Since 0.11.0 every Runic Desktop surface response sends
`Content-Security-Policy: frame-ancestors 'self'`, followed by the surface's
`AdditionalOrigins`, and `X-Frame-Options: SAMEORIGIN`, so a page of another
origin cannot frame the application. A content handler that sets its own
`Content-Security-Policy` header sends it as a second header, so the framing
policy stays in force.

The one exception is a hosted guest's frame surface in region mode: it sends
`frame-ancestors` with the one host page origin that its host named over the
host channel, and no `X-Frame-Options`
([hosted windows in a region](#hosted-windows-in-a-region)).

Add a Content-Security-Policy for your pages' scripts as a defence in depth
against injected script. Set `RunicDesktopHostOptions.ContentSecurityPolicy`
(Views on Runic Desktop), `RunicViewHost.ContentSecurityPolicy` (WPF),
`DesktopSurfaceOptions.ContentSecurityPolicy` (a Runic Desktop surface) or
`AssetRoutingOptions.ContentSecurityPolicy` (Runic Assets adapters); each is
sent with HTML responses. New `runic-app` projects set one outside
`Development`:

```text
default-src 'self'; script-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
```

`script-src 'self'` covers the Bridge on every host, in embedded WebViews and
installed browsers alike. Runic's bridge and bootstrap scripts and the Bridge
WebSocket come from the page's own origin, Bridge calls are data, and since
0.11.0 the Views Bridge's state also arrives as data that the served bridge
script parses with `JSON.parse`, without evaluating script. In embedded
windows the credential arrives in a document-start script, which page policies
do not block. The policy blocks inline, foreign and evaluated scripts
(`eval`, `new Function`). Leave `'unsafe-eval'` out unless something else
needs it:

- `DesktopSurface.ExecuteJavaScriptAsync` evaluates its script in the page
  with the `Function` constructor. Under a policy without `'unsafe-eval'` it
  fails with `InvalidOperationException`. It is meant for tests and
  diagnostics.
- On CS-WebUI, an application's own `RunJavaScript`/`ExecuteJavaScript` calls,
  and WebUI's `SetSize`/`SetPosition` while a browser window is connected,
  evaluate script and need `'unsafe-eval'`. CS-WebUI serves `SetRootFolder`
  files without headers of its own, so give such a page a
  `<meta http-equiv="Content-Security-Policy">` policy; a `<meta>` policy cannot
  carry `frame-ancestors`. Keep `runic-cswebui.js` copies current: a copy from
  before 0.11.0 receives no state under any policy.

`'self'` matches the same-origin `ws:` connection of the Bridge in current
WebView2, WKWebView, WebKitGTK, Chromium and Firefox. An older WebKitGTK that
does not needs `connect-src 'self' ws://127.0.0.1:*`.

The policy does not cover development. A development server is another
origin, and Vite's development client needs inline scripts and styles and its
own WebSocket, so `runic-app` projects send no policy in `Development`
(`dotnet runic dev`). Allow a development server only in development builds.
Frameworks that emit inline scripts need their own CSP support: the SvelteKit
starter uses `kit.csp` hash mode, and the Angular starter allows inline styles
and turns off critical-CSS inlining. Check the policy in each WebView you ship
on.

## Permissions

Sensitive WebView permissions stay denied unless the window opts in with
`DesktopWindowOptions.AllowedPermissions`. `MediaCapture` grants camera and
microphone to the presented origin only, in WebView2, WKWebView and WebKitGTK
windows; a browser fallback opens without it, and an explicitly selected
Chromium-based browser asks the user. Request only the grants a window needs, and
ask for capture before showing frames of other origins. See the
[Runic Desktop README](../packages/dotnet/Runic.Desktop/README.md) for the
diagnostics that report grants a presentation cannot apply.

## Release builds

- **Developer tools** are available in embedded windows only when
  `DOTNET_ENVIRONMENT` (or else `ASPNETCORE_ENVIRONMENT`) is `Development`
  ([links, new windows, downloads and developer tools](../packages/dotnet/Runic.Desktop/README.md#links-new-windows-downloads-and-developer-tools)).
- **Failure detail** (exception type, message and stack) reaches the page only in
  Development, or when a window sets `IncludeFailureDetail`. Detail can contain
  paths and application data; do not enable it for a distributed build
  ([failure detail in development](../packages/dotnet/Runic.Application.Views/README.md#failure-detail-in-development)).
- Do not ship with `DOTNET_ENVIRONMENT=Development`, and ship without debug
  symbols.

## Development server

`dotnet runic dev` serves the application's entry page from a frontend
development server on a free loopback port. On a shared machine, another user
could bind that port first. Since 0.11, `dev` gives the server a random value
for each start and accepts the server only when it answers with that value, so
another process's page is never served as the application's entry.
`@runic-artifex/vite-plugin-runic` answers it; every frontend that
`dotnet new runic-app` generates, including SvelteKit and Angular, is set up
for it. A project without the plugin, for example one created before 0.11, is
not checked: `dev` then relies on the server exiting when its port is taken.
Add the plugin as the
[`dotnet runic dev` documentation](../tools/dotnet-runic/README.md) describes
for your frontend.

## Hosted application windows

A Runic Desktop application can show another Runic Desktop application's windows: the **host** starts the
**guest**, which serves its pages without a window of its own, and opens a native window of its own for each of the
guest's Windows. The two talk over the [Runic host protocol](../specs/desktop/host-protocol.md), which is
experimental (`RUNICDESK103`). What Runic enforces:

- **Both sides opt in.** A guest can be hosted only through a code path of its own, reached by a parameter the
  application defines; Runic has no well-known flag or environment variable for it. A host starts a guest only by
  an explicit call.
- **The channel is the parent's alone.** The host passes a random nonce on the guest's standard input, and the guest
  passes a random key on its standard output: neither appears on a command line or in the environment. The guest's
  channel listens on loopback only, accepts one connection that proves both, and then stops listening. It refuses
  every upgrade that carries an `Origin` header, so no web page can reach it, and every `Host` that is not its
  loopback address. The host trusts the channel, and every answer to its `hello`, only after the guest proved that it
  knows the nonce, so a process that took the port of a guest that exited cannot pose as it. Connections that have not
  proved themselves get a few seconds and a small first message, and a new one replaces the oldest, so another local
  user cannot keep the host out by holding connections open; the host retries a connection that was closed.
- **Pages stay apart.** The guest's page binds to its own surface with a one-time launch code, as an installed
  browser does; the host injects no credential into it, does not admit its origin as one of its own and gives each
  guest session a data store of its own, not persisted, so the guest never receives the host's cookies. A guest's
  page shown in a region of a host page is admitted differently; see
  [hosted windows in a region](#hosted-windows-in-a-region).
- **The guest gets its own windows only.** The channel names windows by ids of the guest's own; it cannot list the
  host's windows, run script in the host's pages or navigate a hosted window away from its own origin. The host's
  policy (`IRunicHostedWindowPolicy`) can refuse or limit every request, and the protocol bounds message sizes, waiting
  requests, menus and dialogs.
- **No elevated guests, no orphans.** A guest refuses hosted mode when it runs elevated, or when it cannot show that
  it runs as the same user as its parent: it compares real and effective user ids on Linux and macOS and the token's
  user on Windows, also checking that its parent is not a newer process that reused a gone parent's id, and refuses
  when it cannot read its parent's. The one exception, the elevated pair, needs both sides to opt in; see
  [elevated hosts and guests](#elevated-hosts-and-guests). It exits when its host goes away and never shows a window of its own; the host
  ends the guest's process tree when it stops. On Windows that tree is a job object that ends with the host, also when
  the host crashes; on Linux and macOS a host that crashes leaves its guests to exit on the end of their standard
  input.
- **The guest gets no more than the host's own windows.** A guest's request for the camera and microphone is granted
  only as far as the host grants its own windows, unless the host's policy grants more.
- **Menus and dialogs: shown by the host, owned by the guest's window, with the guest's texts.** A guest's menus and
  context menus carry ids of the guest's own, never command ids: choosing an item only tells the guest which one, and
  an id names one item for the window's lifetime, so a click on a bar that the guest just replaced runs that item or
  nothing. A hosted window never shows the host application's own menus, and on macOS the host's About, Settings and
  Quit and its reserved shortcuts stay the host's. A guest's file dialogs and message boxes are the host's own
  dialogs, owned by the guest's window and closed with it; the host hands the guest the selected paths and keeps no
  access to them. A guest's context menu shows only in the active window, inside it, and the host bounds the rate of a
  window's menu requests and the dialogs a guest shows at once (`RunicHostedLimits`: 20 menu requests a second per
  window, one dialog per guest, by default).
- **Clipboard, links and files: the host's rights, the user's choices.** With `window-services/1` a guest uses the
  host's clipboard, monitors and launcher, which act with the host's rights. The clipboard holds what the user copies in
  every application, passwords included, so the host reads and writes it for a guest only while the user works in the
  guest's window: it is active, and not within two seconds after any of the guest's requests that can activate one of
  its windows, such as opening, presenting, maximizing or leaving full screen, closing an owned window, which activates
  its owner, or the end of one of its dialogs. A guest that brings its window to the front cannot use that focus to read
  what the user just copied, replace it or open links; the user's own later switch to the window, a choice in its native
  menu bar or in a context menu shown while the user worked in it, or a drop onto it gives it back. A shortcut of its
  menus does not, since a key the guest's page leaves unhandled runs it, and neither does an answer to the guest's own
  dialog or context menu. An activation the desktop deferred, such as macOS activating a window the guest presented
  only when the user next switches to the host, is the guest's too, however late it comes. The guest's windows,
  its first one included, therefore start guest-activated: a link the user clicks in a guest's fresh window is
  refused until the user switches to it. A host that started the guest at the user's request can set
  `RunicGuestStartInfo.UserStarted`, which makes the first activation of the guest's first window the user's; leave it
  off otherwise, since the guest chooses when it opens that window. Activations right after any guest's request count
  as a guest's, also for another guest's window, so one guest cannot hand another, or a second instance of itself, the
  user's activation; a host policy's own prompt counts as a guest's dialog. A write
  is also allowed within a second after the user left the window, for a copy that closes it. The host never tells a
  guest when the clipboard changes: a guest in the background cannot snoop on it or replace what the user copies
  elsewhere (threat T14). A guest the user works in can read what the clipboard holds, as any focused application can;
  a host that does not trust a guest refuses reads, or narrows writes to plain text, in
  `IRunicHostedWindowPolicy.ServiceAsync`, which also sees `RunicHostedServiceRequest.IsGuestActivated`. The host opens
  for a guest only `http`, `https` and `mailto` links and files the user gave it through the host (its open dialogs and
  drops, not a save dialog's destination, whose content the guest wrote), only while it is still the same file and only
  for the window the user works in, with the host launcher's own rules (no programs or scripts on Windows and macOS, no
  `mailto` attachments), at most five per ten seconds by default: a guest cannot start a program, open a settings page
  or flood the desktop with browser tabs unless the host's policy widens this (T15). The paths of a drop come from the
  host's platform, never from a page; the drop's target label and effect are routing hints (T16). `RunicHostedLimits`
  bounds a guest's clipboard and monitor requests (20 a second), its launches, its requests that can activate a window
  (2 a second after a burst of 3) and its announcements (2 a second after a burst of 5).
- **Screen reader announcements.** A guest's announcements speak through the host's window, so assistive technology
  attributes them to the host application. Only the window the user works in announces assertively, interrupting the
  screen reader; another window's announcements are polite, and their rate is bounded (T17). A host that does not trust
  a guest refuses or rewords them in `IRunicHostedWindowPolicy.Present`.
- **Appearance and locale.** A guest's pages show the host's appearance (contrast, forced colours, reduced motion, text
  scale) and follow its locale, which the host sends; they never read the guest process's own desktop settings.
- **What a guest can still word.** The guest chooses the titles, messages, details and button labels of its dialogs
  and the titles of its menus and items, and nothing marks them as the guest's: a message box titled "HostApp —
  Update required" with a single Install button, or a File item "Quit HostApp", looks like the host's own. On macOS
  (a sheet) and GTK 4 (no title) only the box's attachment to the guest's window tells. This is the residual spoofing
  risk of threat T5: a host that does not trust a guest marks or refuses its texts in its policy, such as with the
  guest's name before each dialog title (`IRunicHostedWindowPolicy.DialogAsync`, `Menu`), or refuses its message
  boxes.

What it means for the guest application: **any process of the same user** that knows the guest's parameter can host
it, see its pages and answer its dialogs, as such a process can already read its memory
([what Runic does not protect against](#what-runic-protects-and-what-it-does-not)). `RunicGuestOptions.AcceptHost`
filters hosts by name and version for the product's sake; it is not a security boundary. A host can forge what its
dialogs return and decide its windows' close, so a guest acts only on the files a dialog returned, as with its own
dialogs. A host should treat a guest like any other program it starts: start only the executables it chose, and keep
the policy's limits for guests it does not ship itself.

### Hosted windows in a region

With `region/1`, a host shows a guest's Window in a region of one of its own pages instead of in a window of its own:
the guest's page loads in a frame from the guest's own loopback origin
([regions](../specs/desktop/host-protocol.md#regions-region1)). What Runic enforces:

- **No script access either way.** The host page and the guest's page have different origins, so neither can read or
  script the other's document, address or storage. The frame's `sandbox` takes away top-level navigation, pop-ups,
  `alert` and `confirm`, and downloads.
- **A credential the host page never holds.** The guest admits its frame with a one-time frame launch code, which only
  the host's backend receives, over the channel, and with the frame token that the code redirects to. The token travels
  in the address's fragment, which never reaches a server or a `Referer`, and the guest's page removes it from the
  address at once. It admits one connected page at a time and dies when the region detaches; a spent code presented
  again revokes it. The host page can show the guest's page or keep it from loading, but never act as it (threat T21).
- **No cookies cross.** Frame admission uses no cookie. The guest serves the frame from the loopback family that the
  host page does not use, `[::1]` for a page on `127.0.0.1`, so the frame is cross-site to the host page: no cookie of
  the host page's origin reaches the guest, and Chromium and WebView2 run the frame in a process of its own (T22). A
  same-site frame would receive every cookie of the host page's origin, `HttpOnly` ones included. Where the other
  family is not available, such as without IPv6, an embedded host lets the guest fall back to the same family and logs
  a warning, since its web view profile holds only its own cookies; a browser host refuses, since the user's browser
  holds the cookies of other local applications.
- **Framing by the host's page only.** A frame surface allows framing (`frame-ancestors`) only by the origin that the
  host named over the channel. Another page, a rogue loopback server included, cannot frame the guest, and a guest
  still cannot frame its host's pages.
- **Focus stays the user's.** `present` brings the host window forward but never takes the focus from the host page.
  Focus that entered the region without the user's Tab or click, such as by the guest page's script, is the guest's:
  the clipboard and launches stay refused until the user moves the focus into the region (T20).
- **The host window's rules.** The guest's dialogs are owned by the host window. A guest shows no menu bar in a
  region. Files dropped onto the frame go to the guest, as `files-dropped` under the host's policy, never to the host
  page. The frame gets only the permissions that the host's policy granted the guest's Window, not the host window's.
- **What each side learns.** The host page learns the guest's title and document-edited flag, as the host's backend
  passes them on, and its region's state. The guest learns its region's size and scale and when the focus enters it,
  never the host page's content or the region's place in it.

What it means for applications:

- **Storage in a region is session-scoped.** A region's frame keeps `localStorage` and IndexedDB in the host window's
  web view profile or browser, partitioned under the host page's site, where they can outlive the session; window
  mode's data store per host session does not apply. Runic's page runtime uses `sessionStorage` only. Do not keep
  secrets in a guest's persistent storage.
- **Clickjacking is a residual (T19).** The host page can lay elements over the frame, make it transparent or move it
  under the pointer just before a click. For the host as a whole that gives nothing: a guest trusts its host as it
  trusts its own native dialogs (T7). A *compromised* host page, one with injected script or one that renders remote
  content, can however trick a click into an attached guest. It cannot attach a guest, which takes a code that only the
  host's backend receives. Do not embed guests in host pages that render untrusted content. A guest can guard a
  sensitive confirmation itself, such as with IntersectionObserver v2 visibility on Chromium. Runic adds no input
  guard.
- **Tokens in logs.** An engine's console message for a framing that `frame-ancestors` blocked can carry the frame's
  full address with its token. Runic's hosts forward no engine console messages; the page messages they do log, page
  errors (`client-errors/1`), have frame launch codes and frame tokens redacted, as do the guest's own logs. A token in a
  log is useless while its frame is connected, and it dies at detach.
- **Elevated guests admit elevated peers only.** An elevated guest admits connections to every one of its listeners,
  its channel, its page surfaces and its frame and component surfaces, only from loopback peer processes that run
  elevated. An installed browser, or another process of the user that does not run elevated, then cannot render or
  drive the elevated guest's page, also with a code or token it obtained (T23). The guest looks up each connection's
  process before it reads the request and closes a refused one without a response (event 2029,
  `elevated-peer-refused`): on Windows the owning process of the connection, held open while its token is read, and on
  Linux the socket's owner, which must be root. On macOS, where Runic cannot look up a connection's process, an
  elevated guest refuses region mode, and its other listeners keep the elevated pair's protections without the check.
  Residual: on Windows a process that inherited or was handed a socket from an elevated process passes, as that
  process chose to; Windows services that run as SYSTEM or another service account pass too, since they have High
  integrity or above.

**Trusted component mode** (`region-component/1`, reserved) will let a guest's Views run as a component in the host
page's own document, for theme and layout. There host page and guest code share one realm: the guest can read the host
page and call its Bridge, and the host page holds the guest's token. It needs an explicit opt-in on both sides,
`RunicGuestOptions.AllowComponentEmbedding` and the host policy's `RunicHostedWindowDecision.EmbedComponent`, and is
meant for applications of the same vendor.

### File access

File access is not handed over: a guest receives the paths the host's dialog returned, not the host's access to them,
and opens them with its own rights. Where the operating system grants access through its picker, a sandboxed host or
guest (an App Sandbox on macOS, a Flatpak or Snap on Linux) can receive paths it cannot open, or a host can return
paths that only it could open; the guest then reports the error of the open. Run hosts and guests that use dialogs
unsandboxed, or with the same file access.

### Elevated hosts and guests

Administrative tools whose host and guest both need administrator rights, such as an installer that hosts a check
tool, can opt into the **elevated pair**: the host sets `RunicGuestStartInfo.AllowElevatedGuest` and the guest
`RunicGuestOptions.AllowElevatedHost`. A guest started by an elevated process inherits its elevation. What Runic
enforces:

- **Only this pair.** An elevated guest is hosted only when its parent process, the one that started it and not a newer
  process that reused its id, runs as the same user and runs elevated itself: on Windows its token is elevated or has
  an integrity level of High or above, on Linux and macOS its effective user id is 0. The guest reads this from its
  parent's token or user ids; the offer's opt-in only says that the host allows it, and never counts as evidence. An
  elevated guest without its own opt-in, or whose host did not allow it, refuses with `guest-elevated` as before; one
  whose parent does not run elevated refuses with `guest-elevated-host-not-elevated`; one whose parent is another
  user, or cannot be read, with `guest-other-user`. The guest logs the accepted pair as event 2025.
- **Only an elevated host.** A host that does not run elevated cannot allow an elevated guest: the start fails with
  `host-not-elevated` before any process starts. The host starts an elevated guest only from a fully qualified path,
  never by looking it up in `PATH` or the current directory.

What the user's processes that do not run elevated can still do, and what they cannot:

- **Read the pair's secrets: no.** The nonce, the channel key and the Windows' launch codes travel only on the pipes
  between the two elevated processes and over the authenticated channel. On Windows a process of Medium integrity
  cannot read the memory of a High-integrity process or duplicate its handles, and UIPI keeps it from sending most
  window messages or input to the elevated windows. On Linux and macOS a process that does not run as root cannot
  trace a root process or read its memory, open file descriptors or environment; it can still see its command line
  and that it runs, so never pass a secret there.
- **Reach the guest's listeners: yes, without effect.** Any local process can connect to the guest's loopback channel
  listener and its surfaces. The channel refuses a connection without the key and the host proof, and stops listening
  once the host connected; a surface admits only the client that used a launch code, and on Linux refuses another
  user's process outright. Such a process can occupy the channel's unauthenticated connections until the host
  connects, as another user can (threat T12).
- **See and copy: yes.** It can capture the screen the elevated windows draw on and read the clipboard, which the host
  serves the guest as for any guest.
- **Become the host through a launcher: only with the user's consent.** The guest checks who its parent is, not where
  its standard input and output lead. An elevating launcher that relays another process's pipes, such as `sudo` in a
  pipeline on Linux and macOS, or Windows `sudo` in inline mode, makes the launcher the elevated parent and the
  unelevated process at the other end of the pipes the host. Starting it takes the user's authentication, as any
  elevation does.
- **Replace what the host starts: only if the location allows it.** The elevated guest runs whatever the executable,
  and the libraries its runtime loads from its directory, contain. Install host and guest where only administrators or
  root can write, such as `Program Files` or a root-owned directory, never in the user's profile, `Downloads` or a
  temporary directory: a process that can replace the guest's files runs code with the guest's rights. Runic checks
  that the path is fully qualified, not who can write to it.
- **Read the guest's pages in the host's web view: as far as the engine prevents it.** The elevated host's web view
  starts its processes from the host's token; the engine's own sandboxed processes, such as WebView2's renderers, run
  with fewer rights than the host, and how far the operating system keeps unelevated processes out of them is the
  engine's design, not Runic's.

## CS-WebUI windows

`Runic.Application.Views.CsWebUi` adds no admission of its own. Who can connect
to a CS-WebUI window is decided by native WebUI: its local server, the token it
writes into `webui.js` and its single-client default. `SetPublic(true)`,
serve-only mode (`RUNIC_APPLICATION_SERVE_ONLY=1`), multi-client configuration
and your own WebUI build are security decisions. A window that serves an
embedded frontend through a file handler keeps WebUI's authentication cookie on,
which WebUI otherwise turns off for the whole process. Runic does not review native
WebUI's admission; for untrusted content or shared machines, prefer Runic Desktop
([CS-WebUI security](../packages/dotnet/Runic.Application.Views.CsWebUi/README.md#security)).

## Frontend packages and packed assets

- **Lock files.** By default the build installs frontend packages only from a
  lock file ([`RUNICBRIDGE026`](diagnostics.md#runicbridge026)); commit it, and commit
  `packages.lock.json` for NuGet.
- **Packed assets are public.** Every file of the frontend build output that the
  asset packer includes ships to every user, and anyone can read it. Keep
  secrets out of the frontend: `.env` values that the bundler inlines, keys and
  source maps you do not want to publish. The
  [packer README](../tools/Runic.Assets.Packer/README.md) lists what it leaves
  out by default; exclude more with `RunicAssetsDistExclude`, and check what an
  archive contains before you ship it.

## What Runic protects, and what it does not

Runic protects:

- the Bridge from clients other than the window's own page: other local users,
  other browser tabs and sites, and the network, with the defaults above;
- the ViewModel from oversized and malformed Bridge input;
- embedded windows from navigating away from the application, opening new
  WebView windows, downloading and exposing developer tools in Release;
- platform calls from unsafe arguments: link schemes, `mailto` attachments,
  executable file types (best effort) and undeclared privileged programs.

Runic does not protect against:

- malware running as the same user, which can read the process's memory, its
  files and the session credential;
- script injected into your own page, which acts with the page's Bridge access
  (validate inputs and add a Content-Security-Policy);
- a compromised dependency of your application or its build;
- vulnerabilities of the WebView, the browser or the operating system; keep
  WebView2, WebKitGTK and browsers updated;
- options that relax a default, such as network exposure or multiple clients;
- signing, notarizing and distributing your application, which the application
  owns.

Runic's own packages carry build-provenance attestations; see
[verifying releases](../SECURITY.md#verifying-releases).
