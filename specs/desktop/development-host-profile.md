# Development host profile

The environment a desktop host reads when the development tool starts it, and the lines the host writes back. A host
that supports the development loop (`dotnet runic dev` today, `rax dev` from 0.25) implements this profile; the tool
then needs nothing host-specific to run it. The "in .NET" notes say where `Runic.Application.Views.Desktop` and
`Runic.Desktop` read each name; they are not normative.

**Names.** 0.25 renames every `RUNIC_*` name to `RAX_*` (D079.25). The table gives both. A host for 0.24 reads the
`RUNIC_*` name; a host for 0.25 or later reads only the `RAX_*` name. Names that are not Runic's, such as `WEBKIT_INSPECTOR_SERVER`,
keep their spelling.

## The Development environment

A host runs **in Development** when `DOTNET_ENVIRONMENT`, or else (when it is unset or empty) `ASPNETCORE_ENVIRONMENT`,
equals `Development`, compared without case. The development tool sets `DOTNET_ENVIRONMENT=Development` unless the
developer set either variable. From 0.25 a host that is not .NET reads `RAX_ENVIRONMENT` first, and the tool sets it
too; the two .NET variables stay the fallback. Everything in [Development variables](#development-variables) applies only in
Development; outside it a host ignores them.

In .NET: `WebContentRules.IsDevelopment` (`packages/dotnet/Runic.Desktop/Internal/WebContentRules.cs:47-52`) and the
same rule in `DevelopmentAssets` (`packages/dotnet/Runic.Application.Views.Desktop/DevelopmentAssets.cs:56-61`); the tool
in `tools/dotnet-runic/HostProcessController.cs:351-357`.

## Development variables

| 0.24 name | 0.25 name | The host |
| --- | --- | --- |
| `RUNIC_APPLICATION_DEVELOPMENT_ASSETS` | `RAX_APPLICATION_DEVELOPMENT_ASSETS` | Serves the named directory live, before its packaged web root, and reloads its windows when the directory changes |
| `RUNIC_APPLICATION_VITE_DEV_SERVER` | `RAX_APPLICATION_VITE_DEV_SERVER` | Forwards the requests its web root does not answer to the Vite development server at this origin |
| `RUNIC_APPLICATION_FRONTEND_DEV_SERVER` | `RAX_APPLICATION_FRONTEND_DEV_SERVER` | The same for another development server (Angular); read when the Vite variable is unset or blank |
| `RUNIC_DESKTOP_DEVTOOLS_EXTENSIONS` | `RAX_DESKTOP_DEVTOOLS_EXTENSIONS` | WebView2 only: loads these unpacked browser extensions into the developer tools |
| `WEBKIT_INSPECTOR_SERVER`, `WEBKIT_INSPECTOR_HTTP_SERVER` | unchanged | WebKitGTK only: keeps them in Development, so WebKit opens its remote inspector; removes them outside it |
| `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS` | unchanged | WebView2 only: keeps it in Development; removes it outside it |
| `RUNIC_WEBUI_REMOTE_DEBUGGING_PORT` | `RAX_WEBUI_REMOTE_DEBUGGING_PORT` | Installed Chromium-based browsers only: opens their DevTools Protocol endpoint |

**Development assets.** The value must be a fully qualified path of an existing directory, and the application must not
have chosen its own surface options; otherwise the host serves its web root as without the variable. A window reloads
the last HTML document it loaded from the surface, else its start page, keeping only the path. A connected page is
reloaded through its transport; a page without a transport, such as a half-written `index.html`, by navigating its
webview. The tool sets this variable only when no development server runs, and clears it when one does. In .NET:
`packages/dotnet/Runic.Application.Views.Desktop/DevelopmentAssets.cs:8-52`; a trimmed or NativeAOT publish removes it.

**Development server.** The value must be an `http` origin on the IPv4 loopback address `127.0.0.1`, with an optional
base path, and no user information, query or fragment; `localhost` is not accepted, because it may
resolve to `::1`, where another local user could listen. Any other value is ignored. The host consults the server only for requests its web root and
the application's resources do not answer. A path relative to a window's document is forwarded, so the page keeps its
own origin for it; a root-relative path outside every window's path is redirected to the server. The host forwards only
`GET` and `HEAD`, only from a loopback peer with a loopback or absent `Host`, only same-origin or user-initiated fetches, and never a
first path segment that starts with `__` or `@` other than `__devtools` and `__devtools-assets` at the root. The
forwarded request carries no cookies, credentials, `Origin` or `Sec-Fetch-*`; redirects are not followed; only
successful answers come back, within a size and time limit. WebSockets are not forwarded. In .NET:
`packages/dotnet/Runic.Application.Views.Desktop/DevelopmentServerForwarding.cs:6-50,68-92`, and the
[security guide](../../docs/security.md#development-server).

**Developer tools extensions.** Entries are separated by the platform's path separator (`;` on Windows); blank entries
and entries that are not fully qualified paths are skipped. They load after the application's own, each folder once.
In .NET: `packages/dotnet/Runic.Desktop/Internal/DevToolsExtensionFolders.cs:6-28`.

**Inspector variables.** WebKitGTK opens its remote inspector when one of them is set as its first web context starts,
whatever the window's developer-tools setting says, and WebView2 adds its variable's switches to every browser it
starts. So outside Development a host removes them from its process environment, the native copy included, before it
creates its first web context or WebView2 environment. In .NET:
`packages/dotnet/Runic.Desktop/Internal/WebKitInspectorEnvironment.cs:5-29`,
`packages/dotnet/Runic.Desktop/Internal/WebView2BrowserArgumentsEnvironment.cs:3-29`.

**Remote debugging port.** `0` asks for a free port, another integer from 1 to 65535 for that port. The endpoint opens
only when the window's developer tools are not turned off, the browser is Chromium-based with its own profile directory,
and the application's own browser arguments do not name `--remote-debugging-port`. In .NET:
`packages/dotnet/Runic.Desktop/Internal/WebUiRemoteDebugging.cs:7-56`. A host without installed-browser windows ignores
it.

The tool also sets `RUNIC_APPLICATION_VITE_ENTRY`, `RUNIC_APPLICATION_VITE_PACKAGE_DIRECTORY`,
`RUNIC_APPLICATION_FRONTEND_DEV_SERVER_KIND`, `RUNIC_APPLICATION_FRONTEND_COMPILER_DIAGNOSTICS` and
`RUNIC_APPLICATION_FRONTEND_COMPILER_HOT_RELOAD` for the Window process (0.25 removes them rather than renaming them,
since nothing reads them),
and clears them when no development server runs (`tools/dotnet-runic/ViteDevelopmentServer.cs:61-70`,
`tools/dotnet-runic/AngularDevelopmentServer.cs:26-31`, `tools/dotnet-runic/HostProcessController.cs:333-348`). A host
does not read them.

## Serve-only and automation variables

These are not limited to Development.

| 0.24 name | 0.25 name | The host |
| --- | --- | --- |
| `RUNIC_APPLICATION_SERVE_ONLY` | `RAX_APPLICATION_SERVE_ONLY` | `1`: serves each window's page without opening a window or browser |
| `RUNIC_APPLICATION_SERVE_ORIGIN` | `RAX_APPLICATION_SERVE_ORIGIN` | With serve-only, admits this origin for the pages and connects their transport through its port |
| `RUNIC_APPLICATION_CLOSE_AFTER_OPEN` | `RAX_APPLICATION_CLOSE_AFTER_OPEN` | `1`: closes each window as soon as it opened |

**Serve-only.** With the value `1` (any other value is off), the host opens no window or browser and runs no window
toolkit loop. Each window creates its surface and writes its [lines](#lines-on-standard-output). Serving ends for every
window serving when a line is read from standard input, when the host is asked to shut down (a termination signal), or
when the process ends. The end of standard input is not a stop request. A window opened after a line waits for the
next one. In .NET: `packages/dotnet/Runic.Application.Views.Desktop/RunicDesktopHost.cs:1022,1057-1111`.

**Serve origin.** Read only with serve-only, and not by a hosted guest. Accepted only as `http://localhost:<port>` or
`http://<label>.localhost:<port>`, where `<label>` is one DNS label (1 to 63 letters, digits and inner hyphens), with an
explicit port and nothing after it. Any other value is ignored, and the window serves as without it. The development
tool sets it under `--browser`, with its per-session address. In .NET:
`packages/dotnet/Runic.Application.Views.Desktop/RunicDesktopHost.cs:1024-1055`.

**Close after open.** With the value `1`, the host closes each window it opens once its presentation has opened (and,
when the host waits for the page's connection, after the page connected). Before closing, it writes a warning to
standard error, because a child process inherits the variable, and the `OPENED` line. In .NET:
`packages/dotnet/Runic.Application.Views.Desktop/RunicDesktopHost.cs:1113-1130`.

## Lines on standard output

Each line ends with the platform's newline. Fields are separated by one space; a later release may add fields after
them, so a reader ignores fields it does not know.

| 0.24 prefix | 0.25 prefix | When | Fields |
| --- | --- | --- | --- |
| `RUNIC_APPLICATION_URL=` | `RAX_APPLICATION_URL=` | Serve-only: each window, once its surface serves | The surface URL |
| `RUNIC_APPLICATION_WINDOW=` | `RAX_APPLICATION_WINDOW=` | Right after the `URL` line, in the same write, when the host knows the window's type | The surface URL, then the window's type name without spaces (a space is written as `%20`) |
| `RUNIC_APPLICATION_OPENED=` | `RAX_APPLICATION_OPENED=` | Close after open: before the close | The presentation that opened, such as `Embedded` or the browser |
| `RUNIC_APPLICATION_DEBUG_ENDPOINT=` | `RAX_APPLICATION_DEBUG_ENDPOINT=` | Remote debugging: the endpoint opened | Engine, protocol and URL, such as `chromium cdp http://127.0.0.1:<port>/` |
| `RUNIC_APPLICATION_DEBUG_ENDPOINT_CLOSED=` | `RAX_APPLICATION_DEBUG_ENDPOINT_CLOSED=` | Remote debugging: the browser exited | The endpoint URL |

The `URL` and `WINDOW` lines of one window are written in one write, so another window's lines cannot come between
them. The type name is implementation-defined and stable; in .NET it is the type's full name, and for a closed generic
type the definition's full name with its arguments' names without namespaces, such as
`` MyApp.DocumentWindow`1[Report] ``. In .NET: `packages/dotnet/Runic.Application.Views/ServeOnlyAnnouncement.cs:1-35`,
`packages/dotnet/Runic.Desktop/Internal/WebUiRemoteDebugging.cs:39-42`; the tool reads them in
`tools/dotnet-runic/BrowserEntry.cs:55-62,218-235`.

## What the tool does without the host

These belong to the development tool and the development server; a host has nothing to implement for them:

- `[dev] Browser: Window serves at <url>`, matched by `^\[dev\] Browser: Window serves at (?<url>http://\S+)$`, is
  written by the tool for IDEs, from the host's `URL` lines (`tools/dotnet-runic/DevApplication.cs:81-91`; the
  [IDE contract](../../tools/dotnet-runic/README.md#ide-contract)).
- `/__runic/state` (0.25: `/__rax/state`) is answered by the development server's plugin with
  `RUNIC_APPLICATION_DEV_SESSION` (0.25: `RAX_APPLICATION_DEV_SESSION`), so the tool knows the server on the port is its
  own (`tools/dotnet-runic/ViteDevelopmentServer.cs:27-34`). `RUNIC_APPLICATION_DEV_PROJECT` and
  `RUNIC_APPLICATION_DEV_SOURCE_MAP` (0.25: `RAX_APPLICATION_DEV_PROJECT`, `RAX_APPLICATION_DEV_SOURCE_MAP`) are for the
  development server too.

The list of every Runic variable, with who sets and reads it, is [docs/environment.md](../../docs/environment.md).
