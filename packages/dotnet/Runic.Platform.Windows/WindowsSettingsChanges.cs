using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// The change notifications of WindowsDesktopSettings' watch, instead of polling. A hidden top-level window on a thread
// of its own receives the broadcasts that announce each setting the source reads (a message-only window receives no
// broadcasts):
// - WM_SETTINGCHANGE: "ImmersiveColorSet" for the app mode and accent colour, SPI_SETHIGHCONTRAST for a contrast theme,
//   SPI_SETCLIENTAREAANIMATION for animation effects;
// - WM_SYSCOLORCHANGE and WM_THEMECHANGED: a contrast theme's colours;
// - WM_DWMCOLORIZATIONCOLORCHANGED: the accent colour.
// Accessibility "Text size" broadcasts nothing; RegNotifyChangeKeyValue on HKCU\Software\Microsoft\Accessibility,
// where it is stored, reports it (on Software\Microsoft until that key exists). Each notification is only a wake-up:
// the watch reads every setting again and reports a change only when one differs.
[SupportedOSPlatform("windows8.0")]
internal sealed partial class WindowsSettingsChanges : IDisposable
{
    private const string ClassName = "Runic.SettingsChanges";
    private const string AccessibilityKey = @"Software\Microsoft\Accessibility", ParentKey = @"Software\Microsoft";
    private const uint WmSysColorChange = 0x0015, WmSettingChange = 0x001A, WmThemeChanged = 0x031A, WmDwmColorizationColorChanged = 0x0320;
    private const uint RegNotifyChangeName = 0x1, RegNotifyChangeLastSet = 0x4, QsAllInput = 0x04FF, Infinite = 0xFFFFFFFF;
    private const uint PmRemove = 0x1, WmQuit = 0x0012, WaitFailed = 0xFFFFFFFF;
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;
    private static readonly ConcurrentDictionary<nint, WindowsSettingsChanges> s_windows = new();
    private readonly Action _changed, _failed;
    private nint _window;
    private bool _disposed;

    private WindowsSettingsChanges(Action changed, Action failed) => (_changed, _failed) = (changed, failed);

    // Starts the window's thread and returns once it listens, or null when it could not start. failed reports that the
    // thread ended before it was disposed, so the watch polls instead.
    internal static WindowsSettingsChanges? Start(Action changed, Action failed)
    {
        var changes = new WindowsSettingsChanges(changed, failed);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() => changes.Run(ready)) { IsBackground = true, Name = "Runic settings changes" }.Start();
        if (ready.Task.Wait(TimeSpan.FromSeconds(5)) && ready.Task.Result) return changes;
        changes.Dispose();
        return null;
    }

    private unsafe void Run(TaskCompletionSource<bool> ready)
    {
        HWND window = default;
        RegistryKey? key = null;
        using var registryChanged = new AutoResetEvent(false);
        try
        {
            var instance = PInvoke.GetModuleHandle((PCWSTR)null);
            RegisterClass(instance);
            fixed (char* className = ClassName)
                window = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, null, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0,
                    default, default, (HINSTANCE)instance.Value, null);
            if (window == default) return;
            s_windows[window] = this;
            key = WatchRegistry(registryChanged);
            Interlocked.Exchange(ref _window, window);
            if (Volatile.Read(ref _disposed)) return;
            ready.TrySetResult(true);
            var handle = registryChanged.SafeWaitHandle.DangerousGetHandle();
            while (true)
            {
                // The registry event, or a message in the queue.
                uint woken = MsgWaitForMultipleObjects(key is null ? 0u : 1u, &handle, 0, Infinite, QsAllInput);
                if (woken == WaitFailed) return;
                if (woken == 0 && key is not null)
                {
                    key.Dispose();
                    key = WatchRegistry(registryChanged);
                    Changed();
                    continue;
                }
                MSG message;
                while (PeekMessage(&message, 0, 0, 0, PmRemove) != 0)
                {
                    if (message.message == WmQuit) return;
                    PInvoke.TranslateMessage(&message);
                    PInvoke.DispatchMessage(&message);
                }
            }
        }
        catch (Exception) { /* Reported below: the window never started, or its thread ended. */ }
        finally
        {
            var started = !ready.TrySetResult(false);
            Interlocked.Exchange(ref _window, 0);
            key?.Dispose();
            if (window != default)
            {
                if (PInvoke.IsWindow(window)) PInvoke.DestroyWindow(window);
                s_windows.TryRemove(window, out _);
            }
            if (started && !Volatile.Read(ref _disposed))
            {
                try { _failed(); }
                catch (Exception) { /* The thread ends either way. */ }
            }
        }
    }

    // Arms one notification for the Accessibility key, or for its parent until it exists. Null when neither opens; the
    // window messages still arrive.
    private static RegistryKey? WatchRegistry(AutoResetEvent changed)
    {
        foreach (var (path, filter) in new[] { (AccessibilityKey, RegNotifyChangeLastSet), (ParentKey, RegNotifyChangeName) })
        {
            RegistryKey? key = null;
            try
            {
                key = Registry.CurrentUser.OpenSubKey(path);
                if (key is null) continue;
                if (RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), 0, filter, changed.SafeWaitHandle.DangerousGetHandle(), 1) == 0)
                    return key;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            key?.Dispose();
        }
        return null;
    }

    private void Changed()
    {
        try { _changed(); }
        catch (Exception) { /* A wake-up never fails the window's thread. */ }
    }

    public void Dispose()
    {
        Volatile.Write(ref _disposed, true);
        var window = Interlocked.Exchange(ref _window, 0);
        if (window != 0) PInvoke.PostMessage((HWND)window, PInvoke.WM_CLOSE, default, default);
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
                if (PInvoke.RegisterClassEx(&windowClass) == 0) throw new InvalidOperationException("The settings window class could not be registered.");
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
                case WmSettingChange or WmSysColorChange or WmThemeChanged or WmDwmColorizationColorChanged:
                    if (s_windows.TryGetValue(window, out var changes)) changes.Changed();
                    break;
                case PInvoke.WM_CLOSE:
                    PInvoke.DestroyWindow(window);
                    return (LRESULT)0;
                case PInvoke.WM_DESTROY:
                    PInvoke.PostQuitMessage(0);
                    return (LRESULT)0;
            }
        }
        catch (Exception) { /* No exception may unwind into the window manager. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial uint MsgWaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds, uint wakeMask);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    private static unsafe partial int PeekMessage(void* message, nint window, uint first, uint last, uint remove);

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint filter, nint @event, int asynchronous);
}
