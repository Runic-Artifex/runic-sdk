using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using ReactiveUI.SourceGenerators;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Navigation.ReactiveUI;

namespace Runic.Application.Testing.Tests;

// W260-005: a window's root page that navigates its own region. The holder creates the region empty, the list
// page takes the holder, and the shell starts the region in its constructor; one shell per MVVM flavour.
public sealed partial class ReactiveStartShellWindow : RunicWindow<ReactiveStartShellViewModel>;

public sealed partial class ToolkitStartShellWindow : RunicWindow<ToolkitStartShellViewModel>;

/// <summary>The window-scoped holder of the main region, as in the W250 trial apps.</summary>
public sealed class StartNavigation
{
    public StartNavigation(RunicNavigator navigator) => Main = navigator.CreateRegion<INavPageViewModel>(this);

    internal NavigationRegion<INavPageViewModel> Main { get; }
}

/// <summary>A dependency of the list page; a test registers a factory that throws to fail the root page.</summary>
public sealed class StartListSource
{
    public string Title { get; init; } = "list";
}

/// <summary>The root page: it pushes the detail page into the region that shows it.</summary>
public sealed class StartListViewModel(StartNavigation navigation, StartListSource source) : INavPageViewModel
{
    public string Title => source.Title;

    internal ValueTask<NavigationResult<INavPageViewModel>> OpenDetailAsync() =>
        navigation.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("detail")));

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ReactiveStartShellViewModel : ReactiveObject, IDisposable
{
    public ReactiveStartShellViewModel(StartNavigation navigation)
    {
        Main = navigation.Main;
        _currentTitleHelper = Main.WhenCurrentChanged().Select(page => page?.Title ?? "").ToProperty(this, model => model.CurrentTitle);
        Main.Start(NavigationTarget.Create<StartListViewModel>());
    }

    internal NavigationRegion<INavPageViewModel> Main { get; }

    [ObservableAsProperty]
    public partial string CurrentTitle { get; }

    public void Dispose() => _currentTitleHelper?.Dispose();
}

public sealed class ToolkitStartShellViewModel : ObservableObject
{
    private string _currentTitle = "";

    public ToolkitStartShellViewModel(StartNavigation navigation)
    {
        Main = navigation.Main;
        Main.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(Main.Current)) CurrentTitle = Main.Current?.Title ?? "";
        };
        Main.Start(NavigationTarget.Create<StartListViewModel>());
    }

    internal NavigationRegion<INavPageViewModel> Main { get; }

    public string CurrentTitle
    {
        get => _currentTitle;
        private set => SetProperty(ref _currentTitle, value);
    }
}
