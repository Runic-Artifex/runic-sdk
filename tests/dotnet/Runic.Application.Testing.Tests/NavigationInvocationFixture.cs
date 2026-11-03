using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// A CommunityToolkit shell whose synchronous Back command starts a navigation it does not await. The departing page's
// guard asks the page through the shell's ViewQuestion; the page answers yes, the fallback no.
public sealed partial class SyncBackShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly RunicNavigator _navigator;

    public SyncBackShellViewModel(IRunicModelContext context)
    {
        _navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        Main = _navigator.CreateRegion<object>(this, NavigationTarget.Borrow<object>(Home));
    }

    internal object Home { get; } = new();
    internal NavigationRegion<object> Main { get; }

    public ViewQuestion<string, bool> ConfirmLeave { get; } = new((_, _) => ValueTask.FromResult(false));

    // Deliberately not awaited: the pattern under test is a synchronous command that starts a navigation and returns.
#pragma warning disable CA2012
    [RelayCommand]
    private void Back() => _ = Main.BackAsync();
#pragma warning restore CA2012

    public ValueTask DisposeAsync() => _navigator.DisposeAsync();
}

// A page that asks before it leaves.
public sealed class SyncBackGuardedPage(SyncBackShellViewModel shell) : INavigationDepartureGuard
{
    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        new(shell.ConfirmLeave.AskAsync("Leave the page?", cancellationToken));
}

public sealed partial class SyncBackShellWindow : RunicWindow<SyncBackShellViewModel>;
