using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Brings a Desktop window forward, for example for a second launch: the window, also a hidden or minimized one, is
// ordered front as the key window and the application is asked to activate. macOS 14 activates cooperatively
// (activate): the active application decides, as the forwarding process does when it yields (ForegroundRights).
// macOS needs no activation token.
internal static class MacWindowPresenter
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Activation that macOS refused; the window is in front of the application's other windows.</summary>
    internal static readonly PlatformDiagnostic NotActivated = new("NSApplication", 0,
        "macOS did not activate the application; the active application keeps the focus.");

    internal static async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(INativePickerOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        await owner.InvokeAsync(static window =>
        {
            using var pool = new Pool();
            var application = Send(Class("NSApplication"), Sel("sharedApplication"));
            if (Bool(application, Sel("isHidden")) != 0) Arg(application, Sel("unhide:"), 0);
            if (Bool(window, Sel("isMiniaturized")) != 0) Arg(window, Sel("deminiaturize:"), 0);
            if (BoolArg(application, Sel("respondsToSelector:"), Sel("activate")) != 0) Send(application, Sel("activate"));
            else Arg(application, Sel("activateIgnoringOtherApps:"), 1);
            Arg(window, Sel("makeKeyAndOrderFront:"), 0);
        }, cancellationToken).ConfigureAwait(false);
        // Activation completes asynchronously.
        var deadline = DateTime.UtcNow + ActivationTimeout;
        while (true)
        {
            bool active = false;
            await owner.InvokeAsync(_ => active = Bool(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("isActive")) != 0,
                cancellationToken).ConfigureAwait(false);
            if (active) return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
            if (DateTime.UtcNow > deadline) return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, NotActivated);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}
