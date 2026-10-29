using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Native UI without a window (#712): the application's own STA thread (WindowsApplicationThread) and an unowned file
// dialog on it, which cancellation closes from inside the dialog's modal loop. Part of the NativeMessageBoxes run, which
// CI starts with JIT and NativeAOT.
[SupportedOSPlatform("windows8.0")]
internal sealed partial class UnownedDialogTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativeMessageBoxes")]
    public async Task ApplicationThreadAsync()
    {
        var thread = WindowsApplicationThread.Shared;
        Check(ReferenceEquals(thread, WindowsApplicationThread.Shared) && thread.IsAvailable, "one application thread per process");
        (int Id, ApartmentState Apartment, nint Handle) first = default, second = default;
        await thread.InvokeAsync(handle => first = (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), handle)).AsTask().WaitAsync(Wait);
        await thread.InvokeAsync(handle => second = (Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState(), handle)).AsTask().WaitAsync(Wait);
        Check(first.Id != Environment.CurrentManagedThreadId && first.Id == second.Id && first.Apartment == ApartmentState.STA && first.Handle == 0,
            $"work runs on one STA thread of its own without a window: {first}, {second}");
        // Work queued from the thread itself runs at once, as a dialog's own work must.
        var nested = false;
        await thread.InvokeAsync(_ => thread.InvokeAsync(_ => nested = true).AsTask().GetAwaiter().GetResult()).AsTask().WaitAsync(Wait);
        Check(nested, "work queued from the thread runs inline");
        var failure = await Record(() => thread.InvokeAsync(_ => throw new FormatException()).AsTask());
        Check(failure is FormatException, $"an exception reaches the caller: {failure}");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            Check(await Record(() => thread.InvokeAsync(_ => { }, cancelled.Token).AsTask()) is OperationCanceledException, "a cancelled request does not run");
        }
        Console.WriteLine("PASS the application thread");
    }

    [Test, Explicit, Category("NativeMessageBoxes")]
    public async Task UnownedFileDialogAsync()
    {
        var thread = WindowsApplicationThread.Shared;
        var picker = new WindowsFilePicker(thread, unowned: true);
        var trace = new System.Collections.Concurrent.ConcurrentQueue<string>();
        picker.Trace = trace.Enqueue;
        using var cancel = new CancellationTokenSource();
        var pending = picker.SelectAsync(new NativeFileDialogRequest(false, null) { Title = "Runic unowned dialog" }, cancel.Token).AsTask();
        await picker.Shown.Task.WaitAsync(Wait);
        // The dialog is a top-level window of the application thread with the request's title and no owner.
        nint dialog = 0;
        for (var deadline = DateTime.UtcNow + Wait; dialog == 0 && DateTime.UtcNow < deadline; await Task.Delay(100))
            dialog = FindWindowW(null, "Runic unowned dialog");
        Check(dialog != 0, "the unowned dialog is shown with its title");
        Check(GetWindow(dialog, GW_OWNER) == 0, "the dialog has no owner window");
        cancel.Cancel();
        var ended = await Record(() => pending.WaitAsync(Wait));
        Console.WriteLine($"Unowned file dialog cancellation: {string.Join("; ", trace)}");
        Check(ended is OperationCanceledException or null, $"cancellation ends the dialog ({ended?.GetType().Name}): {string.Join("; ", trace)}");
        Check(!IsWindow(dialog), "cancellation closed the dialog");
        Console.WriteLine("PASS an unowned file dialog shows and closes");
    }

    // #724: the application's task dialogs need no window: they run on the application thread without an owner, answer
    // with their buttons and close on cancellation.
    [Test, Explicit, Category("NativeMessageBoxes")]
    public async Task UnownedMessageBoxAsync()
    {
        var boxes = (WindowsMessageBoxes)WindowsPlatformProvider.CreateUnownedMessageBoxes();
        var cancelButton = new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };
        var box = new DesktopMessageBox("The settings cannot be read.")
        {
            Title = "Runic unowned box", Kind = DesktopMessageBoxKind.Error,
            Buttons = [new DesktopMessageBoxButton("retry", "Retry") { Role = DesktopMessageBoxButtonRole.Default }, cancelButton],
        };
        var created = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Created = window => created.TrySetResult(window);
        var pending = boxes.ShowAsync(box).AsTask();
        var dialog = await created.Task.WaitAsync(Wait);
        Check(GetWindow(dialog, GW_OWNER) == 0, "the box has no owner window");
        PostMessageW(dialog, WM_CLOSE, 0, 0);
        var result = await pending.WaitAsync(Wait);
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var chosen) && chosen == cancelButton, $"the close button chose Cancel: {result}");

        created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var cancel = new CancellationTokenSource())
        {
            var cancelled = boxes.ShowAsync(box, cancel.Token).AsTask();
            var shown = await created.Task.WaitAsync(Wait);
            cancel.Cancel();
            Check(await Record(() => cancelled.WaitAsync(Wait)) is OperationCanceledException, "a cancelled box throws");
            Check(!IsWindow(shown), "cancellation closed the box");
        }
        Console.WriteLine("PASS an unowned task dialog answers and closes");
    }

    private const uint GW_OWNER = 4, WM_CLOSE = 0x0010;
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial nint FindWindowW(string? className, string windowName);
    [LibraryImport("user32.dll")] private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool IsWindow(nint window);

    private static async Task<Exception?> Record(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Unowned dialog check failed: " + message);
    }
}
