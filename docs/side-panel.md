# Side panel

A side panel shows a second page of a window beside the window's page, in a strip
on one edge by which the window grows. The page keeps its size and its place on
the screen: its viewport, media queries, `vw` units and `position: fixed`
elements do not change, as they would if the page drew the panel itself. A debug
tool, an inspector or a help pane is a typical panel. Where the window cannot
grow, the application shows the same content another way, usually as an overlay
of its page.

The side panel is new in 0.19 and experimental for one minor release under
[`RUNICDESK104`](experimental.md#RUNICDESK104). The window building blocks it
uses (`DesktopWindowState.Bounds`, `SetBoundsAsync`, `SetMinimumSizeAsync`,
`IsSnapped`) are stable. The [side panel example](../examples/side-panel/README.md)
is an 860 × 640 window with a debug panel and the overlay fallback.

- [Open it from a ViewModel](#open-it-from-a-viewmodel)
- [The panel's page](#the-panels-page)
- [When the window cannot grow](#when-the-window-cannot-grow)
- [The window while the panel is open](#the-window-while-the-panel-is-open)
- [Accessibility](#accessibility)
- [Platforms](#platforms)
- [Known limits](#known-limits)
- [Testing](#testing)

## Open it from a ViewModel

A ViewModel asks the Window's `IRunicWindowPresentation`, which every host
provides (register it with `AddRunicWindowService()`). Suppress `RUNICDESK104`
in the project, or around the calls:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);RUNICDESK104</NoWarn>
</PropertyGroup>
```

```csharp
public sealed partial class MainViewModel(IRunicWindowPresentation window) : ObservableObject
{
    private IRunicSidePanel? _panel;

    [ObservableProperty] public partial bool IsDebugPanelOpen { get; private set; }
    [ObservableProperty] public partial bool ShowDebugOverlay { get; private set; }

    [RelayCommand]
    private async Task ShowDebugAsync(CancellationToken cancellationToken)
    {
        var result = await window.OpenSidePanelAsync(
            new RunicSidePanelOptions { Edge = RunicSidePanelEdge.End, Width = 372, Path = "#/debug" }, cancellationToken);
        if (result is RunicSidePanelResult.Opened { Panel: var panel })
        {
            _panel = panel;
            // Raised once, in a turn of the Window's model context, whoever closed the panel.
            panel.Closed += (_, _) => IsDebugPanelOpen = false;
            IsDebugPanelOpen = true;
        }
        else if (result is RunicSidePanelResult.Refused { Reason: not RunicSidePanelRefusal.Closed })
        {
            ShowDebugOverlay = true;   // Maximized, Snapped, NoRoom, Unsupported, ...
        }
    }

    [RelayCommand]
    private async Task HideDebugAsync(CancellationToken cancellationToken)
    {
        ShowDebugOverlay = false;
        if (_panel is { } panel) await panel.CloseAsync(cancellationToken);
    }
}
```

- `Width` is in the window's units: device-independent pixels on Windows, points
  on macOS, logical pixels on Linux. It is at least
  `RunicSidePanelOptions.MinimumWidth` (120).
- `Edge` is `End` by default: the right edge of a left-to-right window, the left
  edge of a right-to-left one. `Left` and `Right` ignore the layout direction.
- `Path` is a route of the application's own frontend, relative to the address
  the window's page opened with: `side-panel`, `/side-panel`,
  `side-panel?tab=log` or `#/debug` for a hash router. An absolute URL or a path
  that leaves the page's directory throws `ArgumentException`.
- `IRunicSidePanel` moves to the other edge (`MoveToEdgeAsync`), changes its
  width (`SetWidthAsync`), takes the focus (`FocusAsync`) and closes
  (`CloseAsync`, or disposing it). Move and width changes return `null` when done
  or a refusal; the panel then stays as it was. There is no splitter: an
  application that wants a resizable panel offers its own control and calls
  `SetWidthAsync`.
- `SidePanel` on the presentation is the open panel, or `null`; `PropertyChanged`
  reports it. `Features.HasFlag(RunicWindowPresentationFeatures.SidePanel)` says
  whether the host has a panel at all, for example to hide the command.

Code that holds the `DesktopWindow` uses the same operations there:
`DesktopWindow.OpenSidePanelAsync`, `SidePanel` and `CheckSidePanel(edge, width)`,
which answers synchronously from the last reported state, for example to enable a
command. The [Runic.Desktop README](../packages/dotnet/Runic.Desktop/README.md#side-panel)
describes the geometry in detail.

## The panel's page

The panel's page is a second connection of the same window, with a credential of
its own. It binds to the same ViewModel tree as the window's page, so both show
the same state and either runs the ViewModel's commands; edits from both are
serialized in the window as for any two pages of one window. `IRunicConnections`
lists it.

One frontend bundle usually serves both pages. Render the panel's route as the
panel's content:

```ts
const main = await connectMain();
const render = location.hash === "#/debug" ? renderDebugTool : renderOrderForm;
main.subscribe(() => render(main.snapshot));
```

What differs from the window's page:

- **No address or history sync.** The panel's connection is not granted
  `navigation-path/1`: its URL and history are its own, it neither follows nor
  drives the window's navigation path, and its Back does not navigate the window.
- **`window.close()`** in the panel's page closes the panel, never the window.
- **Its `<title>`** does not rename the window. Its zoom is its own.
- **Drops are refused.** Files dropped on the panel do not reach the
  application; drops go to the window's page only.
- **Hosted regions** may be shown in the panel, as in the page.
- **A crash** of the panel's renderer reloads the panel once; a second crash
  closes it with `RunicSidePanelCloseReason.Crashed` and the window shrinks back.
  A reload of either page creates a new connection for that page only.
- **Storage.** The panel shares the page's profile: cookies and `localStorage`
  are the same, `sessionStorage` is per page.

## When the window cannot grow

`OpenSidePanelAsync` returns `Refused` with a reason, and the window does not
change. Show the content another way. The reasons, in the order the window checks
them:

| Reason | When |
| --- | --- |
| `Unsupported` | No native window to grow: an installed browser after a fallback, a serve-only application, a WPF `RunicWebView` (the WPF application owns its layout and can place a second `RunicWebView` itself), CS-WebUI, a custom window host, a hosted application's window or region (`runic-host/1`), a surface that admits several clients, a transparent GTK 3 window |
| `AlreadyOpen` | A panel is already open; `SidePanel` is that panel |
| `Minimized` | The window is minimized or hidden |
| `FullScreen` | The window is full-screen, including kiosk mode |
| `Maximized` | The window is maximized (zoomed on macOS) |
| `Snapped` | The window is snapped or tiled (`DesktopWindowState.IsSnapped`); widening it would break the layout |
| `PositionUnavailable` | The left edge on Wayland, where an application cannot move its window; the right edge works |
| `NoRoom` | The grown window would leave the monitor's work area, or the panel is wider than half of it. The window is never moved to make room |
| `LoadFailed` | The panel's page did not load within `LoadTimeout` (10 seconds by default) |
| `Closed` | `IRunicWindowPresentation` only: the Window or the panel is closed |

The enum is open: treat a reason you do not know as `Unsupported`.

## The window while the panel is open

- **One step, exact bounds.** The panel's page loads first, outside the window's
  visible area; then the window grows in one native bounds change. On the right
  edge it widens; on the left edge it moves left by the panel's width and widens,
  and the page's view moves right inside it, so the page stays where it was on the
  screen. Closing restores exactly the bounds the window had, unless the user
  moved or resized it meanwhile; then only the docked edge moves back.
- **State.** `DesktopWindowState.Bounds` and `VisibleBounds` include the panel;
  `Placement`, which applications save, excludes it, so a restored window opens at
  its page's size and the application decides whether to reopen the panel.
- **Minimum size.** The window's minimum width grows by the panel's width.
- **User resize, maximize and full screen** keep the panel's width; the page
  takes the rest. Width changes and moves are refused there (`Maximized`,
  `FullScreen`). Closing the panel there gives the page the whole window, and the
  size the window restores to shrinks by the panel's width.
- **A DPI change** scales both views; both keep their widths in window units.
- **Closing the window** closes the panel with `WindowClosed`.

## Accessibility

- Give the panel's page a `<title>` and wrap its content in a named
  `complementary` landmark, such as `<aside aria-label="Debug tools">`. Screen
  readers can announce the landmark when the focus moves into it, and their
  landmark navigation finds it.
- **Reading order follows the edge.** The window's accessibility tree lists the
  panel's view before the page's on the left edge and after it on the right: UI
  Automation (the panel's child window's z-order), the macOS accessibility API
  (the content view's subviews) and AT-SPI (the overlay's children).
- **Tab** past the page's last focusable element focuses the panel's first, and
  Shift+Tab hands the focus back, in both directions.
- `IRunicSidePanel.FocusAsync` moves the focus into the panel, for example for a
  keyboard shortcut that opens it.

## Platforms

| Platform | How | Notes |
| --- | --- | --- |
| Windows (WebView2) | A second WebView2 controller from the page's environment (same user data folder, browser process and profile) in a child window | `WM_NCCALCSIZE` keeps the page's pixels in place when the left edge moves. Focus moves between the views through `MoveFocusRequested` |
| macOS (WKWebView) | A second `WKWebView` with the page's `websiteDataStore` and its own content controller | One `setFrame:display:`. While zoomed or full-screen, closing the panel takes effect on the frame the window restores to once it is back there. A script hands Tab between the views |
| Linux (WebKitGTK, GTK 3 and GTK 4) | A related web view (same web context and web process) in a `GtkOverlay` with the page's view | X11: one move-and-resize. Wayland: the right edge only. `Runic.Desktop.Gtk4`'s window supports it; other custom window hosts refuse |
| Installed browser, serve-only, WPF, CS-WebUI, custom window host, hosted window or region | — | `Unsupported` |

## Known limits

- **Experimental** under `RUNICDESK104` for 0.19: names and behaviour can change
  in the next minor release.
- **Wayland:** the left edge is refused with `PositionUnavailable`; dock right or
  fall back. The right edge on Wayland is not checked in CI, which has no headless
  compositor.
- **Transparent GTK 3 windows** refuse the panel with `Unsupported`, which keeps
  their transparency exactly as before.
- **Drops** on the panel are refused on every platform. On GTK 4 the panel has no
  drop target at all, so a drag inside the panel's own page is refused too; on
  GTK 3 and macOS only drags from outside the panel are.
- **No address or history sync** for the panel's page (above).
- **Snapped detection** uses the operating system where it reports snapping, and
  geometry (halves and quarters of the work area) where it does not, such as macOS
  tiling: best effort.
- **GTK 4 regions** route nothing on the GTK 4 window host, in the panel as in
  the page.
- **macOS Tab hand-off** finds the first and last tab stops with a selector, so
  tab stops in a shadow DOM are skipped, and an element focused this way may not
  show `:focus-visible`.
- **Not verified by CI:**
  - on Windows on Arm (win-arm64), the frame capture and Tab between the views,
    because the runner's desktop shows GDI nothing and delivers no `SendInput`;
  - on macOS, whether the window's frame and the views' frames change in the same
    displayed frame on the left edge (there is no per-frame capture);
  - on Linux, the pixels of the left-edge change, which only Windows captures,
    and window managers other than Openbox;
  - a real Win+arrow snap, a DPI change with the panel open, the crash path and
    `LoadFailed`;
  - Narrator, which stays a manual check.

## Testing

`RunicTestWindowPresentation` (`Runic.Application.Testing`) decides the panel as
a desktop window does: an 860-wide window at `WindowX` 530 on a 1920-wide work
area that grows on the panel's edge and refuses in the desktop's order. Set
`IsMaximized`, `IsSnapped`, `WorkAreaWidth` or `Features` to produce a refusal,
`SetSidePanelRefusal(reason)` for one of your choice (such as
`PositionUnavailable` or `LoadFailed`), and `CloseSidePanel(reason)` to close the
panel as its page, a crash or the window would. The
[Testing README](../packages/dotnet/Runic.Application.Testing/README.md#the-windows-presentation)
has the details, and the
[example's tests](../examples/side-panel/Tests/SidePanelTests.cs) check the
overlay fallback for every refusal.
