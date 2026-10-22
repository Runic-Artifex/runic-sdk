using Microsoft.Win32;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// Interactive evidence that the provider reads the signed-in user's actual preferences.
internal sealed class SettingsTests
{
    [Test, Explicit, Category("NativeSettings")]
    public async Task NativeSettingsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native settings require Windows.");
        await using var settings = WindowsPlatformProvider.CreateSettings();
        if (await settings.ReadAsync() is not PlatformResult<DesktopAppearance>.Success { Value: var appearance })
            throw new InvalidOperationException("Windows appearance read failed.");
        Console.WriteLine($"Appearance: {appearance}");
        if (appearance.AccentColor is null || appearance.HighContrast is null || appearance.ReducedMotion is null || appearance.TextScale is null)
            throw new InvalidOperationException("A native preference was unexpectedly unknown.");
        object? textScale = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", null);
        if (appearance.TextScale != (textScale is int percent ? percent / 100d : 1))
            throw new InvalidOperationException($"Text scale {appearance.TextScale} does not match TextScaleFactor={textScale}.");
        // Without high contrast, the UISettings background follows the personalized app mode.
        if (appearance.HighContrast == false &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null) is int light)
        {
            var expected = light == 0 ? DesktopColorScheme.Dark : DesktopColorScheme.Light;
            if (appearance.ColorScheme != expected) throw new InvalidOperationException($"Color scheme {appearance.ColorScheme} does not match AppsUseLightTheme={light}.");
            Console.WriteLine($"PASS native Windows settings read: color scheme matches AppsUseLightTheme={light}; accent, high contrast and reduced motion are known.");
        }
        else Console.WriteLine("PASS native Windows settings read: all preferences known and text scale matches; color scheme not cross-checked.");
    }
}
