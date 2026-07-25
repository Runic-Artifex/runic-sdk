using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

/// <summary>
/// Provides a bind-once forwarding reference for adapters that must be composed before
/// the lifecycle kernel creates its exact-once stop controller.
/// </summary>
public sealed class ApplicationStopControllerBinding : IApplicationStopController
{
    private IApplicationStopController? _controller;

    /// <summary>Binds the lifecycle kernel's controller exactly once.</summary>
    public void Bind(IApplicationStopController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (Interlocked.CompareExchange(ref _controller, controller, null) is not null)
        {
            throw new InvalidOperationException("The application stop controller is already bound.");
        }
    }

    /// <inheritdoc />
    public CancellationToken Stopping => GetController().Stopping;

    /// <inheritdoc />
    public Task Completion => GetController().Completion;

    /// <inheritdoc />
    public bool RequestStop(StopReason reason) => GetController().RequestStop(reason);

    private IApplicationStopController GetController() =>
        Volatile.Read(ref _controller)
        ?? throw new InvalidOperationException(
            "The application stop controller is not available until the application is built.");
}
