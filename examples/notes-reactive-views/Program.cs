using NotesReactiveViews;
using CsWebUi;

if (args.Contains("--multi-client", StringComparer.Ordinal))
    WebUiApplication.SetConfiguration(WebUiConfiguration.MultiClient, true);

var webRootOption = Array.IndexOf(args, "--web-root");
var webRoot = webRootOption >= 0
    ? webRootOption + 1 < args.Length ? Path.GetFullPath(args[webRootOption + 1])
        : throw new ArgumentException("--web-root requires a directory.")
    : Path.Combine(AppContext.BaseDirectory, "www");

using var app = NotesApplication.Create();
await using var window = app.OpenWindow();
var portOption = Array.IndexOf(args, "--port");
if (portOption >= 0)
{
    if (portOption + 1 >= args.Length || !ushort.TryParse(args[portOption + 1], out var port) || port == 0)
        throw new ArgumentException("--port requires a TCP port from 1 to 65535.");
    window.SetPort(port);
}
window.SetRootFolder(webRoot);
window.SetSize(1000, 660);
var devFrontend = Environment.GetEnvironmentVariable("RUNIC_DEV_FRONTEND");
var serveOnly = args.Contains("--serve-only", StringComparer.Ordinal);
using var debugHost = devFrontend is null ? null : await NotesDebugHost.StartAsync(window, devFrontend, !serveOnly);
if (serveOnly)
{
    Console.WriteLine($"Main: {debugHost?.Url ?? window.StartServer("index.html")}");
    Console.Out.Flush();
    var editor = window.DataContext?.Editor
        ?? throw new InvalidOperationException("The Notes Window has no Editor ViewModel.");
    if (args.Contains("--verify-multi-client", StringComparer.Ordinal))
    {
        await Console.In.ReadLineAsync();
        if (editor.ActivationCount - editor.DeactivationCount != 1)
            throw new InvalidOperationException("The surviving client lost its Editor activation.");
        Console.WriteLine("CLIENT_STILL_ACTIVE");
        Console.Out.Flush();
        await Console.In.ReadLineAsync();
        if (!await ObserveDisconnect(editor))
            throw new InvalidOperationException("The final client disconnect retained an Editor activation.");
        Console.WriteLine("CLIENTS_RELEASED");
    }
    else
    {
        var verifyDisconnect = args.Contains("--verify-client-disconnect", StringComparer.Ordinal);
        var disconnectObserved = verifyDisconnect ? ObserveDisconnect(editor) : null;
        await Console.In.ReadLineAsync();
        if (verifyDisconnect)
        {
            if (!await disconnectObserved!) throw new InvalidOperationException("Client disconnect retained an Editor activation.");
            Console.WriteLine("CLIENT_DISCONNECT_OK");
        }
    }
}
else
{
    window.Show(debugHost?.Url ?? "index.html");
    debugHost?.RecordWindow();
    app.Wait();
}
// Server mode can still have a native worker after the final client disconnects.
// Stop WebUI before the window's async disposal destroys its native state.
WebUiApplication.Exit();
await WebUiApplication.WaitAsync();

static async Task<bool> ObserveDisconnect(EditorViewModel editor)
{
    for (var attempt = 0; attempt < 500; attempt++)
    {
        if (editor.ActivationCount > 0 && editor.ActivationCount == editor.DeactivationCount)
        {
            Console.WriteLine("CLIENT_DISCONNECT_OBSERVED");
            Console.Out.Flush();
            return true;
        }
        await Task.Delay(20);
    }
    return false;
}
