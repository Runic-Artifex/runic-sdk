using Runic.Application.Views;

namespace HostedInstaller;

// The installer's one window: header, step indicator, the step's content and the footer. On the check step its content
// is the check's page, in the region "check". Closing it asks the current step first, as Back and Next do, so a check
// that runs keeps the installer open until the user decides.
public sealed partial class InstallerWindow : RunicWindow<InstallerViewModel>
{
    public InstallerWindow() => GuardClose = true;

    protected override ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken) => ViewModel.CanCloseAsync(cancellationToken);
}
