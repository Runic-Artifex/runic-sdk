using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Runic.Navigation.Wpf;

/// <summary>Presents the current entry of a navigation region.</summary>
/// <remarks>
/// <para>
/// Each entry gets a new <see cref="ContentPresenter"/> and a new view, also when two entries have the same type or the
/// same borrowed content, so no view state leaks from one entry to the next. By default a retained entry gets a new view
/// when it becomes current again: keep state that must survive in the ViewModel, or set <see cref="RetainViews"/> to keep
/// the view of each retained entry and show it again. <see cref="NavigationRequestOptions.RetainView"/> decides it for
/// one entry instead.
/// </para>
/// <para>
/// The view comes from <see cref="ViewLocator"/>, then from an <see cref="INavigationViewLocator"/> registered in the
/// navigator's services, then from the implicit <see cref="DataTemplate"/> keyed by the content type or a base type.
/// The host follows the region while it is loaded, and never changes entries when it is unloaded.
/// <see cref="NavigationCommands.BrowseBack"/> goes back when the region can go back and isn't transitioning, nor busy
/// when it admits requests with <see cref="NavigationAdmission.RejectWhileBusy"/>.
/// </para>
/// <para>
/// The host sets its own <see cref="ContentControl.Content"/>: don't set <c>Content</c>, <c>ContentTemplate</c> or
/// <c>ContentTemplateSelector</c> on it. Present entries with implicit <see cref="DataTemplate"/>s or a locator.
/// </para>
/// <para>
/// With <see cref="GuardWindowClose"/>, closing the host's window clears the region first, so the departure guards that
/// navigation asks also decide whether the window closes. The window keeps guarding the region while the host is
/// unloaded, as on an unselected tab.
/// </para>
/// </remarks>
public class NavigationHost : ContentControl
{
    /// <summary>Identifies the <see cref="Region"/> dependency property.</summary>
    public static readonly DependencyProperty RegionProperty = DependencyProperty.Register(nameof(Region), typeof(NavigationRegion),
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

    private static readonly DependencyPropertyKey IsBusyPropertyKey = DependencyProperty.RegisterReadOnly(nameof(IsBusy),
        typeof(bool), typeof(NavigationHost), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="IsBusy"/> dependency property.</summary>
    public static readonly DependencyProperty IsBusyProperty = IsBusyPropertyKey.DependencyProperty;

    /// <summary>Identifies the <see cref="HandlesBrowseBack"/> dependency property.</summary>
    public static readonly DependencyProperty HandlesBrowseBackProperty = DependencyProperty.Register(nameof(HandlesBrowseBack), typeof(bool),
        typeof(NavigationHost), new PropertyMetadata(true));

    /// <summary>Identifies the <see cref="GuardWindowClose"/> dependency property.</summary>
    public static readonly DependencyProperty GuardWindowCloseProperty = DependencyProperty.Register(nameof(GuardWindowClose), typeof(bool),
        typeof(NavigationHost), new PropertyMetadata(false, static (d, _) => ((NavigationHost)d).OnGuardWindowCloseChanged()));

    /// <summary>Identifies the <see cref="RetainViews"/> dependency property, which is inherited.</summary>
    public static readonly DependencyProperty RetainViewsProperty = DependencyProperty.RegisterAttached(nameof(RetainViews), typeof(bool),
        typeof(NavigationHost), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    private readonly PropertyChangedEventHandler _regionChanged;
    private NavigationRegion? _attached;
    // The region whose entries _presented and _kept refer to; entry ids are unique only within one navigator.
    private NavigationRegion? _presentedRegion;
    private NavigationEntryId? _presented;
    private ContentPresenter? _presenter;
    // With RetainViews: the presenters of retained entries this host presented, by entry id.
    private Dictionary<NavigationEntryId, KeptView>? _kept;
    private bool _showsEmpty;
    private int _refreshPosted;

    /// <summary>Creates a host.</summary>
    public NavigationHost()
    {
        _regionChanged = OnRegionPropertyChanged;
        Focusable = false;
        Loaded += (_, _) =>
        {
            Attach();
            GuardWindow();
        };
        // The window keeps guarding the region: an unloaded host's region, as on an unselected tab, can still hold a draft.
        Unloaded += (_, _) => Detach();
        CommandBindings.Add(new CommandBinding(NavigationCommands.BrowseBack, OnBrowseBack, OnCanBrowseBack));
    }

    /// <summary>Gets or sets the region to present.</summary>
    public NavigationRegion? Region
    {
        get => (NavigationRegion?)GetValue(RegionProperty);
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

    /// <summary>Gets whether a request of the region has not completed (<see cref="NavigationRegion.IsBusy"/>).</summary>
    public bool IsBusy => (bool)GetValue(IsBusyProperty);

    /// <summary>Gets or sets whether <see cref="NavigationCommands.BrowseBack"/> goes back in the region. The default is <see langword="true"/>.</summary>
    public bool HandlesBrowseBack
    {
        get => (bool)GetValue(HandlesBrowseBackProperty);
        set => SetValue(HandlesBrowseBackProperty, value);
    }

    /// <summary>
    /// Gets or sets whether closing the host's window first clears the region, asking the departure guards of every entry
    /// and of the child regions they own. The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When the host loads with this on, its window starts guarding the region. The window keeps guarding it after the
    /// host unloads or is discarded, as on an unselected tab of a <see cref="TabControl"/>, until the region closes, the
    /// window closes, a guarding host loads with the region in another window, or a host of the region turns this off.
    /// The window holds no host, and holds its regions weakly.
    /// </para>
    /// <para>
    /// While a guarded region has entries, the window's close is cancelled and the regions are cleared one after another,
    /// in the order their hosts first loaded; the window closes when every clear commits. A guard that rejects, a
    /// cancelled clear or a superseded clear keeps the window open with its entries and asks no further regions, so a
    /// guard can confirm through a <see cref="NavigationDialogHost"/> in the same window. A close while a clear is still
    /// in flight joins it; a close after the clearing region stops transitioning starts a new check. A close that another
    /// <see cref="Window.Closing"/> handler already cancelled is left alone, and a window whose regions are empty closes
    /// at once.
    /// </para>
    /// <para>
    /// WPF ignores cancellation when <see cref="Application.Shutdown()"/> closes the windows, and doesn't raise
    /// <see cref="Window.Closing"/> when the Windows session ends: those paths don't ask the guards. Close the main window
    /// instead of calling <c>Shutdown</c> to give the guards a say, and handle <see cref="Application.SessionEnding"/>
    /// if a draft must survive a log-off.
    /// </para>
    /// </remarks>
    public bool GuardWindowClose
    {
        get => (bool)GetValue(GuardWindowCloseProperty);
        set => SetValue(GuardWindowCloseProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the host keeps the view of each retained entry it presented and shows it again when the entry
    /// becomes current again, instead of asking for a new view. The default is <see langword="false"/>. The property is
    /// inherited: set on a window or a container, it applies to every host below it, including the hosts in kept views.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The host keeps the entry's <see cref="ContentPresenter"/> with its view and <see cref="FrameworkElement.DataContext"/>,
    /// so state that lives only in the view, such as scroll positions, a text box's caret and selection, expanded tree nodes
    /// or a grid's sort order and column widths, survives Back and BackTo. The locator isn't asked again for a kept view. A
    /// view is never shown for two entries: an instance pushed twice gets two entries and two views. When keyboard focus was
    /// inside the view as it left, the host focuses that element again once the view has loaded again, unless focus has
    /// moved elsewhere outside the host meanwhile; in an inactive window it only sets the element as the window's focused
    /// element.
    /// </para>
    /// <para>
    /// An entry pushed with <see cref="NavigationRequestOptions.RetainView"/> set (<see cref="NavigationEntry.RetainView"/>)
    /// uses that value instead of this property: <see langword="true"/> keeps its view also when this property is
    /// <see langword="false"/>, and <see langword="false"/> never keeps it. The rules below apply to each entry with the
    /// value that decides for it.
    /// </para>
    /// <para>
    /// The host drops a kept view when it sees that its entry has left the region's stack (the entry retired, also while
    /// its content's disposal still waits for its operations), when the region empties, when <see cref="Region"/> changes,
    /// and when it presents an entry while the value that decides for the kept view's entry is <see langword="false"/>.
    /// It doesn't dispose views. An unloaded host doesn't follow its region: it keeps its views,
    /// also its current one, and when it loads again with the same region it shows the same view if the current entry is
    /// unchanged, and drops the views of entries that retired meanwhile. So the hosts inside a kept view keep their views too.
    /// A host that is never loaded again is collected with its views.
    /// </para>
    /// <para>
    /// A kept view leaves and re-enters the visual tree, so its <see cref="FrameworkElement.Unloaded"/> and
    /// <see cref="FrameworkElement.Loaded"/> events are raised again. Each retained entry keeps its visual tree alive, and a
    /// view built by a locator keeps the entry-scoped services it was given, until the host drops it. An inner host can set
    /// the property to <see langword="false"/> to opt out.
    /// </para>
    /// </remarks>
    public bool RetainViews
    {
        get => (bool)GetValue(RetainViewsProperty);
        set => SetValue(RetainViewsProperty, value);
    }

    /// <summary>Gets the inherited <see cref="RetainViews"/> value of an element.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Whether the hosts at and below the element keep the views of retained entries.</returns>
    public static bool GetRetainViews(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(RetainViewsProperty);
    }

    /// <summary>Sets <see cref="RetainViews"/> for the hosts at and below an element, such as a window.</summary>
    /// <param name="element">The element.</param>
    /// <param name="value">Whether the hosts keep the views of retained entries.</param>
    public static void SetRetainViews(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(RetainViewsProperty, value);
    }

    // The entry the host presents, for tests.
    internal NavigationEntryId? PresentedEntry => _presented;

    // The number of kept views of retained entries, for tests.
    internal int KeptViewCount => _kept?.Count ?? 0;

    private void OnRegionChanged()
    {
        if (!IsLoaded) return;
        // The previous region stays guarded: its entries live on, and a template rebinds a host it's about to discard.
        GuardWindow();
        Detach();
        Attach();
    }

    private void OnGuardWindowCloseChanged()
    {
        if (GuardWindowClose) GuardWindow();
        else if (Region is { } region) WindowCloseGuard.Remove(region);
    }

    private void GuardWindow()
    {
        if (IsLoaded && GuardWindowClose && Region is { } region && Window.GetWindow(this) is { } window) WindowCloseGuard.Add(window, region);
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
        // Entry ids are unique only within one navigator: another region starts afresh. A host that loads again builds a
        // new view also for the same entry unless that entry's view is kept, and keeps only the views it may keep.
        if (!ReferenceEquals(_presentedRegion, region)) ForgetPresentations();
        else if (region is not null) KeepOnPresent(region, RetainViews);
        _presentedRegion = region;
        Refresh();
    }

    // Unloading keeps the presentations: with RetainViews, a host that loads again with the same region and current entry
    // shows the same view (Attach decides).
    private void Detach()
    {
        if (_attached is not null) _attached.PropertyChanged -= _regionChanged;
        _attached = null;
    }

    private void ForgetPresentations()
    {
        _presented = null;
        _presenter = null;
        _kept = null;
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
        SetValue(IsBusyPropertyKey, region?.IsBusy ?? false);
        CommandManager.InvalidateRequerySuggested();
        if (entry is null)
        {
            ForgetPresentations();
            if (!_showsEmpty || !ReferenceEquals(Content, EmptyContent))
            {
                _showsEmpty = true;
                Content = EmptyContent;
            }
            return;
        }
        // RetainViews is read when the host presents, never in a change callback: an inherited value, or one from an
        // ancestor's style, reverts to the default while a kept view that contains this host is out of the visual tree,
        // before this host unloads. Reacting to that would drop the views the option is meant to keep.
        // A value set on the entry's request (NavigationEntry.RetainView) decides for that entry instead.
        var retain = RetainViews;
        if (_presented == entry.Id && !_showsEmpty)
        {
            KeepOnly(region!, retain);
            return;
        }
        // The view that leaves stays with its entry while that entry is retained.
        if (_presenter is { } leaving && _presented is { } leavingId && leavingId != entry.Id
            && RetainedEntry(region!, leavingId) is { } leavingEntry && Keeps(leavingEntry, retain))
            (_kept ??= [])[leavingId] = new KeptView(leaving, FocusedWithin(leaving));
        _presenter = null;
        KeptView? kept = null;
        if (Keeps(entry, retain) && _kept is not null && _kept.Remove(entry.Id, out var found)) kept = found;
        ContentPresenter presenter;
        if (kept is not null) presenter = kept.Presenter;
        else
        {
            // A view that may be kept gets its template set on its presenter, so leaving and re-entering the visual tree
            // never makes the presenter look up its implicit template again and build a new view.
            try { presenter = NavigationPresenters.Create(this, region!, entry, ViewLocator, explicitTemplate: Keeps(entry, retain)); }
            catch
            {
                // No stale view: the next change or Loaded tries again.
                _presented = null;
                _showsEmpty = false;
                Content = null;
                KeepOnly(region!, retain);
                throw;
            }
        }
        _showsEmpty = false;
        _presented = entry.Id;
        _presenter = presenter;
        Content = presenter;
        KeepOnly(region!, retain);
        if (kept?.Focused is { } focused) RestoreFocus(presenter, focused);
    }

    // Drops the kept views of entries that are no longer retained in the region, or whose view is no longer kept: without
    // RetainViews, only the entries pushed with RetainView = true keep theirs.
    private void KeepOnly(NavigationRegion region, bool retain)
    {
        if (_kept is null) return;
        HashSet<NavigationEntryId> retained = [.. region.History.Where(entry => Keeps(entry, retain)).Select(entry => entry.Id)];
        foreach (var id in _kept.Keys.Where(id => !retained.Contains(id)).ToList()) _kept.Remove(id);
        if (_kept.Count == 0) _kept = null;
    }

    // On Loaded with the same region: the presented view stays when its entry keeps its view (Refresh shows it again or
    // keeps it for its retained entry). Refresh then takes the current entry's kept view and prunes the others.
    private void KeepOnPresent(NavigationRegion region, bool retain)
    {
        if (_presented is { } presented && (FindEntry(region, presented) is not { } entry || !Keeps(entry, retain)))
        {
            _presented = null;
            _presenter = null;
        }
    }

    private static NavigationEntry? FindEntry(NavigationRegion region, NavigationEntryId id) =>
        region.CurrentEntry is { } current && current.Id == id ? current : RetainedEntry(region, id);

    // Whether the host keeps the entry's view while the entry is retained.
    private static bool Keeps(NavigationEntry entry, bool retain) => entry.RetainView ?? retain;

    private static NavigationEntry? RetainedEntry(NavigationRegion region, NavigationEntryId id)
    {
        foreach (var entry in region.History)
            if (entry.Id == id) return entry;
        return null;
    }

    // The element inside the leaving view that has keyboard focus, or else the logical focus of its focus scope, as in
    // an inactive window.
    private static UIElement? FocusedWithin(ContentPresenter presenter)
    {
        if (Keyboard.FocusedElement is UIElement focused && presenter.IsAncestorOf(focused)) return focused;
        return FocusManager.GetFocusScope(presenter) is { } scope && FocusManager.GetFocusedElement(scope) is UIElement logical
            && presenter.IsAncestorOf(logical) ? logical : null;
    }

    // Best effort, once the kept view has loaded again: focus the element that had focus when the view left, unless focus
    // moved meanwhile to something outside this host. Never activates a window: in an inactive window only the logical
    // focus of the focus scope is set, which the window restores when it is activated.
    private void RestoreFocus(ContentPresenter presenter, UIElement element)
    {
        if (presenter.IsLoaded)
        {
            Restore();
            return;
        }
        presenter.Loaded += OnLoaded;

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            presenter.Loaded -= OnLoaded;
            Restore();
        }

        void Restore()
        {
            if (!ReferenceEquals(_presenter, presenter) || !presenter.IsAncestorOf(element) || !element.Focusable || !element.IsEnabled)
                return;
            var window = Window.GetWindow(this);
            if (window is { IsActive: true })
            {
                if (Free(Keyboard.FocusedElement as DependencyObject, window)) element.Focus();
                return;
            }
            if (FocusManager.GetFocusScope(element) is { } scope && Free(FocusManager.GetFocusedElement(scope) as DependencyObject, window))
                FocusManager.SetFocusedElement(scope, element);
        }
    }

    // Focus is nowhere, on the window itself, or inside this host: restoring doesn't take it from anything else.
    private bool Free(DependencyObject? focused, Window? window) =>
        focused is null || ReferenceEquals(focused, window) || (focused is Visual visual && IsAncestorOf(visual));

    private void OnCanBrowseBack(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!HandlesBrowseBack || Region is not { } region) return;
        e.CanExecute = CanGoBack(region);
        e.Handled = true;
    }

    private void OnBrowseBack(object sender, ExecutedRoutedEventArgs e)
    {
        if (!HandlesBrowseBack || Region is not { } region) return;
        e.Handled = true;
        if (CanGoBack(region)) _ = region.BackAsync().AsTask();
    }

    private sealed record KeptView(ContentPresenter Presenter, UIElement? Focused);

    // A region that rejects requests while busy would reject this Back as Busy.
    private static bool CanGoBack(NavigationRegion region) =>
        region.CanGoBack && !region.IsTransitioning && !(region.Admission == NavigationAdmission.RejectWhileBusy && region.IsBusy);
}
