using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.ApplicationInstallationAndServicing;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.WindowsAndMessaging;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// Message boxes as task dialogs (TaskDialogIndirect) owned by the window, on its thread. The dialog runs its own modal
// loop inside the owner's dispatch, as the file dialog's Show does. Unowned boxes (the application's, on
// WindowsApplicationThread) have no parent window and the executable's name as their default caption.
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsMessageBoxes(INativeWindowOwner owner, WindowsCommonControls controls, bool unowned = false) : IDesktopMessageBoxes
{
    private const int FirstButtonId = 100;
    private const int IdOk = 1, IdCancel = 2;
    // TD_WARNING_ICON, TD_ERROR_ICON and TD_INFORMATION_ICON: MAKEINTRESOURCEW(-1), (-2) and (-3).
    private const ushort WarningIcon = 0xFFFF, ErrorIcon = 0xFFFE, InformationIcon = 0xFFFD;

    // Tests drive the dialog from another thread: it receives the dialog's window once the dialog exists.
    internal Action<nint>? Created { get; set; }

    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        DesktopMessageBoxRules.Check(messageBox);
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = DesktopMessageBoxRules.Ordered(messageBox.Buttons, MessageBoxPlatform.Windows);
        var generation = owner.Generation;
        var dialog = new DialogState(ordered.IsEmpty ? IdOk : Id(messageBox.Buttons, ordered[0]), Created);
        int chosen = 0;
        int failure = 0;
        using var registration = cancellationToken.Register(static state => ((DialogState)state!).Close(), dialog);
        try
        {
            await owner.InvokeAsync(handle =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == 0 && !unowned || !owner.IsAvailable || owner.Generation != generation) throw new OwnerClosedException();
                failure = Show(handle, messageBox, ordered, dialog, out chosen);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerClosedException) { return Closed; }
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable || owner.Generation != generation) return Closed;
        if (failure < 0)
            return new PlatformResult<DesktopMessageBoxButton?>.Failed(PlatformFailureCode.Other, new PlatformDiagnostic("HRESULT", failure));
        return new PlatformResult<DesktopMessageBoxButton?>.Success(Chosen(messageBox.Buttons, chosen));
    }

    private static readonly PlatformResult<DesktopMessageBoxButton?> Closed =
        new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);

    // The button's id in the dialog: IDCANCEL for the cancel button, so Esc and the close button choose it.
    internal static int Id(ImmutableArray<DesktopMessageBoxButton> buttons, DesktopMessageBoxButton button) =>
        button.Role == DesktopMessageBoxButtonRole.Cancel ? IdCancel : FirstButtonId + buttons.IndexOf(button);

    internal static DesktopMessageBoxButton? Chosen(ImmutableArray<DesktopMessageBoxButton> buttons, int id)
    {
        if (buttons.IsDefaultOrEmpty) return null;
        if (id == IdCancel) return buttons.FirstOrDefault(static button => button.Role == DesktopMessageBoxButtonRole.Cancel);
        int index = id - FirstButtonId;
        return index >= 0 && index < buttons.Length ? buttons[index] : null;
    }

    // Shows the dialog on the owner's thread and returns its HRESULT.
    private unsafe int Show(nint ownerWindow, DesktopMessageBox messageBox, ImmutableArray<DesktopMessageBoxButton> ordered, DialogState dialog, out int chosen)
    {
        var strings = new List<nint>();
        var buttons = ordered.IsEmpty ? null : (TASKDIALOG_BUTTON*)NativeMemory.AllocZeroed((nuint)(sizeof(TASKDIALOG_BUTTON) * ordered.Length));
        var state = GCHandle.Alloc(dialog);
        try
        {
            var root = PInvoke.GetAncestor((HWND)ownerWindow, GET_ANCESTOR_FLAGS.GA_ROOT);
            if (root.IsNull) root = (HWND)ownerWindow;
            TASKDIALOGCONFIG config = default;
            config.cbSize = (uint)sizeof(TASKDIALOGCONFIG);
            config.hwndParent = (HWND)ownerWindow;
            config.dwFlags = TASKDIALOG_FLAGS.TDF_POSITION_RELATIVE_TO_WINDOW;
            // A window that Runic Desktop opened right-to-left (WS_EX_LAYOUTRTL) gets a mirrored dialog.
            if ((PInvoke.GetWindowLong(root, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) & (int)WINDOW_EX_STYLE.WS_EX_LAYOUTRTL) != 0)
                config.dwFlags |= TASKDIALOG_FLAGS.TDF_RTL_LAYOUT;
            config.pszWindowTitle = Text(messageBox.Title ?? WindowTitle(root), strings);
            config.pszMainInstruction = Text(messageBox.Message, strings);
            config.pszContent = Text(messageBox.Detail, strings);
            config.pszMainIcon = new PCWSTR((char*)(nint)(messageBox.Kind switch
            {
                DesktopMessageBoxKind.Information => InformationIcon,
                DesktopMessageBoxKind.Warning => WarningIcon,
                DesktopMessageBoxKind.Error => ErrorIcon,
                _ => 0,
            }));
            if (ordered.IsEmpty)
            {
                // One localized OK button; Esc and the close button close the box too.
                config.dwCommonButtons = TASKDIALOG_COMMON_BUTTON_FLAGS.TDCBF_OK_BUTTON;
                config.dwFlags |= TASKDIALOG_FLAGS.TDF_ALLOW_DIALOG_CANCELLATION;
            }
            else
            {
                for (int index = 0; index < ordered.Length; index++)
                {
                    buttons[index].nButtonID = Id(messageBox.Buttons, ordered[index]);
                    buttons[index].pszButtonText = Text(DesktopMessageBoxRules.Label(ordered[index], '&'), strings);
                }
                config.pButtons = buttons;
                config.cButtons = (uint)ordered.Length;
                if (ordered.FirstOrDefault(static button => button.Role == DesktopMessageBoxButtonRole.Default) is { } preferred)
                    config.nDefaultButton = Id(messageBox.Buttons, preferred);
                // Without a cancel button Esc does nothing and the close button is disabled.
                if (ordered.Any(static button => button.Role == DesktopMessageBoxButtonRole.Cancel))
                    config.dwFlags |= TASKDIALOG_FLAGS.TDF_ALLOW_DIALOG_CANCELLATION;
            }
            config.pfCallback = &Callback;
            config.lpCallbackData = GCHandle.ToIntPtr(state);
            return controls.Show(&config, out chosen);
        }
        finally
        {
            dialog.Destroyed();
            state.Free();
            foreach (var text in strings) Marshal.FreeHGlobal(text);
            NativeMemory.Free(buttons);
        }
    }

    private static string? WindowTitle(HWND window)
    {
        int length = PInvoke.GetWindowTextLength(window);
        if (length <= 0) return null;
        Span<char> buffer = new char[length + 1];
        int read = PInvoke.GetWindowText(window, buffer);
        return read > 0 ? new string(buffer[..read]) : null;
    }

    private static unsafe PCWSTR Text(string? value, List<nint> strings)
    {
        if (value is null) return default;
        var text = Marshal.StringToHGlobalUni(value);
        strings.Add(text);
        return new PCWSTR((char*)text);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static HRESULT Callback(HWND window, uint notification, WPARAM wParam, LPARAM lParam, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is DialogState dialog)
            {
                if (notification == (uint)TASKDIALOG_NOTIFICATIONS.TDN_CREATED) dialog.Opened(window);
                else if (notification == (uint)TASKDIALOG_NOTIFICATIONS.TDN_DESTROYED) dialog.Destroyed();
            }
        }
        catch { /* Never unwind through the dialog. */ }
        return HRESULT.S_OK;
    }

    // The dialog's window while it exists, for cancellation from any thread.
    private sealed class DialogState(int closeId, Action<nint>? created)
    {
        private readonly Lock _gate = new();
        private HWND _window;
        private bool _closeRequested;

        internal void Opened(HWND window)
        {
            bool close;
            lock (_gate)
            {
                _window = window;
                close = _closeRequested;
            }
            if (close) PostClose(window);
            created?.Invoke(window);
        }

        internal void Destroyed()
        {
            lock (_gate) _window = default;
        }

        // Clicks a button of the dialog, which ends it; the caller's cancellation discards the result.
        internal void Close()
        {
            HWND window;
            lock (_gate)
            {
                _closeRequested = true;
                window = _window;
            }
            if (!window.IsNull) PostClose(window);
        }

        private void PostClose(HWND window) =>
            PInvoke.PostMessage(window, (uint)TASKDIALOG_MESSAGES.TDM_CLICK_BUTTON, (WPARAM)(nuint)closeId, default);
    }
}

// Common Controls version 6, which alone has TaskDialogIndirect. Runic's hosts declare no Common Controls dependency in
// their manifests, so the provider activates a context that does around each dialog: the manifest resource that
// shell32.dll embeds for its own dialogs (ID 124), else the shell's manifest file in the Windows directory.
[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class WindowsCommonControls
{
    private const uint ResourceNameValid = 0x8; // ACTCTX_FLAG_RESOURCE_NAME_VALID
    private const int Shell32ManifestId = 124;
    private static readonly Lazy<WindowsCommonControls?> s_shared = new(static () => Load(ActivationSource.Any));

    private readonly HANDLE _context;
    private readonly delegate* unmanaged[Stdcall]<TASKDIALOGCONFIG*, int*, int*, BOOL*, HRESULT> _taskDialog;

    private WindowsCommonControls(HANDLE context, nint taskDialog, ActivationSource source)
    {
        _context = context;
        _taskDialog = (delegate* unmanaged[Stdcall]<TASKDIALOGCONFIG*, int*, int*, BOOL*, HRESULT>)taskDialog;
        Source = source;
    }

    internal enum ActivationSource { Any, Shell32Resource, ShellManifestFile }

    // Where the context came from.
    internal ActivationSource Source { get; }

    // The process's context, created once; null when no context provides version 6.
    internal static WindowsCommonControls? Shared => s_shared.Value;

    // Creates a context from one source, or from the first that works for Any.
    internal static WindowsCommonControls? Load(ActivationSource source)
    {
        if (source == ActivationSource.Any)
            return Load(ActivationSource.Shell32Resource) ?? Load(ActivationSource.ShellManifestFile);
        HANDLE context = Create(source);
        if ((nint)context.Value == -1 || context.IsNull) return null;
        nuint cookie = 0;
        if (!PInvoke.ActivateActCtx(context, &cookie)) { PInvoke.ReleaseActCtx(context); return null; }
        nint taskDialog = 0;
        try
        {
            // Loaded inside the context, comctl32.dll is the side-by-side version 6, which stays loaded for the process.
            HMODULE module;
            fixed (char* name = "comctl32.dll") module = PInvoke.LoadLibrary(new PCWSTR(name));
            if (!module.IsNull) NativeLibrary.TryGetExport(module, "TaskDialogIndirect", out taskDialog);
        }
        finally { PInvoke.DeactivateActCtx(0, cookie); }
        if (taskDialog == 0) { PInvoke.ReleaseActCtx(context); return null; }
        return new WindowsCommonControls(context, taskDialog, source);
    }

    private static HANDLE Create(ActivationSource source)
    {
        ACTCTXW request = default;
        request.cbSize = (uint)sizeof(ACTCTXW);
        if (source == ActivationSource.Shell32Resource)
        {
            fixed (char* path = Path.Combine(Environment.SystemDirectory, "shell32.dll"))
            {
                request.lpSource = new PCWSTR(path);
                request.dwFlags = ResourceNameValid;
                request.lpResourceName = new PCWSTR((char*)(nint)Shell32ManifestId);
                return PInvoke.CreateActCtx(&request);
            }
        }
        string manifest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WindowsShell.Manifest");
        if (!File.Exists(manifest)) return default;
        fixed (char* path = manifest)
        {
            request.lpSource = new PCWSTR(path);
            return PInvoke.CreateActCtx(&request);
        }
    }

    // Runs TaskDialogIndirect inside the context on the calling thread.
    internal int Show(TASKDIALOGCONFIG* config, out int chosen)
    {
        nuint cookie = 0;
        if (!PInvoke.ActivateActCtx(_context, &cookie)) { chosen = 0; return Marshal.GetHRForLastWin32Error(); }
        try
        {
            int button = 0;
            var result = _taskDialog(config, &button, null, null);
            chosen = button;
            return result.Value;
        }
        finally { PInvoke.DeactivateActCtx(0, cookie); }
    }
}
