using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Linux.Tests;

// Each native case was a separate run of the former executable (--native, --portal-cancel, --directory-native,
// --directory-loop-stopped): it creates its own GTK owner thread and clipboard, then exports the GTK3 portal parent
// before the mode's own checks. The modes close the window or stop the loop, so they cannot share one owner; run
// one native category per process, as CI does.
internal sealed class ClipboardTests
{
    // The owner can disappear after IsAvailable succeeds but before the queued
    // callback is admitted. These checks require no GTK initialization.
    [Test]
    public async Task OwnerDispatchRacesAsync()
    {
        foreach (Exception failure in new Exception[] { new OwnerClosedException(), new ObjectDisposedException("presentation") })
        {
            await using var raced = LinuxPlatformProvider.CreateTextClipboard(new DisappearingOwner(failure));
            Check(await raced.ReadTextAsync(30) is PlatformResult<string?>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "read owner dispatch race " + failure.GetType().Name);
            Check(await raced.WriteTextAsync("unwritten") is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "write owner dispatch race " + failure.GetType().Name);
        }
        await using (var broken = LinuxPlatformProvider.CreateTextClipboard(new DisappearingOwner(new InvalidOperationException("dispatcher defect"))))
        {
            try { await broken.ReadTextAsync(30); throw new InvalidOperationException("Unexpected dispatcher failure was swallowed."); }
            catch (InvalidOperationException error) when (error.Message == "dispatcher defect") { }
        }
        Console.WriteLine("PASS Linux clipboard owner dispatch races.");
    }

    [Test, Explicit, Category("Native")]
    public async Task NativeClipboardAsync()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        await using var owner = new GtkOwner();
        await using var clipboard = LinuxPlatformProvider.CreateTextClipboard(owner);
        await ExportPortalParentAsync(owner);
        await owner.InvokeAsync(_ => Native.Clear(Native.Clipboard(Native.Atom("CLIPBOARD", 0))));
        Check(await clipboard.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: null }, "no text");
        Check(await clipboard.WriteTextAsync("") is PlatformResult<PlatformUnit>.Success, "empty write");
        Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: "" }, "empty distinct from absent");
        const string sample = "Runic Ω 文本";
        Check(await clipboard.WriteTextAsync(sample) is PlatformResult<PlatformUnit>.Success, "write");
        Check(await clipboard.ReadTextAsync(sample.Length) is PlatformResult<string?>.Success { Value: sample }, "read");
        var bounded = await clipboard.ReadTextAsync(2);
        Check(bounded is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge }, "bound: " + bounded);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            try { await clipboard.WriteTextAsync("wrong", canceled.Token); throw new InvalidOperationException("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }
        Check(await clipboard.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: sample }, "canceled write unchanged and retry");
        using (var late = new CancellationTokenSource())
        {
            owner.AfterDispatch = late.Cancel;
            Check(await clipboard.WriteTextAsync(sample, late.Token) is PlatformResult<PlatformUnit>.Success, "late cancellation retains actual write outcome");
        }
        using (var pending = new CancellationTokenSource())
        {
            owner.AfterDispatch = pending.Cancel;
            try { await clipboard.ReadTextAsync(30, pending.Token); throw new InvalidOperationException("Read cancellation was ignored."); }
            catch (OperationCanceledException) { }
        }
        Check(await clipboard.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: sample }, "retry after drained canceled read");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--observer");
        using var observer = Process.Start(start)!;
        Task<string> observerOutput = observer.StandardOutput.ReadToEndAsync();
        Task<string> observerError = observer.StandardError.ReadToEndAsync();
        bool observerCompleted = false;
        try
        {
            await Task.WhenAll(observerOutput, observerError, observer.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(20));
            observerCompleted = true;
        }
        finally
        {
            if (!observer.HasExited) observer.Kill(entireProcessTree: true);
            await observer.WaitForExitAsync();
            if (!observerCompleted) Console.Error.Write(await observerError);
        }
        string actual = await observerOutput;
        Check(observer.ExitCode == 0 && actual == sample, "independent native observer: " + actual + " stderr: " + await observerError);
        await using var replacement = LinuxPlatformProvider.CreateTextClipboard(owner);
        Check(await replacement.WriteTextAsync("replacement") is PlatformResult<PlatformUnit>.Success, "replacement owns clipboard");
        await clipboard.DisposeAsync();
        Check(await replacement.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: "replacement" }, "disposing previous owner preserves new owner");
        Console.WriteLine("PASS GTK native clipboard: absent, empty, unicode, bounds, cancellation, independent process observer. Display=" + Environment.GetEnvironmentVariable("DISPLAY") + " GDK_BACKEND=" + Environment.GetEnvironmentVariable("GDK_BACKEND") + " Wayland=" + Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    }

    [Test, Explicit, Category("PortalCancel")]
    public async Task PortalCancelAsync()
    {
        await using var owner = new GtkOwner();
        await using var clipboard = LinuxPlatformProvider.CreateTextClipboard(owner);
        await ExportPortalParentAsync(owner);
        var files = LinuxPlatformProvider.CreateFileDialogs(owner);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var result = await files.OpenFileAsync(new OpenFileOptions(), cancellation.Token);
            throw new InvalidOperationException("Expected a live portal request cancelled by the caller; got " + result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Console.WriteLine("PASS real desktop portal request and cancellation; clipboard untouched.");
    }

    [Test, Explicit, Category("DirectoryNative")]
    public async Task DirectoryNativeAsync()
    {
        await using var owner = new GtkOwner();
        await using var clipboard = LinuxPlatformProvider.CreateTextClipboard(owner);
        await ExportPortalParentAsync(owner);
        var directories = LinuxPlatformProvider.CreateGtkNativeFileDialogs(owner);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AfterDispatch = () => shown.TrySetResult();
        using (var cancellation = new CancellationTokenSource())
        {
            var selection = directories.OpenDirectoryAsync(new(), cancellation.Token).AsTask();
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await selection.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Directory cancellation ignored."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        // Filters, the initial folder and multiple selection reach the chooser. Without a portal, GTK shows its own
        // dialog, which reports the options it was given.
        string folder = Directory.CreateTempSubdirectory("runic-chooser-").FullName;
        try
        {
            ChooserState? state = null;
            shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            owner.AfterDispatch = () => shown.TrySetResult();
            using var cancellation = new CancellationTokenSource();
            var files = directories.OpenFilesAsync(new OpenFileOptions
            {
                Filters = [new("Images", ["png", "jpg"]), new("All files", ["*"])], InitialDirectory = folder,
            }, cancellation.Token).AsTask();
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The dialog applies the folder once its main loop has run; read it from later dispatches.
            for (var deadline = DateTime.UtcNow.AddSeconds(5); state?.Folder is null && DateTime.UtcNow < deadline; await Task.Delay(50))
                await owner.InvokeAsync(_ => state = ChooserState.Read());
            cancellation.Cancel();
            try { await files.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Multiple selection cancellation ignored."); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            Check(state is { Multiple: true, Current: "Images", Folder: var shownFolder } && state.Filters.SequenceEqual(["Images", "All files"]) && shownFolder == folder,
                $"GTK chooser options: {state}");
        }
        finally { Directory.Delete(folder); }
        shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AfterDispatch = () => shown.TrySetResult();
        var pendingDirectory = directories.OpenDirectoryAsync(new()).AsTask();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.CloseWindowAsync();
        Check(await pendingDirectory.WaitAsync(TimeSpan.FromSeconds(5)) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "GTK native directory owner destruction");
        Console.WriteLine("PASS GTK3 native directory chooser cancellation, retry and destroyed-owner cleanup.");
    }

    [Test, Explicit, Category("DirectoryLoopStopped")]
    public async Task DirectoryLoopStoppedAsync()
    {
        await using var owner = new GtkOwner();
        await using var clipboard = LinuxPlatformProvider.CreateTextClipboard(owner);
        await ExportPortalParentAsync(owner);
        // The GTK loop can quit while a chooser is open, before presentation shutdown.
        // Queued native cancellation and cleanup can then never run; shutdown must not wait for them.
        // Not `await using`: a regression must fail with the bounded wait below, not hang in disposal.
        var presentation = new PresentationLifetime(() => owner.IsAvailable, owner.Generation);
        var files = new PresentationFiles(presentation, LinuxPlatformProvider.CreateGtkNativeFileDialogs(owner));
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        owner.AfterDispatch = () => shown.TrySetResult();
        var pending = files.OpenDirectoryAsync(new()).AsTask();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await owner.StopLoopAsync(destroyWindow: false);
        await presentation.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Check(await pending.WaitAsync(TimeSpan.FromSeconds(1)) is PickerResult<IDirectoryLease>.Unavailable { Reason: PlatformUnavailableReason.OwnerClosed }, "stopped GTK loop chooser shutdown");
        Console.WriteLine("PASS GTK3 native directory chooser completes and presentation shutdown drains after the GTK loop stopped.");
    }

    // Every native mode of the former executable exported the GTK3 portal parent first.
    private static async Task ExportPortalParentAsync(GtkOwner owner)
    {
        await using (var parent = await new Gtk3PortalWindowOwner(owner).ExportParentAsync())
        {
            Check(parent.Identifier.StartsWith("x11:", StringComparison.Ordinal) || parent.Identifier.StartsWith("wayland:", StringComparison.Ordinal), "native portal parent export");
            Console.WriteLine("PASS GTK3 portal parent export: " + parent.Identifier.Split(':')[0]);
        }
    }

    private static void Check(bool value, string scenario) { if (!value) throw new InvalidOperationException(scenario); }
}

internal sealed class DisappearingOwner(Exception failure) : INativePickerOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => true;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => ValueTask.FromException(failure);
}

internal sealed class GtkOwner : INativePickerOwner, IAsyncDisposable
{
    readonly ConcurrentQueue<(Action<nint>, TaskCompletionSource)> queue = new();
    readonly Thread thread;
    readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool stopping;
    volatile bool closed;
    volatile bool keepWindow;
    public Action? AfterDispatch { get; set; }
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => !stopping && !closed;
    public GtkOwner()
    {
        thread = new Thread(() =>
        {
            if (Native.Init(0, 0) == 0) { started.SetException(new InvalidOperationException("GTK display unavailable")); return; }
            nint window = Native.NewWindow(0);
            Native.Show(window);
            started.SetResult();
            while (!stopping)
            {
                while (Native.Iteration(0, 0) != 0) { }
                if (queue.TryDequeue(out var item))
                    try { item.Item1(window); var after = AfterDispatch; AfterDispatch = null; after?.Invoke(); item.Item2.SetResult(); } catch (Exception e) { item.Item2.SetException(e); }
                while (Native.Iteration(0, 0) != 0) { }
                Thread.Sleep(1);
            }
            if (!closed && !keepWindow) Native.Destroy(window);
        });
        thread.Start();
    }
    public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        await started.Task;
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue((handle => { cancellationToken.ThrowIfCancellationRequested(); if (closed) throw new OwnerClosedException(); action(handle); }, completion));
        await completion.Task;
    }
    public ValueTask CloseWindowAsync() => InvokeAsync(window => { Native.Destroy(window); closed = true; });
    // Quits the loop; the thread no longer iterates GTK. A kept window models a loop
    // that quit before its windows were destroyed.
    public ValueTask StopLoopAsync(bool destroyWindow) { keepWindow = !destroyWindow; return DisposeAsync(); }
    public ValueTask DisposeAsync() { stopping = true; thread.Join(); return ValueTask.CompletedTask; }
}

// The options of the GTK file chooser dialog that is open, read on the GTK thread.
internal sealed record ChooserState(bool Multiple, string[] Filters, string? Current, string? Folder)
{
    internal static ChooserState? Read()
    {
        nint toplevels = Native.ListToplevels();
        try
        {
            for (nint node = toplevels; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                nint window = Marshal.ReadIntPtr(node);
                if (Native.IsA(window, Native.FileChooserType()) == 0) continue;
                var filters = new List<string>();
                nint list = Native.ListFilters(window);
                for (nint item = list; item != 0; item = Marshal.ReadIntPtr(item, IntPtr.Size))
                    filters.Add(Marshal.PtrToStringUTF8(Native.FilterName(Marshal.ReadIntPtr(item))) ?? "");
                Native.FreeSList(list);
                nint current = Native.CurrentFilter(window);
                nint folder = Native.CurrentFolder(window);
                try
                {
                    return new(Native.SelectMultiple(window) != 0, [.. filters],
                        current == 0 ? null : Marshal.PtrToStringUTF8(Native.FilterName(current)), Marshal.PtrToStringUTF8(folder));
                }
                finally { Native.Free(folder); }
            }
            return null;
        }
        finally { Native.FreeList(toplevels); }
    }
    public override string ToString() => $"multiple {Multiple}, filters [{string.Join(", ", Filters)}], current {Current}, folder {Folder}";
}

internal static partial class Native
{
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_list_toplevels")] internal static partial nint ListToplevels();
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_list_free")] internal static partial void FreeList(nint list);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_slist_free")] internal static partial void FreeSList(nint list);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")] internal static partial int IsA(nint instance, nint type);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_get_type")] internal static partial nint FileChooserType();
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_get_select_multiple")] internal static partial int SelectMultiple(nint chooser);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_list_filters")] internal static partial nint ListFilters(nint chooser);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_get_filter")] internal static partial nint CurrentFilter(nint chooser);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_chooser_get_current_folder")] internal static partial nint CurrentFolder(nint chooser);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_file_filter_get_name")] internal static partial nint FilterName(nint filter);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_new")] internal static partial nint NewWindow(int type);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_show")] internal static partial void Show(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_destroy")] internal static partial void Destroy(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_init_check")] internal static partial int Init(nint argc, nint argv);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_main_context_iteration")] internal static partial int Iteration(nint context, int block);
    [LibraryImport("libgdk-3.so.0", EntryPoint = "gdk_atom_intern", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint Atom(string name, int only);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_get")] internal static partial nint Clipboard(nint atom);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_wait_for_text")] internal static partial nint WaitText(nint clipboard);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_clipboard_clear")] internal static partial void Clear(nint clipboard);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_free")] internal static partial void Free(nint pointer);
}
