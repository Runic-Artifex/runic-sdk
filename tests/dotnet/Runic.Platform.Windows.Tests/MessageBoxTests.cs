using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// The NativeMessageBoxes case shows real task dialogs owned by a window on a thread of the test's own, and answers them
// with the messages the keyboard and the window's buttons send: Return, Esc, WM_CLOSE and TDM_CLICK_BUTTON. The
// NativeNetwork case reads and watches the runner's connectivity hint without asserting its state.
[SupportedOSPlatform("windows10.0.19041")]
internal sealed partial class MessageBoxTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CLOSE = 0x0010, TDM_CLICK_BUTTON = 0x0466;
    private const nuint VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, BS_DEFPUSHBUTTON = 0x1, WS_EX_LAYOUTRTL = 0x00400000;

    private static readonly DesktopMessageBoxButton Save = new("save", "Save") { Role = DesktopMessageBoxButtonRole.Default, AccessKey = 's' };
    private static readonly DesktopMessageBoxButton Discard = new("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive, AccessKey = 'n' };
    private static readonly DesktopMessageBoxButton Cancel = new("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };

    [Test, Explicit, Category("NativeMessageBoxes")]
    public async Task NativeMessageBoxesAsync()
    {
        var shell32 = WindowsCommonControls.Load(WindowsCommonControls.ActivationSource.Shell32Resource);
        var manifest = WindowsCommonControls.Load(WindowsCommonControls.ActivationSource.ShellManifestFile);
        Console.WriteLine($"Common Controls 6 from shell32.dll resource 124: {shell32 is not null}; from WindowsShell.Manifest: {manifest is not null}; " +
            $"process {(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")}");
        Check(WindowsCommonControls.Shared is not null, "a context with Common Controls 6 activates");

        using var owner = new ThreadOwner("Runic message box owner", rightToLeft: false);
        var close = new DesktopMessageBox("Save changes to “Notes”?")
        {
            Detail = "Your changes are lost if you don't save them.",
            Kind = DesktopMessageBoxKind.Warning,
            Buttons = [Discard, Cancel, Save],
        };

        // Return chooses the default button; the buttons follow Windows' order with their access keys; the caption is
        // the owner's title.
        var (dialog, result) = await ShowAsync(owner, close, dialog =>
        {
            var buttons = Buttons(dialog);
            Console.WriteLine($"Task dialog buttons as Win32 windows: {string.Join(", ", buttons.Select(static b => $"{b.Text}{(b.IsDefault ? " (default)" : "")}"))}");
            if (buttons.Count > 0)
                Check(buttons.Select(static b => b.Text).SequenceEqual(["&Save", "Do&n't Save", "Cancel"]) && buttons[0].IsDefault,
                    "Save, Don't Save, Cancel, with Save the default push button");
            Check(Text(dialog) == "Runic message box owner", $"the caption is the owner's title: {Text(dialog)}");
            Key(dialog, VK_RETURN);
        });
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var saved) && saved == Save, $"Return chose the default button: {result}");
        Check(!IsWindow(dialog), "the dialog is gone");
        Console.WriteLine("PASS Return chooses the default button");

        (_, result) = await ShowAsync(owner, close, dialog => Key(dialog, VK_ESCAPE));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var cancelled) && cancelled == Cancel, $"Esc chose the cancel button: {result}");
        (_, result) = await ShowAsync(owner, close, dialog => Post(dialog, WM_CLOSE, 0, 0));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var closed) && closed == Cancel, $"the close button chose the cancel button: {result}");
        Console.WriteLine("PASS Esc and the close button choose the cancel button");

        // The box closes on the key down, and its thread can destroy it before the test posts the key up. A fast dialog
        // thread did that on CI; here the key up waits until the box is gone, so the order is certain.
        (_, result) = await ShowAsync(owner, close, dialog => Key(dialog, VK_ESCAPE,
            afterKeyDown: () => Check(SpinWait.SpinUntil(() => !IsWindow(dialog), TestTimeouts.Scale(Wait)), "Esc closed the box on its key down")));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var early) && early == Cancel, $"a box closed before the key up: {result}");
        Console.WriteLine("PASS a box that closes before the key up answers");

        // Without a cancel button Esc and WM_CLOSE leave the box open; only its buttons answer it.
        var yes = new DesktopMessageBoxButton("yes", "Yes") { Role = DesktopMessageBoxButtonRole.Default };
        var no = new DesktopMessageBoxButton("no", "No");
        var question = new DesktopMessageBox("Replace the file?") { Title = "Replace", Kind = DesktopMessageBoxKind.Information, Buttons = [yes, no] };
        (_, result) = await ShowAsync(owner, question, dialog =>
        {
            Check(Text(dialog) == "Replace", "the title is the caption");
            Key(dialog, VK_ESCAPE);
            Post(dialog, WM_CLOSE, 0, 0);
            Thread.Sleep(500);
            Check(IsWindow(dialog), "Esc and WM_CLOSE do not close a box without a cancel button");
            Post(dialog, TDM_CLICK_BUTTON, (nuint)WindowsMessageBoxes.Id(question.Buttons, no), 0);
        });
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var answered) && answered == no, $"the clicked button: {result}");
        Console.WriteLine("PASS without a cancel button only the buttons answer");

        // Without buttons of the application one OK button answers, and Esc closes the box too; both are null.
        (_, result) = await ShowAsync(owner, new DesktopMessageBox("Exported.") { Kind = DesktopMessageBoxKind.Information }, dialog => Key(dialog, VK_ESCAPE));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"Esc in a box without buttons: {result}");
        (_, result) = await ShowAsync(owner, new DesktopMessageBox("Failed.") { Kind = DesktopMessageBoxKind.Error }, dialog => Key(dialog, VK_RETURN));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"OK in a box without buttons: {result}");
        Console.WriteLine("PASS a box without buttons answers null");

        // Cancellation closes the box and throws.
        var boxes = new WindowsMessageBoxes(owner, WindowsCommonControls.Shared!);
        var created = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Created = window => created.TrySetResult(window);
        using (var cancel = new CancellationTokenSource())
        {
            var pending = boxes.ShowAsync(close, cancel.Token).AsTask();
            var shown = await created.Task.WaitScaledAsync(Wait);
            cancel.Cancel();
            try
            {
                await pending.WaitScaledAsync(Wait);
                throw new InvalidOperationException("A cancelled box returned.");
            }
            catch (OperationCanceledException) { }
            Check(!IsWindow(shown), "cancellation closed the dialog");
        }
        Console.WriteLine("PASS cancellation closes the box");

        // A right-to-left owner gets a mirrored dialog.
        using (var mirrored = new ThreadOwner("Runic RTL owner", rightToLeft: true))
        {
            (_, result) = await ShowAsync(mirrored, close, dialog =>
            {
                Check((GetWindowLongW(dialog, GWL_EXSTYLE) & WS_EX_LAYOUTRTL) != 0, "the dialog of a right-to-left owner is mirrored");
                Key(dialog, VK_ESCAPE);
            });
            Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var rtlCancel) && rtlCancel == Cancel, $"the mirrored box answers: {result}");
        }
        Console.WriteLine("PASS a right-to-left owner gets a right-to-left dialog");

        // A closed owner reports it.
        var gone = new ThreadOwner("Runic closed owner", rightToLeft: false);
        gone.Dispose();
        result = await new WindowsMessageBoxes(gone, WindowsCommonControls.Shared!).ShowAsync(close);
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed), $"a closed owner: {result}");
        Console.WriteLine("PASS native Windows message boxes");
    }

    [Test, Explicit, Category("NativeNetwork")]
    public async Task NativeNetworkAsync()
    {
        await using var status = WindowsPlatformProvider.CreateNetworkStatus();
        Console.WriteLine($"Windows {Environment.OSVersion.Version}; network status {DesktopServiceStatus.Get(status)}");
        Check(DesktopServiceStatus.Get(status) is CapabilityStatus.Available, "Windows 10 2004 and later have the connectivity hint");
        var read = await status.ReadAsync().AsTask().WaitScaledAsync(Wait);
        Console.WriteLine($"ReadAsync: {read}");
        Check(read is PlatformResult<DesktopNetworkState>.Success, "the hint reads");
        using var cancel = TestTimeouts.Cancellation(Wait);
        await using var watch = status.WatchAsync(cancel.Token).GetAsyncEnumerator(cancel.Token);
        Check(await watch.MoveNextAsync(), "the watch reports");
        Console.WriteLine($"WatchAsync first: {watch.Current}");
        Check(watch.Current is PlatformResult<DesktopNetworkState>.Success, "the watch's initial state");
        cancel.Cancel();
        try { await watch.MoveNextAsync(); throw new InvalidOperationException("A cancelled watch continued."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS native Windows network status: read, watched and unregistered");
    }

    // Shows a box on the owner and answers it with drive, which runs on the test thread once the dialog exists.
    private static async Task<(nint Dialog, PlatformResult<DesktopMessageBoxButton?> Result)> ShowAsync(ThreadOwner owner, DesktopMessageBox box, Action<nint> drive)
    {
        var boxes = new WindowsMessageBoxes(owner, WindowsCommonControls.Shared!);
        var created = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Created = window => created.TrySetResult(window);
        var pending = boxes.ShowAsync(box).AsTask();
        var dialog = await created.Task.WaitScaledAsync(Wait);
        try { drive(dialog); }
        catch
        {
            // Closes a box that drive left open. The box may be gone already, so these posts are not checked: drive's
            // own failure is the one to report.
            PostMessageW(dialog, TDM_CLICK_BUTTON, 2, 0);
            PostMessageW(dialog, TDM_CLICK_BUTTON, 1, 0);
            throw;
        }
        return (dialog, await pending.WaitScaledAsync(Wait));
    }

    // A key press as the keyboard delivers it: to the focused control, through the dialog's message loop. A key that
    // answers the box closes it on the key down, and the dialog's thread can destroy the box before the key up is
    // posted, so the key up only has to reach a control that still exists. afterKeyDown runs between the two.
    private static void Key(nint dialog, nuint key, Action? afterKeyDown = null)
    {
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        var target = GetGUIThreadInfo(GetWindowThreadProcessId(dialog, 0), ref info) && info.Focus != 0 ? info.Focus : dialog;
        Post(target, WM_KEYDOWN, key, 1);
        afterKeyDown?.Invoke();
        Check(PostMessageW(target, WM_KEYUP, key, unchecked((nint)0xC0000001)) || !IsWindow(target),
            $"message 0x{WM_KEYUP:X} posted to a control that still exists");
    }

    private static void Post(nint window, uint message, nuint wParam, nint lParam) =>
        Check(PostMessageW(window, message, wParam, lParam), $"message 0x{message:X} posted");

    private static unsafe string Text(nint window)
    {
        var buffer = stackalloc char[256];
        int length = GetWindowTextW(window, buffer, 256);
        return new string(buffer, 0, Math.Max(length, 0));
    }

    // The dialog's push buttons, where the task dialog makes them Win32 Button windows.
    private static unsafe List<(string Text, bool IsDefault)> Buttons(nint dialog)
    {
        var found = new List<(string, bool)>();
        var handle = GCHandle.Alloc(found);
        try { EnumChildWindows(dialog, &Visit, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
        return found;
    }

    [UnmanagedCallersOnly]
    private static unsafe int Visit(nint window, nint context)
    {
        var buffer = stackalloc char[64];
        int length = GetClassNameW(window, buffer, 64);
        if (new string(buffer, 0, Math.Max(length, 0)) == "Button" && IsWindowVisible(window))
            ((List<(string, bool)>)GCHandle.FromIntPtr(context).Target!).Add((Text(window), (GetWindowLongW(window, GWL_STYLE) & 0xF) == BS_DEFPUSHBUTTON));
        return 1;
    }

    // An owner window with a thread of its own that runs each dispatched action, as a host's UI thread does.
    private sealed class ThreadOwner : INativeWindowOwner, IDisposable
    {
        private readonly BlockingCollection<Action> _work = [];
        private readonly Thread _thread;
        private readonly TaskCompletionSource<nint> _window = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _closed;

        internal ThreadOwner(string title, bool rightToLeft)
        {
            _thread = new Thread(() =>
            {
                nint window = CreateWindowExW(rightToLeft ? WS_EX_LAYOUTRTL : 0, "STATIC", title, 0x00CF0000 | 0x10000000, 120, 120, 480, 320, 0, 0, 0, 0);
                _window.TrySetResult(window);
                foreach (var action in _work.GetConsumingEnumerable()) action();
                DestroyWindow(window);
            }) { IsBackground = true, Name = title };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Check(_window.Task.Wait(TestTimeouts.Scale(Wait)) && _window.Task.Result != 0, "the owner window is created");
        }

        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => !_closed;

        public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            if (_closed) throw new OwnerClosedException();
            cancellationToken.ThrowIfCancellationRequested();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _work.Add(() =>
            {
                try { action(_window.Task.Result); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
            }, CancellationToken.None);
            return new(done.Task);
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            _work.CompleteAdding();
            _thread.Join(TestTimeouts.Scale(Wait));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(int exStyle, string className, string title, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindowVisible(nint window);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial int GetWindowLongW(nint window, int index);
    [LibraryImport("user32.dll")] private static unsafe partial int GetWindowTextW(nint window, char* text, int count);
    [LibraryImport("user32.dll")] private static unsafe partial int GetClassNameW(nint window, char* text, int count);
    [LibraryImport("user32.dll")] private static partial int GetWindowThreadProcessId(nint window, nint process);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetGUIThreadInfo(int thread, ref GuiThreadInfo info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint context);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
