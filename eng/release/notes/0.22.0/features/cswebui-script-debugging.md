## CS-WebUI pages in a JavaScript debugger

In Development, a CS-WebUI window in an installed Chrome, Edge or other
Chromium browser can open that browser's Chrome DevTools Protocol endpoint on
127.0.0.1, so an IDE's JavaScript debugger attaches to the page: Visual Studio
with Attach to Process ("Chrome devtools protocol websocket (no
authentication)"), VS Code with js-debug's `attach`. Runic.Desktop adds
`--remote-debugging-port` when `RUNIC_WEBUI_REMOTE_DEBUGGING_PORT` asks for it,
`0` for a free port or a fixed one, beside its Chromium defaults. It adds none
outside Development, when the window's `WebContent` policy sets
`DevTools = false`, or when the application's `BrowserArguments` name the switch
themselves.

- `dotnet runic dev` asks for a free port for a CS-WebUI project, prints
  `[dev] Debugging:` with it, and lists it in the session file's
  `debugEndpoints` as `{ "engine": "chromium", "protocol": "cdp", "url": … }`
  while the window's browser runs. With `--no-window` it uses port 9225 and
  lists that for the session. `--no-debug-endpoint` turns it off.
- New CS-WebUI projects from `dotnet new runic-app --host cswebui` have a
  `(CS-WebUI script debugging)` launch profile that opens port 9225.

Any local process can attach to the port; on a shared machine, run
`dotnet runic dev --no-debug-endpoint`. See
[CS-WebUI pages in a JavaScript debugger](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/debugging.md#cs-webui-pages-in-a-javascript-debugger)
and the [security guide](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/security.md#development-server).
