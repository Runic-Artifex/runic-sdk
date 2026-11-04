using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;
using static Runic.Platform.Windows.WindowsNotificationInterop;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

// SystemParametersInfoW uses generated bindings. UISettings is a WinRT projection read
// through fixed ABI slots (see docs/interop-inventory.md).
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsDesktopSettings : DesktopSettingsSource
{
    protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try { return await Task.Run(ReadNative, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        { return new PlatformResult<DesktopAppearance>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }
    private static unsafe PlatformResult<DesktopAppearance> ReadNative()
    {
        InitializeRuntime(); nint instance = 0, settings = 0;
        try
        {
            instance = Activate("Windows.UI.ViewManagement.UISettings"); settings = Query(instance, "03021be4-5254-4781-8194-5168f7d06d7b");
            Color background = default, accent = default;
            Check(((delegate* unmanaged[Stdcall]<nint, int, Color*, int>)Slot(settings, 6))(settings, 0, &background));
            Check(((delegate* unmanaged[Stdcall]<nint, int, Color*, int>)Slot(settings, 6))(settings, 5, &accent));
            var scheme = background.Red + background.Green + background.Blue < 384 ? DesktopColorScheme.Dark : DesktopColorScheme.Light;
            var highContrast = new HIGHCONTRASTW { cbSize = (uint)sizeof(HIGHCONTRASTW) };
            // A contrast theme forces its colours on every application (forced-colors: active in WebView2); Windows has
            // no setting for more contrast without one.
            DesktopContrast? contrast = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETHIGHCONTRAST, highContrast.cbSize, &highContrast, 0)
                ? (highContrast.dwFlags & HIGHCONTRASTW_FLAGS.HCF_HIGHCONTRASTON) != 0 ? DesktopContrast.ForcedColors : DesktopContrast.NoPreference
                : null;
            int animations = 0;
            bool? reduced = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) ? animations == 0 : null;
            return new PlatformResult<DesktopAppearance>.Success(new(scheme, new(accent.Red / 255d, accent.Green / 255d, accent.Blue / 255d), ReducedMotion: reduced)
            {
                Contrast = contrast,
                SystemColors = contrast == DesktopContrast.ForcedColors ? SystemColors(static index => PInvoke.GetSysColor(index)) : null,
                TextScale = TextScale(),
            });
        }
        finally { Release(settings); Release(instance); UninitializeRuntime(); }
    }
    // Settings > Accessibility > Text size, which UISettings.TextScaleFactor also reports: 100 to 225 percent, and no
    // value until the user changes it. Polled with the rest of the appearance (DesktopSettingsSource).
    internal static double? TextScale() => TextScale(static () =>
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
        return key?.GetValue("TextScaleFactor");
    });
    internal static double? TextScale(Func<object?> read)
    {
        try { return read() switch { null => 1, int percent and >= 100 and <= 225 => percent / 100d, _ => null }; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }
    // The eight colours of a contrast theme, which Chromium also uses for CSS system colours in forced-colours mode.
    internal static DesktopSystemColors SystemColors(Func<SYS_COLOR_INDEX, uint> read) => new(
        FromColorRef(read(SYS_COLOR_INDEX.COLOR_WINDOW)), FromColorRef(read(SYS_COLOR_INDEX.COLOR_WINDOWTEXT)),
        FromColorRef(read(SYS_COLOR_INDEX.COLOR_HOTLIGHT)), FromColorRef(read(SYS_COLOR_INDEX.COLOR_GRAYTEXT)),
        FromColorRef(read(SYS_COLOR_INDEX.COLOR_HIGHLIGHT)), FromColorRef(read(SYS_COLOR_INDEX.COLOR_HIGHLIGHTTEXT)),
        FromColorRef(read(SYS_COLOR_INDEX.COLOR_BTNFACE)), FromColorRef(read(SYS_COLOR_INDEX.COLOR_BTNTEXT)));
    // A COLORREF is 0x00BBGGRR.
    internal static DesktopAccentColor FromColorRef(uint value) =>
        new((value & 0xFF) / 255d, ((value >> 8) & 0xFF) / 255d, ((value >> 16) & 0xFF) / 255d);
    // Windows.UI.Color has no Win32 metadata; it belongs to the WinRT projection.
    [StructLayout(LayoutKind.Sequential)] private struct Color { internal byte Alpha, Red, Green, Blue; }
}
