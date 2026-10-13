// Shows a status icon on a real desktop and logs, one line each, every result and
// activation to the file in the first argument. The desktop VM tests in
// nixos/status-icon drive it through the desktop's own status area.
using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.Portal;

if (args.Length != 1) { Console.Error.WriteLine("Usage: Runic.StatusIcon.Smoke LOG"); return 2; }
var log = new StreamWriter(args[0], append: true) { AutoFlush = true };
void Log(string line) { lock (log) log.WriteLine(line); }

// A 22×22 blue (#1e5ac8) square with a white bar.
var pixels = new byte[22 * 22 * 4];
for (int y = 0; y < 22; y++)
    for (int x = 0; x < 22; x++)
    {
        bool bar = y is >= 9 and <= 12 && x is >= 4 and <= 17;
        pixels.AsSpan((y * 22 + x) * 4, 4).Fill(0xff);
        if (!bar) { pixels[(y * 22 + x) * 4] = 0x1e; pixels[(y * 22 + x) * 4 + 1] = 0x5a; pixels[(y * 22 + x) * 4 + 2] = 0xc8; }
    }
// Pixels only, so tests can find the icon by colour and see the pixmap encoding.
var icon = new DesktopIcon { Images = [new DesktopIconImage(22, 22, [.. pixels])] };
bool daily = false;
var status = DesktopStatusIconStatus.Active;
DesktopStatusIconState State() => new(icon, "Runic status icon smoke")
{
    Status = status,
    Tooltip = status == DesktopStatusIconStatus.NeedsAttention ? "Needs attention" : "All quiet",
    Menu =
    [
        new DesktopMenuCommand("open", "Open window"),
        new DesktopMenuCommand("daily", "Check daily") { Checked = daily },
        new DesktopMenuSeparator(),
        new DesktopMenuSubmenu("More",
        [
            new DesktopMenuCommand("attention", "Ask for attention"),
            new DesktopMenuCommand("disabled", "Unavailable") { Enabled = false },
        ]),
        new DesktopMenuCommand("quit", "Quit"),
    ],
};

await using var statusIcon = PortalPlatformProvider.CreateStatusIcon(diagnostic => Log($"diagnostic {diagnostic.Code}"));
var quit = new TaskCompletionSource();
var updates = System.Threading.Channels.Channel.CreateUnbounded<string>();
statusIcon.Activated += (_, activation) =>
{
    Log($"activated {activation.Kind} {activation.MenuItemId ?? "-"} token={(activation.PlatformContext?.ActivationToken is null ? "no" : "yes")}");
    switch (activation.MenuItemId)
    {
        case "daily": daily = !daily; updates.Writer.TryWrite("daily"); break;
        case "attention": status = DesktopStatusIconStatus.NeedsAttention; updates.Writer.TryWrite("attention"); break;
        case "quit": quit.TrySetResult(); break;
    }
};
Log($"show {Describe(await statusIcon.ShowAsync(State()))}");
var reader = updates.Reader.ReadAllAsync().GetAsyncEnumerator();
while (true)
{
    var next = reader.MoveNextAsync().AsTask();
    if (await Task.WhenAny(next, quit.Task) == quit.Task) break;
    Log($"update {reader.Current} {Describe(await statusIcon.ShowAsync(State()))}");
}
Log("quit");
return 0;

static string Describe(PlatformResult<PlatformUnit> result) => result switch
{
    PlatformResult<PlatformUnit>.Success => "success",
    PlatformResult<PlatformUnit>.Unavailable unavailable => $"unavailable {unavailable.Reason}",
    PlatformResult<PlatformUnit>.Failed failed => $"failed {failed.Code}",
    _ => "unknown",
};
