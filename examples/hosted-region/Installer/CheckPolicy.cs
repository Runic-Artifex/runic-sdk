using Runic.Application.Views.Desktop;
using Runic.Desktop;

namespace HostedInstaller;

// The installer's rules for the check's windows: its main window shows in the region "check" of the installer's step
// page, never in a window of its own. Every member it does not implement keeps its default.
// The check's window shows the installer's design: design reads the current one (InstallerViewModel.CheckAppearance).
public sealed class CheckPolicy(Func<RunicHostedAppearance?> design) : IRunicHostedWindowPolicy
{
    public const string Region = "check";

    // The frame's permissions: the frame's allow attribute lists only what this grants, and the host window's engine
    // decides the frame's camera and microphone requests by it. The check needs neither, so it gets none, also where the
    // installer's own windows would grant them. DesktopPermissionGrant.MediaCapture would allow both (not on WebKitGTK,
    // which denies them in a region). FocusOnAttach moves the keyboard focus into the check when it attaches, but only
    // while the installer's page holds no focus of its own.
    public static RunicHostedRegionOptions RegionOptions { get; } = new()
    {
        Permissions = DesktopPermissionGrant.None,
        FocusOnAttach = true,
    };

    public ValueTask<RunicHostedWindowDecision> OpenWindowAsync(RunicHostedWindowRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(request.Owner is null && request.CanEmbed
            // The installer has one window, so the region's name finds it. ReattachOnReload, on by default, shows the
            // check again when the page reloads. The appearance override gives the check's page the installer's design
            // before its first render. A check of 0.18 (request.CanOverrideAppearance is false) still shows, in the
            // desktop's appearance, and the installer logs event 2030 once; a policy that needs the design could
            // refuse it instead.
            ? RunicHostedWindowDecision.Embed(Region, RegionOptions with { Appearance = design() })
            // A check older than region/1 (0.17) cannot show in a region; the installer then shows none.
            : RunicHostedWindowDecision.Refuse("The installer shows the system check only in its step page."));
}
