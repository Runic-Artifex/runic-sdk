- **.NET API names from the 0.21 API review.** Members added since 0.12 are
  renamed so that one feature uses one word. Rename the calls; nothing else
  changes, and the wire formats stay: `NavigationPath.ToJson` and
  `NavigationSelectionPath.ToJson` still write a selection's index as
  `"selected"` (format version 1), so saved paths load unchanged.

  | Package | 0.20 | 0.21 |
  | --- | --- | --- |
  | `Runic.Desktop` | `IDesktopWindowBoundsHost` | `IDesktopBoundsWindowHost` |
  | `Runic.Desktop` | `IDesktopWindowShowStateHost` | `IDesktopShowStateWindowHost` |
  | `Runic.Desktop` | `DesktopWindowCapabilities.WindowPlacement` | `DesktopWindowCapabilities.Placement` (same value) |
  | `Runic.Desktop` | `DesktopHost.NoteRecentDocumentAsync(path)` | `DesktopHost.AddRecentDocumentAsync(path)` |
  | `Runic.Navigation` | `NavigationSelectionPath.Selected` | `NavigationSelectionPath.SelectedIndex` |
  | `Runic.Application.Testing` | `RunicRegionCollectionState.Selected` | `RunicRegionCollectionState.SelectedIndex` |
  | `Runic.Application.Views` | `RunicWindowOptions.MaximumWindowUploadBytes`, `WindowContentSessionOptions.MaximumWindowUploadBytes` | `MaximumPendingUploadBytes` |
  | `Runic.Application.Views` | `RunicWindowsChangedEventArgs.Opened` (`bool`) and its `bool opened` constructor parameter | `RunicWindowsChangedEventArgs.Change`, an open enum `RunicWindowsChange` (`Opened`, `Closed`): `args.Opened` becomes `args.Change == RunicWindowsChange.Opened` |
  | `Runic.Application.Views` | `RunicUpload.SaveAsAsync(path, overwrite)` | `RunicUpload.SaveToFileAsync(path, overwrite)` |
  | `Runic.Application.Testing` | `RunicServeOnlyTestPlatform.EnqueueOpenFile`, `EnqueueSaveFile`, `EnqueueOpenDirectory`, `EnqueueFileDialogDismissed`, `EnqueueFileDialogUnavailable`, `EnqueueFileDialogFailed` | `platform.FileDialogs.EnqueueOpen`, `EnqueueSave`, `EnqueueOpenDirectory`, `EnqueueDismissed`, `EnqueueUnavailable`, `EnqueueFailed` |
  | `Runic.Application.Testing` | `RunicServeOnlyTestPlatform.EnqueueMessageBoxAnswer` | `platform.MessageBoxes.EnqueueAnswer` |
  | `Runic.Application.Testing` | `RunicServeOnlyTestPlatform.EnqueueContextMenuChoice`, `EnqueueContextMenuDismissed` | `platform.ContextMenus.EnqueueChoice`, `EnqueueDismissed` |
  | `Runic.Application.Testing` | `RunicServeOnlyTestPlatform.SetClipboard`, `SetClipboardText` | `platform.Clipboard.SetContent`, `SetText` |

  The serve-only fakes now have the names of `RunicTestPlatform`'s, so a test
  moves between the in-process host and serve-only unchanged. The host-wide
  default of the pending-upload limit is new: `MaximumPendingUploadBytes` on
  `RunicDesktopHostOptions`, `RunicCsWebUiHostOptions` and
  `RunicWindowTestHostOptions`, beside `MaximumUploadBytes`. Only a custom
  Runic Desktop window host implements the two renamed interfaces.
