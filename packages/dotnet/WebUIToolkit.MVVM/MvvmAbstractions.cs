namespace WebUIToolkit.MVVM;

/// <summary>A closed, generated adapter for one explicitly registered ViewModel contract.</summary>
public interface IMvvmBindingAdapter : IAsyncDisposable
{
    /// <summary>Creates an authoritative full state snapshot.</summary>
    ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Validates and commits one generated-member mutation.</summary>
    /// <remarks>
    /// A successful result means the mutation has committed exactly once. If state changes before
    /// cancellation, timeout, or consumer failure is observed, the adapter must return
    /// <see cref="MvvmBindingResult.CommittedFailure"/> with the complete patch transaction.
    /// An adapter must validate result limits before mutating because the runtime cannot roll back.
    /// </remarks>
    ValueTask<MvvmBindingResult> DispatchAsync(MvvmMutationRequest request, CancellationToken cancellationToken);
}

/// <summary>Owns one ViewModel, its adapter, ordered dispatch, revisions, and teardown.</summary>
public interface IMvvmSession : IAsyncDisposable
{
    /// <summary>Gets the runtime session identifier.</summary>
    MvvmSessionId Id { get; }

    /// <summary>Gets the registered logical contract.</summary>
    MvvmContract Contract { get; }

    /// <summary>Gets the random per-session invocation capability.</summary>
    string CapabilityToken { get; }

    /// <summary>Compares an invocation capability without data-dependent byte comparison.</summary>
    bool Authorizes(string capabilityToken);

    /// <summary>Gets the current authoritative state revision.</summary>
    long Revision { get; }

    /// <summary>Gets the greatest monotonically acknowledged revision.</summary>
    long? AcknowledgedRevision { get; }

    /// <summary>Dispatches a request using per-session ordering and cancellation semantics.</summary>
    ValueTask<MvvmResponse> DispatchAsync(MvvmRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Creates and owns explicitly registered sessions.</summary>
public interface IMvvmSessionFactory : IAsyncDisposable
{
    /// <summary>Opens a contract in an independently owned lifetime.</summary>
    ValueTask<IMvvmSession> OpenAsync(MvvmContract contract, CancellationToken cancellationToken = default);

    /// <summary>Closes a session. Repeated or unknown closes are harmless.</summary>
    ValueTask<bool> CloseAsync(MvvmSessionId sessionId);
}

/// <summary>The result of activating one explicitly registered contract.</summary>
public sealed class MvvmSessionActivation
{
    private readonly object[] _ownedResources;

    /// <summary>Creates an activation and records resources in creation order.</summary>
    /// <param name="adapter">The generated closed binding adapter.</param>
    /// <param name="ownedResources">
    /// Resources such as scope and ViewModel, in creation order. Each item must implement
    /// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>.
    /// </param>
    public MvvmSessionActivation(IMvvmBindingAdapter adapter, params object[] ownedResources)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ArgumentNullException.ThrowIfNull(ownedResources);
        _ownedResources = ownedResources.ToArray();

        if (_ownedResources.Any(static resource => resource is not IAsyncDisposable and not IDisposable))
        {
            throw new ArgumentException("Every session resource must be disposable.", nameof(ownedResources));
        }

        if (_ownedResources.Any(resource => ReferenceEquals(resource, adapter)))
        {
            throw new ArgumentException("The adapter is already owned separately and cannot also be an activation resource.", nameof(ownedResources));
        }
    }

    /// <summary>Gets the generated adapter, which owns binding subscriptions.</summary>
    public IMvvmBindingAdapter Adapter { get; }

    internal object[] OwnedResources => _ownedResources;
}

/// <summary>Activates one registered contract without reflection or runtime discovery.</summary>
/// <param name="cancellationToken">Cancels activation before ownership transfers to the runtime.</param>
public delegate ValueTask<MvvmSessionActivation> MvvmSessionActivator(CancellationToken cancellationToken);
