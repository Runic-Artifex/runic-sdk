using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace Runic.Application.Views.ReactiveUI;

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
        _mounted = true;
        Activate();
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
        _subscription = router.CurrentViewModel.Subscribe(viewModel =>
        {
            if (viewModel is not null && viewModel is not TViewModel)
                throw new InvalidOperationException($"Route {viewModel.GetType().Name} is not a {typeof(TViewModel).Name}.");
            var current = (TViewModel?)viewModel;
            if (ReferenceEquals(_current, current)) return;
            _current = current;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
        });
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
