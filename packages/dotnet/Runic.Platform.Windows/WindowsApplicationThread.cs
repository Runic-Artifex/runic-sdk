using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// The application's own STA thread for native UI without a window: task dialogs and shell handoffs (Shared), and each
// unowned file dialog's thread of its own (RentDialog), so that two windows' dialogs never nest in one modal loop. Each
// background thread runs a message loop with a message-only window. Work is queued to that window, so it also runs
// inside the modal loop of a dialog shown on the thread: cancelling an unowned file dialog closes it from there. Actions
// receive no window handle (0).
// No thread ever ends: a dialog's thread waits, idle, for the next unowned dialog, so there are only ever as many as
// were shown at once, and all of them end with the process. A dialog thread that ended (its window destroyed,
// CoUninitialize, thread detach) while another dialog was still shown crashed the process when that other dialog
// closed: exit code 0xC000041D (STATUS_FATAL_USER_CALLBACK_EXCEPTION), faulting module ntdll, a null dereference in
// RtlpWaitOnCriticalSection under oleacc!CPropMgr::ClearProps, called from
// explorerframe!CNavBar::ClearHwndAccProperties as comdlg32!CFileOpenSave::_OnDestroyDialog tore down the navigation
// bar inside the second dialog's DestroyWindow. oleacc keeps its accessibility property store for the whole process,
// and the thread that had ended left that store's lock unusable (#845; the windows-11-arm runners, about one run in
// six). Ending a thread only at shutdown would gain nothing: the process ends them, and a dialog can still be closing
// then.
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsApplicationThread : INativeWindowOwner
{
    private const string ClassName = "RunicApplicationThread";
    private const uint WorkMessage = PInvoke.WM_APP + 0x52;
    private static readonly Lazy<WindowsApplicationThread> Instance = new(static () => new WindowsApplicationThread("Runic application UI"));
    // The window class every thread's message-only window uses, registered once per process.
    private static readonly Lazy<bool> ClassRegistered = new(RegisterClass);
    // The instance whose message loop runs on this thread, for the window procedure.
    [ThreadStatic] private static WindowsApplicationThread? t_current;

    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _work = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Dialog threads whose dialog is done, for the next unowned dialog.
    private static readonly System.Collections.Concurrent.ConcurrentStack<WindowsApplicationThread> IdleDialogThreads = new();
    private nint _window;
    private int _threadId;

    private WindowsApplicationThread(string name)
    {
        var thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // The process's thread, started on first use.
    internal static WindowsApplicationThread Shared => Instance.Value;

    // A thread for one dialog: an idle one whose earlier dialog is done, or a new one. Return gives it back.
    internal static WindowsApplicationThread RentDialog()
    {
        while (IdleDialogThreads.TryPop(out var idle))
            // A thread that could not start its message loop takes no more dialogs.
            if (idle._ready.Task is not { IsCompleted: true, Result: false }) return idle;
        return new("Runic unowned dialog");
    }

    // The dialog on this thread is done (Show returned and released it): the thread waits for the next one.
    internal void Return()
    {
        if (ReferenceEquals(this, Instance.IsValueCreated ? Instance.Value : null)) throw new InvalidOperationException("The application's thread lives as long as the process.");
        if (_ready.Task is { IsCompleted: true, Result: false }) return;
        IdleDialogThreads.Push(this);
    }

    public Guid Generation { get; } = Guid.NewGuid();

    // Neither the shared thread nor a dialog's thread closes: both live as long as the process.
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
            // Only a thread whose loop failed gets here: work queued after it fails, as its window is gone.
            Volatile.Write(ref _window, 0);
            if (window != default) PInvoke.DestroyWindow(window);
            if (com) PInvoke.CoUninitialize();
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
        }
        catch (Exception) { /* No exception may unwind into the window manager; queued work reports its own. */ }
        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }
}
