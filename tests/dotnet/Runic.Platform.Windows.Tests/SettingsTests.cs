using Microsoft.Win32;
using Runic.Platform;
using Runic.Platform.Windows;

// Interactive evidence that the provider reads the signed-in user's actual preferences.
internal static class SettingsTests
{
    internal static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native settings require Windows.");
        await using var settings = WindowsPlatformProvider.CreateSettings();
        if (await settings.ReadAsync() is not PlatformResult<DesktopAppearance>.Success { Value: var appearance })
            throw new InvalidOperationException("Windows appearance read failed.");
        Console.WriteLine($"Appearance: {appearance}");
        if (appearance.AccentColor is null || appearance.HighContrast is null || appearance.ReducedMotion is null)
            throw new InvalidOperationException("A native preference was unexpectedly unknown.");
        // Without high contrast, the UISettings background follows the personalized app mode.
        if (appearance.HighContrast == false &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null) is int light)
        {
            var expected = light == 0 ? DesktopColorScheme.Dark : DesktopColorScheme.Light;
            if (appearance.ColorScheme != expected) throw new InvalidOperationException($"Color scheme {appearance.ColorScheme} does not match AppsUseLightTheme={light}.");
            Console.WriteLine($"PASS native Windows settings read: color scheme matches AppsUseLightTheme={light}; accent, high contrast and reduced motion are known.");
        }
        else Console.WriteLine("PASS native Windows settings read: all preferences known; color scheme not cross-checked.");
        return 0;
    }
}
