using Runic.Spike.WebKitCdp;

var inspector = Arg("--inspector") ?? "127.0.0.1:9340";
var parts = inspector.Split(':');
Log.File = Arg("--log");
if (Log.File is { } f) File.WriteAllText(f, "");
await using var socket = new InspectorSocket();
await socket.ConnectAsync(parts[0], int.Parse(parts[1]), CancellationToken.None);
if (args.Contains("--probe"))
{
    await Task.Delay(1500);
    if (Arg("--commands") is { } path) File.WriteAllText(path, socket.BackendCommands);
    foreach (var t in socket.Targets) Console.WriteLine($"TARGET {t.ConnectionId}/{t.TargetId} {t.Type} {t.Name} {t.Url}");
    return;
}
var origins = args.Select((a, i) => (a, i)).Where(x => x.a == "--allow-origin" && x.i + 1 < args.Length).Select(x => args[x.i + 1]).ToHashSet();
await Task.Delay(500); // the first SetTargetList
await CdpServer.RunAsync(socket, int.Parse(Arg("--port") ?? "9229"), origins);

string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
