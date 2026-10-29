using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// The application's own STA thread for native UI without a window: unowned file dialogs and task dialogs, and shell
// handoffs. One background thread per process runs a message loop with a message-only window. Work is queued to that
// window, so it also runs inside the modal loop of a dialog shown on the thread: cancelling an unowned file dialog
// closes it from there. Actions receive no window handle (0).
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsApplicationThread : INativeWindowOwner
{
    private const string ClassName = "RunicApplicationThread";
    private const uint WorkMessage = PInvoke.WM_APP + 0x52;
    private static readonly Lazy<WindowsApplicationThread> Instance = new(static () => new WindowsApplicationThread());
    private static WindowsApplicationThread? s_started;

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _work = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _window;
    private int _threadId;

    private WindowsApplicationThread()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "Runic application UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // The process's thread, started on first use.
    internal static WindowsApplicationThread Shared => Instance.Value;

    public Guid Generation { get; } = Guid.NewGuid();

    // The thread never closes: it lives as long as the process. A thread whose window could not be created fails each
    // invocation with NativeBackendUnavailableException instead.
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
        try
        {
            // The thread is an STA (SetApartmentState); make sure COM agrees before any dialog is created on it.
            _ = PInvoke.CoInitializeEx(null, global::Windows.Win32.System.Com.COINIT.COINIT_APARTMENTTHREADED);
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
                if (PInvoke.RegisterClassEx(&windowClass) == 0) return;
                // HWND_MESSAGE: a message-only window, which no broadcast reaches and nothing shows.
                window = PInvoke.CreateWindowEx(0, name, null, 0, 0, 0, 0, 0, (HWND)(nint)(-3), default, (HINSTANCE)instance.Value, null);
            }
            if (window == default) return;
            Volatile.Write(ref s_started, this);
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
        finally { _ready.TrySetResult(false); }
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
            if (message == WorkMessage && Volatile.Read(ref s_started) is { } thread)
            {
                thread.RunQueued();
                return (LRESULT)0;
            }
        }
        catch (Exception) { /* No exception may unwind into the window manager; queued work reports its own. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }
}
