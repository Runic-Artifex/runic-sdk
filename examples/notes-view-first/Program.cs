using CsWebUi;
using NotesWindowViews;

var useSplat = args.Contains("--splat", StringComparer.Ordinal);
if (args.Contains("--check-view-lifetime", StringComparer.Ordinal))
{
    await ViewLifetimeCheck.RunAsync(useSplat);
    return;
}
var webRootOption = Array.IndexOf(args, "--web-root");
var webRoot = webRootOption >= 0
    ? webRootOption + 1 < args.Length
        ? Path.GetFullPath(args[webRootOption + 1])
        : throw new ArgumentException("--web-root requires a directory.")
    : Path.Combine(AppContext.BaseDirectory, "www");

var twoWindows = args.Contains("--two-windows", StringComparer.Ordinal);
ushort? port = null;
var portOption = Array.IndexOf(args, "--port");
if (portOption >= 0)
{
    if (twoWindows) throw new ArgumentException("--port cannot be combined with --two-windows.");
    if (portOption + 1 >= args.Length || !ushort.TryParse(args[portOption + 1], out var value) || value == 0)
        throw new ArgumentException("--port requires a TCP port from 1 to 65535.");
    port = value;
}
var serveOnly = args.Contains("--serve-only", StringComparer.Ordinal);

using var app = NotesApplication.Create(useSplat);
// Server mode only starts the windows' servers, below.
await using (var window = await app.OpenWindowAsync(webRoot, 1050, 690, port, show: !serveOnly))
await using (var second = twoWindows ? await app.OpenWindowAsync(webRoot, 1050, 690, show: !serveOnly) : null)
{
    if (second is not null && ReferenceEquals(window.DataContext, second.DataContext))
        throw new InvalidOperationException("Two windows unexpectedly share one window-scoped ShellViewModel.");
    if (serveOnly)
    {
        Console.WriteLine($"Main: {window.NativeWindow.StartServer("index.html")}");
        if (second is not null) Console.WriteLine($"Second: {second.NativeWindow.StartServer("index.html")}");
        Console.Out.Flush();
        await Console.In.ReadLineAsync();
    }
    else
    {
        // CS-WebUI reports closing for all of its windows at once.
        await window.WaitForCloseAsync();
    }
    // Server mode can still have a native worker after the final client disconnects.
    // Stop WebUI before the windows' async disposal destroys their native state.
    WebUiApplication.Exit();
    await WebUiApplication.WaitAsync();
}
if (args.Contains("--verify-web-mount", StringComparer.Ordinal))
{
    ViewTrace.VerifyWebMounts();
    Console.WriteLine("WEB_MOUNT_BROWSER_OK");
}
