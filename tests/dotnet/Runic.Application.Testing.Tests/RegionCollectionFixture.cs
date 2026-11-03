using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReactiveUI;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// region-collections/1 (D1): document tabs that each keep their own stack, opened and closed at run time.
// The Window guards its close, so a close asks every tab.
public sealed partial class NavDocumentsWindow : RunicWindow<NavDocumentsViewModel>
{
    public NavDocumentsWindow() => GuardClose = true;
}

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

    // Opens a tab that presents a borrowed page, such as a NavHomeViewModel with a guard.
    public NavigationRegion<INavPageViewModel> OpenPage(INavPageViewModel page)
    {
        var region = _navigator.CreateRegion<INavPageViewModel>(this, NavigationTarget.Borrow(page));
        Documents.Add(region);
        return region;
    }

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

// A page whose own tabs are a region collection, presented in a shell's region: replacing the page releases the route
// that asked for a close.
public sealed partial class NavTabbedPageView : RunicView<NavTabbedPageViewModel>;

/// <summary>A page with tabs of its own.</summary>
public sealed class NavTabbedPageViewModel : INavPageViewModel
{
    public NavTabbedPageViewModel(RunicNavigator navigator, INavPageViewModel tab)
    {
        Tab = navigator.CreateRegion<INavPageViewModel>(this, NavigationTarget.Borrow(tab));
        Documents = navigator.CreateSelection(this, [Tab]);
    }

    public string Title => "tabbed";

    [RunicIgnore]
    public NavigationRegion<INavPageViewModel> Tab { get; }

    /// <summary>The page's tabs.</summary>
    [RunicRegionCollection(typeof(INavPageViewModel))]
    public NavigationSelection Documents { get; }

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

// A shell whose page is a plain content property that .NET clears, which releases the page's route at once.
public sealed partial class NavContentShellWindow : RunicWindow<NavContentShellViewModel>;

/// <summary>A shell that presents one page as content.</summary>
public sealed class NavContentShellViewModel(INavPageViewModel page) : INotifyPropertyChanged
{
    /// <summary>The presented page.</summary>
    public INavPageViewModel? Page { get; private set; } = page;

    public void Clear()
    {
        Page = null;
        PropertyChanged?.Invoke(this, new(nameof(Page)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
