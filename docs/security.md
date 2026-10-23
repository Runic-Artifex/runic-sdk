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
default-src 'self'; script-src 'self' 'unsafe-eval'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
```

`script-src 'self'` alone does not cover the Bridge yet. Runic Desktop runs
the scripts it sends to the page, including the Views Bridge's state and
replies, with the `Function` constructor, so the policy needs `'unsafe-eval'`
for now; it still blocks inline and foreign scripts. A later release removes
that need. The Bridge WebSocket and Runic's bootstrap scripts come from the
surface's own origin, which `'self'` covers, and in embedded windows the
credential arrives in a document-start script, which page policies do not
block. A development server is another origin: allow it only in development
builds. Frameworks that emit inline scripts need their own CSP support: the
SvelteKit starter uses `kit.csp` hash mode, and the Angular starter allows
inline styles and turns off critical-CSS inlining. Check the policy in each
WebView you ship on.

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

## CS-WebUI windows

`Runic.Application.Views.CsWebUi` adds no admission of its own. Who can connect
to a CS-WebUI window is decided by native WebUI: its local server, the token it
writes into `webui.js` and its single-client default. `SetPublic(true)`,
serve-only mode (`RUNIC_APPLICATION_SERVE_ONLY=1`), multi-client configuration
and your own WebUI build are security decisions. Runic does not review native
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
