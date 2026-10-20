using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Brings a Desktop window forward, for example for a second launch: the window, also a hidden or minimized one, is
// ordered front as the key window and the application activates. macOS 14 activates cooperatively (activate), when
// the active application yields, as a forwarding launch does (ForegroundRights). When it does not, the request the
// user made by launching again is repeated with activateIgnoringOtherApps:, which macOS 26 still honours. macOS
// needs no activation token.
internal static class MacWindowPresenter
{
    private static readonly TimeSpan CooperativeTimeout = TimeSpan.FromMilliseconds(500), ActivationTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Activation that macOS refused; the window is in front of the application's other windows.</summary>
    internal static readonly PlatformDiagnostic NotActivated = new("NSApplication", 0,
        "macOS did not activate the application; the active application keeps the focus.");

    internal static async ValueTask<PlatformResult<PlatformUnit>> PresentAsync(INativeWindowOwner owner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        bool cooperative = false;
        await owner.InvokeAsync(window =>
        {
            using var pool = new Pool();
            var application = Application();
            if (Bool(application, Sel("isHidden")) != 0) Arg(application, Sel("unhide:"), 0);
            if (Bool(window, Sel("isMiniaturized")) != 0) Arg(window, Sel("deminiaturize:"), 0);
            cooperative = BoolArg(application, Sel("respondsToSelector:"), Sel("activate")) != 0;
            if (cooperative) Send(application, Sel("activate"));
            else Arg(application, Sel("activateIgnoringOtherApps:"), 1);
            Arg(window, Sel("makeKeyAndOrderFront:"), 0);
        }, cancellationToken).ConfigureAwait(false);
        if (await ActiveAsync(owner, cooperative ? CooperativeTimeout : ActivationTimeout, cancellationToken).ConfigureAwait(false))
            return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        if (cooperative)
        {
            await owner.InvokeAsync(window =>
            {
                using var pool = new Pool();
                Arg(Application(), Sel("activateIgnoringOtherApps:"), 1);
                Arg(window, Sel("makeKeyAndOrderFront:"), 0);
            }, cancellationToken).ConfigureAwait(false);
            if (await ActiveAsync(owner, ActivationTimeout, cancellationToken).ConfigureAwait(false))
                return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        }
        return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied, NotActivated);
    }

    private static nint Application() => Send(Class("NSApplication"), Sel("sharedApplication"));

    // Activation completes asynchronously.
    private static async Task<bool> ActiveAsync(INativeWindowOwner owner, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            bool active = false;
            await owner.InvokeAsync(_ => active = Bool(Application(), Sel("isActive")) != 0, cancellationToken).ConfigureAwait(false);
            if (active) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}
