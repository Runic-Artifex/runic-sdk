- **Hosted applications, side panel and close drain: API review changes**
  (`RUNICDESK103`, `RUNICDESK104`, `RUNICDESK105`). The host protocol's wire
  tokens, such as the dialog kinds `folder` and `folder-multiple` and the
  `region-detach` reasons, stay unchanged.

  | ID | 0.20 | 0.21 |
  | --- | --- | --- |
  | `RUNICDESK103` | `RunicHostedDialogKind.OpenFolder`, `OpenFolders`; `RunicTestDialogKind.OpenFolder`, `OpenFolders` | `OpenDirectory`, `OpenDirectories`, as `IFileDialogs` names them |
  | `RUNICDESK103` | `RunicHostedWindow.Presentation` | `RunicHostedWindow.DesktopWindow` |
  | `RUNICDESK103` | `RunicHostedRegion.DetachAsync`, `MoveToAsync` return `Task`; `SetAppearanceAsync` returns `Task<bool>` | `ValueTask` and `ValueTask<bool>`, as `RequestCloseAsync`. A caller that stores the task calls `.AsTask()` |
  | `RUNICDESK103` | `RunicHostedRegionEventArgs.Reason` is a `string` (`"removed"`, `"reload"`, `"crashed"`, `"host"`, `"closed"`) | An open enum `RunicHostedRegionDetachReason` (`Removed`, `Reload`, `Crashed`, `Host`, `Closed`) |
  | `RUNICDESK103` | `RunicTestDialogRequest.Kind` (the wire kind, a `string`) beside `DialogKind` | `Kind` is the `RunicTestDialogKind`; `DialogKind` is removed |
  | `RUNICDESK103` | `RunicTestPresentationRequest.Kind` is the wire kind, a `string` (`"set-full-screen"`) | An open enum `RunicTestPresentationKind` (`SetFullScreen`) |
  | `RUNICDESK104` | No `DesktopSidePanelRefusal.Closed`; a window that closed while `OpenSidePanelAsync` ran threw `ObjectDisposedException` | `DesktopSidePanelRefusal.Closed`, as `RunicSidePanelRefusal.Closed`: `OpenSidePanelAsync`, `MoveToEdgeAsync` and `SetWidthAsync` refuse with it when the window or the panel closes while they run. A window or panel closed before the call still throws `ObjectDisposedException` |
  | `RUNICDESK105` | `RunicCloseDrain.KeepsWindow` | `RunicCloseDrain.KeepWindow` |
