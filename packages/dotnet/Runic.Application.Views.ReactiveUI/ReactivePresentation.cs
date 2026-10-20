// Shared by Runic.Application.Views.ReactiveUI and, compiled with SYSTEM_REACTIVE,
// Runic.Application.Views.ReactiveUI.Reactive.
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
