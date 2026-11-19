using System.Runtime.InteropServices;
using Runic.Desktop;

// sdk#1181: WebView2 activates the window to carry out a UI Automation action from another process, such as the probe's
// or a screen reader's press of a button, and can leave the keyboard focus on the window itself, while the page's
// button stays the screen reader's focus. NVDA then dropped the window's notification for the assertive message. The
// accessibility fixture puts the focus there before it announces the assertive message, so that the probe checks that
// the page still announces it (Scenario.cs, announcement-assertive), and gives the focus back to the page after.
internal static partial class WindowsAnnouncementFocus
{
    // Whether the window itself has the keyboard focus after the call.
    internal static async ValueTask<bool> FocusWindowItselfAsync(DesktopWindow window)
    {
        var focused = false;
        await window.InvokeNativeAsync(handle =>
        {
            SetFocus(handle);
            focused = GetFocus() == handle;
        });
        return focused;
    }

    [LibraryImport("user32.dll")] private static partial nint SetFocus(nint window);
    [LibraryImport("user32.dll")] private static partial nint GetFocus();
}
