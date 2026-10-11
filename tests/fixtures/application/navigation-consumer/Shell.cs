#pragma warning disable RUNICNAV001 // The experimental navigation API (docs/experimental.md).
using System.ComponentModel;
using Runic.Application.Views;
using Runic.Navigation;

namespace NavigationConsumer;

public sealed partial class ShellWindow : RunicWindow<ShellViewModel>;

public sealed partial class HomeView : RunicView<HomeViewModel>;

public sealed partial class EditorView : RunicView<EditorViewModel>;

public interface IPageViewModel : INotifyPropertyChanged
{
    string Title { get; }
}

// The window's root: a generated NavigationRegion<TContent> slot, with no forwarding.
public sealed class ShellViewModel : INotifyPropertyChanged
{
    public ShellViewModel(RunicNavigator navigator, HomeViewModel home)
    {
        Navigator = navigator;
        Main = navigator.CreateRegion<IPageViewModel>(this, NavigationTarget.Borrow<IPageViewModel>(home));
    }

    [RunicIgnore]
    public RunicNavigator Navigator { get; }

    public NavigationRegion<IPageViewModel> Main { get; }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// Window-scoped and borrowed: the navigator never disposes it.
public sealed class HomeViewModel : IPageViewModel
{
    public string Title => "home";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// Created per visit and owned: its guard can veto leaving, and retiring disposes it.
public sealed class EditorViewModel(string title) : IPageViewModel, INavigationDepartureGuard, IDisposable
{
    public string Title { get; } = title;

    [RunicIgnore]
    public bool CanLeave { get; set; }

    [RunicIgnore]
    public bool Disposed { get; private set; }

    public ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken) =>
        ValueTask.FromResult(departure.Kind == NavigationDepartureKind.Retain || CanLeave);

    public void Dispose() => Disposed = true;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// An initial target must not initialize asynchronously; checked under NativeAOT too.
public sealed class InputPage : INavigationInitialize<string>
{
    public ValueTask InitializeAsync(NavigationEntryContext entry, string input, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
