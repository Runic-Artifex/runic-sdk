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
