using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// region-collections/1 (D1): document tabs that each keep their own stack, opened and closed at run time.
public sealed partial class NavDocumentsWindow : RunicWindow<NavDocumentsViewModel>;

/// <summary>Document tabs, CommunityToolkit flavor: a command opens a tab; the page selects and closes them.</summary>
public sealed partial class NavDocumentsViewModel : INotifyPropertyChanged
{
    private readonly RunicNavigator _navigator;

    public NavDocumentsViewModel(RunicNavigator navigator)
    {
        _navigator = navigator;
        Documents = navigator.CreateSelection(this, []);
    }

    /// <summary>The open documents.</summary>
    [RunicRegionCollection(typeof(INavPageViewModel))]
    public NavigationSelection Documents { get; }

    /// <summary>The ViewModels the opened documents present first, by title.</summary>
    [RunicIgnore]
    public Dictionary<string, INavPageViewModel> Opened { get; } = [];

    [RelayCommand]
    private async Task<NavigationOutcome> OpenAsync(string title)
    {
        var page = new NavEditorViewModel(title);
        Opened[title] = page;
        var region = _navigator.CreateRegion<INavPageViewModel>(this, NavigationTarget.Own<INavPageViewModel>(page));
        Documents.Add(region);
        return (await Documents.SelectAsync(region)).Outcome;
    }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// The same tabs with ReactiveUI; only the ViewModel selects, so the page has no close method (PageCanClose = false).
public sealed partial class ReactiveNavDocumentsWindow : RunicWindow<ReactiveNavDocumentsViewModel>;

/// <summary>Document tabs, ReactiveUI flavor.</summary>
public sealed class ReactiveNavDocumentsViewModel : ReactiveObject, IDisposable
{
    private readonly IDisposable _count;
    private int _count1;

    public ReactiveNavDocumentsViewModel(RunicNavigator navigator, IReadOnlyList<INavPageViewModel> pages)
    {
        Tabs = navigator.CreateSelection(this,
            [.. pages.Select(page => navigator.CreateRegion<INavPageViewModel>(this, NavigationTarget.Borrow(page)))]);
        _count = Tabs.WhenRegionsChanged().Subscribe(new CountObserver(this));
        CloseCommand = Tabs.CreateCloseCommand();
    }

    /// <summary>The tabs.</summary>
    [RunicRegionCollection(typeof(INavPageViewModel), PageCanClose = false)]
    public NavigationSelection Tabs { get; }

    /// <summary>How many tabs are open.</summary>
    public int Count
    {
        get => _count1;
        private set => this.RaiseAndSetIfChanged(ref _count1, value);
    }

    [RunicIgnore]
    public ReactiveCommand<NavigationRegion, NavigationOutcome> CloseCommand { get; }

    public void Dispose()
    {
        _count.Dispose();
        CloseCommand.Dispose();
    }

    private sealed class CountObserver(ReactiveNavDocumentsViewModel owner) : IObserver<IReadOnlyList<NavigationRegion>>
    {
        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(IReadOnlyList<NavigationRegion> value) => owner.Count = value.Count;
    }
}
