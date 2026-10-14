using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Brings a Desktop window forward, for example for a second launch: the application is activated and the window,
// also a hidden or minimized one, ordered front as the key window. macOS needs no activation token.
internal static class MacWindowPresenter
{
    internal static ValueTask PresentAsync(INativePickerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.InvokeAsync(static window =>
        {
            using var pool = new Pool();
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            if (Bool(application, Sel("isHidden")) != 0) Arg(application, Sel("unhide:"), 0);
            if (Bool(window, Sel("isMiniaturized")) != 0) Arg(window, Sel("deminiaturize:"), 0);
            Arg(application, Sel("activateIgnoringOtherApps:"), 1);
            Arg(window, Sel("makeKeyAndOrderFront:"), 0);
        }, cancellationToken);
    }
}
