using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.Linux;
using Runic.Platform.Runtime;

try
{
if (args.Contains("--native") && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
if (args.Contains("--observer"))
{
    if (Native.Init(0, 0) == 0) throw new InvalidOperationException("GTK display unavailable.");
    nint text = Native.WaitText(Native.Clipboard(Native.Atom("CLIPBOARD", 0)));
    try { Console.Write(Marshal.PtrToStringUTF8(text) ?? "<no-text>"); }
    finally { if (text != 0) Native.Free(text); }
    return;
}
// The owner can disappear after IsAvailable succeeds but before the queued
// callback is admitted. These checks require no GTK initialization.
foreach (Exception failure in new Exception[] { new OwnerClosedException(), new ObjectDisposedException("presentation") })
{
    await using var raced = LinuxPlatformProvider.CreateTextClipboard(new DisappearingOwner(failure));
    Check(await raced.ReadTextAsync(30) is PlatformResult<string?>.Unavailable { Reason: UnavailableReason.OwnerClosed }, "read owner dispatch race " + failure.GetType().Name);
    Check(await raced.WriteTextAsync("unwritten") is PlatformResult<Unit>.Unavailable { Reason: UnavailableReason.OwnerClosed }, "write owner dispatch race " + failure.GetType().Name);
}
await using (var broken = LinuxPlatformProvider.CreateTextClipboard(new DisappearingOwner(new InvalidOperationException("dispatcher defect"))))
{
    try { await broken.ReadTextAsync(30); throw new InvalidOperationException("Unexpected dispatcher failure was swallowed."); }
    catch (InvalidOperationException error) when (error.Message == "dispatcher defect") { }
}
if (!args.Contains("--native") && !args.Contains("--portal-cancel")) { Console.WriteLine("PASS Linux clipboard owner dispatch races."); return; }
await using var owner = new GtkOwner();
await using var clipboard = LinuxPlatformProvider.CreateTextClipboard(owner);
await using (var parent = await new Gtk3PortalWindowOwner(owner).ExportParentAsync())
{
    Check(parent.Identifier.StartsWith("x11:", StringComparison.Ordinal) || parent.Identifier.StartsWith("wayland:", StringComparison.Ordinal), "native portal parent export");
    Console.WriteLine("PASS GTK3 portal parent export: " + parent.Identifier.Split(':')[0]);
}
if (args.Contains("--portal-cancel"))
{
    var files = LinuxPlatformProvider.CreateFileDialogs(owner);
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try
    {
        var result = await files.OpenFileAsync(new OpenFileOptions(), cancellation.Token);
        throw new InvalidOperationException("Expected a live portal request cancelled by the caller; got " + result);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    Console.WriteLine("PASS real desktop portal request and cancellation; clipboard untouched.");
    return;
}
await owner.InvokeAsync(_ => Native.Clear(Native.Clipboard(Native.Atom("CLIPBOARD", 0))));
Check(await clipboard.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: null }, "no text");
Check(await clipboard.WriteTextAsync("") is PlatformResult<Unit>.Success, "empty write");
Check(await clipboard.ReadTextAsync(0) is PlatformResult<string?>.Success { Value: "" }, "empty distinct from absent");
const string sample = "Runic Ω 文本";
Check(await clipboard.WriteTextAsync(sample) is PlatformResult<Unit>.Success, "write");
Check(await clipboard.ReadTextAsync(sample.Length) is PlatformResult<string?>.Success { Value: sample }, "read");
var bounded = await clipboard.ReadTextAsync(2);
Check(bounded is PlatformResult<string?>.Failed { Code: FailureCode.TooLarge }, "bound: " + bounded);
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
    Check(await clipboard.WriteTextAsync(sample, late.Token) is PlatformResult<Unit>.Success, "late cancellation retains actual write outcome");
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
Check(await replacement.WriteTextAsync("replacement") is PlatformResult<Unit>.Success, "replacement owns clipboard");
await clipboard.DisposeAsync();
Check(await replacement.ReadTextAsync(30) is PlatformResult<string?>.Success { Value: "replacement" }, "disposing previous owner preserves new owner");
Console.WriteLine("PASS GTK native clipboard: absent, empty, unicode, bounds, cancellation, independent process observer. Display=" + Environment.GetEnvironmentVariable("DISPLAY") + " GDK_BACKEND=" + Environment.GetEnvironmentVariable("GDK_BACKEND") + " Wayland=" + Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
static void Check(bool value, string scenario) { if (!value) throw new InvalidOperationException(scenario); }

sealed class DisappearingOwner(Exception failure) : INativePickerOwner
{
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => true;
    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default) => ValueTask.FromException(failure);
}

sealed class GtkOwner : INativePickerOwner, IAsyncDisposable
{
    readonly ConcurrentQueue<(Action<nint>, TaskCompletionSource)> queue = new();
    readonly Thread thread;
    readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool stopping;
    public Action? AfterDispatch { get; set; }
    public Guid Generation { get; } = Guid.NewGuid();
    public bool IsAvailable => !stopping;
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
            Native.Destroy(window);
        });
        thread.Start();
    }
    public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        await started.Task;
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Enqueue((handle => { cancellationToken.ThrowIfCancellationRequested(); action(handle); }, completion));
        await completion.Task;
    }
    public ValueTask DisposeAsync() { stopping = true; thread.Join(); return ValueTask.CompletedTask; }
}
static partial class Native
{
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
