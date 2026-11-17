## The side panel and the close drain are stable

The side panel (`RUNICDESK104`, 0.19) and the close drain (`RUNICDESK105`, 0.19)
graduate: their `[Experimental]` attributes are removed and their API is supported
like the rest of its package. Both are unchanged since 0.21, which made the last
API review changes (`DesktopSidePanelRefusal.Closed`, `RunicCloseDrain.KeepWindow`).
Their IDs are retired and never name another family.

- **Side panel:** `DesktopWindow.OpenSidePanelAsync`, `SidePanel`,
  `CheckSidePanel` and the `DesktopSidePanel*` types in Runic.Desktop;
  `IRunicWindowPresentation.OpenSidePanelAsync` and `SidePanel`,
  `RunicWindowPresentationFeatures.SidePanel`, `IRunicSidePanel` and the
  `RunicSidePanel*` types in Runic.Application.Views; the side panel members of
  `RunicTestWindowPresentation` in Runic.Application.Testing. See the
  [side panel guide](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/side-panel.md).
- **Close drain:** `RunicCloseDrain`, `RunicWindowOptions.CloseDrain`,
  `RunicOperationClose`, `IRunicWindowContext.DrainOnClose`,
  `RunicWindowCloseResult.RemainingDurableWork` and
  `ReportCloseDrainUnavailable`; `RunicDesktopHostOptions.CloseDrain`;
  `RunicWindowTestHostOptions.CloseDrain` and `RunicTestWindow.IsDraining`;
  `DesktopWindowOptions.CloseDrainAsync`, `DesktopCloseDrainContext`,
  `DesktopCloseReason`, `DesktopHostOptions.ShutdownDrainTimeout` and
  `DesktopHost.ShutdownStarting`. On npm, `onBridgeDraining` is a root export
  of `@runic-artifex/views`; `./experimental` keeps a deprecated re-export
  until 0.23. Generated Bridges no longer suppress `RUNICDESK105`.

What to remove:

- `RUNICDESK104` and `RUNICDESK105` from `<NoWarn>` in project files and
  `Directory.Build.props`, and their `#pragma warning disable`/`restore` lines.
  A leftover suppression is harmless.
- Import `onBridgeDraining` from `@runic-artifex/views`. The
  `@runic-artifex/views/experimental` import still works in 0.22, marked
  `@deprecated`, and 0.23 removes it (one-minor transition, D069.3).

See [graduated and retired IDs](https://github.com/Runic-Artifex/runic-sdk/blob/v0.22.0/docs/experimental.md#retired).
