// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive.
using System.ComponentModel;
using System.Diagnostics;
#if SYSTEM_REACTIVE
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Reactive;
#else
using ReactiveUI;
using ReactiveUI.Primitives;
#endif
using Runic.Application.Views;

#if SYSTEM_REACTIVE
namespace Runic.Application.Views.ReactiveUI.Reactive;
#else
namespace Runic.Application.Views.ReactiveUI;
#endif

/// <summary>
/// A logical Runic View with ReactiveUI's typed ViewModel and mount activation.
/// Each instance owns one activation lease, so a presentation-aware content
/// session can resolve several Views for the same ViewModel safely.
/// </summary>
public abstract class ReactiveRunicView<TViewModel> : RunicView<TViewModel>,
    IViewFor<TViewModel>, IRunicViewLifetime, IRunicWebMountLifetime where TViewModel : class
{
    private readonly ReactiveMount<TViewModel> _mount = new();

    public override TViewModel? DataContext
    {
        get => base.DataContext;
        set
        {
            base.DataContext = value;
            _mount.Bind(value);
        }
    }

    public TViewModel? ViewModel { get => DataContext; set => DataContext = value; }
    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value is null ? null : value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }

    public virtual void OnAttached() { }
    public virtual void OnDetached() => _mount.Unmount();
    public void OnWebMounted() => _mount.Mount();
    public void OnWebUnmounted() => _mount.Unmount();
}

/// <summary>
/// A native Runic Window with ReactiveUI's typed ViewModel and mount activation.
/// Each instance owns one activation lease, so a presentation-aware content
/// session can resolve several Views for the same ViewModel safely.
/// </summary>
public abstract class ReactiveRunicWindow<TViewModel> : RunicWindow<TViewModel>,
    IViewFor<TViewModel>, IRunicViewLifetime, IRunicWebMountLifetime where TViewModel : class
{
    private readonly ReactiveMount<TViewModel> _mount = new();

    protected ReactiveRunicWindow(TViewModel dataContext) : base(dataContext) => _mount.Bind(dataContext);

    public override TViewModel? DataContext
    {
        get => base.DataContext;
        set
        {
            base.DataContext = value;
            _mount.Bind(value);
        }
    }

    public TViewModel? ViewModel { get => DataContext; set => DataContext = value; }
    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value is null ? null : value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }

    public virtual void OnAttached() { }
    public virtual void OnDetached() => _mount.Unmount();
    public void OnWebMounted() => _mount.Mount();
    public void OnWebUnmounted() => _mount.Unmount();
}

internal sealed class ReactiveMount<TViewModel> where TViewModel : class
{
    private TViewModel? _viewModel;
    private IDisposable? _activation;
    private bool _mounted;

    public void Bind(TViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        _activation?.Dispose();
        _activation = null;
        _viewModel = viewModel;
        if (_mounted) Activate();
    }

    public void Mount()
    {
        if (_mounted) return;
        // A failed activation leaves the View unmounted, so a later mount can
        // retry instead of being ignored.
        Activate();
        _mounted = true;
    }

    public void Unmount()
    {
        if (!_mounted) return;
        _mounted = false;
        _activation?.Dispose();
        _activation = null;
    }

    private void Activate()
    {
        if (_viewModel is IActivatableViewModel activatable)
            _activation = activatable.Activator.Activate();
    }
}

/// <summary>Projects one ReactiveUI router into an observable content property.</summary>
public sealed class ReactiveRoutedRegion<TViewModel> : INotifyPropertyChanged, IDisposable
    where TViewModel : class
{
    private readonly IDisposable _subscription;
    private TViewModel? _current;

    public ReactiveRoutedRegion(RoutingState router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _subscription = router.CurrentViewModel.Subscribe(new RouteObserver(this));
    }

    private void OnRoute(object? viewModel)
    {
        // Throwing here would tear down the router's notification for every
        // observer. An incompatible route instead presents no content.
        if (viewModel is not null && viewModel is not TViewModel)
            Trace.TraceError($"Route {viewModel.GetType().Name} is not a {typeof(TViewModel).Name}; the region presents no content.");
        var current = viewModel as TViewModel;
        if (ReferenceEquals(_current, current)) return;
        _current = current;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }

    private sealed class RouteObserver(ReactiveRoutedRegion<TViewModel> owner) : IObserver<IRoutableViewModel?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => Trace.TraceError($"A routed region's router failed: {error}");
        public void OnNext(IRoutableViewModel? value) => owner.OnRoute(value);
    }

    public TViewModel? Current => _current;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Dispose() => _subscription.Dispose();
}

/// <summary>Adapts ReactiveUI's AOT-safe typed view lookup to Runic presentation.</summary>
public sealed class ReactiveRunicViewLocator(IViewLocator locator) : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class => Locate<TView, TViewModel>(null);

    public TView Locate<TView, TViewModel>(string? contract)
        where TView : class, IRunicView
        where TViewModel : class =>
        locator.ResolveView<TViewModel>(contract) as TView
        ?? throw new InvalidOperationException($"No {typeof(TView).Name} is registered for {typeof(TViewModel).Name} with contract '{contract ?? "default"}'.");
}
