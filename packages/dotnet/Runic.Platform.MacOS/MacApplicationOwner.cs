using Runic.Platform.Runtime;

namespace Runic.Platform.MacOS;

// The application, not a window, owns native UI without a window: unowned panels and alerts, and URI and file
// handoffs. Its work runs on the main queue, which Runic Desktop's event loop serves, also before the first window
// opens; called on the main thread, it runs at once. Actions receive no window (0).
internal sealed class MacApplicationOwner : INativeWindowOwner
{
    internal static MacApplicationOwner Shared { get; } = new();

    public Guid Generation { get; } = Guid.NewGuid();

    // The application lives as long as the process.
    public bool IsAvailable => true;

    public ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();
        return MacOsMainQueue.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MacAppKit.IsAvailable) throw new NativeBackendUnavailableException("AppKit is not available.");
            // A process that runs no Desktop event loop yet, or one that is not a bundle, still gets an application.
            MacAppKit.EnsureApplication();
            action(0);
        });
    }
}
