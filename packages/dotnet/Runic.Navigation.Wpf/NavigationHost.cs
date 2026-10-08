using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Runic.Navigation.Wpf;

/// <summary>Presents the current entry of a navigation region.</summary>
/// <remarks>
/// <para>
/// Each entry gets a new <see cref="ContentPresenter"/> and a new view, also when two entries have the same type or the
/// same borrowed content, so no view state leaks from one entry to the next. A retained entry gets a new view when it
/// becomes current again: keep state that must survive in the ViewModel.
/// </para>
/// <para>
/// The view comes from <see cref="ViewLocator"/>, then from an <see cref="INavigationViewLocator"/> registered in the
/// navigator's services, then from the implicit <see cref="DataTemplate"/> keyed by the content type or a base type.
/// The host follows the region while it is loaded, and never changes entries when it is unloaded.
/// <see cref="NavigationCommands.BrowseBack"/> goes back when the region can go back and isn't transitioning.
/// </para>
/// <para>
/// The host sets its own <see cref="ContentControl.Content"/>: don't set <c>Content</c>, <c>ContentTemplate</c> or
/// <c>ContentTemplateSelector</c> on it. Present entries with implicit <see cref="DataTemplate"/>s or a locator.
/// </para>
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public class NavigationHost : ContentControl
{
    /// <summary>Identifies the <see cref="Region"/> dependency property.</summary>
    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(nameof(Region), typeof(INavigationRegion),
        typeof(NavigationHost), new PropertyMetadata(null, static (d, _) => ((NavigationHost)d).OnRegionChanged()));

    /// <summary>Identifies the <see cref="ViewLocator"/> dependency property.</summary>
    public static readonly DependencyProperty ViewLocatorProperty = DependencyProperty.Register(nameof(ViewLocator),
        typeof(INavigationViewLocator), typeof(NavigationHost), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="EmptyContent"/> dependency property.</summary>
    public static readonly DependencyProperty EmptyContentProperty = DependencyProperty.Register(nameof(EmptyContent), typeof(object),
        typeof(NavigationHost), new PropertyMetadata(null, static (d, _) => ((NavigationHost)d).OnEmptyContentChanged()));

    private static readonly DependencyPropertyKey IsTransitioningPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsTransitioning),
        typeof(bool), typeof(NavigationHost), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="IsTransitioning"/> dependency property.</summary>
    public static readonly DependencyProperty IsTransitioningProperty = IsTransitioningPropertyKey.DependencyProperty;

    /// <summary>Identifies the <see cref="HandlesBrowseBack"/> dependency property.</summary>
    public static readonly DependencyProperty HandlesBrowseBackProperty = DependencyProperty.Register(nameof(HandlesBrowseBack), typeof(bool),
        typeof(NavigationHost), new PropertyMetadata(true));

    private readonly PropertyChangedEventHandler _regionChanged;
    private INavigationRegion? _attached;
    private NavigationEntryId? _presented;
    private bool _showsEmpty;
    private int _refreshPosted;

    /// <summary>Creates a host.</summary>
    public NavigationHost()
    {
        _regionChanged = OnRegionPropertyChanged;
        Focusable = false;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, OnBrowseBack, OnCanBrowseBack));
    }

    /// <summary>Gets or sets the region to present.</summary>
    public INavigationRegion? Region
    {
        get => (INavigationRegion?)GetValue(RegionProperty);
        set => SetValue(RegionProperty, value);
    }

    /// <summary>Gets or sets the locator asked first for each entry's view.</summary>
    public INavigationViewLocator? ViewLocator
    {
        get => (INavigationViewLocator?)GetValue(ViewLocatorProperty);
        set => SetValue(ViewLocatorProperty, value);
    }

    /// <summary>Gets or sets the content shown while the region is empty or no region is set.</summary>
    public object? EmptyContent
    {
        get => GetValue(EmptyContentProperty);
        set => SetValue(EmptyContentProperty, value);
    }

    /// <summary>Gets whether the region has a transition in flight.</summary>
    public bool IsTransitioning => (bool)GetValue(IsTransitioningProperty);

    /// <summary>Gets or sets whether <see cref="NavigationCommands.BrowseBack"/> goes back in the region. The default is <see langword="true"/>.</summary>
    public bool HandlesBrowseBack
    {
        get => (bool)GetValue(HandlesBrowseBackProperty);
        set => SetValue(HandlesBrowseBackProperty, value);
    }

    // The entry the host presents, for tests.
    internal NavigationEntryId? PresentedEntry => _presented;

    private void OnRegionChanged()
    {
        if (!IsLoaded) return;
        Detach();
        Attach();
    }

    private void OnEmptyContentChanged()
    {
        if (_showsEmpty) Content = EmptyContent;
    }

    private void Attach()
    {
        var region = Region;
        if (!ReferenceEquals(_attached, region))
        {
            Detach();
            if (region is not null) region.PropertyChanged += _regionChanged;
            _attached = region;
        }
        Refresh();
    }

    private void Detach()
    {
        if (_attached is not null) _attached.PropertyChanged -= _regionChanged;
        _attached = null;
        // Entry ids are unique only within one navigator: the next region starts afresh.
        _presented = null;
    }

    private void OnRegionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _attached) && _attached is not null) return;
        if (Dispatcher.CheckAccess())
        {
            Refresh();
            return;
        }
        // A navigator on another context: read the state on the dispatcher, once per burst.
        if (Interlocked.Exchange(ref _refreshPosted, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Volatile.Write(ref _refreshPosted, 0);
            if (_attached is not null) Refresh();
        });
    }

    private void Refresh()
    {
        var region = _attached;
        var entry = region?.CurrentEntry;
        SetValue(IsTransitioningPropertyKey, region?.IsTransitioning ?? false);
        CommandManager.InvalidateRequerySuggested();
        if (entry is null)
        {
            _presented = null;
            if (!_showsEmpty || !ReferenceEquals(Content, EmptyContent))
            {
                _showsEmpty = true;
                Content = EmptyContent;
            }
            return;
        }
        if (_presented == entry.Id && !_showsEmpty) return;
        ContentPresenter presenter;
        try { presenter = NavigationPresenters.Create(this, region!, entry, ViewLocator, explicitTemplate: false); }
        catch
        {
            // No stale view: the next change or Loaded tries again.
            _presented = null;
            _showsEmpty = false;
            Content = null;
            throw;
        }
        _showsEmpty = false;
        _presented = entry.Id;
        Content = presenter;
    }

    private void OnCanBrowseBack(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!HandlesBrowseBack || Region is not { } region) return;
        e.CanExecute = region.CanGoBack && !region.IsTransitioning;
        e.Handled = true;
    }

    private void OnBrowseBack(object sender, ExecutedRoutedEventArgs e)
    {
        if (!HandlesBrowseBack || Region is not { } region) return;
        e.Handled = true;
        if (region.CanGoBack && !region.IsTransitioning) _ = region.BackAsync().AsTask();
    }
}
