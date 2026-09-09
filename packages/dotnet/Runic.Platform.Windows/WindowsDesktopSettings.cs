using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static Runic.Platform.Windows.WindowsNotificationInterop;
using Runic.Platform.Runtime;

namespace Runic.Platform.Windows;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDesktopSettings : DesktopSettingsSource
{
    protected override async ValueTask<PlatformResult<DesktopAppearance>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try { return await Task.Run(ReadNative, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException)
        { return new PlatformResult<DesktopAppearance>.Unavailable(UnavailableReason.BackendUnavailable); }
    }
    private static unsafe PlatformResult<DesktopAppearance> ReadNative()
    {
        Check(RoInitialize(1)); nint instance = 0, settings = 0;
        try
        {
            instance = Activate("Windows.UI.ViewManagement.UISettings"); settings = Query(instance, "03021be4-5254-4781-8194-5168f7d06d7b");
            Color background = default, accent = default;
            Check(((delegate* unmanaged[Stdcall]<nint, int, Color*, int>)Slot(settings, 6))(settings, 0, &background));
            Check(((delegate* unmanaged[Stdcall]<nint, int, Color*, int>)Slot(settings, 6))(settings, 5, &accent));
            var scheme = background.Red + background.Green + background.Blue < 384 ? DesktopColorScheme.Dark : DesktopColorScheme.Light;
            var highContrast = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
            bool? contrast = GetHighContrast(0x42, highContrast.Size, ref highContrast, 0) != 0 ? (highContrast.Flags & 1) != 0 : null;
            bool? reduced = GetAnimation(0x1042, 0, out var animations, 0) != 0 ? animations == 0 : null;
            return new PlatformResult<DesktopAppearance>.Success(new(scheme, new(accent.Red / 255d, accent.Green / 255d, accent.Blue / 255d), contrast, reduced));
        }
        finally { Release(settings); Release(instance); RoUninitialize(); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Color { internal byte Alpha, Red, Green, Blue; }
    [StructLayout(LayoutKind.Sequential)] private struct HighContrast { internal uint Size, Flags; internal nint Scheme; }
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static partial int GetHighContrast(uint action, uint size, ref HighContrast result, uint flags);
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")] private static partial int GetAnimation(uint action, uint size, out int result, uint flags);
}
