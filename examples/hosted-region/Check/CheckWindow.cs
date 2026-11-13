using Runic.Application.Views;

namespace CheckTool;

// The same Window opens in the check's own native window and, hosted, in the installer's step page. Its close guard runs
// for a user's close of its own window and, in a region, for every departure the installer decides: Back, Next or the
// installer's own close (RunicHostedRegion.AsDepartureGuard).
public sealed partial class CheckWindow : RunicWindow<CheckViewModel>
{
    public CheckWindow() => GuardClose = true;

    protected override ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken) => ViewModel.CanCloseAsync(cancellationToken);
}
