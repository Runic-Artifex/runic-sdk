using NotesWindowViews;

var useSplat = args.Contains("--splat", StringComparer.Ordinal);
if (args.Contains("--check-view-lifetime", StringComparer.Ordinal))
{
    ViewLifetimeCheck.Run(useSplat);
    return;
}
var webRootOption = Array.IndexOf(args, "--web-root");
var webRoot = webRootOption >= 0
    ? webRootOption + 1 < args.Length
        ? Path.GetFullPath(args[webRootOption + 1])
        : throw new ArgumentException("--web-root requires a directory.")
    : Path.Combine(AppContext.BaseDirectory, "www");

using var app = NotesApplication.Create(useSplat);
var twoWindows = args.Contains("--two-windows", StringComparer.Ordinal);
using (var window = app.OpenWindow())
using (var second = twoWindows ? app.OpenWindow() : null)
{
    var portOption = Array.IndexOf(args, "--port");
    if (portOption >= 0)
    {
        if (twoWindows) throw new ArgumentException("--port cannot be combined with --two-windows.");
        if (portOption + 1 >= args.Length || !ushort.TryParse(args[portOption + 1], out var port) || port == 0)
            throw new ArgumentException("--port requires a TCP port from 1 to 65535.");
        window.SetPort(port);
    }
    window.SetRootFolder(webRoot);
    window.SetSize(1050, 690);
    if (second is not null)
    {
        if (ReferenceEquals(window.DataContext, second.DataContext))
            throw new InvalidOperationException("Two windows unexpectedly share one window-scoped ShellViewModel.");
        second.SetRootFolder(webRoot);
        second.SetSize(1050, 690);
    }
    if (args.Contains("--serve-only", StringComparer.Ordinal))
    {
        Console.WriteLine($"Main: {window.StartServer("index.html")}");
        if (second is not null) Console.WriteLine($"Second: {second.StartServer("index.html")}");
        Console.Out.Flush();
        await Console.In.ReadLineAsync();
    }
    else
    {
        window.Show("index.html");
        second?.Show("index.html");
        app.Wait();
    }
}
if (args.Contains("--verify-web-mount", StringComparer.Ordinal))
{
    ViewTrace.VerifyWebMounts();
    Console.WriteLine("WEB_MOUNT_BROWSER_OK");
}
