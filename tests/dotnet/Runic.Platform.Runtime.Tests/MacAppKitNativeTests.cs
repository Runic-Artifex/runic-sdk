using System.Threading.Channels;
using Runic.Desktop;
using Runic.Platform;
using Runic.Platform.MacOS;
using Runic.Platform.Runtime;
using TUnit.Core;
using static Runic.Platform.MacOS.MacDesktopNative;

// The macOS services that need AppKit without a user: a real NSStatusItem in the menu bar, its menu and actions, the
// login item of an unbundled process, and bringing a Desktop window forward. Program.cs runs the category inside the
// Desktop event loop, so the status item starts without a window, as in a windowless application. CI runs it on the
// macOS runner under JIT and NativeAOT. Opening the menu and SMAppService with a signed bundle stay manual checks.
internal sealed class MacAppKitNativeTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(15);

    [Test, Explicit, Category("NativeAppKit"), DisplayName("native AppKit: status item, login item and window presentation"), Timeout(180_000)]
    public async Task NativeAppKitAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The AppKit checks need macOS.");
        await StatusItemAsync();
        await LoginItemAsync();
        await PresentationAsync(cancellationToken);
    }

    private static async Task StatusItemAsync()
    {
        var activations = Channel.CreateUnbounded<DesktopStatusIconActivation>();
        await using var icon = new MacStatusIcon();
        icon.Activated += (_, activation) => activations.Writer.TryWrite(activation);
        var glyph = new DesktopIcon { Images = [Glyph(16, 0), Glyph(32, 0)] };
        var badge = new DesktopIcon { Images = [Glyph(16, 0xc8)] };
        var state = new DesktopStatusIconState(glyph, "Runic status item")
        {
            Tooltip = "All quiet",
            AttentionIcon = badge,
            Menu =
            [
                new DesktopMenuCommand("open", "Open window"),
                new DesktopMenuCommand("daily", "Check daily") { Checked = true },
                new DesktopMenuSeparator(),
                new DesktopMenuSubmenu("More",
                [
                    new DesktopMenuCommand("attention", "Ask for attention"),
                    new DesktopMenuCommand("disabled", "Unavailable") { Enabled = false },
                ]),
                new DesktopMenuCommand("quit", "Quit"),
            ],
        };
        Require(await icon.ShowAsync(state).AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "The status item was not shown.");
        var shown = await icon.InspectAsync();
        Pass($"status item shown: {shown}");
        Require(shown is { Shown: true, HasImage: true, Template: true, Representations: 2, Title: "", ToolTip: "Runic status item\nAll quiet", AccessibilityLabel: "Runic status item" },
            $"Unexpected status item {shown}.");
        Require(await ApplicationAsync(static application => Send(application, Sel("activationPolicy"))) == 1,
            "A windowless unbundled process should run as an accessory application.");

        await icon.PressAsync();
        await ExpectAsync(activations, DesktopStatusIconActivationKind.Primary, null);
        Pass("pressing the button raises the primary activation");

        var menu = await icon.ChooseMenuItemAsync(0);
        Require(menu == "Open window;Check daily[on];-;More(Ask for attention;Unavailable[disabled]);Quit", $"Unexpected menu {menu}.");
        await ExpectAsync(activations, DesktopStatusIconActivationKind.MenuItem, "open");
        await icon.ChooseMenuItemAsync(3, 0);
        await ExpectAsync(activations, DesktopStatusIconActivationKind.MenuItem, "attention");
        Pass($"the menu is built from the state and its items route to their IDs: {menu}");

        Require(await icon.ShowAsync(state with { Status = DesktopStatusIconStatus.NeedsAttention, Tooltip = null }).AsTask().WaitAsync(Step)
            is PlatformResult<PlatformUnit>.Success, "The attention state was not shown.");
        var attention = await icon.InspectAsync();
        Require(attention is { HasImage: true, Template: false, Representations: 1, ToolTip: "Runic status item" }, $"Unexpected attention item {attention}.");
        Pass($"the attention state shows the colour attention image: {attention}");

        Require(await icon.ShowAsync(new DesktopStatusIconState(new DesktopIcon { ThemeName = "applications-system" }, "Runic"))
            .AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "The text item was not shown.");
        var text = await icon.InspectAsync();
        Require(text is { HasImage: false, Title: "Runic" }, $"Unexpected text item {text}.");
        Pass("an icon without images shows the title");

        Require(await icon.HideAsync().AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success, "Hiding failed.");
        Require(await icon.InspectAsync() is { Shown: false }, "The status item is still in the menu bar.");
        Require(await icon.ShowAsync(state).AsTask().WaitAsync(Step) is PlatformResult<PlatformUnit>.Success
            && (await icon.InspectAsync()).Shown, "The status item did not come back.");
        Pass("hiding removes the item and showing adds it again");
    }

    private static async Task LoginItemAsync()
    {
        Require(MacLoginStartup.IsServiceAvailable, "SMAppService should exist on the macOS 13+ runner.");
        Require(!MacLoginStartup.IsBundled, "The test host is not an application bundle.");
        var startup = new MacLoginStartup();
        var unavailable = new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        Require(await startup.IsEnabledAsync() is PlatformResult<bool>.Unavailable(PlatformUnavailableReason.BackendUnavailable), "IsEnabledAsync.");
        Require(Equals(await startup.EnableAsync(new LoginStartupRequest([])), unavailable), "EnableAsync.");
        Require(Equals(await startup.DisableAsync(), unavailable), "DisableAsync.");
        Pass("an unbundled process reports login startup unavailable");
    }

    private static async Task PresentationAsync(CancellationToken cancellationToken)
    {
        await using var host = await DesktopHost.StartAsync(new DesktopHostOptions { WaitForConnection = false }, cancellationToken);
        await using var surface = await host.CreateSurfaceAsync(new()
        {
            Content = new DesktopContent.Html("<!doctype html><meta charset=\"utf-8\"><title>Runic presentation</title><p>Presentation</p>"),
        }, cancellationToken);
        await using var window = await surface.OpenWindowAsync(new()
        {
            Browser = BrowserKind.Embedded,
            PresentationPolicy = DesktopPresentationPolicy.RequestedOnly,
            Hidden = true,
            Width = 480,
            Height = 320,
        }, cancellationToken);
        var owner = new WindowOwner(window);
        Require(!await WindowAsync(owner, static handle => Bool(handle, Sel("isVisible")) != 0), "The hidden window is visible.");
        Require(await ApplicationAsync(static application => Send(application, Sel("activationPolicy"))) == 0,
            "An unbundled process with a window should run as a regular application.");
        var menu = await MenuAsync();
        Require(menu.Contains("View(", StringComparison.Ordinal) && menu.Contains("Zoom In", StringComparison.Ordinal)
            && menu.Contains("Actual Size", StringComparison.Ordinal), $"The window host adds a View menu with the zoom items: {menu}.");
        Pass($"the main menu has the zoom items: {menu}");
        await MacWindowPresenter.PresentAsync(owner, cancellationToken);
        await WaitAsync(() => WindowAsync(owner, static handle => Bool(handle, Sel("isVisible")) != 0), "the presented window is visible");
        var key = await WindowAsync(owner, static handle => Bool(handle, Sel("isKeyWindow")) != 0);
        var active = await ApplicationAsync(static application => Bool(application, Sel("isActive")));
        Pass($"presenting orders a hidden window front (key window: {key}, application active: {active != 0})");
        await window.CloseAsync(cancellationToken);
        try
        {
            await MacWindowPresenter.PresentAsync(owner, cancellationToken);
            throw new InvalidOperationException("Presenting a closed window succeeded.");
        }
        catch (OwnerClosedException) { Pass("presenting a closed window reports it closed"); }
    }

    private static async Task ExpectAsync(Channel<DesktopStatusIconActivation> activations, DesktopStatusIconActivationKind kind, string? id)
    {
        var activation = await activations.Reader.ReadAsync().AsTask().WaitAsync(Step);
        Require(activation.Kind == kind && activation.MenuItemId == id && activation.PlatformContext is null, $"Expected {kind} {id}; got {activation}.");
    }

    private static async Task WaitAsync(Func<Task<bool>> check, string description)
    {
        var deadline = DateTime.UtcNow + Step;
        while (!await check())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {description}.");
            await Task.Delay(100);
        }
    }

    private static async Task<long> ApplicationAsync(Func<nint, long> read)
    {
        long value = 0;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            value = read(Send(Class("NSApplication"), Sel("sharedApplication")));
        });
        return value;
    }

    // The main menu's titles, with each submenu's titles in parentheses.
    private static async Task<string> MenuAsync()
    {
        string text = "";
        await MacOsMainQueue.InvokeAsync(() =>
        {
            using var pool = new Pool();
            text = Describe(Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("mainMenu")), 0);
        });
        return text;

        static string Describe(nint menu, int depth)
        {
            if (menu == 0) return "(none)";
            var titles = new List<string>();
            for (nint i = 0, count = Send(menu, Sel("numberOfItems")); i < count; i++)
            {
                var item = Arg(menu, Sel("itemAtIndex:"), i);
                var title = Text(Send(item, Sel("title")));
                if (Send(item, Sel("submenu")) is var submenu and not 0 && depth < 1) title += "(" + Describe(submenu, depth + 1) + ")";
                titles.Add(title);
            }
            return string.Join(";", titles);
        }
    }

    private static async Task<bool> WindowAsync(INativePickerOwner owner, Func<nint, bool> read)
    {
        bool value = false;
        await owner.InvokeAsync(handle => { using var pool = new Pool(); value = read(handle); });
        return value;
    }

    // A filled circle: monochrome with antialiased edges for blue 0, a red badge otherwise.
    private static DesktopIconImage Glyph(int size, byte red)
    {
        var pixels = new byte[size * size * 4];
        double radius = size / 2.0 - 1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x + 0.5 - size / 2.0, 2) + Math.Pow(y + 0.5 - size / 2.0, 2));
                int i = (y * size + x) * 4;
                pixels[i] = red;
                pixels[i + 3] = (byte)Math.Clamp((radius - distance + 0.5) * 255, 0, 255);
            }
        return new DesktopIconImage(size, size, [.. pixels]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pass(string message) => Console.WriteLine("PASS " + message);

    private sealed class WindowOwner(DesktopWindow window) : INativePickerOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => window.IsOpen && window.NativeHandle != 0;
        public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable) throw new OwnerClosedException();
            await window.DispatchNativeAsync(action, cancellationToken);
        }
    }
}
