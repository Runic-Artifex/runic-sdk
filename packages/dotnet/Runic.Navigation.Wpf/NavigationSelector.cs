using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace Runic.Navigation.Wpf;

/// <summary>Adapts a WPF single-selection control to a navigation selection or a shared navigation region.</summary>
/// <remarks>
/// <para>
/// <see cref="SelectionProperty"/> selects among sibling regions that each keep their own stack, such as tabs: the
/// control's items correspond to <see cref="NavigationSelection.Regions"/> by position, and a choice is a guarded
/// <see cref="NavigationSelection.SelectAsync"/>. <see cref="RegionProperty"/> selects existing models with borrowed
/// Replace requests, for models without stacks of their own. A control takes one of the two.
/// </para>
/// <para>
/// A rejected, failed or cancelled request restores the committed selection; a newer selection supersedes an older
/// request. Unloading cancels pending selection and detaches handlers; loading again reads the committed state. A
/// TabControl gets a default NavigationHost content template if no content template or selector was supplied. Do not
/// also navigate from a SelectedItem or SelectedIndex setter.
/// </para>
/// </remarks>
public static class NavigationSelector
{
    /// <summary>Identifies the attached region property.</summary>
    public static readonly DependencyProperty RegionProperty = DependencyProperty.RegisterAttached("Region", typeof(NavigationRegion),
        typeof(NavigationSelector), new PropertyMetadata(null, RegionChanged));

    /// <summary>Identifies the attached selection property.</summary>
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.RegisterAttached("Selection", typeof(NavigationSelection),
        typeof(NavigationSelector), new PropertyMetadata(null, SelectionChanged));

    private static readonly DependencyProperty AdapterProperty = DependencyProperty.RegisterAttached("Adapter", typeof(Adapter),
        typeof(NavigationSelector), new PropertyMetadata(null));

    /// <summary>Gets the region selected by the control.</summary>
    public static NavigationRegion? GetRegion(Selector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return (NavigationRegion?)selector.GetValue(RegionProperty);
    }

    /// <summary>Sets the region selected by the control.</summary>
    /// <exception cref="ArgumentException">The control is not a single-selection Selector.</exception>
    /// <exception cref="InvalidOperationException">The control already has a <see cref="SelectionProperty"/>.</exception>
    public static void SetRegion(Selector selector, NavigationRegion? region)
    {
        ArgumentNullException.ThrowIfNull(selector);
        selector.SetValue(RegionProperty, region);
    }

    /// <summary>Gets the selection whose regions the control's items present, by position.</summary>
    public static NavigationSelection? GetSelection(Selector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return (NavigationSelection?)selector.GetValue(SelectionProperty);
    }

    /// <summary>
    /// Sets the selection whose regions the control's items present, by position: the item at index <c>i</c> selects
    /// <c>Regions[i]</c> through <see cref="NavigationSelection.SelectAsync"/>, and the committed
    /// <see cref="NavigationSelection.SelectedIndex"/> sets the control's <see cref="Selector.SelectedIndex"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The control is not a single-selection Selector.</exception>
    /// <exception cref="InvalidOperationException">The control already has a <see cref="RegionProperty"/>.</exception>
    public static void SetSelection(Selector selector, NavigationSelection? selection)
    {
        ArgumentNullException.ThrowIfNull(selector);
        selector.SetValue(SelectionProperty, selection);
    }

    private static void RegionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        Adapt(target, args, SelectionProperty, selector => new RegionAdapter(selector, (NavigationRegion)args.NewValue));

    private static void SelectionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        Adapt(target, args, RegionProperty, selector => new SelectionAdapter(selector, (NavigationSelection)args.NewValue));

    private static void Adapt(DependencyObject target, DependencyPropertyChangedEventArgs args, DependencyProperty other,
        Func<Selector, Adapter> create)
    {
        var name = args.Property.Name;
        if (target is not Selector selector || target is MultiSelector || target is ListBox { SelectionMode: not SelectionMode.Single })
            throw new ArgumentException($"NavigationSelector.{name} requires a single-selection Selector.", nameof(target));
        var current = (Adapter?)selector.GetValue(AdapterProperty);
        if (args.NewValue is not null && selector.GetValue(other) is not null)
            throw new InvalidOperationException(
                $"NavigationSelector.{name} can't be set on a control that has NavigationSelector.{other.Name}. Use Selection for regions that "
                + "keep their own stacks, such as tabs, and Region for borrowed models without stacks.");
        // Clearing a value that was rejected above keeps the adapter of the other property.
        if (current is not null && current.Property != args.Property) return;
        current?.Dispose();
        selector.SetValue(AdapterProperty, args.NewValue is null ? null : create(selector));
    }

    // Test seam: the latest selection request started by the control's adapter.
    internal static Task SelectionCompletion(Selector selector) =>
        ((Adapter?)selector.GetValue(AdapterProperty))?.Selection ?? Task.CompletedTask;

    private static DataTemplate CreateTabTemplate(PropertyPath region)
    {
        var host = new FrameworkElementFactory(typeof(NavigationHost));
        host.SetBinding(NavigationHost.RegionProperty, new Binding
        {
            Path = region,
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(TabControl), 1),
        });
        var template = new DataTemplate { VisualTree = host };
        template.Seal();
        return template;
    }

    // Selects a borrowed model with a guarded Replace of the region (NavigationSelector.Region).
    private sealed class RegionAdapter : Adapter
    {
        private readonly NavigationRegion _region;

        public RegionAdapter(Selector selector, NavigationRegion region) : base(selector, new PropertyPath("(0)", RegionProperty))
        {
            _region = region;
            Start();
        }

        public override DependencyProperty Property => RegionProperty;
        protected override INotifyPropertyChanged Source => _region;
        protected override bool IsTransitioning => _region.IsTransitioning;
        protected override IServiceProvider? Services => _region.Navigator.Services;
        protected override object? Chosen => Control.SelectedItem;

        protected override bool ChangesChoice(string? propertyName) => propertyName is null or "" or nameof(NavigationRegion.Current);
        protected override bool IsCommitted(object chosen) => ReferenceEquals(chosen, _region.Current);
        protected override void Show() => Control.SetCurrentValue(Selector.SelectedItemProperty, _region.Current);

        protected override async Task RequestAsync(object chosen, CancellationToken cancellationToken) =>
            await _region.ReplaceBorrowedAsync(chosen, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    // Selects the region at the chosen item's position with a guarded SelectAsync (NavigationSelector.Selection).
    private sealed class SelectionAdapter : Adapter
    {
        private readonly NavigationSelection _selection;

        public SelectionAdapter(Selector selector, NavigationSelection selection)
            : base(selector, new PropertyPath("(0).Selected", SelectionProperty))
        {
            _selection = selection;
            Start();
        }

        public override DependencyProperty Property => SelectionProperty;
        protected override INotifyPropertyChanged Source => _selection;
        protected override bool IsTransitioning => _selection.IsTransitioning;
        protected override IServiceProvider? Services => _selection.Navigator.Services;

        // Items that don't match the regions one to one, such as while an items collection follows a change of Regions,
        // map no choice: the adapter shows the committed selection again.
        protected override object? Chosen =>
            Control.SelectedIndex is >= 0 and var index && Control.Items.Count == _selection.Regions.Count ? index : null;

        protected override bool ChangesChoice(string? propertyName) => propertyName is null or ""
            or nameof(NavigationSelection.SelectedIndex) or nameof(NavigationSelection.Regions);

        // An items collection bound to Regions follows it through a binding, which can run after this handler.
        protected override bool ChangesItems(string? propertyName) => propertyName is null or "" or nameof(NavigationSelection.Regions);

        protected override bool IsCommitted(object chosen) => (int)chosen == _selection.SelectedIndex;
        protected override void Show() => Control.SetCurrentValue(Selector.SelectedIndexProperty, _selection.SelectedIndex);

        protected override async Task RequestAsync(object chosen, CancellationToken cancellationToken)
        {
            // Map the position when the user chooses, before any await: Regions can change later.
            var index = (int)chosen;
            var regions = _selection.Regions;
            if (index >= regions.Count)
                throw new InvalidOperationException($"The control selected item {index}, but {_selection} has {regions.Count} regions.");
            await _selection.SelectAsync(regions[index], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private abstract class Adapter : IDisposable
    {
        protected readonly Selector Control;
        private readonly DataTemplate? _tabTemplate;
        private CancellationTokenSource? _pending;
        private Task? _selection;
        // Completed when the source stops transitioning: the commit of the pending selection is decided.
        private TaskCompletionSource? _decided;
        private long _request;
        private bool _attached;
        private bool _synchronizing;

        internal Task Selection => _selection ?? Task.CompletedTask;

        protected Adapter(Selector selector, PropertyPath tabRegion)
        {
            Control = selector;
            if (selector is TabControl { ContentTemplate: null, ContentTemplateSelector: null } tabs
                && tabs.ReadLocalValue(TabControl.ContentTemplateProperty) == DependencyProperty.UnsetValue)
            {
                _tabTemplate = CreateTabTemplate(tabRegion);
                tabs.SetCurrentValue(TabControl.ContentTemplateProperty, _tabTemplate);
            }
        }

        // The attached property this adapter serves.
        public abstract DependencyProperty Property { get; }

        protected abstract INotifyPropertyChanged Source { get; }
        protected abstract bool IsTransitioning { get; }
        protected abstract IServiceProvider? Services { get; }

        // The control's choice, or null when it maps to nothing.
        protected abstract object? Chosen { get; }

        // Whether a change of the source's property can change the committed choice.
        protected abstract bool ChangesChoice(string? propertyName);

        // Whether a change of the source's property can change the control's items through a binding.
        protected virtual bool ChangesItems(string? propertyName) => false;

        protected abstract bool IsCommitted(object chosen);

        // Shows the committed choice in the control.
        protected abstract void Show();

        protected abstract Task RequestAsync(object chosen, CancellationToken cancellationToken);

        // Called by the derived constructor once its state is set.
        protected void Start()
        {
            Control.Loaded += Loaded;
            Control.Unloaded += Unloaded;
            if (Control.IsLoaded) Attach();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) => Detach();

        private void Attach()
        {
            if (_attached) return;
            _attached = true;
            Source.PropertyChanged += Changed;
            Control.SelectionChanged += Selected;
            Reconcile();
        }

        private void Detach()
        {
            _attached = false;
            Source.PropertyChanged -= Changed;
            Control.SelectionChanged -= Selected;
            _request++;
            var pending = _pending;
            _pending = null;
            CancelPending(pending); // The execution owns disposal of its token source.
        }

        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if ((args.PropertyName is null or "" or nameof(NavigationRegion.IsTransitioning)) && !IsTransitioning)
                Interlocked.Exchange(ref _decided, null)?.TrySetResult();
            var dispatcher = Control.Dispatcher;
            // Show the committed choice again once the bindings that follow the change have updated the items.
            if (ChangesItems(args.PropertyName) && !dispatcher.HasShutdownStarted)
                dispatcher.BeginInvoke(Reconcile, DispatcherPriority.Loaded);
            if (!ChangesChoice(args.PropertyName)) return;
            if (dispatcher.CheckAccess()) Reconcile();
            else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Reconcile);
        }

        private void Reconcile()
        {
            if (!_attached) return;
            _synchronizing = true;
            try { Show(); }
            finally { _synchronizing = false; }
        }

        private void Selected(object sender, SelectionChangedEventArgs args)
        {
            if (!_attached || _synchronizing || !ReferenceEquals(args.OriginalSource, Control)) return;
            var chosen = Chosen;
            // Deselection does not retire content or empty a selection. Only an explicit core request does.
            if (chosen is null) { Reconcile(); return; }
            if (IsCommitted(chosen))
            {
                var request = ++_request;
                CancelPending(_pending);
                _pending = null;
                if (_selection is { } previous) _ = KeepSelectionAsync(chosen, request, previous);
                return;
            }
            StartSelection(chosen);
        }

        private void StartSelection(object chosen)
        {
            var previous = _pending;
            var cancellation = new CancellationTokenSource();
            _pending = cancellation;
            var request = ++_request;
            CancelPending(previous);
            _selection = SelectAsync(chosen, request, cancellation);
        }

        private async Task KeepSelectionAsync(object chosen, long request, Task previous)
        {
            // Cancellation cannot undo a commit that already started. If it wins that race,
            // restore the latest selection through another ordinary guarded core request. Wait only until the previous
            // request's commit is decided, not for its task: that includes the previous tab's arrival hook, which the
            // restoring Replace cancels (W315-020 §1.3). The race needs a navigator on another model context than the
            // dispatcher: this handler and a commit turn on the dispatcher never interleave, so there the cancellation
            // always wins and the previous task ends without an activation.
            await Task.WhenAny(previous, WhenDecided()).ConfigureAwait(false);
            await OnDispatcherAsync(() =>
            {
                if (!_attached || request != _request) return;
                if (!IsCommitted(chosen)) StartSelection(chosen);
                else Reconcile();
            }).ConfigureAwait(false);
        }

        private async Task SelectAsync(object chosen, long request, CancellationTokenSource cancellation)
        {
            try { await RequestAsync(chosen, cancellation.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                WpfNavigationLog.SelectionFailed(WpfNavigationLog.For(Services), error, WpfNavigationLog.ErrorType(error));
            }
            finally
            {
                try
                {
                    await OnDispatcherAsync(() =>
                    {
                        if (request != _request) return;
                        _pending = null;
                        Reconcile();
                    }).ConfigureAwait(false);
                }
                finally
                {
                    // After dispatcher shutdown the reset above is skipped: never leave a disposed source pending.
                    Interlocked.CompareExchange(ref _pending, null, cancellation);
                    cancellation.Dispose();
                }
            }
        }

        // Completes once the source is not transitioning, read when the source raises IsTransitioning.
        private Task WhenDecided()
        {
            if (!IsTransitioning) return Task.CompletedTask;
            var decided = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = Interlocked.Exchange(ref _decided, decided);
            previous?.TrySetResult();
            // The source may have stopped transitioning between the read and the publication.
            if (!IsTransitioning) Interlocked.Exchange(ref _decided, null)?.TrySetResult();
            return decided.Task;
        }

        // The execution can dispose its source on another thread just after a UI handler read it.
        private static void CancelPending(CancellationTokenSource? pending)
        {
            try { pending?.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task OnDispatcherAsync(Action action)
        {
            var dispatcher = Control.Dispatcher;
            if (dispatcher.HasShutdownStarted) return;
            try
            {
                if (dispatcher.CheckAccess()) action();
                else await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (dispatcher.HasShutdownStarted) { }
            catch (Exception error)
            {
                WpfNavigationLog.SelectionFailed(WpfNavigationLog.For(Services), error, WpfNavigationLog.ErrorType(error));
            }
        }

        public void Dispose()
        {
            Detach();
            Control.Loaded -= Loaded;
            Control.Unloaded -= Unloaded;
            if (Control is TabControl tabs && _tabTemplate is not null && ReferenceEquals(tabs.ContentTemplate, _tabTemplate))
                tabs.ClearValue(TabControl.ContentTemplateProperty);
        }
    }
}
