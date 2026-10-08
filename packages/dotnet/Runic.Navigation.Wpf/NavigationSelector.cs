using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;

namespace Runic.Navigation.Wpf;

/// <summary>Adapts a WPF single-selection control to a shared navigation region.</summary>
/// <remarks>
/// Select existing models with borrowed Replace requests. A rejected request restores the region's
/// current selection; a newer selection supersedes an older request. Unloading cancels pending
/// selection and detaches region handlers. A TabControl gets a default NavigationHost content template
/// if no content template or selector was supplied. Do not also navigate from a SelectedItem setter.
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public static class NavigationSelector
{
    /// <summary>Identifies the attached region property.</summary>
    public static readonly DependencyProperty RegionProperty = DependencyProperty.RegisterAttached("Region", typeof(INavigationRegion),
        typeof(NavigationSelector), new PropertyMetadata(null, RegionChanged));

    private static readonly DependencyProperty AdapterProperty = DependencyProperty.RegisterAttached("Adapter", typeof(Adapter),
        typeof(NavigationSelector), new PropertyMetadata(null));

    /// <summary>Gets the region selected by the control.</summary>
    public static INavigationRegion? GetRegion(Selector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return (INavigationRegion?)selector.GetValue(RegionProperty);
    }

    /// <summary>Sets the region selected by the control.</summary>
    public static void SetRegion(Selector selector, INavigationRegion? region)
    {
        ArgumentNullException.ThrowIfNull(selector);
        selector.SetValue(RegionProperty, region);
    }

    private static void RegionChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Selector selector || target is MultiSelector || target is ListBox { SelectionMode: not SelectionMode.Single })
            throw new ArgumentException("NavigationSelector.Region requires a single-selection Selector.", nameof(target));
        ((Adapter?)selector.GetValue(AdapterProperty))?.Dispose();
        selector.SetValue(AdapterProperty, args.NewValue is INavigationRegion region ? new Adapter(selector, region) : null);
    }

    private static DataTemplate CreateTabTemplate()
    {
        var host = new FrameworkElementFactory(typeof(NavigationHost));
        host.SetBinding(NavigationHost.RegionProperty, new Binding
        {
            Path = new PropertyPath("(0)", RegionProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(TabControl), 1),
        });
        var template = new DataTemplate { VisualTree = host };
        template.Seal();
        return template;
    }

    private sealed class Adapter : IDisposable
    {
        private readonly Selector _selector;
        private readonly INavigationRegion _region;
        private readonly DataTemplate? _tabTemplate;
        private CancellationTokenSource? _pending;
        private Task? _selection;
        private long _request;
        private bool _attached;
        private bool _synchronizing;

        public Adapter(Selector selector, INavigationRegion region)
        {
            _selector = selector;
            _region = region;
            if (selector is TabControl { ContentTemplate: null, ContentTemplateSelector: null } tabs
                && tabs.ReadLocalValue(TabControl.ContentTemplateProperty) == DependencyProperty.UnsetValue)
            {
                _tabTemplate = CreateTabTemplate();
                tabs.SetCurrentValue(TabControl.ContentTemplateProperty, _tabTemplate);
            }
            selector.Loaded += Loaded;
            selector.Unloaded += Unloaded;
            if (selector.IsLoaded) Attach();
        }

        private void Loaded(object sender, RoutedEventArgs args) => Attach();
        private void Unloaded(object sender, RoutedEventArgs args) => Detach();

        private void Attach()
        {
            if (_attached) return;
            _attached = true;
            _region.PropertyChanged += Changed;
            _selector.SelectionChanged += Selected;
            Reconcile();
        }

        private void Detach()
        {
            _attached = false;
            _region.PropertyChanged -= Changed;
            _selector.SelectionChanged -= Selected;
            _request++;
            var pending = _pending;
            _pending = null;
            pending?.Cancel(); // The execution owns disposal of its token source.
        }

        private void Changed(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (null or "" or nameof(INavigationRegion.Current))) return;
            if (_selector.Dispatcher.CheckAccess()) Reconcile();
            else if (!_selector.Dispatcher.HasShutdownStarted) _selector.Dispatcher.BeginInvoke(Reconcile);
        }

        private void Reconcile()
        {
            if (!_attached) return;
            _synchronizing = true;
            try { _selector.SetCurrentValue(Selector.SelectedItemProperty, _region.Current); }
            finally { _synchronizing = false; }
        }

        private void Selected(object sender, SelectionChangedEventArgs args)
        {
            if (!_attached || _synchronizing || !ReferenceEquals(args.OriginalSource, _selector)) return;
            var selected = _selector.SelectedItem;
            // Deselection does not retire content. Only an explicit core Clear empties the region.
            if (selected is null) { Reconcile(); return; }
            if (ReferenceEquals(selected, _region.Current))
            {
                var request = ++_request;
                _pending?.Cancel();
                _pending = null;
                if (_selection is { } previous) _ = KeepSelectionAsync(selected, request, previous);
                return;
            }
            StartSelection(selected);
        }

        private void StartSelection(object selected)
        {
            var previous = _pending;
            var cancellation = new CancellationTokenSource();
            _pending = cancellation;
            var request = ++_request;
            previous?.Cancel();
            _selection = SelectAsync(selected, request, cancellation);
        }

        private async Task KeepSelectionAsync(object selected, long request, Task previous)
        {
            // Cancellation cannot undo a commit that already started. If it wins that race,
            // restore the latest selection through another ordinary guarded core request.
            await previous.ConfigureAwait(false);
            await OnDispatcherAsync(() =>
            {
                if (!_attached || request != _request) return;
                if (!ReferenceEquals(_region.Current, selected)) StartSelection(selected);
                else Reconcile();
            }).ConfigureAwait(false);
        }

        private async Task SelectAsync(object selected, long request, CancellationTokenSource cancellation)
        {
            try { await _region.ReplaceBorrowedAsync(selected, cancellationToken: cancellation.Token).ConfigureAwait(false); }
            catch (Exception error)
            {
                WpfNavigationLog.SelectionFailed(WpfNavigationLog.For(_region.Navigator.Services), error, WpfNavigationLog.ErrorType(error));
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
                finally { cancellation.Dispose(); }
            }
        }

        private async Task OnDispatcherAsync(Action action)
        {
            var dispatcher = _selector.Dispatcher;
            if (dispatcher.HasShutdownStarted) return;
            try
            {
                if (dispatcher.CheckAccess()) action();
                else await dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (dispatcher.HasShutdownStarted) { }
            catch (Exception error)
            {
                WpfNavigationLog.SelectionFailed(WpfNavigationLog.For(_region.Navigator.Services), error, WpfNavigationLog.ErrorType(error));
            }
        }

        public void Dispose()
        {
            Detach();
            _selector.Loaded -= Loaded;
            _selector.Unloaded -= Unloaded;
            if (_selector is TabControl tabs && _tabTemplate is not null && ReferenceEquals(tabs.ContentTemplate, _tabTemplate))
                tabs.ClearValue(TabControl.ContentTemplateProperty);
        }
    }
}
