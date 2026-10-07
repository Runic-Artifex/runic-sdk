using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
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
            bool? contrast = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETHIGHCONTRAST, highContrast.cbSize, &highContrast, 0)
                ? (highContrast.dwFlags & HIGHCONTRASTW_FLAGS.HCF_HIGHCONTRASTON) != 0 : null;
            int animations = 0;
            bool? reduced = PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCLIENTAREAANIMATION, 0, &animations, 0) ? animations == 0 : null;
            return new PlatformResult<DesktopAppearance>.Success(new(scheme, new(accent.Red / 255d, accent.Green / 255d, accent.Blue / 255d), contrast, reduced));
        }
        finally { Release(settings); Release(instance); UninitializeRuntime(); }
    }
    // Windows.UI.Color has no Win32 metadata; it belongs to the WinRT projection.
    [StructLayout(LayoutKind.Sequential)] private struct Color { internal byte Alpha, Red, Green, Blue; }
}
