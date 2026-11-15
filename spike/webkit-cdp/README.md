# Spike: WebKit Inspector Protocol ↔ CDP adapter (runic-sdk#1029)

Throwaway prototype, not built by `RunicSdk.slnx`, not packed, not shipped. Report:
local-planning `design/dx-0.21/WEBKIT-CDP-ADAPTER.md`.

- `WebKitCdpAdapter/` (C#, net10.0): connects to WebKitGTK's remote inspector server (`WEBKIT_INSPECTOR_SERVER`, the
  socket GNOME Web uses for `inspector://`) and serves a Chrome DevTools Protocol endpoint on loopback:
  `/json/version`, `/json/list`, `/devtools/browser/<secret>`, `/devtools/page/<secret>/<id>`. Refuses a foreign `Host`
  and any `Origin` (403). `InspectorSocket.cs` is the socket protocol, `CdpServer.cs` the HTTP/WebSocket side and the
  browser-level `Target.*`, `WipPage.cs` the per-page CDP ↔ WIP translation.
- `dap-harness.mjs`: drives vscode-js-debug's standalone DAP server (`js-debug-dap-v1.140.1`) like VS Code's
  `"request": "attach"`: breakpoint in a source file, click, stack/scopes/locals, evaluate, console, step, continue.
- `dap-scenarios.mjs`: nine scenarios against the Runic template's React page (`Frontend/src/App.tsx`): step in/out,
  conditional breakpoint, logpoint, pause on uncaught exception, uncaught exception output, console levels,
  locals/setVariable/hover, breakpoint during the first render after a reload, rebinding after an HMR edit.
- `evidence.md`: the runs the report cites.

Run (Linux, runic-sdk dev shell):

```sh
# a Runic app under dev with WebKit's inspector server
dotnet runic dev -e WEBKIT_INSPECTOR_SERVER=127.0.0.1:9340
# the adapter
dotnet build spike/webkit-cdp/WebKitCdpAdapter && dotnet spike/webkit-cdp/WebKitCdpAdapter/bin/Debug/net10.0/WebKitCdpAdapter.dll --inspector 127.0.0.1:9340 --port 9229
# js-debug against it (gh release download v1.140.1 -R microsoft/vscode-js-debug -p 'js-debug-dap-*')
node spike/webkit-cdp/dap-scenarios.mjs --jsdebug <js-debug dir> --port 9229 --frontend <app>/Frontend
```

VS Code: `{ "type": "chrome", "request": "attach", "name": "Window (WebKitGTK)", "port": 9229, "webRoot": "${workspaceFolder}/Frontend" }`.
