using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// The application's own STA thread for native UI without a window: task dialogs and shell handoffs (Shared), and each
// unowned file dialog's thread of its own (StartDialog), so that two windows' dialogs never nest in one modal loop. Each
// background thread runs a message loop with a message-only window. Work is queued to that window, so it also runs
// inside the modal loop of a dialog shown on the thread: cancelling an unowned file dialog closes it from there. Actions
// receive no window handle (0).
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsApplicationThread : INativeWindowOwner
{
    private const string ClassName = "RunicApplicationThread";
    private const uint WorkMessage = PInvoke.WM_APP + 0x52, StopMessage = PInvoke.WM_APP + 0x53;
    private static readonly Lazy<WindowsApplicationThread> Instance = new(static () => new WindowsApplicationThread("Runic application UI"));
    // The window class every thread's message-only window uses, registered once per process.
    private static readonly Lazy<bool> ClassRegistered = new(RegisterClass);
    // The instance whose message loop runs on this thread, for the window procedure.
    [ThreadStatic] private static WindowsApplicationThread? t_current;

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _work = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _window;
    private int _threadId;
    // Tests follow a dialog thread's end: its message loop, window and COM.
    private readonly Action<string>? _trace;

    private WindowsApplicationThread(string name, Action<string>? trace = null)
    {
        _trace = trace;
        var thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // The process's thread, started on first use.
    internal static WindowsApplicationThread Shared => Instance.Value;

    // A thread of its own for one dialog; Stop ends it once the dialog is done.
    internal static WindowsApplicationThread StartDialog(Action<string>? trace = null) => new("Runic unowned dialog", trace);

    // Ends the thread's message loop after the work queued before it; the thread then releases its window and COM.
    internal void Stop()
    {
        if (ReferenceEquals(this, Instance.IsValueCreated ? Instance.Value : null)) throw new InvalidOperationException("The application's thread lives as long as the process.");
        _ = _ready.Task.ContinueWith(static (ready, state) =>
        {
            if (ready.Result) PInvoke.PostMessage((HWND)Volatile.Read(ref ((WindowsApplicationThread)state!)._window), StopMessage, default, default);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Guid Generation { get; } = Guid.NewGuid();

    // The shared thread never closes: it lives as long as the process; a dialog's thread lives until its picker stops it.
    // A thread whose window could not be created fails each invocation with NativeBackendUnavailableException instead.
    public bool IsAvailable => true;

    public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        // Work queued from the thread itself, such as from inside a dialog, runs at once.
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId))
        {
            action(0);
            return;
        }
        if (!await _ready.Task.ConfigureAwait(false)) throw new NativeBackendUnavailableException("The application's UI thread could not start.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return;
            }
            try
            {
                action(0);
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
        });
        if (!PInvoke.PostMessage((HWND)Volatile.Read(ref _window), WorkMessage, default, default))
            completion.TrySetException(new NativeBackendUnavailableException("The application's UI thread does not accept work."));
        await completion.Task.ConfigureAwait(false);
    }

    private unsafe void Run()
    {
        HWND window = default;
        bool com = false;
        try
        {
            // The thread is an STA (SetApartmentState); make sure COM agrees before any dialog is created on it.
            com = PInvoke.CoInitializeEx(null, global::Windows.Win32.System.Com.COINIT.COINIT_APARTMENTTHREADED).Value >= 0;
            if (!ClassRegistered.Value) return;
            var instance = PInvoke.GetModuleHandle((PCWSTR)null);
            fixed (char* name = ClassName)
                // HWND_MESSAGE: a message-only window, which no broadcast reaches and nothing shows.
                window = PInvoke.CreateWindowEx(0, name, null, 0, 0, 0, 0, 0, (HWND)(nint)(-3), default, (HINSTANCE)instance.Value, null);
            if (window == default) return;
            t_current = this;
            Volatile.Write(ref _window, (nint)window);
            Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
            _ready.TrySetResult(true);
            MSG message;
            while (PInvoke.GetMessage(&message, default, 0, 0).Value > 0)
            {
                PInvoke.TranslateMessage(&message);
                PInvoke.DispatchMessage(&message);
            }
        }
        catch (Exception) { /* Reported to every caller: the thread did not start. */ }
        finally
        {
            _ready.TrySetResult(false);
            t_current = null;
            _trace?.Invoke("thread's message loop ended");
            // A stopped dialog thread: work queued after Stop fails, as its window is gone.
            Volatile.Write(ref _window, 0);
            if (window != default) PInvoke.DestroyWindow(window);
            if (com) PInvoke.CoUninitialize();
            _trace?.Invoke("thread ends");
        }
    }

    private static unsafe bool RegisterClass()
    {
        var instance = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* name = ClassName)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WindowProcedure,
                hInstance = (HINSTANCE)instance.Value,
                lpszClassName = name,
            };
            return PInvoke.RegisterClassEx(&windowClass) != 0;
        }
    }

    private void RunQueued()
    {
        while (_work.TryDequeue(out var work)) work();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProcedure(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (message == WorkMessage && t_current is { } thread)
            {
                thread.RunQueued();
                return (LRESULT)0;
            }
            if (message == StopMessage && t_current is { } stopping)
            {
                stopping.RunQueued();
                PInvoke.PostQuitMessage(0);
                return (LRESULT)0;
            }
        }
        catch (Exception) { /* No exception may unwind into the window manager; queued work reports its own. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }
}
