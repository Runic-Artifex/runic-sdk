# Runic.Platform.Windows interop inventory

This inventory lists every native declaration in `Runic.Platform.Windows`. Each one
is either migrated to a private CsWin32 binding or retained with a stated reason.
CsWin32 is pinned centrally (`Microsoft.Windows.CsWin32` 0.3.346, which locks
`Microsoft.Windows.SDK.Win32Metadata` 71.0.14-preview). The package uses the same
`NativeMethods.json` settings as `Runic.Platform.Administration.Windows`:
`allowMarshaling: false`, `public: false` and `preserveSigMethods: ["*"]`. Generated
types are internal and never appear in the public API (`PublicAPI.*.txt` unchanged).

## Conventions

- Call the raw pointer and handle overloads. Do not call the generated SafeHandle
  "friendly" overloads unless the generated release function matches the API's
  ownership contract (see the cases below).
- Generated imports carry `[SupportedOSPlatform]` minimums. Native entry points run
  only under `WindowsSupport.IsAvailable` (Windows 8 guard). .NET 10 runs only on
  Windows 10 or later, so on supported hosts the guard is equivalent to
  `OperatingSystem.IsWindows()` and the provider factories behave as before.
- Generated imports set the last P/Invoke error when the metadata declares
  `SetLastError`. `Marshal.GetLastPInvokeError()` therefore reads the same value it
  read from the former `LibraryImport(SetLastError = true)` declarations.

## Migrated declarations

| Area | Former handwritten declaration | Generated binding | Notes |
| --- | --- | --- | --- |
| Clipboard | `OpenClipboard`, `CloseClipboard`, `EmptyClipboard` (user32) | `PInvoke.OpenClipboard(HWND)`, `CloseClipboard()`, `EmptyClipboard()` | Exposed internally as `Win32Clipboard.Open/Empty/Close` for the native test harness. |
| Clipboard | `IsClipboardFormatAvailable`, `GetClipboardData`, `SetClipboardData` | Same names; `CLIPBOARD_FORMAT.CF_UNICODETEXT` replaces the literal `13` | Raw `HANDLE` overloads only. The generated `GetClipboardData_SafeHandle` / SafeHandle `SetClipboardData` overloads wrap a `SafeFileHandle` and would call `CloseHandle` on clipboard-owned memory. The `HGLOBAL` is dropped only after `SetClipboardData` succeeds, so ownership transfer is unchanged. |
| Clipboard | `GlobalAlloc`, `GlobalSize`, `GlobalLock`, `GlobalUnlock`, `GlobalFree` (kernel32) | Same names with `HGLOBAL`; `GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE \| GMEM_ZEROINIT` replaces `0x42` | Raw overloads. `GlobalFreeSafeHandle` is not used because a successful `SetClipboardData` must leave the memory unfreed. Bounded decoding (`DecodeText`) is unchanged. |
| Settings | `SystemParametersInfoW` x2 (`GetHighContrast`, `GetAnimation` aliases) | `PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION, uint, void*, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS)` | One import replaces both aliases. `SPI_GETHIGHCONTRAST` / `SPI_GETCLIENTAREAANIMATION` replace `0x42` / `0x1042`. |
| Settings | `HighContrast` struct | `HIGHCONTRASTW`, `HIGHCONTRASTW_FLAGS.HCF_HIGHCONTRASTON` | Same layout (`uint, uint, PWSTR`); `cbSize = sizeof(HIGHCONTRASTW)`. |
| Launcher | `ShellExecuteW` (UTF-16 string marshalling) | `PInvoke.ShellExecute(HWND, PCWSTR, PCWSTR, PCWSTR, PCWSTR, SHOW_WINDOW_CMD)` | Strings pinned with `fixed`; no marshalling stub. The generated `FreeLibrarySafeHandle` overload is avoided because the returned `HINSTANCE` is a legacy status code, not a module to free. The `> 32` success test and diagnostics are unchanged. |
| Launcher | `SHOpenWithDialog` + `OpenWithInfo` struct | `PInvoke.SHOpenWithDialog(HWND, OPENASINFO*)`, `OPEN_AS_INFO_FLAGS.OAIF_EXEC` | Same layout (`PCWSTR, PCWSTR, uint`). |
| Launcher | `SHParseDisplayName`, `SHOpenFolderAndSelectItems` | Same names with `ITEMIDLIST*` | `SHParseDisplayName` transfers the absolute PIDL to the caller. It is still released once with `CoTaskMemFree` (`Marshal.FreeCoTaskMem`) in `finally` and is not released when parsing fails. |
| Launcher | `CoInitializeEx`, `CoUninitialize` (ole32) | Same names; `COINIT.COINIT_APARTMENTTHREADED` | Apartment and balance rules are unchanged (no uninitialize after a failed initialize). |
| Inhibition | `PowerCreateRequest` + `ReasonContext` struct | `PInvoke.PowerCreateRequest(in REASON_CONTEXT)` returning `SafeFileHandle` | The friendly overload is kept here because its `SafeFileHandle` closes the request with `CloseHandle`, which is what the documentation requires. The generated `REASON_CONTEXT` union reserves the full detailed-reason size (24 bytes x86, 32 bytes x64/ARM64), matching the former struct. |
| Inhibition | `PowerSetRequest` | `PInvoke.PowerSetRequest(SafeHandle, POWER_REQUEST_TYPE)` | `PowerRequestSystemRequired` / `PowerRequestDisplayRequired` replace `1` / `0`. |
| File dialogs | None; added for filters, multiple selection and the initial folder | `IFileOpenDialog`, `IShellItemArray`, `COMDLG_FILTERSPEC`, `PInvoke.SHCreateItemFromParsingName(PCWSTR, IBindCtx*, Guid*, void**)` | Raw COM vtables like `IFileDialog`. `SetFileTypes` copies the filter strings, which are pinned only for the call. `IFileOpenDialog` comes from `QueryInterface` on the open dialog and is released with the result array and each `IShellItem`. A folder that cannot be parsed is not set. |
| WinRT support | `RoInitialize`, `RoUninitialize`, `RoGetActivationFactory`, `RoActivateInstance` (combase) | Same names from `api-ms-win-core-winrt-l1-1-0.dll`; `RO_INIT_TYPE.RO_INIT_MULTITHREADED` | **Decision: migrate.** These are flat C exports with blittable signatures. The API set forwards to `combase.dll` on every supported Windows. `S_FALSE` still succeeds and `RPC_E_CHANGED_MODE` still throws. |
| WinRT support | `WindowsCreateString`, `WindowsDeleteString`, `WindowsGetStringRawBuffer` (combase) | Same names from `api-ms-win-core-winrt-string-l1-1-0.dll` with `HSTRING` | **Decision: migrate.** `HString` still owns exactly one handle. The generated `WindowsDeleteStringSafeHandle` overloads are not used, because the handle is passed by value through fixed WinRT ABI slots. |
| Status icon | (new) window class, hidden window and message loop | `RegisterClassEx`, `CreateWindowEx`, `DefWindowProc`, `GetMessage`, `TranslateMessage`, `DispatchMessage`, `PostMessage`, `PostQuitMessage`, `DestroyWindow`, `RegisterWindowMessage`, `ChangeWindowMessageFilterEx`, `GetModuleHandle` | Raw overloads. The window procedure is an `[UnmanagedCallersOnly]` static method assigned to `WNDCLASSEXW.lpfnWndProc`, so no delegate is marshalled. |
| Status icon | (new) menu | `CreatePopupMenu`, `AppendMenu`, `TrackPopupMenuEx`, `DestroyMenu`, `EndMenu`, `SetForegroundWindow`, `GetSystemMetrics` | Raw `HMENU` overloads; `DestroyMenu` on the top menu releases its submenus. |
| Status icon | (new) icons | `CreateDIBSection`, `CreateBitmap`, `CreateIconIndirect`, `DeleteObject`, `DestroyIcon`, `LoadImage` | Raw overloads. The generated `DeleteObjectSafeHandle`/`DestroyIconSafeHandle` overloads are not used: the bitmaps are deleted right after `CreateIconIndirect`, and the icon lives until the shell shows its successor. A shared system icon (`LR_SHARED`) is never destroyed. |
| Secret storage | (new) | `CredRead`, `CredWrite`, `CredDelete`, `CredFree` (advapi32) with `CREDENTIALW`, `CRED_TYPE`, `CRED_PERSIST` | Raw pointer overloads with strings and the blob pinned by `fixed`. A credential `CredRead` returns is released once with `CredFree` in `finally`, after its blob is decoded. |
| Trash | (new) | `IFileOperation`, `IShellItem`, `SHCreateItemFromParsingName`, `CoCreateInstance`, `CoInitializeEx`, `CoUninitialize`, `FILEOPERATION_FLAGS` | Raw COM vtables like `IFileDialog`, on a dedicated STA thread per request. The operation and the item are released once in `finally`. `IFileOperationProgressSink` is generated only as the type `Advise` takes; the sink is handwritten (below). |
| Thumbnails | (new) | `IShellItemImageFactory`, `SIIGBF`, `SHCreateItemFromParsingName`, `IWICImagingFactory`, `IWICBitmap`, `IWICFormatConverter`, `IWICBitmapEncoder`, `IWICBitmapFrameEncode`, `SHCreateMemStream`, `IStream`, `GetObject` with `BITMAP`, `DeleteObject`, `CoCreateInstance`, `CoInitializeEx`, `CoUninitialize` | Raw COM vtables on a dedicated STA thread per request. `GetImage` transfers the `HBITMAP`, deleted once with `DeleteObject` in `finally`; every interface pointer is released once in `finally`. The generated `GetObject` SafeHandle and span `SHCreateMemStream` overloads are not used. |
| Desktop applications | (new) | `AssocQueryString` (`ASSOCF`, `ASSOCSTR`), `SHAssocEnumHandlers` (`ASSOC_FILTER`), `SHAssocEnumHandlersForProtocolByApplication`, `IEnumAssocHandlers`, `IAssocHandler`, `SHParseDisplayName`, `SHCreateShellItemArrayFromIDLists`, `IDataObject`, `BHID_DataObject`, `SHGetKnownFolderItem`, `FOLDERID_AppsFolder`, `BHID_EnumItems`, `IEnumShellItems`, `IShellItem2`, `PKEY_Link_TargetParsingPath`, `IShellItemImageFactory`, `SIIGBF`, `SHSimpleIDListFromPath`, `SHCreateItemFromIDList`, `SHDefExtractIcon`, `GetIconInfo`, `GetDIBits`, `BITMAPINFO`, `CreateCompatibleDC`, `DeleteDC`, `FindMimeFromData` (urlmon), `ShellExecute` | Raw COM vtables on a dedicated STA thread per request, like the Recycle Bin. `AssocQueryString` is called without a buffer for the length, then with `ASSOCF_NOTRUNCATE`. Strings the shell allocates (`GetName`, `GetUIName`, `GetDisplayName`, `GetString`, `FindMimeFromData`) and the ID lists of `SHParseDisplayName` and `SHSimpleIDListFromPath` are copied and freed once with `CoTaskMemFree`. Every enumerator, handler, item, item array and data object is released in `finally`; the handler kept for `Invoke` is `AddRef`'d while enumerating and released after the launch. `SHDefExtractIcon`'s `HICON` is destroyed, and the bitmaps from `GetIconInfo` and `IShellItemImageFactory::GetImage` are deleted. `GetDIBits` writes into a stack buffer with room for a 256-entry colour table. `ShellExecute` uses the raw overload, as the launcher does. PNG encoding is managed (`WindowsIconImage`), so no WIC interfaces are declared. |
| Package identity | (new) | `GetCurrentApplicationUserModelId` (kernel32) with `WIN32_ERROR` | The span overload: a first call with an empty span reads the length (`ERROR_INSUFFICIENT_BUFFER`; `APPMODEL_ERROR_NO_APPLICATION` for an unpackaged process), a second fills a stack buffer. Read once per process (`WindowsPackageIdentity`). |
| Volumes | (new) listing | `FindFirstVolume`, `FindNextVolume`, `FindVolumeClose`, `GetVolumePathNamesForVolumeName`, `GetVolumeInformation`, `GetDiskFreeSpaceEx`, `GetDriveType`, `SetThreadErrorMode` (kernel32) | The span and string overloads; the listing sets `SEM_FAILCRITICALERRORS` for its thread and restores the previous mode, so a drive without media fails the query instead of showing a dialog; `FindFirstVolume` returns the generated `FindVolumeCloseSafeHandle`, which closes the search with `FindVolumeClose`. A volume whose `GetVolumeInformation` fails (no media, unformatted, locked) is left out. |
| Volumes | (new) device queries | `CreateFile`, `DeviceIoControl` with `IOCTL_STORAGE_GET_DEVICE_NUMBER`, `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, `IOCTL_DISK_GET_PARTITION_INFO_EX`; `STORAGE_DEVICE_NUMBER`, `VOLUME_DISK_EXTENTS`, `PARTITION_INFORMATION_EX` | `CreateFile` returns a `SafeFileHandle` (closed with `CloseHandle`), opened with no access rights: the three IOCTLs are `FILE_ANY_ACCESS`, and such a handle neither mounts the volume nor stands in the way of a lock or removal. The raw `DeviceIoControl` overload writes into stack structures. |
| Volumes | (new) device nodes | `CM_Get_Device_Interface_List(_Size)`, `CM_Get_Device_Interface_Property`, `CM_Locate_DevNode`, `CM_Get_Parent`, `CM_Get_DevNode_Registry_Property` (`CM_DRP_CAPABILITIES`), `CM_Get_DevNode_Property` (cfgmgr32) with `GUID_DEVINTERFACE_DISK`/`CDROM`/`FLOPPY`, `DEVPKEY_Device_InstanceId`, `DEVPKEY_Device_FriendlyName` | Device nodes are numbers, not handles, and need no release. |
| Volumes | (new) eject and notification | `CM_Request_Device_Eject`, `CM_Register_Notification`, `CM_Unregister_Notification` with `CM_NOTIFY_FILTER` (`GUID_DEVINTERFACE_VOLUME`) | The eject passes a veto buffer, so Windows reports a veto instead of showing its notification. The notification callback is an `[UnmanagedCallersOnly]` static method whose context is a `GCHandle` to the watch's wake-up; `CM_Unregister_Notification` waits for a running callback before the handle is freed. |
| Presentation | (new) | `GetAncestor`, `IsIconic`, `IsWindowVisible`, `ShowWindow`, `SetForegroundWindow`, `GetForegroundWindow`, `FlashWindowEx`, `IsWindow` | Raw overloads, called on the window's thread through `INativePickerOwner.InvokeAsync`. |

## Retained handwritten interop

| Area | Declaration | Limitation |
| --- | --- | --- |
| Notifications, settings | Raw vtable slot calls (`Slot`, `Query`, `Get`, `Call`, `Release` and `delegate* unmanaged[Stdcall]` calls on `XmlDocument`, `ToastNotification`, `ToastNotifier`, `ToastNotificationHistory`, `UISettings`) | These are Windows Runtime projection interfaces, not Win32 metadata, so CsWin32 cannot generate them. Projecting them needs CsWinRT, which W120-020 explicitly excludes. Adopting CsWinRT needs its own decision backed by NativeAOT, activation and callback-lifetime evidence. |
| Notifications | `ToastActivationHandler` (static unmanaged vtable, four `[UnmanagedCallersOnly]` methods, parameterized IID `82fc5297-…`) | It implements the WinRT parameterized delegate `TypedEventHandler<ToastNotification,IInspectable>`. That delegate is absent from Win32 metadata, so there is no generated interface or IID. The static vtable keeps the callback NativeAOT-safe and contains application exceptions. |
| Trash | `WindowsTrash.RecycleSink` (static unmanaged vtable, 19 `[UnmanagedCallersOnly]` slots, IID `04b0f1a7-…`) | It implements `IFileOperationProgressSink` rather than calling it; with `allowMarshaling: false` CsWin32 generates no implementable interface. The static vtable keeps the sink NativeAOT-safe; only `PreDeleteItem` (refusing a permanent deletion with `E_ABORT`) and `PostDeleteItem` act. |
| Settings | `Color` struct (`Windows.UI.Color`) | This is a WinRT projection type that is absent from Win32 metadata. |
| Status icon | `Shell_NotifyIconW` (`LibraryImport`) and `NOTIFYICONDATAW` (`WindowsShellInterop`) | The Win32 metadata declares `NOTIFYICONDATAW` per CPU architecture, because 32-bit packing differs, and CsWin32 refuses to generate it or `Shell_NotifyIcon` for this AnyCPU assembly (PInvoke005). The struct is declared with the natural x64/ARM64 layout (976 bytes, checked by a portable test) and passed by pointer, so the import needs no marshalling. |
| Launcher | `Marshal.FreeCoTaskMem` for the PIDL | This is a BCL call, not a declaration. It is the documented `CoTaskMemFree` release, so no extra generated import is needed. |

## Outside the package

`tests/dotnet/Runic.Platform.Windows.Tests` keeps two handwritten test-only imports,
`CreateWindowExW` and `DestroyWindow`. They create the HWND that owns the native
clipboard. `StatusIconTests` adds `FindWindowW`, `GetMenuItemCount`, `GetMenuState` and
`GetSubMenu` to find the open context menu and inspect the native menu. The test project does
not reference CsWin32, and these imports do not ship.

`Runic.Platform.Runtime` declares one Windows import of its own, `AllowSetForegroundWindow`
(user32, `LibraryImport`), in `ForegroundRights`. A launch forwarded by
`ApplicationInstance.ClaimAsync` calls it for the running instance, whose process ID it
reads from the Unix domain socket with `SIO_AF_UNIX_GETPEERPID` through `Socket.IOControl`.

## Verification

- `dotnet build -p:RunicApplicationBuildMode=Verification` makes warnings errors,
  including CA1416 platform compatibility, trimming and AOT analyzers.
- Portable: `dotnet run --project tests/dotnet/Runic.Platform.Windows.Tests` and
  `tests/dotnet/Runic.Platform.Runtime.Tests` (launcher program-extension refusal).
- Windows CI (`Native / win-x64`) runs the explicit TUnit cases for the native
  clipboard (`--treenode-filter "/*/*/*/*[Category=Native]"`), system-sleep
  inhibition (`--treenode-filter "/*/*/*/*[Category=NativeInhibition]"
  --test-parameter system-only=true`) and settings reads
  (`--treenode-filter "/*/*/*/*[Category=NativeSettings]"`), the status icon
  (`NativeStatusIcon`) and window presentation (`NativePresentation`) with JIT and the
  NativeAOT publish (`-p:IlcTreatWarningsAsErrors=true`).
- Interactive: owned file launch (`Open`, `ChooseApplication`, `Reveal`), clipboard
  transfer, settings reads and notification activation on an interactive Windows
  desktop (see [Interactive evidence](#interactive-evidence)).

## Interactive evidence

On 2026-10-07 the Windows 11 VM (build 26200, .NET SDK 10.0.401) ran the
NativeAOT publishes of `Runic.Platform.Windows.Tests` and
`Runic.Platform.Runtime.Tests`, built from `b1c5d5af`. Neither publish produced a
warning. Each check ran in the signed-in user's session.

`Runic.Platform.Windows.Tests.exe` passed `--native-settings`, `--native`
(clipboard) and `--native-inhibition`, each exiting with 0. These were the
command-line modes of that revision; the TUnit suite now selects the same checks
with the `NativeSettings`, `Native` and `NativeInhibition` categories.

`Runic.Platform.Runtime.Tests.exe` passed its conformance run without arguments
(14/14). It also ran `--native-services` (now the `NativeServices` category), with
`RUNIC_TEST_SERVICE` selecting each operation:

- `Open` exited with 0, and Notepad opened the owned file.
- `Reveal` exited with 0, and Explorer opened with the file selected.
- `ChooseApplication` printed `PASS` with no error output. Its exit code was
  not captured: the PowerShell driver used `Start-Process -PassThru
  -NoNewWindow`, which returned no `ExitCode`. Notepad was selected in the
  picker and confirmed with "Just once", and the owned file then opened in a
  new Notepad process. The picker offers no "Always" button for this call.
  Notepad was already the default application for `.txt`, so this result
  cannot tell a choice made in the picker from a plain `Open`. It shows that
  the picker appeared and that "Just once" launched the file.
- `Notifications` exited with 0, using a per-user test AppUserModelID from
  `tests/native/windows-notifications/Configure-TestIdentity.ps1`. The toast
  appeared, and clicking its "Open result" action raised the activation event
  (`NotificationId = native-services, ActionId = open`). This run covers
  `RoGetActivationFactory` and `WindowsGetStringRawBuffer`.

UI Automation could not reach the Windows 11 Open With picker or the toast
from the test session; neither appeared in the UI Automation tree. Both were
operated with absolute pointer input, and UI Automation then confirmed the
resulting Notepad window.
