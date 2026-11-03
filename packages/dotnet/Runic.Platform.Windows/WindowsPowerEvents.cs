using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// B4: power and session events. Each watch runs a hidden top-level window on a thread of its own, because
// WM_POWERBROADCAST is broadcast to top-level windows only; RegisterSuspendResumeNotification also delivers it on
// modern standby. WTSRegisterSessionNotification sends WM_WTSSESSION_CHANGE for lock and unlock, and the power saving
// status setting announces Battery Saver. GetSystemPowerStatus is the battery and saver state, read again on each
// change; WTSQuerySessionInformation(WTSSessionInfoEx) the lock state.
[SupportedOSPlatform("windows8.0")]
internal sealed partial class WindowsPowerEvents : IPowerEventSource
{
    private const string ClassName = "Runic.PowerEvents";
    private const uint PbtApmSuspend = 0x4, PbtApmResumeSuspend = 0x7, PbtApmPowerStatusChange = 0xA, PbtApmResumeAutomatic = 0x12,
        PbtPowerSettingChange = 0x8013;
    private const uint WtsSessionLock = 0x7, WtsSessionUnlock = 0x8, NotifyForThisSession = 0;
    private const uint DeviceNotifyWindowHandle = 0, WtsCurrentSession = uint.MaxValue;
    // GUID_POWER_SAVING_STATUS: Battery Saver (Energy Saver on Windows 11) on or off.
    private static readonly Guid PowerSavingStatus = new("e00958c0-c213-4ace-ac77-fecced2eeea5");
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;
    private static readonly ConcurrentDictionary<nint, Watch> s_windows = new();
    private static int s_teardowns, s_lateTeardowns;

    // Watches that released their registrations, and those that did so after their window was gone; for the native test.
    internal static (int Teardowns, int Late) Teardowns => (Volatile.Read(ref s_teardowns), Volatile.Read(ref s_lateTeardowns));

    public ValueTask<PlatformResult<DesktopPowerState>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<DesktopPowerState>>(new PlatformResult<DesktopPowerState>.Success(ReadState()));
    }

    public async ValueTask<IAsyncDisposable> SubscribeAsync(Action<PlatformResult<DesktopPowerEvent>> report, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var watch = new Watch(report);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() => watch.Run(ready)) { IsBackground = true, Name = "Runic power events" }.Start();
        if (!await ready.Task.ConfigureAwait(false)) throw new NativeBackendUnavailableException("The power events window could not start.");
        return watch;
    }

    internal static DesktopPowerState ReadState() => State(PowerStatus(), IsSessionLocked());

    // SYSTEM_POWER_STATUS: ACLineStatus 0 offline, 1 online, 255 unknown; BatteryFlag 128 no system battery;
    // SystemStatusFlag 1 while Battery Saver is on.
    internal static DesktopPowerState State((byte ACLine, byte BatteryFlag, byte SaverFlag)? status, bool? locked) => new()
    {
        IsOnBattery = status switch
        {
            { BatteryFlag: 128 } => false,
            { ACLine: 0 } => true,
            { ACLine: 1 } => false,
            _ => null,
        },
        IsLowPower = status is { } known ? known.SaverFlag == 1 : null,
        IsLocked = locked,
    };

    private static unsafe (byte, byte, byte)? PowerStatus()
    {
        global::Windows.Win32.System.Power.SYSTEM_POWER_STATUS status;
        return PInvoke.GetSystemPowerStatus(&status) ? (status.ACLineStatus, status.BatteryFlag, status.SystemStatusFlag) : null;
    }

    // WTSINFOEXW: Level (DWORD), then WTSINFOEX_LEVEL1_W: SessionId, SessionState, SessionFlags (WTS_SESSIONSTATE_LOCK 0,
    // WTS_SESSIONSTATE_UNLOCK 1). Windows 7 reported the two the other way round; Runic needs Windows 10.
    private static unsafe bool? IsSessionLocked()
    {
        global::Windows.Win32.System.RemoteDesktop.WTS_INFO_CLASS sessionInfoEx = (global::Windows.Win32.System.RemoteDesktop.WTS_INFO_CLASS)25;
        if (!PInvoke.WTSQuerySessionInformation(HANDLE.Null, WtsCurrentSession, sessionInfoEx, out PWSTR buffer, out uint bytes)) return null;
        try
        {
            if (bytes < 16 || *(uint*)buffer.Value != 1) return null;
            return *(int*)((byte*)buffer.Value + 12) switch { 0 => true, 1 => false, _ => null };
        }
        finally
        {
            PInvoke.WTSFreeMemory(buffer.Value);
        }
    }

    private sealed class Watch(Action<PlatformResult<DesktopPowerEvent>> report) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly PowerStateTracker _tracker = new(report);
        private nint _window;
        // The window thread's registrations, released in WM_DESTROY while the window is still valid.
        private bool _session;
        private nint _suspendResume, _saver;

        internal unsafe void Run(TaskCompletionSource<bool> ready)
        {
            HWND window = default;
            try
            {
                var instance = PInvoke.GetModuleHandle((PCWSTR)null);
                RegisterClass(instance);
                // A top-level window that is never shown: broadcasts reach it, unlike a message-only window.
                fixed (char* className = ClassName)
                    window = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, null, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0,
                        default, default, (HINSTANCE)instance.Value, null);
                if (window == default) return;
                s_windows[window] = this;
                _session = PInvoke.WTSRegisterSessionNotification(window, NotifyForThisSession);
                _suspendResume = RegisterSuspendResumeNotification(window, DeviceNotifyWindowHandle);
                var setting = PowerSavingStatus;
                _saver = RegisterPowerSettingNotification(window, &setting, DeviceNotifyWindowHandle);
                _tracker.Start(ReadState());
                Volatile.Write(ref _window, window);
                ready.TrySetResult(true);
                MSG message;
                while (PInvoke.GetMessage(&message, default, 0, 0).Value > 0)
                {
                    PInvoke.TranslateMessage(&message);
                    PInvoke.DispatchMessage(&message);
                }
            }
            catch (Exception) { /* Reported below: the watch never started, or its thread ended. */ }
            finally
            {
                ready.TrySetResult(false);
                Volatile.Write(ref _window, 0);
                if (window != default)
                {
                    // WM_DESTROY unregistered already unless the thread failed before the window closed.
                    if (PInvoke.IsWindow(window)) PInvoke.DestroyWindow(window);
                    Unregister(window);
                    s_windows.TryRemove(window, out _);
                }
                _stopped.TrySetResult();
            }
        }

        // The window's thread.
        internal void Power(uint kind)
        {
            switch (kind)
            {
                case PbtApmSuspend:
                    _tracker.Suspending();
                    break;
                case PbtApmResumeAutomatic or PbtApmResumeSuspend:
                    _tracker.Resumed();
                    _tracker.Update(ReadState() with { IsLocked = _tracker.State.IsLocked });
                    break;
                case PbtApmPowerStatusChange or PbtPowerSettingChange:
                    _tracker.Update(ReadState() with { IsLocked = _tracker.State.IsLocked });
                    break;
            }
        }

        // WM_DESTROY, while the window is still valid; again from the thread's end, which then does nothing.
        internal void Unregister(HWND window)
        {
            if (_session || _suspendResume != 0 || _saver != 0)
            {
                Interlocked.Increment(ref s_teardowns);
                if (!PInvoke.IsWindow(window)) Interlocked.Increment(ref s_lateTeardowns);
            }
            if (_session) PInvoke.WTSUnRegisterSessionNotification(window);
            _session = false;
            if (_suspendResume != 0) _ = UnregisterSuspendResumeNotification(_suspendResume);
            _suspendResume = 0;
            if (_saver != 0) _ = UnregisterPowerSettingNotification(_saver);
            _saver = 0;
        }

        internal void Session(uint change)
        {
            if (change == WtsSessionLock) _tracker.Locked(true);
            else if (change == WtsSessionUnlock) _tracker.Locked(false);
        }

        public async ValueTask DisposeAsync()
        {
            var window = Volatile.Read(ref _window);
            if (window != 0) PInvoke.PostMessage((HWND)window, PInvoke.WM_CLOSE, default, default);
            await _stopped.Task.ConfigureAwait(false);
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
                if (PInvoke.RegisterClassEx(&windowClass) == 0) throw new InvalidOperationException("The power events window class could not be registered.");
            }
            s_classRegistered = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            switch (message)
            {
                case PInvoke.WM_POWERBROADCAST:
                    if (s_windows.TryGetValue(window, out var power)) power.Power((uint)wParam.Value);
                    return (LRESULT)1;
                case PInvoke.WM_WTSSESSION_CHANGE:
                    if (s_windows.TryGetValue(window, out var session)) session.Session((uint)wParam.Value);
                    return (LRESULT)0;
                case PInvoke.WM_CLOSE:
                    PInvoke.DestroyWindow(window);
                    return (LRESULT)0;
                case PInvoke.WM_DESTROY:
                    if (s_windows.TryGetValue(window, out var closing)) closing.Unregister(window);
                    PInvoke.PostQuitMessage(0);
                    return (LRESULT)0;
            }
        }
        catch (Exception) { /* No exception may unwind into the window manager. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    // Declared here with plain handles: CsWin32 wraps their results in SafeHandles that would unregister on finalization.
    [LibraryImport("user32.dll")]
    private static partial nint RegisterSuspendResumeNotification(nint recipient, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int UnregisterSuspendResumeNotification(nint handle);

    [LibraryImport("user32.dll")]
    private static unsafe partial nint RegisterPowerSettingNotification(nint recipient, Guid* setting, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int UnregisterPowerSettingNotification(nint handle);
}
