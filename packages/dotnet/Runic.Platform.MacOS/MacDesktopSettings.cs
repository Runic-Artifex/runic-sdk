using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// A watch reads again when MacSettingsChanges reports a change.
internal sealed class MacDesktopSettings : DesktopSettingsSource
{
    protected override IDisposable? StartChanges(Action changed, Action failed) => MacSettingsChanges.Start(changed);

    protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        DesktopAppearance? result = null;
        await MacOsMainQueue.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pool = new Pool();
            var app = Send(Class("NSApplication"), Sel("sharedApplication"));
            var list = Send(Class("NSMutableArray"), Sel("array"));
            Arg(list, Sel("addObject:"), String("NSAppearanceNameAqua"));
            Arg(list, Sel("addObject:"), String("NSAppearanceNameDarkAqua"));
            // The desktop's scheme. An application that chose one for every window (DesktopHost.SetColorSchemeAsync) sets
            // NSApp.appearance, which its effectiveAppearance then reports; the global AppleInterfaceStyle preference is
            // the desktop's then ("Dark", or absent for light).
            var match = Send(app, Sel("appearance")) == 0
                ? Text(Arg(Send(app, Sel("effectiveAppearance")), Sel("bestMatchFromAppearancesWithNames:"), list))
                : Text(Arg(Send(Class("NSUserDefaults"), Sel("standardUserDefaults")), Sel("stringForKey:"), String("AppleInterfaceStyle"))) == "Dark"
                    ? "NSAppearanceNameDarkAqua" : "NSAppearanceNameAqua";
            var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
            var rgb = Arg(Send(Class("NSColor"), Sel("controlAccentColor")), Sel("colorUsingColorSpace:"), Send(Class("NSColorSpace"), Sel("sRGBColorSpace")));
            result = new(match == "NSAppearanceNameDarkAqua" ? DesktopColorScheme.Dark : match == "NSAppearanceNameAqua" ? DesktopColorScheme.Light : DesktopColorScheme.NoPreference,
                rgb == 0 ? null : new(Double(rgb, Sel("redComponent")), Double(rgb, Sel("greenComponent")), Double(rgb, Sel("blueComponent"))),
                ReducedMotion: Bool(workspace, Sel("accessibilityDisplayShouldReduceMotion")) != 0)
            {
                // Increase contrast keeps the application's colours; macOS has no forced-colours mode, and no palette.
                Contrast = Bool(workspace, Sel("accessibilityDisplayShouldIncreaseContrast")) != 0 ? DesktopContrast.More : DesktopContrast.NoPreference,
            };
        }).ConfigureAwait(false);
        return new PlatformResult<DesktopAppearance>.Success(result!);
    }
}
