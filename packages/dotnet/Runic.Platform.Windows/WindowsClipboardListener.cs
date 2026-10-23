using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Runic.Platform.Windows;

// A clipboard watch: a message-only window on a thread of its own, registered with AddClipboardFormatListener, that
// signals each WM_CLIPBOARDUPDATE. The watch reads only the formats afterwards, so no content leaves the clipboard.
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsClipboardListener : IAsyncDisposable
{
    private const string ClassName = "Runic.ClipboardListener";
    private static readonly Lock s_classLock = new();
    private static bool s_classRegistered;
    private static readonly ConcurrentDictionary<nint, WindowsClipboardListener> s_windows = new();

    private readonly Action _changed;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _window;

    private WindowsClipboardListener(Action changed) => _changed = changed;

    // The running listener, or null when its window could not start.
    internal static async ValueTask<IAsyncDisposable?> StartAsync(Action changed)
    {
        var listener = new WindowsClipboardListener(changed);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(() => listener.Run(ready)) { IsBackground = true, Name = "Runic clipboard listener" }.Start();
        return await ready.Task.ConfigureAwait(false) ? listener : null;
    }

    private unsafe void Run(TaskCompletionSource<bool> ready)
    {
        HWND window = default;
        bool listening = false;
        try
        {
            var instance = PInvoke.GetModuleHandle((PCWSTR)null);
            RegisterClass(instance);
            fixed (char* className = ClassName)
                window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, default, (HINSTANCE)instance.Value, null);
            if (window == default) return;
            s_windows[window] = this;
            listening = PInvoke.AddClipboardFormatListener(window);
            if (!listening) return;
            Volatile.Write(ref _window, window);
            ready.TrySetResult(true);
            MSG message;
            while (PInvoke.GetMessage(&message, default, 0, 0).Value > 0)
            {
                PInvoke.TranslateMessage(&message);
                PInvoke.DispatchMessage(&message);
            }
        }
        catch (Exception) { /* Reported below: the listener never started, or its thread ended. */ }
        finally
        {
            ready.TrySetResult(false);
            Volatile.Write(ref _window, 0);
            if (window != default)
            {
                if (listening) PInvoke.RemoveClipboardFormatListener(window);
                if (PInvoke.IsWindow(window)) PInvoke.DestroyWindow(window);
                s_windows.TryRemove(window, out _);
            }
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
                if (PInvoke.RegisterClassEx(&windowClass) == 0) throw new InvalidOperationException("The clipboard listener window class could not be registered.");
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
                case PInvoke.WM_CLIPBOARDUPDATE:
                    if (s_windows.TryGetValue(window, out var listener)) listener._changed();
                    return (LRESULT)0;
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

    public async ValueTask DisposeAsync()
    {
        var window = Volatile.Read(ref _window);
        if (window != 0) PInvoke.PostMessage((HWND)window, PInvoke.WM_CLOSE, default, default);
        await _stopped.Task.ConfigureAwait(false);
    }
}
