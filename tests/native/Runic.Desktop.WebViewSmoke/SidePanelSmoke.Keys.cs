using Runic.Desktop;

internal static partial class SidePanelSmoke
{
    // For the accessibility fixture's side panel scenario (AccessibilitySmoke.cs): Tab or Shift+Tab posted to the
    // application's event queue on macOS, as a user presses it, so the side panel's focus hand-off runs.
    internal static ValueTask PressTabOnMacOSAsync(DesktopWindow window, bool shift) => MacOS.PressTabAsync(window, shift);
}
