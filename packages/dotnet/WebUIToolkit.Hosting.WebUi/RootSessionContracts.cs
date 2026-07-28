using System;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting.WebUi;

/// <summary>Opens one explicitly registered root session in an owned scope.</summary>
public interface IRootSessionFactory
{
    /// <summary>Opens the root session without activating it.</summary>
    ValueTask<IRootSession> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>Owns one root session and its scoped resources.</summary>
public interface IRootSession : IAsyncDisposable
{
    /// <summary>Activates the root after the browser runtime and window exist.</summary>
    ValueTask ActivateAsync(CancellationToken cancellationToken);

    /// <summary>Deactivates the root. Repeated calls must be harmless.</summary>
    ValueTask DeactivateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Receives the exact native window lifetime owned by <see cref="WebUiModeRunner"/>.
/// Implementations bind frontend-neutral services before the root session opens.
/// </summary>
public interface IWebUiWindowAttachment
{
    /// <summary>Attaches one initialized host and hidden native window.</summary>
    void Attach(IBrowserHost browserHost, IBrowserWindow window);

    /// <summary>Detaches the exact window before its native resources are closed.</summary>
    ValueTask DetachAsync(IBrowserWindow window, CancellationToken cancellationToken);
}

/// <summary>Receives notification after the native host reports that its window is gone.</summary>
public interface IWebUiNativeCloseNotification
{
    /// <summary>Signals forced close so application-scoped cancellation starts immediately.</summary>
    ValueTask NativeWindowClosedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Optional, explicitly registered activation callback for a scoped MVVM root.
/// </summary>
public interface IMvvmRootActivation
{
    /// <summary>Activates an opened MVVM session.</summary>
    ValueTask ActivateAsync(
        global::WebUIToolkit.MVVM.IMvvmSession session,
        CancellationToken cancellationToken);

    /// <summary>Deactivates an opened MVVM session.</summary>
    ValueTask DeactivateAsync(
        global::WebUIToolkit.MVVM.IMvvmSession session,
        CancellationToken cancellationToken);
}
