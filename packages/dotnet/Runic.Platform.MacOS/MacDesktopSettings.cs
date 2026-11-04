using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

internal sealed class MacDesktopSettings : DesktopSettingsSource
{
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
            var match = Text(Arg(Send(app, Sel("effectiveAppearance")), Sel("bestMatchFromAppearancesWithNames:"), list));
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
