using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// Global shortcuts through RegisterHotKey on a message-only window on its own thread, as the status icon keeps its
// window: WM_HOTKEY arrives in that thread's queue whichever application has the focus. The window's thread registers
// and unregisters every key, since a hot key belongs to the thread that registered it.
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsGlobalShortcuts : IDesktopGlobalShortcuts, IDesktopServiceStatus
{
    private const string ClassName = "Runic.GlobalShortcuts";
    private const uint WorkMessage = PInvoke.WM_APP + 1;
    // RegisterHotKey's error when another hot key already uses the combination.
    internal const int ErrorHotKeyAlreadyRegistered = 1409;
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;
    private static readonly ConcurrentDictionary<nint, WindowsGlobalShortcuts> s_windows = new();
    private static readonly PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>> Gone =
        new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<Action> _work = new();
    // Activations reach the application one at a time, in order, and never after disposal.
    private readonly Channel<string> _activations = Channel.CreateUnbounded<string>(new() { SingleReader = true });
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _pump;
    private volatile bool _disposed;
    private nint _window;
    // The registered hot key IDs and their actions; owned by the window thread.
    private readonly Dictionary<int, string> _registered = [];
    private ImmutableArray<DesktopGlobalShortcutBinding> _bindings = [];

    public CapabilityStatus Status { get; } = new CapabilityStatus.Available();

    public event EventHandler<DesktopGlobalShortcutActivation>? Activated;

    // Windows bindings change only through BindAsync.
    public event EventHandler<DesktopGlobalShortcutsChange>? BindingsChanged { add { } remove { } }

    // The window's handle, for the native tests.
    internal nint Window => Volatile.Read(ref _window);

    public async ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> BindAsync(
        ImmutableArray<DesktopGlobalShortcut> shortcuts, CancellationToken cancellationToken = default)
    {
        DesktopGlobalShortcutRules.Validate(shortcuts);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Window == 0 && (shortcuts.IsEmpty || !await StartAsync().ConfigureAwait(false)))
            {
                _bindings = [];
                return shortcuts.IsEmpty ? new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success([]) : Gone;
            }
            var result = await InvokeAsync<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>>(
                () => new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(Bind(shortcuts)), Gone).ConfigureAwait(false);
            if (result is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(var bindings)) _bindings = bindings;
            return result;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success(_bindings);
        }
        finally { _gate.Release(); }
    }

    public ValueTask<PlatformResult<PlatformUnit>> ConfigureAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(
            new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _activations.Writer.TryComplete();
            if (Window == 0) return;
            await InvokeAsync(() => { UnregisterAll(); PInvoke.DestroyWindow((HWND)Window); return 0; }, 0).ConfigureAwait(false);
            await _stopped.Task.ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // On the window thread: the previous set goes, then each key of the new one is registered on its own.
    private ImmutableArray<DesktopGlobalShortcutBinding> Bind(ImmutableArray<DesktopGlobalShortcut> shortcuts)
    {
        UnregisterAll();
        var layout = PInvoke.GetKeyboardLayout(0);
        var bindings = ImmutableArray.CreateBuilder<DesktopGlobalShortcutBinding>(shortcuts.Length);
        for (var index = 0; index < shortcuts.Length; index++)
        {
            var action = shortcuts[index];
            var requested = DesktopGlobalShortcutRules.Requested(action);
            var status = DesktopGlobalShortcutStatus.Unbound;
            if (requested is { } shortcut)
            {
                status = DesktopGlobalShortcutStatus.Refused;
                if (WindowsHotKeys.Map(shortcut, character => PInvoke.VkKeyScanEx(character, layout)) is { } hotKey)
                {
                    // IDs 1 to 32, within the range 0x0000-0xBFFF that applications use.
                    var id = index + 1;
                    if (PInvoke.RegisterHotKey((HWND)Window, id, (HOT_KEY_MODIFIERS)(hotKey.Modifiers | DesktopShortcutKeys.ModNoRepeat), hotKey.VirtualKey))
                    {
                        _registered[id] = action.Id;
                        status = DesktopGlobalShortcutStatus.Bound;
                    }
                    else if (Marshal.GetLastPInvokeError() == ErrorHotKeyAlreadyRegistered)
                    {
                        status = DesktopGlobalShortcutStatus.Conflict;
                    }
                }
            }
            bindings.Add(DesktopGlobalShortcutRules.Binding(action.Id, requested, status, macOS: false));
        }
        return bindings.MoveToImmutable();
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) PInvoke.UnregisterHotKey((HWND)Window, id);
        _registered.Clear();
    }

    private Task<bool> StartAsync()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pump ??= Task.Run(PumpAsync, CancellationToken.None);
        new Thread(() => Run(ready)) { IsBackground = true, Name = "Runic global shortcuts" }.Start();
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
                // HWND_MESSAGE: a message-only window, which no broadcast reaches and nothing shows.
                window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, (HWND)(nint)(-3), default,
                    (HINSTANCE)instance.Value, null);
            if (window == default) return;
            s_windows[window] = this;
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
            _registered.Clear();
            while (_work.TryDequeue(out var work)) work();
            _stopped.TrySetResult();
        }
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
                if (PInvoke.RegisterClassEx(&windowClass) == 0) throw new InvalidOperationException("The global shortcut window class could not be registered.");
            }
            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (s_windows.TryGetValue(window, out var owner) && owner.Handle(message, wParam)) return (LRESULT)0;
        }
        catch (Exception) { /* No exception may unwind into the window manager. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    private bool Handle(uint message, WPARAM wParam)
    {
        switch (message)
        {
            case WorkMessage:
                while (_work.TryDequeue(out var work)) work();
                return true;
            case PInvoke.WM_HOTKEY:
                if (_registered.TryGetValue((int)wParam.Value, out var id)) _activations.Writer.TryWrite(id);
                return true;
            case PInvoke.WM_DESTROY:
                UnregisterAll();
                Volatile.Write(ref _window, 0);
                PInvoke.PostQuitMessage(0);
                return true;
            default:
                return false;
        }
    }

    // Runs on the window thread; answers gone when the window has ended before the work ran.
    private Task<T> InvokeAsync<T>(Func<T> action, T gone)
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

    private async Task PumpAsync()
    {
        await foreach (var id in _activations.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_disposed) return;
            try { Activated?.Invoke(this, new DesktopGlobalShortcutActivation(id)); }
            catch (Exception error)
            {
                // As with an event raised on the thread pool, a throwing handler fails the process; later activations still arrive until then.
                var capture = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                ThreadPool.QueueUserWorkItem(static state => ((System.Runtime.ExceptionServices.ExceptionDispatchInfo)state!).Throw(), capture);
            }
        }
    }
}

// The pure part of RegisterHotKey: a shortcut as a virtual key and modifiers for a keyboard layout, tested on every platform.
internal static class WindowsHotKeys
{
    // How Windows registers a shortcut: the virtual key and the RegisterHotKey modifiers, or why it cannot.
    internal readonly record struct HotKey(uint VirtualKey, uint Modifiers);

    // Maps a shortcut for the keyboard layout: letters, digits and named keys have fixed virtual keys; a punctuation
    // character is the key that types it, with Shift when the layout needs it. A character that needs AltGr, or that the
    // layout lacks, is refused, as are F12, which Windows keeps for debuggers, and the editing and window shortcuts.
    internal static HotKey? Map(DesktopShortcut shortcut, Func<char, short> keyScan)
    {
        if (shortcut.Key == "F12" || DesktopShortcutKeys.ReservedBy(shortcut, macOS: false) is not null) return null;
        var modifiers = DesktopShortcutKeys.HotKeyModifiers(shortcut.Modifiers);
        if (DesktopShortcutKeys.Character(shortcut.Key) is { } character)
        {
            var scan = keyScan(character);
            if (scan == -1) return null;
            var shiftState = (scan >> 8) & 0xFF;
            // Ctrl, Alt or the Hankaku key: the character needs more than Shift.
            if ((shiftState & ~1) != 0) return null;
            if ((shiftState & 1) != 0) modifiers |= DesktopShortcutKeys.ModShift;
            return new HotKey((uint)(scan & 0xFF), modifiers);
        }
        return DesktopShortcutKeys.VirtualKey(shortcut.Key) is { } virtualKey ? new HotKey(virtualKey, modifiers) : null;
    }
}
