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
  which matters on Windows, where http.sys routes requests by their `Host`.
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
  guest session a data store of its own, not persisted, so the guest never receives the host's cookies.
- **The guest gets its own windows only.** The channel names windows by ids of the guest's own; it cannot list the
  host's windows, run script in the host's pages or navigate a hosted window away from its own origin. The host's
  policy (`IRunicHostedWindowPolicy`) can refuse or limit every request, and the protocol bounds message sizes, waiting
  requests, menus and dialogs.
- **No elevated guests, no orphans.** A guest refuses hosted mode when it runs elevated, or when it cannot show that
  it runs as the same user as its parent: it compares real and effective user ids on Linux and macOS and the token's
  user on Windows, also checking that its parent is not a newer process that reused a gone parent's id, and refuses
  when it cannot read its parent's. It exits when its host goes away and never shows a window of its own; the host
  ends the guest's process tree when it stops. On Windows that tree is a job object that ends with the host, also when
  the host crashes; on Linux and macOS a host that crashes leaves its guests to exit on the end of their standard
  input.
- **The guest gets no more than the host's own windows.** A guest's request for the camera and microphone is granted
  only as far as the host grants its own windows, unless the host's policy grants more.
- **Menus and dialogs stay the host's UI.** A guest's menu items and context menus carry titles, shortcuts and ids of
  the guest's own, never its command ids, and choosing one only tells the guest which item; a hosted window never
  shows the host application's own menus, and on macOS the host's About, Settings and Quit stay the host's. A guest's
  file dialogs and message boxes are the host's own dialogs, owned by the guest's window, one at a time, and closed
  with it; the host hands the guest the selected paths and keeps no access to them.

What it means for the guest application: **any process of the same user** that knows the guest's parameter can host
it, see its pages and answer its dialogs, as such a process can already read its memory
([what Runic does not protect against](#what-runic-protects-and-what-it-does-not)). `RunicGuestOptions.AcceptHost`
filters hosts by name and version for the product's sake; it is not a security boundary. A host can forge what its
dialogs return and decide its windows' close, so a guest acts only on the files a dialog returned, as with its own
dialogs. A host should treat a guest like any other program it starts: start only the executables it chose, and keep
the policy's limits for guests it does not ship itself.

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
