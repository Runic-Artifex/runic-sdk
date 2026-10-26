using System.Drawing;
using System.Text.RegularExpressions;
using TUnit.Core;
using Runic.Desktop.Internal;

namespace Runic.Desktop.Tests;

// DesktopWindowOptions.MacOS and the drag regions of the macOS window, without AppKit. The native side, a hidden title
// bar with its traffic lights, a mouse-down in a drag region and a frameless window that takes keys, is covered by
// tests/native/Runic.Desktop.WebViewSmoke --window-chrome on the macOS CI runner, and the script by
// tests/native/Runic.Desktop.WebViewSmoke/window-chrome-script.test.mjs in Chromium.
public sealed class MacOsWindowChromeTests
{
    private const ulong Titled = 1, Closable = 2, Miniaturizable = 4, Resizable = 8, FullSizeContentView = 1UL << 15;

    private static WebUiEmbeddedHostOptions Options(MacOSTitleBarStyle titleBar = MacOSTitleBarStyle.Default,
        bool frameless = false, bool kiosk = false, bool resizable = true) =>
        new() { Frameless = frameless, Kiosk = kiosk, Resizable = resizable, MacOS = new() { TitleBar = titleBar } };

    [Test]
    public void TitleBarStylesKeepTheTrafficLightsUnderAFullSizeContentView()
    {
        Assert.Equal(Titled | Closable | Miniaturizable | Resizable, MacOsWindowChrome.StyleMask(Options()));
        Assert.Equal(Titled | Closable | Miniaturizable | Resizable | FullSizeContentView,
            MacOsWindowChrome.StyleMask(Options(MacOSTitleBarStyle.Hidden)));
        Assert.Equal(Titled | Closable | Miniaturizable | FullSizeContentView,
            MacOsWindowChrome.StyleMask(Options(MacOSTitleBarStyle.Transparent, resizable: false)));
        // Frameless and kiosk windows have no title bar: borderless, whatever TitleBar says.
        Assert.Equal(Resizable, MacOsWindowChrome.StyleMask(Options(MacOSTitleBarStyle.Hidden, frameless: true)));
        Assert.Equal(0UL, MacOsWindowChrome.StyleMask(Options(MacOSTitleBarStyle.Hidden, kiosk: true, resizable: false)));

        Assert.Equal(1, MacOsWindowChrome.TitleVisibility(MacOSTitleBarStyle.Hidden)); // NSWindowTitleHidden
        Assert.Equal(0, MacOsWindowChrome.TitleVisibility(MacOSTitleBarStyle.Transparent));
        Assert.True(MacOsWindowChrome.HasCustomTitleBar(Options(MacOSTitleBarStyle.Transparent)));
        Assert.False(MacOsWindowChrome.HasCustomTitleBar(Options()));
        Assert.False(MacOsWindowChrome.HasCustomTitleBar(Options(MacOSTitleBarStyle.Hidden, frameless: true)));
    }

    [Test]
    public void DragRegionsApplyToWindowsWithoutTheStandardTitleBar()
    {
        Assert.False(MacOsWindowChrome.UsesDragRegions(Options()));
        Assert.True(MacOsWindowChrome.UsesDragRegions(Options(MacOSTitleBarStyle.Hidden)));
        Assert.True(MacOsWindowChrome.UsesDragRegions(Options(MacOSTitleBarStyle.Transparent)));
        Assert.True(MacOsWindowChrome.UsesDragRegions(Options(frameless: true, resizable: false)));
        // A kiosk window is full-screen.
        Assert.False(MacOsWindowChrome.UsesDragRegions(Options(MacOSTitleBarStyle.Hidden, kiosk: true)));
    }

    [Test]
    public void ReportsParseIntoRegions()
    {
        var regions = MacOsWindowChrome.Parse("""{"w":800,"r":[[1,0,0,800,28],[0,700.5,4,80,20]]}""");
        Assert.NotNull(regions);
        Assert.Equal(800, regions.ViewportWidth);
        Assert.Equal(
            [new MacOsWindowChrome.DragRegion(true, 0, 0, 800, 28), new MacOsWindowChrome.DragRegion(false, 700.5, 4, 80, 20)],
            regions.Regions.ToArray());
        Assert.Empty(MacOsWindowChrome.Parse("""{"w":640,"r":[]}""")!.Regions);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"r":[]}""")]
    [Arguments("""{"w":-1,"r":[]}""")]
    [Arguments("""{"w":800}""")]
    [Arguments("""{"w":800,"r":[[2,0,0,1,1]]}""")]
    [Arguments("""{"w":800,"r":[[1,0,0,-1,1]]}""")]
    [Arguments("""{"w":800,"r":[[1,0,0,1]]}""")]
    [Arguments("""{"w":800,"r":[[1,"0",0,1,1]]}""")]
    [Arguments("""{"w":800,"r":[{"x":0}]}""")]
    [DisplayName("MalformedReportsAreIgnored(json: $json)")]
    public void MalformedReportsAreIgnored(string? json) => Assert.Null(MacOsWindowChrome.Parse(json));

    [Test]
    public void ReportsWithTooManyRegionsAreIgnored()
    {
        var region = "[1,0,0,1,1]";
        var full = $$"""{"w":800,"r":[{{string.Join(",", Enumerable.Repeat(region, MacOsWindowChrome.MaximumRegions))}}]}""";
        Assert.Equal(MacOsWindowChrome.MaximumRegions, MacOsWindowChrome.Parse(full)!.Regions.Length);
        var over = $$"""{"w":800,"r":[{{string.Join(",", Enumerable.Repeat(region, MacOsWindowChrome.MaximumRegions + 1))}}]}""";
        Assert.Null(MacOsWindowChrome.Parse(over));
    }

    [Test]
    public void TheLastRegionOverAPointDecides()
    {
        // A header that drags, a button in it that does not, and a drag handle inside the button's box.
        var regions = MacOsWindowChrome.Parse("""{"w":800,"r":[[1,0,0,800,40],[0,700,5,90,30],[1,760,10,10,10]]}""")!;
        Assert.True(MacOsWindowChrome.IsDraggable(regions, 10, 10));
        Assert.False(MacOsWindowChrome.IsDraggable(regions, 710, 10));
        Assert.True(MacOsWindowChrome.IsDraggable(regions, 765, 15));
        // Outside every region, and on the far edges, which belong to the next pixel.
        Assert.False(MacOsWindowChrome.IsDraggable(regions, 10, 40));
        Assert.False(MacOsWindowChrome.IsDraggable(regions, 800, 10));
        Assert.True(MacOsWindowChrome.IsDraggable(regions, 0, 0));
        Assert.False(MacOsWindowChrome.IsDraggable(MacOsWindowChrome.DragRegions.Empty, 0, 0));
    }

    [Test]
    public void PointsScaleToTheZoomedViewport()
    {
        var regions = MacOsWindowChrome.Parse("""{"w":400,"r":[]}""")!;
        // At 200 % page zoom the 800-point view shows a 400-pixel viewport.
        Assert.Equal((50.0, 10.0), MacOsWindowChrome.ToViewport(regions, 800, 100, 20));
        Assert.Equal((100.0, 20.0), MacOsWindowChrome.ToViewport(MacOsWindowChrome.DragRegions.Empty, 800, 100, 20));
    }

    [Test]
    [Arguments(null, false, "Zoom")]
    [Arguments(null, true, "Minimize")]
    [Arguments("Maximize", true, "Zoom")]
    [Arguments("Fill", false, "Zoom")]
    [Arguments("Zoom", false, "Zoom")]
    [Arguments("Minimize", false, "Minimize")]
    [Arguments("None", false, "None")]
    [Arguments("No Action", false, "None")]
    [DisplayName("DoubleClickFollowsTheSystemSetting(action: $action, miniaturize: $miniaturize)")]
    public void DoubleClickFollowsTheSystemSetting(string? action, bool miniaturize, string expected) =>
        Assert.Equal(Enum.Parse<MacOsWindowChrome.DoubleClickAction>(expected), MacOsWindowChrome.ForDoubleClick(action, miniaturize));

    [Test]
    public void TrafficLightsMoveAsWithElectronsPosition()
    {
        // A 600-point window, 14-point buttons 20 points apart, the close button at (20, 18) from the top-left.
        var layout = MacOsWindowChrome.LayoutTrafficLights(new Point(20, 18), 600, 14, 7, 27);
        Assert.Equal(14 + 2 * 18, layout.ContainerHeight);
        Assert.Equal(600 - 50, layout.ContainerY);
        Assert.Equal(18, layout.ButtonY);
        Assert.Equal(20, layout.FirstButtonX);
        Assert.Equal(20, layout.Step);
    }

    [Test]
    public void TheEmbeddedScriptMatchesTheHost()
    {
        var script = MacOsWindowChrome.Script;
        Assert.Contains($"messageHandlers?.{MacOsWindowChrome.MessageHandler}", script, StringComparison.Ordinal);
        Assert.Equal(MacOsWindowChrome.MaximumRegions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Regex.Match(script, @"const MaximumRegions = (\d+);").Groups[1].Value);
        // The CSS the page writes: the property of Electron, its prefixed form and the custom property.
        Assert.Contains("(?:-webkit-)?app-region", script, StringComparison.Ordinal);
        Assert.Contains("--app-region", script, StringComparison.Ordinal);
    }

    [Test]
    public async Task InvalidMacOSOptionsFailBeforeOpening()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopSurface.ValidateWindowOptions(new() { MacOS = new() { TitleBar = (MacOSTitleBarStyle)9 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DesktopSurface.ValidateWindowOptions(new() { MacOS = new() { TrafficLightPosition = new Point(-1, 10) } }));
        Assert.Throws<ArgumentNullException>(() => DesktopSurface.ValidateWindowOptions(new() { MacOS = null! }));
        DesktopSurface.ValidateWindowOptions(new()
        {
            MacOS = new() { TitleBar = MacOSTitleBarStyle.Hidden, TrafficLightPosition = new Point(0, 0) },
        });

        await using var host = await DesktopHost.StartAsync(new());
        Assert.Contains(host.Validate(new() { MacOS = new() { TitleBar = (MacOSTitleBarStyle)9 } }).Diagnostics,
            static item => item.Code == "window-option-invalid");
    }

    [Test]
    public void TheMacOSWindowReportsTitleBarOptionsItIgnores()
    {
        List<DesktopDiagnostic> diagnostics = [];
        MacOsWindowChrome_Checks(new() { Frameless = true, MacOS = new() { TitleBar = MacOSTitleBarStyle.Hidden } }, diagnostics);
        Assert.Equal(["DesktopWindowOptions.MacOS.TitleBar"], diagnostics.Select(static item => item.Option));
        Assert.All(diagnostics, static item => Assert.Equal(("window-option-unsupported", DesktopDiagnosticSeverity.Warning), (item.Code, item.Severity)));

        diagnostics.Clear();
        MacOsWindowChrome_Checks(new() { MacOS = new() { TrafficLightPosition = new Point(20, 18) } }, diagnostics);
        Assert.Equal(["DesktopWindowOptions.MacOS.TrafficLightPosition"], diagnostics.Select(static item => item.Option));

        diagnostics.Clear();
        MacOsWindowChrome_Checks(new() { Kiosk = true, MacOS = new() { TitleBar = MacOSTitleBarStyle.Transparent, TrafficLightPosition = new Point(1, 1) } },
            diagnostics);
        Assert.Equal(["DesktopWindowOptions.MacOS.TitleBar", "DesktopWindowOptions.MacOS.TrafficLightPosition"],
            diagnostics.Select(static item => item.Option));

        diagnostics.Clear();
        MacOsWindowChrome_Checks(new() { MacOS = new() { TitleBar = MacOSTitleBarStyle.Hidden, TrafficLightPosition = new Point(20, 18) } }, diagnostics);
        Assert.Empty(diagnostics);
    }

    private static void MacOsWindowChrome_Checks(DesktopWindowOptions options, List<DesktopDiagnostic> diagnostics) =>
        DesktopWindowOptionValidation.AddMacOSTitleBarChecks(options, "The macOS WKWebView window", diagnostics);

    // DesktopSurface passes DesktopWindowOptions.MacOS to its window with SetMacOS, and every window's options start
    // from the defaults.
    [Test]
    public async Task TheOptionsReachTheEmbeddedWindow()
    {
        var factory = new EmbeddedHostTests.RecordingHostFactory();
        WebUiApplication.SetEmbeddedHostFactory(factory);
        WebUiApplication.SetConfiguration(WebUiConfiguration.ShowWaitConnection, false);
        try
        {
            await using var window = new WebUiWindow();
            var macOS = new MacOSWindowOptions { TitleBar = MacOSTitleBarStyle.Hidden, TrafficLightPosition = new Point(20, 18) };
            window.SetMacOS(macOS);
            await window.ShowWebViewAsync("chrome");
            Assert.Equal(macOS, Assert.IsType<EmbeddedHostTests.RecordingHost>(factory.Host).Options!.MacOS);
            await window.ClosePresentationAsync();

            window.ResetDesktopPresentationOptions();
            await window.ShowWebViewAsync("standard");
            Assert.Equal(new MacOSWindowOptions(), Assert.IsType<EmbeddedHostTests.RecordingHost>(factory.Host).Options!.MacOS);
        }
        finally
        {
            WebUiApplication.SetConfiguration(WebUiConfiguration.ShowWaitConnection, true);
            WebUiApplication.SetEmbeddedHostFactory(null);
        }
    }
}
