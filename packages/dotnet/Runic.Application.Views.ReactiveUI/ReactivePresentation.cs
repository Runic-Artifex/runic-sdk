// Shared by Runic.Application.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.ReactiveUI.Reactive.
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
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

    /// <summary>The typed ViewModel; assigning it rebinds ReactiveUI activation.</summary>
    public override TViewModel? DataContext
    {
        get => base.DataContext;
        set
        {
            base.DataContext = value;
            _mount.Bind(value);
        }
    }

    /// <summary>The same as <see cref="DataContext"/>, as required by <see cref="IViewFor{T}"/>.</summary>
    public TViewModel? ViewModel { get => DataContext; set => DataContext = value; }
    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value is null ? null : value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }

    /// <inheritdoc />
    public virtual void OnAttached() { }
    /// <summary>Called when the view stops being presented; deactivates the ViewModel.</summary>
    public virtual void OnDetached() => _mount.Unmount();
    /// <summary>Activates an <see cref="IActivatableViewModel"/> when the web component mounts.</summary>
    public void OnWebMounted() => _mount.Mount();
    /// <summary>Deactivates the ViewModel when the web component unmounts.</summary>
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

    /// <summary>Creates a window for its root ViewModel.</summary>
    protected ReactiveRunicWindow(TViewModel dataContext) : base(dataContext) => _mount.Bind(dataContext);

    /// <summary>The typed ViewModel; assigning it rebinds ReactiveUI activation.</summary>
    public override TViewModel? DataContext
    {
        get => base.DataContext;
        set
        {
            base.DataContext = value;
            _mount.Bind(value);
        }
    }

    /// <summary>The same as <see cref="DataContext"/>, as required by <see cref="IViewFor{T}"/>.</summary>
    public TViewModel? ViewModel { get => DataContext; set => DataContext = value; }
    object? IViewFor.ViewModel
    {
        get => ViewModel;
        set => ViewModel = value is null ? null : value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }

    /// <inheritdoc />
    public virtual void OnAttached() { }
    /// <summary>Called when the view stops being presented; deactivates the ViewModel.</summary>
    public virtual void OnDetached() => _mount.Unmount();
    /// <summary>Activates an <see cref="IActivatableViewModel"/> when the web component mounts.</summary>
    public void OnWebMounted() => _mount.Mount();
    /// <summary>Deactivates the ViewModel when the web component unmounts.</summary>
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
/// <remarks>
/// An incompatible route or a failed router is logged in the category
/// <see cref="RunicViewsTelemetry.LogCategory"/> (events 1040 and 1041), or written to
/// <see cref="Trace"/> when the region has no logger factory.
/// </remarks>
public sealed class ReactiveRoutedRegion<TViewModel> : INotifyPropertyChanged, IDisposable
    where TViewModel : class
{
    private readonly IDisposable _subscription;
    private readonly ILogger? _logger;
    private TViewModel? _current;

    /// <summary>Observes <paramref name="router"/>'s current ViewModel and writes failures to <see cref="Trace"/>.</summary>
    public ReactiveRoutedRegion(RoutingState router) : this(router, loggerFactory: null)
    {
    }

    /// <summary>Observes <paramref name="router"/>'s current ViewModel.</summary>
    /// <param name="router">The router to project.</param>
    /// <param name="loggerFactory">
    /// Creates the region's logger, or <see langword="null"/> for <see cref="Trace"/> output.
    /// </param>
    public ReactiveRoutedRegion(RoutingState router, ILoggerFactory? loggerFactory)
        : this((router ?? throw new ArgumentNullException(nameof(router))).CurrentViewModel, loggerFactory)
    {
    }

    internal ReactiveRoutedRegion(IObservable<IRoutableViewModel?> currentViewModel, ILoggerFactory? loggerFactory)
    {
        _logger = loggerFactory?.CreateLogger(RunicViewsTelemetry.LogCategory);
        _subscription = currentViewModel.Subscribe(new RouteObserver(this));
    }

    private void OnRoute(object? viewModel)
    {
        // Throwing here would tear down the router's notification for every
        // observer. An incompatible route instead presents no content.
        if (viewModel is not null && viewModel is not TViewModel)
        {
            var region = typeof(TViewModel).Name;
            var model = viewModel.GetType().Name;
            if (_logger is null)
                Trace.TraceError($"A routed region for {region} received {model}, which it cannot present; the region presents no content.");
            else
                ReactiveLog.RouteIncompatible(_logger, region, model);
        }
        var current = viewModel as TViewModel;
        if (ReferenceEquals(_current, current)) return;
        _current = current;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }

    private void OnRouterFailed(Exception error)
    {
        var region = typeof(TViewModel).Name;
        var errorType = error.GetType().FullName ?? error.GetType().Name;
        // D-12: the entry carries the exception in every environment.
        if (_logger is null)
            Trace.TraceError($"The router of a routed region for {region} failed with {errorType}; the region keeps its last content. {error}");
        else
            ReactiveLog.RouterFailed(_logger, error, region, errorType);
    }

    private sealed class RouteObserver(ReactiveRoutedRegion<TViewModel> owner) : IObserver<IRoutableViewModel?>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => owner.OnRouterFailed(error);
        public void OnNext(IRoutableViewModel? value) => owner.OnRoute(value);
    }

    /// <summary>The routed ViewModel, or <see langword="null"/> when the route is empty or incompatible.</summary>
    public TViewModel? Current => _current;
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Stops observing the router.</summary>
    public void Dispose() => _subscription.Dispose();
}

/// <summary>Adapts ReactiveUI's AOT-safe typed view lookup to Runic presentation.</summary>
public sealed class ReactiveRunicViewLocator(IViewLocator locator) : IRunicViewLocator
{
    /// <inheritdoc />
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class => Locate<TView, TViewModel>(null);

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No matching view is registered.</exception>
    public TView Locate<TView, TViewModel>(string? contract)
        where TView : class, IRunicView
        where TViewModel : class =>
        locator.ResolveView<TViewModel>(contract) as TView
        ?? throw new InvalidOperationException($"No {typeof(TView).Name} is registered for {typeof(TViewModel).Name} with contract '{contract ?? "default"}'.");
}
