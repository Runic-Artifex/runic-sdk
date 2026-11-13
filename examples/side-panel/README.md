# Side panel

An 860 × 640 order form on Runic Desktop with a debug panel that opens beside
the window instead of covering it, with a CommunityToolkit.Mvvm ViewModel. The
side panel is experimental under
[`RUNICDESK104`](../../docs/experimental.md#RUNICDESK104); the
[side panel guide](../../docs/side-panel.md) describes it on each platform.

- [`MainViewModel`](MainViewModel.cs) takes the Window's
  `IRunicWindowPresentation` and opens the panel with
  `OpenSidePanelAsync(new RunicSidePanelOptions { Width = 372, Path = "#/debug" })`.
  The window grows by 372 on its right edge, so the form keeps its size and its
  place on the screen. Dock left/right (`MoveToEdgeAsync`) moves the window by the
  panel's width; Close (`CloseAsync`) shrinks it back.
- When the window cannot grow, the result is `Refused` with a reason, and the
  ViewModel sets `ShowDebugOverlay`: the page shows the same debug tool over its
  own content. That happens while the window is maximized, full-screen or snapped,
  when the grown window would leave the monitor's work area (`NoRoom`), for the
  left edge on Wayland (`PositionUnavailable`), when the panel's page does not load
  (`LoadFailed`), and wherever there is no native window to grow (`Unsupported`):
  an installed browser, a serve-only run, WPF, CS-WebUI or a hosted window.
- The panel's page is a second connection of the same Window and binds to the
  same `MainViewModel`, so the debug tool shows the form's quantity and the log
  as they change, and its buttons run the ViewModel's commands. The panel closes
  itself with `window.close()` too; `IRunicSidePanel.Closed` then tells the
  ViewModel, whoever closed it.
- [`Frontend/src/app.ts`](Frontend/src/app.ts) is one bundle for both pages:
  with `#/debug` it renders the debug tool alone. The panel's address is its own:
  Runic does not sync it with the window's navigation path, so the hash is all the
  route needs. The debug tool is an `<aside aria-label="Debug tools">`, a named
  complementary landmark, and the panel's page has its own `<title>`, which does
  not rename the window.

## Run it from this repository

```sh
bun run bootstrap
dotnet run --project examples/side-panel/SidePanelSample.csproj
```

The panel opens beside the window on Windows (WebView2), macOS (WKWebView) and
Linux (WebKitGTK, GTK 3 and GTK 4); elsewhere the page shows the overlay.
Maximize the window, or snap it, before you press **Show debug panel** to see the
fallback.

`dotnet run --project examples/side-panel/Tests` runs the ViewModel's tests with
`RunicTestWindowPresentation`, which grows and refuses as a desktop window does.
`node examples/side-panel/browser-smoke.mjs` runs the example without a window
after a Release build and drives it from headless Chromium: the panel is refused
there (`Unsupported`), so it checks the overlay, and then the `#/debug` route.
