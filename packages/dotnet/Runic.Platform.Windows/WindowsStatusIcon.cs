using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using static Runic.Platform.Windows.WindowsShellInterop;
using Model = Runic.Platform.Windows.WindowsStatusIconModel;

namespace Runic.Platform.Windows;

// A notification-area icon (Shell_NotifyIconW, NOTIFYICON_VERSION_4) owned by a hidden window on its own thread.
// The window is a hidden top-level window rather than a message-only one: message-only windows do not receive the
// TaskbarCreated broadcast that Explorer sends after it restarts, and the shell needs a window that can take the
// foreground before it shows the menu. Every Shell_NotifyIcon and menu call runs on that thread.
[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
internal sealed class WindowsStatusIcon : IDesktopStatusIcon
{
    internal const uint IconId = 1, CallbackMessage = PInvoke.WM_APP + 1;
    // NIN_KEYSELECT repeated this soon is the same key press: Enter is reported to arrive twice with NOTIFYICON_VERSION_4.
    internal const long KeySelectRepeatMilliseconds = 250;
    private const uint WorkMessage = PInvoke.WM_APP + 2;
    private const string ClassName = "Runic.StatusIcon";
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;
    // Explorer broadcasts it to top-level windows after it (re)starts the taskbar.
    internal static readonly uint s_taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
    private static readonly ConcurrentDictionary<nint, Session> s_windows = new();
    private static readonly PlatformResult<PlatformUnit> Gone = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Command IDs stay with their commands across menu changes; used only under _gate.
    private readonly Model.Ids _ids = new();
    // Activations reach the application one at a time, in order, and never after disposal.
    private readonly Channel<DesktopStatusIconActivation> _activations =
        Channel.CreateUnbounded<DesktopStatusIconActivation>(new() { SingleReader = true });
    private Task? _pump;
    private volatile bool _disposed;
    // The window and its thread; replaced under _gate when the window has ended.
    private volatile Session? _session;

    public event EventHandler<DesktopStatusIconActivation>? Activated;

    // The window's handle, for the native tests.
    internal nint Window => _session?.Window ?? 0;

    public async ValueTask<PlatformResult<PlatformUnit>> ShowAsync(DesktopStatusIconState state, CancellationToken cancellationToken = default)
    {
        DesktopServiceValidation.StatusIcon(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A window that ended, for example with the session, is started again.
            var session = _session is { IsRunning: true } running ? running : await StartAsync().ConfigureAwait(false);
            if (session is null) return Gone;
            var menu = Model.BuildMenu(state.Menu, _ids);
            cancellationToken.ThrowIfCancellationRequested();
            return await session.InvokeAsync(() => session.Apply(state, menu), Gone).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<PlatformUnit>> HideAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } session) await session.InvokeAsync(() => { session.Remove(); return 0; }, 0).ConfigureAwait(false);
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _activations.Writer.TryComplete();
            if (_session is not { } session) return;
            await session.InvokeAsync(() => { session.Remove(); session.Close(); return 0; }, 0).ConfigureAwait(false);
            // Completes when this window's thread ends, which it does once the window is destroyed.
            await session.Stopped.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<Session?> StartAsync()
    {
        var session = new Session(this);
        _pump ??= Task.Run(PumpAsync, CancellationToken.None);
        if (!await session.Start().ConfigureAwait(false)) return null;
        _session = session;
        return session;
    }

    private static unsafe void RegisterClass(HMODULE instance)
    {
        lock (s_classLock)
        {
            if (s_classRegistered) return;
            fixed (char* name = ClassName)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WindowProcedure,
                    hInstance = (HINSTANCE)instance.Value,
                    lpszClassName = name,
                };
                if (PInvoke.RegisterClassEx(&windowClass) == 0) throw new InvalidOperationException("The status icon window class could not be registered.");
            }
            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue(window, out var session) && session.Handle(message, wParam, lParam)) return (LRESULT)0;
        }
        catch (Exception) { /* No exception may unwind into the window manager. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    // One window, its thread and what it shows. A window that ended is not used again.
    private sealed class Session(WindowsStatusIcon owner)
    {
        private readonly ConcurrentQueue<Action> _work = new();
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint _window;
        // Owned by the window thread.
        private DesktopStatusIconState? _state;
        private Model.Menu _menu = Model.Menu.Empty;
        private bool _added, _tracking, _closing;
        private long _lastKeySelect = long.MinValue / 2;
        private (HICON Handle, bool Owned) _icon;

        internal nint Window => Volatile.Read(ref _window);
        internal bool IsRunning => Window != 0;
        internal Task Stopped => _stopped.Task;

        internal Task<bool> Start()
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            new Thread(() => Run(ready)) { IsBackground = true, Name = "Runic status icon" }.Start();
            return ready.Task;
        }

        private unsafe void Run(TaskCompletionSource<bool> ready)
        {
            HWND window = default;
            try
            {
                var instance = PInvoke.GetModuleHandle((PCWSTR)null);
                RegisterClass(instance);
                fixed (char* className = ClassName)
                fixed (char* title = "Runic status icon")
                    window = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, title, WINDOW_STYLE.WS_POPUP,
                        0, 0, 0, 0, default, default, (HINSTANCE)instance.Value, null);
                if (window == default) return;
                s_windows[window] = this;
                // An elevated process still hears Explorer, which runs at medium integrity.
                _ = PInvoke.ChangeWindowMessageFilterEx(window, s_taskbarCreated, WINDOW_MESSAGE_FILTER_ACTION.MSGFLT_ALLOW);
                Volatile.Write(ref _window, window);
                ready.TrySetResult(true);
                MSG message;
                while (PInvoke.GetMessage(&message, default, 0, 0).Value > 0)
                {
                    PInvoke.TranslateMessage(&message);
                    PInvoke.DispatchMessage(&message);
                }
            }
            catch (Exception) { /* Reported below: the window never started, or its thread ended. */ }
            finally
            {
                ready.TrySetResult(false);
                Volatile.Write(ref _window, 0);
                if (window != default)
                {
                    if (PInvoke.IsWindow(window)) PInvoke.DestroyWindow(window);
                    s_windows.TryRemove(window, out _);
                }
                // Work still queued answers that the icon is gone.
                while (_work.TryDequeue(out var work)) work();
                _stopped.TrySetResult();
            }
        }

        internal bool Handle(uint message, WPARAM wParam, LPARAM lParam)
        {
            if (message == WorkMessage)
            {
                while (_work.TryDequeue(out var work)) work();
                return true;
            }
            if (message == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: the event in the low word of lParam, the anchor point in wParam.
                uint kind = (uint)(lParam.Value & 0xFFFF);
                if (kind == NIN_KEYSELECT)
                {
                    long now = Environment.TickCount64;
                    bool repeated = now - _lastKeySelect < KeySelectRepeatMilliseconds;
                    _lastKeySelect = now;
                    if (!repeated) owner.Raise(DesktopStatusIconActivationKind.Primary);
                }
                else if (kind == NIN_SELECT) owner.Raise(DesktopStatusIconActivationKind.Primary);
                else if (kind == PInvoke.WM_MBUTTONUP) owner.Raise(DesktopStatusIconActivationKind.Secondary);
                else if (kind == PInvoke.WM_CONTEXTMENU) ShowMenu((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF));
                return true;
            }
            if (message == s_taskbarCreated && s_taskbarCreated != 0)
            {
                // Explorer restarted and forgot every icon.
                _added = false;
                if (_state is { } state) Apply(state, _menu);
                return true;
            }
            switch (message)
            {
                // The icon belongs to the application: only HideAsync and DisposeAsync remove it. WM_CLOSE from another
                // process (taskkill without /F, Restart Manager) is for the application's own windows.
                case PInvoke.WM_CLOSE:
                    return true;
                // The session ends: remove the icon now. A later ShowAsync starts a new window.
                case PInvoke.WM_ENDSESSION when wParam.Value != 0:
                    Remove();
                    Close();
                    return true;
                // However the window goes, the icon goes with it and the thread ends.
                case PInvoke.WM_DESTROY:
                    Remove();
                    Volatile.Write(ref _window, 0);
                    PInvoke.PostQuitMessage(0);
                    return true;
                default:
                    return false;
            }
        }

        // Runs on the window thread; answers gone when the window has ended before the work ran.
        internal Task<T> InvokeAsync<T>(Func<T> action, T gone)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            int claimed = 0;
            _work.Enqueue(() =>
            {
                if (Interlocked.Exchange(ref claimed, 1) != 0) return;
                try { completion.TrySetResult(Window == 0 ? gone : action()); }
                catch (Exception error) { completion.TrySetException(error); }
            });
            if ((Window == 0 || !PInvoke.PostMessage((HWND)Window, WorkMessage, default, default)) && Interlocked.Exchange(ref claimed, 1) == 0)
                completion.TrySetResult(gone);
            return completion.Task;
        }

        internal unsafe PlatformResult<PlatformUnit> Apply(DesktopStatusIconState state, Model.Menu menu)
        {
            if (Window == 0) return Gone;
            _state = state;
            _menu = menu;
            var icon = CreateIcon(Model.ShownIcon(state));
            var data = Data(Window);
            data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
            data.uCallbackMessage = CallbackMessage;
            data.hIcon = icon.Handle;
            string tip = Model.Tooltip(state.Title, state.Tooltip);
            for (int i = 0; i < tip.Length; i++) data.szTip[i] = tip[i];
            // NIM_ADD can fail on a busy Explorer that added the icon anyway; NIM_MODIFY tells. A known icon that
            // Explorer lost is added again.
            uint first = _added ? NIM_MODIFY : NIM_ADD, second = _added ? NIM_ADD : NIM_MODIFY;
            uint succeeded = Shell_NotifyIcon(first, &data) != 0 ? first : Shell_NotifyIcon(second, &data) != 0 ? second : uint.MaxValue;
            bool added = succeeded != uint.MaxValue;
            // A newly added icon needs its version again.
            if (added && (succeeded == NIM_ADD || !_added))
            {
                var version = Data(Window);
                version.uVersion = NOTIFYICON_VERSION_4;
                _ = Shell_NotifyIcon(NIM_SETVERSION, &version);
            }
            _added = added;
            // The shell keeps its own copy; the previous icon is released once the new one is shown.
            Release(_icon);
            _icon = icon;
            // Without a taskbar the icon stays ready and appears on TaskbarCreated.
            return added ? new PlatformResult<PlatformUnit>.Success(new PlatformUnit()) : Gone;
        }

        internal unsafe void Remove()
        {
            if (_added)
            {
                var data = Data(Window);
                _ = Shell_NotifyIcon(NIM_DELETE, &data);
            }
            _added = false;
            _state = null;
            _menu = Model.Menu.Empty;
            Release(_icon);
            _icon = default;
        }

        // Destroys the window, which ends the thread (WM_DESTROY). A menu still open is closed first; the window
        // goes once TrackPopupMenuEx returns.
        internal void Close()
        {
            if (_tracking) { _closing = true; PInvoke.EndMenu(); return; }
            var window = (HWND)Window;
            if (window != default) PInvoke.DestroyWindow(window);
        }

        private static unsafe NOTIFYICONDATAW Data(nint window) =>
            new() { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = window, uID = IconId };

        private unsafe void ShowMenu(int x, int y)
        {
            var menu = _menu;
            if (_state is null || menu.IsEmpty || _tracking) return;
            bool rightToLeft = _state.LayoutDirection == DesktopLayoutDirection.RightToLeft;
            var handle = CreateMenu(menu.Entries, rightToLeft);
            _tracking = true;
            try
            {
                var window = (HWND)Window;
                // Without the foreground the menu does not close when the user clicks elsewhere.
                PInvoke.SetForegroundWindow(window);
                uint flags = TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN
                    | (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN)
                    // Mirrors the menu: submenus open to the left and check marks sit on the right.
                    | (rightToLeft ? TPM_LAYOUTRTL : 0);
                int chosen = PInvoke.TrackPopupMenuEx(handle, flags, x, y, window, null).Value;
                // Lets the menu's foreground switch finish before the next one (KB135788).
                PInvoke.PostMessage(window, PInvoke.WM_NULL, default, default);
                // Resolved against the menu that was shown, even if ShowAsync replaced it meanwhile.
                if (chosen > 0 && menu.Choose((uint)chosen) is { } id) owner.Raise(DesktopStatusIconActivationKind.MenuItem, id);
            }
            finally
            {
                _tracking = false;
                PInvoke.DestroyMenu(handle);
                if (_closing) Close();
            }
        }
    }

    // DestroyMenu on the top menu destroys its submenus.
    internal static unsafe HMENU CreateMenu(ImmutableArray<Model.Entry> entries, bool rightToLeft = false)
    {
        var menu = PInvoke.CreatePopupMenu();
        if (menu == default) throw new InvalidOperationException("The status icon menu could not be created.");
        try
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case Model.Command command:
                        var flags = MENU_ITEM_FLAGS.MF_STRING;
                        if (!command.Enabled) flags |= MENU_ITEM_FLAGS.MF_GRAYED;
                        if (command.Checked == true) flags |= MENU_ITEM_FLAGS.MF_CHECKED;
                        Append(menu, flags, command.Id, command.Label);
                        break;
                    case Model.Submenu submenu:
                        var child = CreateMenu(submenu.Entries, rightToLeft);
                        var submenuFlags = MENU_ITEM_FLAGS.MF_POPUP | MENU_ITEM_FLAGS.MF_STRING;
                        if (!submenu.Enabled) submenuFlags |= MENU_ITEM_FLAGS.MF_GRAYED;
                        if (!Append(menu, submenuFlags, (nuint)(nint)child.Value, submenu.Label)) PInvoke.DestroyMenu(child);
                        break;
                    default:
                        Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
                        break;
                }
            }
            if (rightToLeft) MarkRightToLeft((nint)menu.Value);
            return menu;
        }
        catch { PInvoke.DestroyMenu(menu); throw; }
    }

    private static unsafe bool Append(HMENU menu, MENU_ITEM_FLAGS flags, nuint id, string? label)
    {
        fixed (char* text = label) return PInvoke.AppendMenu(menu, flags, id, text);
    }

    // The application's icon stands in when the state has no image, since theme names mean nothing on Windows.
    private static unsafe (HICON Handle, bool Owned) CreateIcon(DesktopIcon icon)
    {
        int size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        if (Model.SelectImage(icon, size) is { } image && FromImage(image) is { } created && created != default) return (created, true);
        var instance = PInvoke.GetModuleHandle((PCWSTR)null);
        var application = PInvoke.LoadImage((HINSTANCE)instance.Value, (char*)(nuint)PInvoke.OIC_SAMPLE, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, 0);
        if (application != default) return ((HICON)application.Value, true);
        return ((HICON)PInvoke.LoadImage(default, (char*)(nuint)PInvoke.OIC_SAMPLE, GDI_IMAGE_TYPE.IMAGE_ICON, 0, 0,
            IMAGE_FLAGS.LR_SHARED | IMAGE_FLAGS.LR_DEFAULTSIZE).Value, false);
    }

    // A 32-bit top-down DIB with straight alpha, and an all-zero AND mask so the alpha channel decides.
    internal static unsafe HICON FromImage(DesktopIconImage image)
    {
        var info = new BITMAPINFO();
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = image.Width;
        info.bmiHeader.biHeight = -image.Height;
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        void* bits = null;
        var color = PInvoke.CreateDIBSection(default, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
        if (color == default) return default;
        HBITMAP mask = default;
        try
        {
            Model.ToBgra(image.Rgba, new Span<byte>(bits, image.Rgba.Length));
            // Monochrome rows are padded to 16 bits.
            var zero = new byte[(image.Width + 15) / 16 * 2 * image.Height];
            fixed (byte* maskBits = zero) mask = PInvoke.CreateBitmap(image.Width, image.Height, 1, 1, maskBits);
            if (mask == default) return default;
            var icon = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            return PInvoke.CreateIconIndirect(&icon);
        }
        finally
        {
            PInvoke.DeleteObject((HGDIOBJ)color.Value);
            if (mask != default) PInvoke.DeleteObject((HGDIOBJ)mask.Value);
        }
    }

    private static void Release((HICON Handle, bool Owned) icon)
    {
        if (icon.Owned && icon.Handle != default) PInvoke.DestroyIcon(icon.Handle);
    }

    private void Raise(DesktopStatusIconActivationKind kind, string? commandId = null) =>
        _activations.Writer.TryWrite(new DesktopStatusIconActivation(kind, commandId));

    private async Task PumpAsync()
    {
        await foreach (var activation in _activations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) return;
            try { Activated?.Invoke(this, activation); }
            catch (Exception error)
            {
                // As with an event raised on the thread pool, a throwing handler fails the process; later activations still arrive until then.
                var capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
            }
        }
    }
}
