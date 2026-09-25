using Runic.Application.Bridge;

namespace Runic.Application.CsWebUi;

/// <summary>
/// Adapter-owned best-effort refresh publisher for the internal CS-WebUI
/// fixture. The Bridge core admits opaque tokens; this class owns endpoint
/// selection, coalescing, JSON delivery, and shutdown.
/// </summary>
/// <remarks>
/// Native <c>RunJavaScript</c> is synchronous and offers no cancellation or
/// timeout contract. Shutdown therefore waits for an already-admitted native
/// send before the host releases its transport. Queued work is cancellable.
/// </remarks>
internal sealed class CsWebUiWindowBridgeInvalidationPublisher
{
    private readonly object _gate = new();
    private readonly WindowBridgeSession _session;
    private readonly CsWebUiWindowBridgeTransport _transport;
    private readonly int _maximumReferences;
    // Deferred references have their own cap so a full active queue can retain
    // one latest hint for other views without making total publisher memory
    // depend on registration cardinality.
    private readonly int _maximumDeferredReferences;
    private readonly Dictionary<WindowBridgeReference, CsWebUiWindowBridgeEndpoint> _endpoints = [];
    private readonly Dictionary<WindowBridgeReference, WindowBridgeInvalidation> _queued = [];
    private readonly HashSet<WindowBridgeReference> _active = [];
    private readonly Dictionary<WindowBridgeReference, WindowBridgeInvalidation> _followUps = [];
    private readonly Dictionary<WindowBridgeReference, WindowBridgeInvalidation> _deferred = [];
    private readonly Queue<WindowBridgeReference> _order = [];
    private readonly Queue<WindowBridgeReference> _deferredOrder = [];
    private TaskCompletionSource? _drained;
    private bool _workerActive;
    private bool _stopped;

    internal CsWebUiWindowBridgeInvalidationPublisher(WindowBridgeSession session,
        CsWebUiWindowBridgeTransport transport, int maximumReferences = 64, int maximumDeferredReferences = 64)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDeferredReferences);
        _maximumReferences = maximumReferences;
        _maximumDeferredReferences = maximumDeferredReferences;
    }

    internal int RegisteredReferenceCount { get { lock (_gate) return _endpoints.Count; } }

    /// <summary>Associates an adapter-local outbound endpoint with one exposed reference.</summary>
    internal IDisposable Register(WindowBridgeReference reference, WindowBridgeEndpointLease endpoint)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(endpoint);
        CsWebUiWindowBridgeEndpoint descriptor = _transport.DescriptorFor(endpoint);
        lock (_gate)
        {
            ThrowIfStopped();
            _endpoints[reference] = descriptor;
        }
        return new Registration(this, reference, descriptor);
    }

    /// <summary>
    /// Performs host-neutral admission and queues its latest revision. One
    /// reference occupies at most one queued or active slot.
    /// </summary>
    internal bool TryPublish(object model, string kind)
    {
        if (!_session.TryAdmitInvalidation(model, kind, out WindowBridgeInvalidation invalidation)) return false;
        var startWorker = false;
        lock (_gate)
        {
            if (_stopped || !_endpoints.ContainsKey(invalidation.Reference)) return false;
            if (_queued.ContainsKey(invalidation.Reference))
            {
                _queued[invalidation.Reference] = invalidation;
                return true;
            }
            if (_active.Contains(invalidation.Reference))
            {
                _followUps[invalidation.Reference] = invalidation;
                return true;
            }
            if (_deferred.ContainsKey(invalidation.Reference))
            {
                _deferred[invalidation.Reference] = invalidation;
                return false;
            }
            if (_queued.Count + _active.Count >= _maximumReferences)
            {
                if (_deferred.Count >= _maximumDeferredReferences) return false;
                _deferred.Add(invalidation.Reference, invalidation);
                _deferredOrder.Enqueue(invalidation.Reference);
                return false;
            }
            _queued.Add(invalidation.Reference, invalidation);
            _order.Enqueue(invalidation.Reference);
            if (!_workerActive)
            {
                _workerActive = true;
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                startWorker = true;
            }
        }
        if (startWorker) _ = Task.Run(DrainLoopAsync);
        return true;
    }

    /// <summary>
    /// Stops new admissions and cancels queued work. It does not claim to
    /// interrupt a native send that has already entered.
    /// </summary>
    internal void StopAdmission()
    {
        lock (_gate)
        {
            _stopped = true;
            _queued.Clear();
            _followUps.Clear();
            _deferred.Clear();
            _order.Clear();
            _deferredOrder.Clear();
            if (!_workerActive) _drained?.TrySetResult();
        }
    }

    /// <summary>Joins queued cancellation and any native send already in its critical section.</summary>
    internal Task DrainAsync()
    {
        lock (_gate) return _drained?.Task ?? Task.CompletedTask;
    }

    internal Task StopAndDrainAsync()
    {
        StopAdmission();
        return DrainAsync();
    }

    private async Task DrainLoopAsync()
    {
        while (true)
        {
            WindowBridgeInvalidation invalidation;
            CsWebUiWindowBridgeEndpoint endpoint;
            lock (_gate)
            {
                if (!_stopped) PromoteDeferredUnsafe();
                if (_order.Count == 0 || _stopped)
                {
                    _workerActive = false;
                    _drained?.TrySetResult();
                    return;
                }
                WindowBridgeReference reference = _order.Dequeue();
                if (!_queued.Remove(reference, out invalidation))
                {
                    if (!_stopped) PromoteDeferredUnsafe();
                    continue;
                }
                if (!_endpoints.TryGetValue(reference, out CsWebUiWindowBridgeEndpoint? candidate) || candidate is null)
                {
                    if (!_stopped) PromoteDeferredUnsafe();
                    continue;
                }
                endpoint = candidate;
                // A reference remains active until the host call returns. A
                // concurrent change replaces exactly one deferred follow-up.
                _active.Add(reference);
            }

            try
            {
                // Recheck as late as possible, immediately before entering
                // the transport's synchronous native-send critical section.
                if (_session.CanDispatch(invalidation))
                    _transport.PublishRefreshHint(endpoint, invalidation.Revision);
            }
            catch
            {
                // Refresh hints are best effort. Typed routes still enforce
                // their exact presentation authorization before state reads.
            }
            finally
            {
                lock (_gate)
                {
                    _active.Remove(invalidation.Reference);
                    if (!_stopped && _followUps.Remove(invalidation.Reference, out WindowBridgeInvalidation followUp))
                    {
                        EnqueueOrDeferUnsafe(followUp);
                    }
                    if (!_stopped) PromoteDeferredUnsafe();
                }
            }
            await Task.Yield();
        }
    }

    private void ThrowIfStopped()
    {
        if (_stopped) throw new InvalidOperationException("The Window Bridge invalidation publisher has stopped.");
    }

    private void Unregister(WindowBridgeReference reference, CsWebUiWindowBridgeEndpoint descriptor)
    {
        lock (_gate)
        {
            if (!_endpoints.TryGetValue(reference, out CsWebUiWindowBridgeEndpoint? current) || current != descriptor) return;
            _endpoints.Remove(reference);
            _queued.Remove(reference);
            _followUps.Remove(reference);
            _deferred.Remove(reference);
        }
    }

    private void EnqueueOrDeferUnsafe(WindowBridgeInvalidation invalidation)
    {
        if (_queued.Count + _active.Count < _maximumReferences)
        {
            _queued[invalidation.Reference] = invalidation;
            _order.Enqueue(invalidation.Reference);
            return;
        }
        if (_deferred.TryAdd(invalidation.Reference, invalidation)) _deferredOrder.Enqueue(invalidation.Reference);
        else _deferred[invalidation.Reference] = invalidation;
    }

    private void PromoteDeferredUnsafe()
    {
        while (_queued.Count + _active.Count < _maximumReferences && _deferredOrder.Count != 0)
        {
            WindowBridgeReference reference = _deferredOrder.Dequeue();
            if (!_deferred.Remove(reference, out WindowBridgeInvalidation invalidation)
                || !_endpoints.ContainsKey(reference)) continue;
            _queued.Add(reference, invalidation);
            _order.Enqueue(reference);
        }
    }

    private sealed class Registration(CsWebUiWindowBridgeInvalidationPublisher owner,
        WindowBridgeReference reference, CsWebUiWindowBridgeEndpoint descriptor) : IDisposable
    {
        private CsWebUiWindowBridgeInvalidationPublisher? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unregister(reference, descriptor);
    }
}
