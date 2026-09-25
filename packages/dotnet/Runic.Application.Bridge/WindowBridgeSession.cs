using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace Runic.Application.Bridge;

/// <summary>
/// Internal, host-neutral ownership core for one logical window. It deliberately
/// has no application-envelope, generator, or frontend authoring surface.
/// </summary>
internal sealed class WindowBridgeSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IWindowBridgeTransport _transport;
    private readonly IAsyncDisposable? _ownedScope;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConditionalWeakTable<object, Dictionary<string, Entry>> _models = new();
    private readonly Dictionary<string, Entry> _references = new(StringComparer.Ordinal);
    private readonly Dictionary<(string OwnerId, string Slot), Entry> _slots = [];
    private readonly Dictionary<WindowBridgeConnection, DocumentEntry> _documents = [];
    private readonly HashSet<WindowBridgeConnection> _disconnectedConnections = [];
    private readonly Dictionary<PresentationKey, PresentationLease> _presentations = [];
    private readonly Dictionary<OperationKey, OperationEntry> _operations = [];
    private readonly LinkedList<OperationKey> _terminalOrder = [];
    private readonly Dictionary<OperationKey, OperationTombstone> _expired = [];
    private readonly Queue<Entry> _outboundPublications = [];
    private readonly Dictionary<Entry, OutboundPublication> _pendingOutboundPublications = [];
    private readonly Dictionary<Entry, ActiveOutboundPublication> _activeOutboundPublications = [];
    private readonly List<Exception> _attachmentFailures = [];
    private readonly LinkedList<OperationKey> _expiredOrder = [];
    private readonly int _maximumOperations;
    private readonly int _maximumRetainedTerminals;
    private readonly int _maximumExpired;
    private readonly int _maximumRequestIdLength;
    private readonly int _maximumOutboundPublications;
    private readonly Action? _beforeRetiredCleanup;
    private readonly Action? _beforeAttachmentWait;
    private readonly AsyncLocal<Entry?> _disposingEntry = new();
    private readonly AsyncLocal<Entry?> _attachingEntry = new();
    private readonly AsyncLocal<PublicationFrame?> _publishing = new();
    private bool _disposed;
    private TaskCompletionSource? _closeCompletion;
    private int _remainingOperationsAtClose;
    private int _attachmentTransitions;
    private TaskCompletionSource? _attachmentsDrained;
    private TaskCompletionSource? _outboundPublicationsDrained;
    private bool _outboundPublisherActive;
    private int _outboundPublicationCount;
    private long _nextDeferredPublicationSequence;

    internal WindowBridgeSession(IWindowBridgeTransport transport, IAsyncDisposable? ownedScope = null,
        int maximumOperations = 64, int maximumRetainedTerminals = 32, int maximumExpired = 128,
        int maximumRequestIdLength = 128, Action? beforeRetiredCleanup = null, Action? beforeAttachmentWait = null,
        int maximumOutboundPublications = 64)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOperations);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedTerminals);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumExpired);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRequestIdLength);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutboundPublications);
        _ownedScope = ownedScope;
        _maximumOperations = maximumOperations;
        _maximumRetainedTerminals = maximumRetainedTerminals;
        _maximumExpired = maximumExpired;
        _maximumRequestIdLength = maximumRequestIdLength;
        _maximumOutboundPublications = maximumOutboundPublications;
        _beforeRetiredCleanup = beforeRetiredCleanup;
        _beforeAttachmentWait = beforeAttachmentWait;
    }

    internal WindowBridgeReference Expose<T>(string kind, T model,
        Func<IWindowBridgeTransport, T, string, WindowBridgeAttachment> attach) where T : class =>
        Expose(kind, model, attach, out _, reserveForPresent: false, retainRoot: true);

    private WindowBridgeReference Expose<T>(string kind, T model,
        Func<IWindowBridgeTransport, T, string, WindowBridgeAttachment> attach, out bool created, bool reserveForPresent, bool retainRoot) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(attach);
        Entry entry;
        bool attachNow = false;
        created = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            PublicationFrame? publication = _publishing.Value;
            if (publication is not null && (publication.Entry.Generation != publication.Generation
                || publication.Entry.Detaching || publication.Entry.Attachment is null))
                throw new InvalidOperationException("A detached operation cannot publish content.");
            Dictionary<string, Entry> variants = _models.GetValue(model, _ => new(StringComparer.Ordinal));
            if (variants.TryGetValue(kind, out Entry? existing))
            {
                if (existing.PendingPresentation)
                    throw new InvalidOperationException("The ViewModel route is being assigned to content.");
                if (ReferenceEquals(_disposingEntry.Value, existing))
                {
                    if (retainRoot) existing.RootOwned = true;
                    existing.ReattachRequested = true;
                    return existing.Reference;
                }

                if (existing.Detaching)
                {
                    if (retainRoot) existing.RootOwned = true;
                    existing.ReattachRequested = true;
                    return existing.Reference;
                }

                if (existing.Attaching && ReferenceEquals(_attachingEntry.Value, existing))
                    throw new InvalidOperationException("A route cannot re-expose itself from its attachment callback.");
                if (existing.Attaching) _beforeAttachmentWait?.Invoke();
                while (existing.Attaching) Monitor.Wait(_gate);
                ThrowIfDisposed();
                if (!_models.TryGetValue(model, out Dictionary<string, Entry>? current) || !ReferenceEquals(current, variants)
                    || !current.TryGetValue(kind, out Entry? currentEntry) || !ReferenceEquals(currentEntry, existing))
                    throw new InvalidOperationException("The ViewModel route was forgotten while it was being attached.");
                if (retainRoot) existing.RootOwned = true;
                if (existing.Attachment is null)
                {
                    existing.Attaching = true;
                    BeginAttachmentTransition();
                    attachNow = true;
                }
                entry = existing;
            }
            else
            {
                var reference = new WindowBridgeReference(kind, Guid.NewGuid().ToString("N"));
                entry = new Entry(reference, model, () => attach(_transport, model, "content" + reference.Id));
                entry.Attaching = true;
                BeginAttachmentTransition();
                entry.PendingPresentation = reserveForPresent;
                entry.RootOwned = retainRoot;
                attachNow = true;
                created = true;
                variants.Add(kind, entry);
                _references.Add(reference.Id, entry);
            }
        }
        if (attachNow)
        {
            AttachEntry(entry, removeNewEntryOnFailure: created);
        }
        return entry.Reference;
    }

    internal void Suspend(object model) => SuspendCore(model, requiredRetirement: null);

    private void SuspendRetired(RetiredChild retired)
    {
        _beforeRetiredCleanup?.Invoke();
        SuspendCore(retired.Entry.Model, retired);
    }

    private void SuspendCore(object model, RetiredChild? requiredRetirement)
    {
        ArgumentNullException.ThrowIfNull(model);
        List<(Entry Entry, WindowBridgeAttachment Attachment)> detached = [];
        List<PresentationLease> leases = [];
        List<RetiredChild> children = [];
        lock (_gate)
        {
            ThrowIfDisposed();
            if (requiredRetirement is not null && !IsStillRetired(requiredRetirement)) return;
            if (!_models.TryGetValue(model, out Dictionary<string, Entry>? variants)) return;
            foreach (Entry entry in variants.Values)
            {
                entry.Generation++;
                if (entry.Publishing > 0 && !ReferenceEquals(_publishing.Value?.Entry, entry))
                    while (entry.Publishing > 0) Monitor.Wait(_gate);
                if (entry.Attachment is WindowBridgeAttachment attachment)
                {
                    entry.Attachment = null;
                    entry.Detaching = true;
                    BeginAttachmentTransition();
                    detached.Add((entry, attachment));
                }
                else if (entry.Attaching)
                {
                    entry.SuspendRequested = true;
                }
                foreach ((PresentationKey key, PresentationLease lease) in _presentations.Where(pair => pair.Key.ReferenceId == entry.Reference.Id).ToArray())
                {
                    _presentations.Remove(key);
                    leases.Add(lease);
                }
                foreach ((string OwnerId, string Slot) slot in _slots.Where(pair => pair.Key.OwnerId == entry.Reference.Id).Select(pair => pair.Key).ToArray())
                {
                    Entry child = _slots[slot];
                    _slots.Remove(slot);
                    if (!child.RootOwned && !_slots.Values.Any(candidate => ReferenceEquals(candidate, child))) children.Add(new(child, child.OwnershipGeneration));
                }
            }
        }
        List<Action> cleanup = [];
        foreach (PresentationLease lease in leases) cleanup.Add(() => lease.Activation?.Dispose());
        foreach ((Entry entry, WindowBridgeAttachment attachment) in detached) cleanup.Add(() => DisposeAttachment(entry, attachment));
        foreach (RetiredChild child in children) cleanup.Add(() => SuspendRetired(child));
        RunCleanup(cleanup);
    }

    /// <summary>Assigns a child to one logical parent slot and suspends the replaced child.</summary>
    internal WindowBridgeReference Present<T>(WindowBridgeReference owner, string slot, string kind, T child,
        Func<IWindowBridgeTransport, T, string, WindowBridgeAttachment> attach) where T : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(slot);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_references.TryGetValue(owner.Id, out Entry? ownerEntry) || ownerEntry.Reference.Kind != owner.Kind)
                throw new InvalidOperationException("The content slot does not belong to this window.");
        }
        WindowBridgeReference reference = Expose(kind, child, attach, out bool created, reserveForPresent: true, retainRoot: false);
        RetiredChild? replaced = null;
        bool ownerForgotten = false;
        lock (_gate)
        {
            if (!_references.TryGetValue(owner.Id, out Entry? ownerEntry) || ownerEntry.Reference.Kind != owner.Kind
                || !_references.TryGetValue(reference.Id, out Entry? entry))
            {
                ownerForgotten = true;
            }
            else
            {
                var key = (owner.Id, slot);
                Entry? previous = _slots.TryGetValue(key, out Entry? value) ? value : null;
                _slots[key] = entry;
                entry.OwnershipGeneration++;
                if (previous is not null && !ReferenceEquals(previous, entry)
                    && !previous.RootOwned && !_slots.Values.Any(candidate => ReferenceEquals(candidate, previous))) replaced = new(previous, previous.OwnershipGeneration);
                entry.PendingPresentation = false;
                Monitor.PulseAll(_gate);
            }
        }
        if (ownerForgotten)
        {
            if (created) DiscardUnownedReference(reference);
            throw new InvalidOperationException("The content slot does not belong to this window.");
        }
        if (replaced is not null) SuspendRetired(replaced);
        return reference;
    }

    private void DiscardUnownedReference(WindowBridgeReference reference)
    {
        WindowBridgeAttachment? attachment = null;
        Entry? discarded = null;
        lock (_gate)
        {
            if (!_references.Remove(reference.Id, out Entry? entry)) return;
            discarded = entry;
            entry.PendingPresentation = false;
            Monitor.PulseAll(_gate);
            if (_models.TryGetValue(entry.Model, out Dictionary<string, Entry>? variants)) variants.Remove(reference.Kind);
            if (entry.Attachment is WindowBridgeAttachment current)
            {
                entry.Attachment = null;
                entry.Detaching = true;
                BeginAttachmentTransition();
                attachment = current;
            }
            else if (entry.Attaching)
            {
                entry.SuspendRequested = true;
            }
        }
        if (attachment is not null) DisposeAttachment(discarded!, attachment);
    }

    internal void Clear(WindowBridgeReference owner, string slot)
    {
        RetiredChild? child = null;
        lock (_gate)
        {
            if (!_references.TryGetValue(owner.Id, out Entry? ownerEntry) || ownerEntry.Reference.Kind != owner.Kind)
                throw new InvalidOperationException("The content slot does not belong to this window.");
            if (_slots.Remove((owner.Id, slot), out Entry? previous)
                && !previous.RootOwned && !_slots.Values.Any(candidate => ReferenceEquals(candidate, previous))) child = new(previous, previous.OwnershipGeneration);
        }
        if (child is not null) SuspendRetired(child);
    }

    internal void ReleaseRoot(WindowBridgeReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RetiredChild? retired = null;
        lock (_gate)
        {
            if (!_references.TryGetValue(reference.Id, out Entry? entry) || entry.Reference.Kind != reference.Kind)
                throw new InvalidOperationException("The ViewModel reference does not belong to this window.");
            entry.RootOwned = false;
            if (!_slots.Values.Any(candidate => ReferenceEquals(candidate, entry))) retired = new(entry, entry.OwnershipGeneration);
        }
        if (retired is not null) SuspendRetired(retired);
    }

    /// <summary>Removes a dynamic model from this window's strong route table.</summary>
    internal void Forget(object model) => ForgetCore(model, requiredRetirement: null);

    private void ForgetRetired(RetiredChild retired)
    {
        _beforeRetiredCleanup?.Invoke();
        ForgetCore(retired.Entry.Model, retired);
    }

    private void ForgetCore(object model, RetiredChild? requiredRetirement)
    {
        ArgumentNullException.ThrowIfNull(model);
        List<(Entry Entry, WindowBridgeAttachment Attachment)> detached = [];
        List<PresentationLease> leases = [];
        List<RetiredChild> children = [];
        lock (_gate)
        {
            if (requiredRetirement is not null && !IsStillRetired(requiredRetirement)) return;
            if (!_models.TryGetValue(model, out Dictionary<string, Entry>? variants)) return;
            _models.Remove(model);
            foreach (Entry entry in variants.Values)
            {
                _references.Remove(entry.Reference.Id);
                foreach ((string OwnerId, string Slot) slot in _slots.Where(pair => ReferenceEquals(pair.Value, entry)).Select(pair => pair.Key).ToArray())
                    _slots.Remove(slot);
                foreach ((string OwnerId, string Slot) slot in _slots.Where(pair => pair.Key.OwnerId == entry.Reference.Id).Select(pair => pair.Key).ToArray())
                {
                    Entry child = _slots[slot];
                    _slots.Remove(slot);
                    if (!child.RootOwned && !_slots.Values.Any(candidate => ReferenceEquals(candidate, child))) children.Add(new(child, child.OwnershipGeneration));
                }
                if (entry.Attachment is WindowBridgeAttachment attachment)
                {
                    entry.Attachment = null;
                    entry.Detaching = true;
                    BeginAttachmentTransition();
                    detached.Add((entry, attachment));
                }
                else if (entry.Attaching)
                {
                    entry.SuspendRequested = true;
                }
                foreach ((PresentationKey key, PresentationLease lease) in _presentations.Where(pair => pair.Key.ReferenceId == entry.Reference.Id).ToArray())
                {
                    _presentations.Remove(key);
                    leases.Add(lease);
                }
            }
        }
        List<Action> cleanup = [];
        foreach (PresentationLease lease in leases) cleanup.Add(() => lease.Activation?.Dispose());
        foreach ((Entry entry, WindowBridgeAttachment attachment) in detached) cleanup.Add(() => DisposeAttachment(entry, attachment));
        foreach (RetiredChild child in children) cleanup.Add(() => ForgetRetired(child));
        RunCleanup(cleanup);
    }

    private bool IsStillRetired(RetiredChild retired) =>
        retired.Entry.OwnershipGeneration == retired.OwnershipGeneration
        && !retired.Entry.RootOwned
        && !_slots.Values.Any(candidate => ReferenceEquals(candidate, retired.Entry));

    internal WindowBridgeReference Resolve(string kind, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_references.TryGetValue(id, out Entry? entry) || entry.Reference.Kind != kind)
                throw new InvalidOperationException("The ViewModel reference does not belong to this window.");
            return entry.Reference;
        }
    }

    /// <summary>
    /// Publishes an internal fixture invalidation for every current document that
    /// presents one exposed model. The envelope intentionally contains no model
    /// state: each recipient must use its exact presentation lease to pull its
    /// typed snapshot.
    ///
    /// Admission is synchronized with ownership, then drained by one internal
    /// publisher without holding the window ownership lock across host work.
    /// A queued or deferred publication is cancelled before host dispatch if
    /// its exposed entry retired or has no current document presentation. Once
    /// a host send has entered, this core cannot retract it; every eventual
    /// typed pull is still exact-presentation authorized. This is not a
    /// durable, targeted outbound channel: broadcast hosts still send the
    /// envelope to every peer.
    /// </summary>
    internal bool PublishPublicInvalidation(object model, string kind, string route)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        OutboundPublication publication;
        var startPublisher = false;
        lock (_gate)
        {
            if (_disposed || !_models.TryGetValue(model, out Dictionary<string, Entry>? variants)
                || !variants.TryGetValue(kind, out Entry? entry) || entry.Attachment is null || entry.Detaching)
                return false;

            if (!HasCurrentDocumentPresentation(entry)) return false;
            publication = new(entry, entry.Generation, route, RenderPublicInvalidation(++entry.InvalidationRevision));
            if (_pendingOutboundPublications.ContainsKey(entry))
            {
                // Preserve one queue position per exposed entry and let the
                // next send carry its newest invalidation revision.
                _pendingOutboundPublications[entry] = publication;
                return true;
            }
            if (_activeOutboundPublications.TryGetValue(entry, out ActiveOutboundPublication? active))
            {
                // An active entry retains its bounded queue slot until its
                // host call returns. Keep one dirty/latest record beside it
                // so a change during a slow send is not silently lost.
                active.FollowUp = publication;
                return true;
            }
            if (_outboundPublicationCount >= _maximumOutboundPublications)
            {
                // Preserve only the newest rejected revision on its already
                // retained entry. The publisher promotes it when a slot frees.
                // The return value still reports that immediate admission lost.
                DeferOutboundPublicationUnsafe(publication);
                return false;
            }
            _pendingOutboundPublications.Add(entry, publication);
            _outboundPublications.Enqueue(entry);
            if (_outboundPublicationCount++ == 0)
                _outboundPublicationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_outboundPublisherActive)
            {
                _outboundPublisherActive = true;
                startPublisher = true;
            }
        }
        if (startPublisher) _ = Task.Run(DrainOutboundPublicationsAsync);
        return true;
    }

    /// <summary>Completes when all admitted public invalidations have dispatched or been cancelled.</summary>
    internal Task OutboundPublicationDrain
    {
        get
        {
            lock (_gate) return _outboundPublicationsDrained?.Task ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Admits one loaded document on a native connection. Replacing its epoch
    /// releases every presentation from the preceding document, while keeping
    /// window models and accepted work alive. A disconnected raw callback pair
    /// remains closed for its current epoch, but a strictly newer epoch may
    /// reopen that pair when a native host reuses its callback identifiers.
    /// </summary>
    internal WindowBridgeDocumentAdmission BeginDocument(WindowBridgeConnection connection, WindowBridgeDocumentEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        PresentationLease[] retired = [];
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_documents.TryGetValue(connection, out DocumentEntry? document))
            {
                if (_disconnectedConnections.Contains(connection))
                    return WindowBridgeDocumentAdmission.Rejected(epoch, "The native connection is no longer active.");
                _documents.Add(connection, new DocumentEntry(epoch));
                return WindowBridgeDocumentAdmission.AcceptedEpoch(epoch);
            }
            if (document.Current == epoch)
                return _disconnectedConnections.Contains(connection)
                    ? WindowBridgeDocumentAdmission.Rejected(epoch, "The native connection is no longer active.")
                    : WindowBridgeDocumentAdmission.AcceptedEpoch(epoch);
            if (epoch.Ordinal <= document.Current.Ordinal)
                return WindowBridgeDocumentAdmission.Rejected(epoch, "The document epoch is not newer than the current document.");

            _disconnectedConnections.Remove(connection);
            document.Current = epoch;
            retired = _presentations
                .Where(pair => pair.Key.ClientId == connection.ClientId && pair.Key.ConnectionId == connection.ConnectionId
                    && pair.Key.DocumentEpoch is not null)
                .Select(pair => pair.Value)
                .ToArray();
            foreach (PresentationLease lease in retired) _presentations.Remove(lease.Key);
        }
        RunCleanup(retired.Select(lease => (Action)(() => lease.Activation?.Dispose())));
        return WindowBridgeDocumentAdmission.AcceptedEpoch(epoch);
    }

    internal bool IsCurrentDocument(WindowBridgeConnection connection, WindowBridgeDocumentEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        lock (_gate)
            return !_disposed && IsCurrentDocumentCore(connection, epoch);
    }

    internal WindowBridgePresentationLease Mount(WindowBridgeReference reference, WindowBridgeConnection connection,
        string presentationId, Func<IDisposable>? activate = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        PresentationLease lease;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_disconnectedConnections.Contains(connection))
                throw new InvalidOperationException("The native connection is no longer active.");
            if (!_references.TryGetValue(reference.Id, out Entry? entry) || entry.Reference.Kind != reference.Kind || entry.Attachment is null || entry.Detaching || entry.Attaching || entry.SuspendRequested)
                throw new InvalidOperationException("A suspended ViewModel reference cannot be mounted.");
            var key = new PresentationKey(reference.Id, connection.ClientId, connection.ConnectionId, null, presentationId);
            if (_presentations.TryGetValue(key, out PresentationLease? existing)) return existing.Handle;
            lease = new PresentationLease(this, key, null);
            _presentations.Add(key, lease);
        }
        IDisposable? activation;
        try { activation = activate?.Invoke(); }
        catch { lease.Handle.Dispose(); throw; }
        bool releaseActivation = false;
        lock (_gate)
        {
            if (_presentations.TryGetValue(lease.Key, out PresentationLease? active) && ReferenceEquals(active, lease)) lease.Activation = activation;
            else releaseActivation = true;
        }
        if (releaseActivation) activation?.Dispose();
        return lease.Handle;
    }

    internal WindowBridgePresentationLease Mount(WindowBridgeReference reference, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch epoch, string presentationId, Func<IDisposable>? activate = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        PresentationLease lease;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_disconnectedConnections.Contains(connection))
                throw new InvalidOperationException("The native connection is no longer active.");
            if (!IsCurrentDocumentCore(connection, epoch))
                throw new InvalidOperationException("The document epoch is not active.");
            if (!_references.TryGetValue(reference.Id, out Entry? entry) || entry.Reference.Kind != reference.Kind || entry.Attachment is null || entry.Detaching || entry.Attaching || entry.SuspendRequested)
                throw new InvalidOperationException("A suspended ViewModel reference cannot be mounted.");
            var key = new PresentationKey(reference.Id, connection.ClientId, connection.ConnectionId, epoch.Value, presentationId);
            if (_presentations.TryGetValue(key, out PresentationLease? existing)) return existing.Handle;
            lease = new PresentationLease(this, key, null);
            _presentations.Add(key, lease);
        }
        IDisposable? activation;
        try { activation = activate?.Invoke(); }
        catch { lease.Handle.Dispose(); throw; }
        bool releaseActivation = false;
        lock (_gate)
        {
            if (_presentations.TryGetValue(lease.Key, out PresentationLease? active) && ReferenceEquals(active, lease)) lease.Activation = activation;
            else releaseActivation = true;
        }
        if (releaseActivation) activation?.Dispose();
        return lease.Handle;
    }

    internal void Disconnect(WindowBridgeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        PresentationLease[] leases;
        lock (_gate)
        {
            _disconnectedConnections.Add(connection);
            leases = _presentations.Where(pair => pair.Key.ClientId == connection.ClientId
                && pair.Key.ConnectionId == connection.ConnectionId).Select(pair => pair.Value).ToArray();
        }
        RunCleanup(leases.Select(lease => (Action)(() => lease.Handle.Dispose())));
    }

    internal void Unmount(WindowBridgeReference reference, WindowBridgeConnection connection, string presentationId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        PresentationLease? lease;
        lock (_gate)
            _presentations.TryGetValue(new(reference.Id, connection.ClientId, connection.ConnectionId, null, presentationId), out lease);
        lease?.Handle.Dispose();
    }

    /// <summary>Releases only the exact current-document presentation lease.</summary>
    internal bool Unmount(WindowBridgeReference reference, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch epoch, string presentationId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        PresentationLease? lease;
        lock (_gate)
        {
            if (!IsCurrentDocumentCore(connection, epoch)) return false;
            _presentations.TryGetValue(new(reference.Id, connection.ClientId, connection.ConnectionId, epoch.Value, presentationId), out lease);
        }
        if (lease is null) return false;
        lease.Handle.Dispose();
        return true;
    }

    internal bool HasPresentation(WindowBridgeReference reference, WindowBridgeConnection connection)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        lock (_gate)
        {
            return !_disposed && _references.TryGetValue(reference.Id, out Entry? entry)
                && entry.Reference.Kind == reference.Kind && entry.Attachment is not null && !entry.Detaching
                && _presentations.Keys.Any(key => key.ReferenceId == reference.Id
                    && key.ClientId == connection.ClientId && key.ConnectionId == connection.ConnectionId);
        }
    }

    internal bool HasPresentation(WindowBridgeReference reference, WindowBridgeConnection connection, WindowBridgeDocumentEpoch epoch)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        lock (_gate)
        {
            return !_disposed && IsCurrentDocumentCore(connection, epoch)
                && _references.TryGetValue(reference.Id, out Entry? entry)
                && entry.Reference.Kind == reference.Kind && entry.Attachment is not null && !entry.Detaching
                && _presentations.Keys.Any(key => key.ReferenceId == reference.Id
                    && key.ClientId == connection.ClientId && key.ConnectionId == connection.ConnectionId
                    && key.DocumentEpoch == epoch.Value);
        }
    }

    /// <summary>Checks the exact current-document component lease, rather than any peer presenting the same reference.</summary>
    internal bool HasPresentation(WindowBridgeReference reference, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch epoch, string presentationId)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        lock (_gate)
        {
            return !_disposed && IsCurrentDocumentCore(connection, epoch)
                && _references.TryGetValue(reference.Id, out Entry? entry)
                && entry.Reference.Kind == reference.Kind && entry.Attachment is not null && !entry.Detaching
                && _presentations.ContainsKey(new(reference.Id, connection.ClientId, connection.ConnectionId,
                    epoch.Value, presentationId));
        }
    }

    internal WindowBridgeOperationAcceptance StartOperation(WindowBridgeReference source, string requestId,
        Func<CancellationToken, Task<WindowBridgeOperationResult>> work,
        Func<WindowBridgeOperationResult, string>? projectState = null) =>
        StartOperationCore(source, connection: null, documentEpoch: null, requestId, work, projectState);

    internal WindowBridgeOperationAcceptance StartOperationFromPresentation(WindowBridgeReference source, WindowBridgeConnection connection,
        string requestId, Func<CancellationToken, Task<WindowBridgeOperationResult>> work,
        Func<WindowBridgeOperationResult, string>? projectState = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return StartOperationCore(source, connection, documentEpoch: null, requestId, work, projectState);
    }

    internal WindowBridgeOperationAcceptance StartOperationFromPresentation(WindowBridgeReference source, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch epoch, string requestId, Func<CancellationToken, Task<WindowBridgeOperationResult>> work,
        Func<WindowBridgeOperationResult, string>? projectState = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        return StartOperationCore(source, connection, epoch, requestId, work, projectState);
    }

    /// <summary>Atomically admits work only from the exact mounted component lease.</summary>
    internal WindowBridgeOperationAcceptance StartOperationFromPresentation(WindowBridgeReference source, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch epoch, string presentationId, string requestId,
        Func<CancellationToken, Task<WindowBridgeOperationResult>> work,
        Func<WindowBridgeOperationResult, string>? projectState = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
        return StartOperationCore(source, connection, epoch, requestId, work, projectState, presentationId);
    }

    private WindowBridgeOperationAcceptance StartOperationCore(WindowBridgeReference source, WindowBridgeConnection? connection,
        WindowBridgeDocumentEpoch? documentEpoch, string requestId,
        Func<CancellationToken, Task<WindowBridgeOperationResult>> work, Func<WindowBridgeOperationResult, string>? projectState,
        string? presentationId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(work);
        Entry entry;
        OperationEntry operation;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_references.TryGetValue(source.Id, out entry!) || entry.Reference.Kind != source.Kind)
                throw new InvalidOperationException("The operation source does not belong to this window.");
            if (connection is not null && (entry.Attachment is null || entry.Detaching
                || !HasPresentationCore(source, connection, documentEpoch, presentationId)))
                return new(requestId, false, WindowBridgeOperationStatus.Rejected(requestId, "The source presentation is not active."));
            var key = new OperationKey(source.Id, requestId);
            if (requestId.Length > _maximumRequestIdLength)
                return new(requestId, false, WindowBridgeOperationStatus.Rejected(requestId, "The operation request identifier is invalid."));
            if (_operations.TryGetValue(key, out OperationEntry? duplicate))
            {
                if (connection is not null && (duplicate.InitiatingConnection != connection || duplicate.InitiatingDocument != documentEpoch))
                    return new(requestId, false, WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this presentation."));
                return new(requestId, false, duplicate.Status);
            }
            if (_expired.TryGetValue(key, out OperationTombstone? expiredOwner))
            {
                if (connection is not null && (expiredOwner.Connection != connection || expiredOwner.Document != documentEpoch))
                    return new(requestId, false, WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this presentation."));
                return new(requestId, false, WindowBridgeOperationStatus.Expired(requestId));
            }
            TrimTerminals();
            if (_operations.Count >= _maximumOperations)
                return new(requestId, false, WindowBridgeOperationStatus.Rejected(requestId, "The window operation limit was reached."));
            operation = new OperationEntry(key, entry.Generation, connection, documentEpoch);
            _operations.Add(key, operation);
        }
        operation.Task = RunOperationAsync(entry, operation, work, projectState);
        return new(requestId, true, operation.Status);
    }

    internal WindowBridgeOperationStatus LookupOperation(WindowBridgeReference source, string requestId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            var key = new OperationKey(source.Id, requestId);
            if (_operations.TryGetValue(key, out OperationEntry? operation)) return operation.Status;
            return _expired.ContainsKey(key) ? WindowBridgeOperationStatus.Expired(requestId) : WindowBridgeOperationStatus.Unknown(requestId);
        }
    }

    internal WindowBridgeOperationStatus LookupOperationFromConnection(WindowBridgeReference source, WindowBridgeConnection connection, string requestId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            var key = new OperationKey(source.Id, requestId);
            if (!_operations.TryGetValue(key, out OperationEntry? operation))
            {
                if (!_expired.TryGetValue(key, out OperationTombstone? expiredOwner)) return WindowBridgeOperationStatus.Unknown(requestId);
                return expiredOwner.Connection == connection
                    ? WindowBridgeOperationStatus.Expired(requestId)
                    : WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this connection.");
            }
            return operation.InitiatingConnection == connection
                ? operation.Status
                : WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this connection.");
        }
    }

    internal WindowBridgeOperationStatus LookupOperationFromPresentation(WindowBridgeReference source, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch epoch, string requestId)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        lock (_gate)
        {
            var key = new OperationKey(source.Id, requestId);
            if (!_operations.TryGetValue(key, out OperationEntry? operation))
            {
                if (!_expired.TryGetValue(key, out OperationTombstone? expired)) return WindowBridgeOperationStatus.Unknown(requestId);
                return expired.Connection == connection && expired.Document == epoch
                    ? WindowBridgeOperationStatus.Expired(requestId)
                    : WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this presentation.");
            }
            return operation.InitiatingConnection == connection && operation.InitiatingDocument == epoch
                ? operation.Status
                : WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this presentation.");
        }
    }

    internal async ValueTask<WindowBridgeOperationStatus> WaitForTerminalAsync(WindowBridgeReference source, string requestId,
        CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        OperationEntry? operation;
        bool expired;
        lock (_gate) { var key = new OperationKey(source.Id, requestId); _operations.TryGetValue(key, out operation); expired = _expired.ContainsKey(key); }
        if (operation is null) return expired ? WindowBridgeOperationStatus.Expired(requestId) : WindowBridgeOperationStatus.Unknown(requestId);
        await operation.Completed.Task.WaitAsync(observerCancellation).ConfigureAwait(false);
        return operation.Status;
    }

    internal async ValueTask<WindowBridgeOperationStatus> WaitForTerminalFromConnectionAsync(WindowBridgeReference source,
        WindowBridgeConnection connection, string requestId, CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        OperationEntry? operation;
        bool expired;
        lock (_gate)
        {
            var key = new OperationKey(source.Id, requestId);
            _operations.TryGetValue(key, out operation);
            expired = _expired.ContainsKey(key);
            if (operation is not null && operation.InitiatingConnection != connection)
                return WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this connection.");
            if (operation is null && expired && _expired[key].Connection != connection)
                return WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this connection.");
        }
        if (operation is null) return expired ? WindowBridgeOperationStatus.Expired(requestId) : WindowBridgeOperationStatus.Unknown(requestId);
        await operation.Completed.Task.WaitAsync(observerCancellation).ConfigureAwait(false);
        return operation.Status;
    }

    /// <summary>Observes accepted work from its initiating document after the component presentation detaches.</summary>
    internal async ValueTask<WindowBridgeOperationStatus> WaitForTerminalFromPresentationAsync(WindowBridgeReference source,
        WindowBridgeConnection connection, WindowBridgeDocumentEpoch epoch, string requestId,
        CancellationToken observerCancellation = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(epoch);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        OperationEntry? operation;
        bool expired;
        lock (_gate)
        {
            var key = new OperationKey(source.Id, requestId);
            _operations.TryGetValue(key, out operation);
            expired = _expired.TryGetValue(key, out OperationTombstone? tombstone);
            if (operation is not null && (operation.InitiatingConnection != connection || operation.InitiatingDocument != epoch))
                return WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this presentation.");
            if (operation is null && expired && (tombstone!.Connection != connection || tombstone.Document != epoch))
                return WindowBridgeOperationStatus.Rejected(requestId, "The operation does not belong to this presentation.");
        }
        if (operation is null) return expired ? WindowBridgeOperationStatus.Expired(requestId) : WindowBridgeOperationStatus.Unknown(requestId);
        await operation.Completed.Task.WaitAsync(observerCancellation).ConfigureAwait(false);
        return operation.Status;
    }

    private async Task RunOperationAsync(Entry source, OperationEntry operation,
        Func<CancellationToken, Task<WindowBridgeOperationResult>> work,
        Func<WindowBridgeOperationResult, string>? projectState)
    {
        // An admitted command can have synchronous work before its first incomplete await.
        // Do not hold the native callback on that synchronous prefix.
        await Task.Yield();
        WindowBridgeOperationStatus status = new(operation.Key.RequestId, WindowBridgeOperationKind.Failed, null, "The operation failed.");
        WindowBridgeOperationResult? result = null;
        try
        {
            WindowBridgeOperationResult completed = await work(_shutdown.Token).ConfigureAwait(false);
            if (completed.Kind is WindowBridgeOperationKind.Unknown or WindowBridgeOperationKind.Expired or WindowBridgeOperationKind.Running)
                throw new ArgumentException("Operation work must return a terminal result.");
            result = completed;
        }
        catch (OperationCanceledException)
        {
            status = new(operation.Key.RequestId, WindowBridgeOperationKind.Cancelled, null, null);
        }
        catch (ArgumentException)
        {
            status = WindowBridgeOperationStatus.Rejected(operation.Key.RequestId, "The operation was rejected.");
        }
        catch (Exception)
        {
            status = new(operation.Key.RequestId, WindowBridgeOperationKind.Failed, null, "The operation failed.");
        }

        long? projectedGeneration = null;
        if (result is not null)
        {
            string? state = null;
            PublicationFrame? publication = null;
            lock (_gate)
            {
                if (!_disposed && source.Attachment is not null && !source.Detaching && source.Generation == operation.SourceGeneration && projectState is not null)
                {
                    source.Publishing++;
                    publication = new PublicationFrame(source, source.Generation);
                    projectedGeneration = publication.Generation;
                }
            }
            if (publication is not null)
            {
                PublicationFrame? previousPublication = _publishing.Value;
                _publishing.Value = publication;
                try { state = projectState!(result); }
                catch { state = null; }
                finally
                {
                    _publishing.Value = previousPublication;
                    lock (_gate)
                    {
                        source.Publishing--;
                        if (_disposed || source.Detaching || source.Attachment is null || source.Generation != publication.Generation)
                            state = null;
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            status = new(operation.Key.RequestId, result.Kind, state, result.Error);
        }
        lock (_gate)
        {
            if (status.State is not null && (projectedGeneration is null || _disposed || source.Detaching
                || source.Attachment is null || source.Generation != projectedGeneration.Value))
                status = status with { State = null };
            operation.Status = status;
            operation.Completed.TrySetResult();
            _terminalOrder.AddLast(operation.Key);
            TrimTerminals();
        }
    }

    private void TrimTerminals()
    {
        while (_terminalOrder.Count > _maximumRetainedTerminals)
        {
            OperationKey key = _terminalOrder.First!.Value;
            _terminalOrder.RemoveFirst();
            _operations.Remove(key, out OperationEntry? evicted);
            if (_maximumExpired == 0) continue;
            _expired.Add(key, new OperationTombstone(evicted?.InitiatingConnection, evicted?.InitiatingDocument));
            _expiredOrder.AddLast(key);
            while (_expiredOrder.Count > _maximumExpired)
            {
                _expired.Remove(_expiredOrder.First!.Value);
                _expiredOrder.RemoveFirst();
            }
        }
    }

    private void AttachEntry(Entry entry, bool removeNewEntryOnFailure = false)
    {
        WindowBridgeAttachment? attachment = null;
        Exception? failure = null;
        Entry? previous = _attachingEntry.Value;
        _attachingEntry.Value = entry;
        IDisposable? manifestUpdate = null;
        try
        {
            manifestUpdate = (_transport as IWindowBridgeEndpointManifestBatcher)?.BeginEndpointManifestUpdate();
            attachment = entry.CreateAttachment();
        }
        catch (Exception exception) { failure = exception; }
        finally
        {
            try { manifestUpdate?.Dispose(); }
            catch (Exception exception) { failure ??= exception; }
            _attachingEntry.Value = previous;
        }
        if (failure is null && attachment is null)
            failure = new InvalidOperationException("A ViewModel route attachment returned no binding.");

        bool disposeAttachment = false;
        lock (_gate)
        {
            entry.Attaching = false;
            if (failure is null && !_disposed && !entry.SuspendRequested)
            {
                entry.Attachment = attachment;
                EndAttachmentTransition();
            }
            else
            {
                entry.SuspendRequested = false;
                entry.Detaching = attachment is not null;
                disposeAttachment = attachment is not null;
                if (failure is not null) entry.PendingPresentation = false;
                if (failure is not null && removeNewEntryOnFailure)
                {
                    _references.Remove(entry.Reference.Id);
                    if (_models.TryGetValue(entry.Model, out Dictionary<string, Entry>? variants)
                        && variants.TryGetValue(entry.Reference.Kind, out Entry? current) && ReferenceEquals(current, entry))
                        variants.Remove(entry.Reference.Kind);
                }
            }
            Monitor.PulseAll(_gate);
        }
        if (disposeAttachment) DisposeAttachment(entry, attachment!);
        if (failure is not null)
        {
            if (!disposeAttachment)
            {
                lock (_gate) EndAttachmentTransition();
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void DisposeAttachment(Entry entry, WindowBridgeAttachment attachment)
    {
        Entry? previous = _disposingEntry.Value;
        _disposingEntry.Value = entry;
        IDisposable? manifestUpdate = null;
        List<Exception>? failures = null;
        try
        {
            try { manifestUpdate = (_transport as IWindowBridgeEndpointManifestBatcher)?.BeginEndpointManifestUpdate(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            try { attachment.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        finally
        {
            try { manifestUpdate?.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            finally
            {
                _disposingEntry.Value = previous;
                _ = CompleteAttachmentAsync(entry, attachment, failures);
            }
        }
        if (failures is null) return;
        if (failures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        throw new AggregateException(failures);
    }

    private async Task CompleteAttachmentAsync(Entry entry, WindowBridgeAttachment attachment, List<Exception>? failures = null)
    {
        Exception? completionFailure = null;
        try { await attachment.Completion.ConfigureAwait(false); }
        catch (Exception exception) { completionFailure = exception; }
        bool reattach;
        lock (_gate)
        {
            if (failures is not null) _attachmentFailures.AddRange(failures);
            if (completionFailure is not null) _attachmentFailures.Add(completionFailure);
            entry.Detaching = false;
            reattach = entry.ReattachRequested && !_disposed && _references.ContainsKey(entry.Reference.Id);
            entry.ReattachRequested = false;
            if (reattach) entry.Attaching = true;
            else EndAttachmentTransition();
            Monitor.PulseAll(_gate);
        }
        if (!reattach) return;
        try { AttachEntry(entry); }
        catch (Exception exception)
        {
            // A reattach is scheduled from an observed attachment-drain
            // continuation. AttachEntry has already ended its transition on a
            // factory failure; retain the error so close observes it instead
            // of leaving an unobserved task fault behind.
            lock (_gate) _attachmentFailures.Add(exception);
        }
    }

    private void BeginAttachmentTransition()
    {
        if (_attachmentTransitions++ == 0)
            _attachmentsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void EndAttachmentTransition()
    {
        if (--_attachmentTransitions != 0) return;
        _attachmentsDrained?.TrySetResult();
    }

    private void Release(PresentationLease lease)
    {
        IDisposable? activation = null;
        lock (_gate)
        {
            // A suspended reference can be reattached and mounted with the same
            // key before its former handle is disposed. Only that handle's own
            // lease may be removed by its late cleanup.
            if (_presentations.TryGetValue(lease.Key, out PresentationLease? current)
                && ReferenceEquals(current, lease))
            {
                _presentations.Remove(lease.Key);
                activation = lease.Activation;
            }
        }
        activation?.Dispose();
    }

    private bool IsCurrentDocumentCore(WindowBridgeConnection connection, WindowBridgeDocumentEpoch epoch) =>
        !_disconnectedConnections.Contains(connection)
        && _documents.TryGetValue(connection, out DocumentEntry? document) && document.Current == epoch;

    private bool HasCurrentDocumentPresentation(Entry entry) =>
        _presentations.Keys.Any(key => key.ReferenceId == entry.Reference.Id && key.DocumentEpoch is not null
            && !_disconnectedConnections.Contains(new WindowBridgeConnection(key.ClientId, key.ConnectionId))
            && _documents.TryGetValue(new WindowBridgeConnection(key.ClientId, key.ConnectionId), out DocumentEntry? document)
            && document.Current.Value == key.DocumentEpoch);

    private bool CanDispatchOutboundPublicationUnsafe(OutboundPublication publication) =>
        !_disposed && publication.Entry.Generation == publication.Generation
        && publication.Entry.Attachment is not null && !publication.Entry.Detaching
        && HasCurrentDocumentPresentation(publication.Entry);

    private void DeferOutboundPublicationUnsafe(OutboundPublication publication)
    {
        Entry entry = publication.Entry;
        if (entry.DeferredOutboundPublication is null)
            entry.DeferredOutboundPublicationSequence = checked(++_nextDeferredPublicationSequence);
        entry.DeferredOutboundPublication = publication;
    }

    /// <summary>
    /// Moves the oldest live deferred entry into available bounded queue slots.
    /// Each exposed entry owns only one deferred/latest record, so rejected
    /// notifications cannot create an unbounded overflow queue.
    /// </summary>
    private void PromoteDeferredOutboundPublicationsUnsafe()
    {
        while (!_disposed && _outboundPublicationCount < _maximumOutboundPublications)
        {
            Entry? candidate = null;
            foreach (Entry entry in _references.Values)
            {
                if (entry.DeferredOutboundPublication is not { } deferred) continue;
                if (!CanDispatchOutboundPublicationUnsafe(deferred))
                {
                    entry.DeferredOutboundPublication = null;
                    continue;
                }
                if (candidate is null || entry.DeferredOutboundPublicationSequence < candidate.DeferredOutboundPublicationSequence)
                    candidate = entry;
            }
            if (candidate?.DeferredOutboundPublication is not { } publication) return;
            candidate.DeferredOutboundPublication = null;
            _pendingOutboundPublications.Add(candidate, publication);
            _outboundPublications.Enqueue(candidate);
            _outboundPublicationCount++;
        }
    }

    private async Task DrainOutboundPublicationsAsync()
    {
        while (true)
        {
            OutboundPublication publication;
            ActiveOutboundPublication active;
            lock (_gate)
            {
                if (_outboundPublications.Count == 0)
                {
                    _outboundPublisherActive = false;
                    return;
                }
                Entry entry = _outboundPublications.Dequeue();
                publication = _pendingOutboundPublications[entry];
                _pendingOutboundPublications.Remove(entry);
                active = new();
                _activeOutboundPublications.Add(entry, active);
            }

            try
            {
                bool dispatch;
                lock (_gate)
                {
                    dispatch = CanDispatchOutboundPublicationUnsafe(publication);
                }
                if (dispatch) _transport.Publish(publication.Route, publication.Payload);
            }
            catch
            {
                // Public invalidation is best-effort. A failed host send must
                // not fault model notification or retain the window scope.
            }
            finally
            {
                lock (_gate)
                {
                    _activeOutboundPublications.Remove(publication.Entry);
                    if (active.FollowUp is { } followUp)
                        DeferOutboundPublicationUnsafe(followUp);
                    _outboundPublicationCount--;
                    PromoteDeferredOutboundPublicationsUnsafe();
                    if (_outboundPublicationCount == 0)
                        _outboundPublicationsDrained?.TrySetResult();
                }
            }
            await Task.Yield();
        }
    }

    private static string RenderPublicInvalidation(long revision)
    {
        var bytes = new System.Buffers.ArrayBufferWriter<byte>();
        using var writer = new System.Text.Json.Utf8JsonWriter(bytes);
        writer.WriteStartObject();
        writer.WriteString("protocol", "runic-sdk.fixture-public-invalidation");
        writer.WriteNumber("version", 1);
        writer.WriteNumber("revision", revision);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(bytes.WrittenSpan);
    }

    private bool HasPresentationCore(WindowBridgeReference reference, WindowBridgeConnection connection,
        WindowBridgeDocumentEpoch? epoch, string? presentationId = null)
    {
        if (epoch is null ? _disconnectedConnections.Contains(connection) : !IsCurrentDocumentCore(connection, epoch))
            return false;

        // Accepted operations carry their exact component lease. Use the keyed
        // lookup rather than scanning every presentation in a busy window.
        if (presentationId is not null)
            return _presentations.ContainsKey(new(reference.Id, connection.ClientId, connection.ConnectionId,
                epoch?.Value, presentationId));

        return _presentations.Keys.Any(key => key.ReferenceId == reference.Id
            && key.ClientId == connection.ClientId && key.ConnectionId == connection.ConnectionId
            && key.DocumentEpoch == epoch?.Value);
    }

    private static void RunCleanup(IEnumerable<Action> actions)
    {
        List<Exception>? failures = null;
        foreach (Action action in actions)
        {
            try { action(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }

    /// <summary>
    /// Seals this window synchronously and requests cancellation before returning.
    /// Native hosts may bound their wait on <see cref="WindowBridgeCloseAdmission.Completion"/>;
    /// attachments and the owned scope remain alive until that completion drains.
    /// </summary>
    internal WindowBridgeCloseAdmission BeginClose()
    {
        TaskCompletionSource completion;
        bool begin = false;
        lock (_gate)
        {
            if (_closeCompletion is not null) return new(_remainingOperationsAtClose, _closeCompletion.Task);
            _disposed = true;
            _remainingOperationsAtClose = _operations.Values.Count(operation => !operation.Completed.Task.IsCompleted);
            completion = _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            begin = true;
        }
        Task cancellation;
        try { cancellation = _shutdown.CancelAsync(); }
        catch (Exception exception) { cancellation = Task.FromException(exception); }
        if (begin)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await DisposeCoreAsync(cancellation).ConfigureAwait(false);
                    completion.TrySetResult();
                }
                catch (Exception exception) { completion.TrySetException(exception); }
            });
        }
        return new(_remainingOperationsAtClose, completion.Task);
    }

    public ValueTask DisposeAsync() => new(BeginClose().Completion);

    private async Task DisposeCoreAsync(Task cancellation)
    {
        List<Exception>? failures = null;
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        PresentationLease[] leases;
        (Entry Entry, WindowBridgeAttachment Attachment)[] attachments;
        Task[] operations;
        Task attachmentTransitions;
        Task outboundPublicationDrain;
        lock (_gate)
        {
            leases = _presentations.Values.ToArray();
            foreach (Entry entry in _references.Values.Where(entry => entry.Attaching)) entry.SuspendRequested = true;
            attachments = _references.Values.Where(entry => entry.Attachment is WindowBridgeAttachment)
                .Select(entry =>
                {
                    var attachment = (WindowBridgeAttachment)entry.Attachment!;
                    entry.Attachment = null;
                    entry.Detaching = true;
                    BeginAttachmentTransition();
                    return (entry, attachment);
                }).ToArray();
            operations = _operations.Values.Select(operation => operation.Completed.Task).ToArray();
            attachmentTransitions = _attachmentsDrained?.Task ?? Task.CompletedTask;
            outboundPublicationDrain = _outboundPublicationsDrained?.Task ?? Task.CompletedTask;
        }
        foreach (PresentationLease lease in leases)
        {
            try { lease.Handle.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        foreach ((Entry entry, WindowBridgeAttachment attachment) in attachments)
        {
            try { DisposeAttachment(entry, attachment); }
            // DisposeAttachment records a synchronous manifest-batch failure
            // with the attachment completion before it returns. Keep draining
            // the remaining attachments; the collected close failure below
            // reports it once the transition has finished.
            catch (Exception) { }
        }
        try { await attachmentTransitions.ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        try { await outboundPublicationDrain.ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        lock (_gate)
            if (_attachmentFailures.Count > 0)
            {
                (failures ??= []).AddRange(_attachmentFailures);
                _attachmentFailures.Clear();
            }
        try { await Task.WhenAll(operations).ConfigureAwait(false); }
        catch (Exception exception) { (failures ??= []).Add(exception); }
        if (_ownedScope is not null)
        {
            try { await _ownedScope.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        _shutdown.Dispose();
        if (failures is not null) throw new AggregateException(failures);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class Entry(WindowBridgeReference reference, object model, Func<WindowBridgeAttachment> createAttachment)
    {
        internal WindowBridgeReference Reference { get; } = reference;
        internal object Model { get; } = model;
        internal Func<WindowBridgeAttachment> CreateAttachment { get; } = createAttachment;
        internal WindowBridgeAttachment? Attachment { get; set; }
        internal bool Detaching { get; set; }
        internal bool Attaching { get; set; }
        internal bool SuspendRequested { get; set; }
        internal bool ReattachRequested { get; set; }
        internal bool PendingPresentation { get; set; }
        internal bool RootOwned { get; set; }
        internal long Generation { get; set; }
        internal long OwnershipGeneration { get; set; }
        internal long InvalidationRevision { get; set; }
        internal OutboundPublication? DeferredOutboundPublication { get; set; }
        internal long DeferredOutboundPublicationSequence { get; set; }
        internal int Publishing { get; set; }
    }

    private sealed record OutboundPublication(Entry Entry, long Generation, string Route, string Payload);

    /// <summary>
    /// One active entry may retain one latest revision while its synchronous
    /// host send is in flight. The worker is single-threaded, so this adds at
    /// most one dirty record beyond the bounded entry queue.
    /// </summary>
    private sealed class ActiveOutboundPublication
    {
        internal OutboundPublication? FollowUp { get; set; }
    }

    private sealed class OperationEntry(OperationKey key, long sourceGeneration, WindowBridgeConnection? initiatingConnection,
        WindowBridgeDocumentEpoch? initiatingDocument)
    {
        internal OperationKey Key { get; } = key;
        internal long SourceGeneration { get; } = sourceGeneration;
        internal WindowBridgeConnection? InitiatingConnection { get; } = initiatingConnection;
        internal WindowBridgeDocumentEpoch? InitiatingDocument { get; } = initiatingDocument;
        internal WindowBridgeOperationStatus Status { get; set; } = WindowBridgeOperationStatus.Running(key.RequestId);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? Task { get; set; }
    }

    private readonly record struct PresentationKey(string ReferenceId, string ClientId, string ConnectionId,
        string? DocumentEpoch, string PresentationId);
    private readonly record struct OperationKey(string SourceId, string RequestId);
    private sealed record OperationTombstone(WindowBridgeConnection? Connection, WindowBridgeDocumentEpoch? Document);
    private sealed record RetiredChild(Entry Entry, long OwnershipGeneration);
    private sealed record PublicationFrame(Entry Entry, long Generation);

    private sealed class DocumentEntry(WindowBridgeDocumentEpoch epoch)
    {
        internal WindowBridgeDocumentEpoch Current { get; set; } = epoch;
    }

    private sealed class PresentationLease
    {
        internal PresentationLease(WindowBridgeSession session, PresentationKey key, IDisposable? activation)
        {
            Key = key;
            Activation = activation;
            Handle = new WindowBridgePresentationLease(() => session.Release(this));
        }

        internal PresentationKey Key { get; }
        internal IDisposable? Activation { get; set; }
        internal WindowBridgePresentationLease Handle { get; }
    }
}

internal sealed record WindowBridgeReference(string Kind, string Id);

internal sealed class WindowBridgePresentationLease(Action release) : IDisposable
{
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}

internal enum WindowBridgeOperationKind { Unknown, Expired, Running, Succeeded, Rejected, Cancelled, Failed }

internal sealed record WindowBridgeOperationResult(WindowBridgeOperationKind Kind, string? Error = null)
{
    internal static WindowBridgeOperationResult Succeeded() => new(WindowBridgeOperationKind.Succeeded);
}

internal sealed record WindowBridgeOperationStatus(string RequestId, WindowBridgeOperationKind Kind, string? State, string? Error)
{
    internal static WindowBridgeOperationStatus Running(string requestId) => new(requestId, WindowBridgeOperationKind.Running, null, null);
    internal static WindowBridgeOperationStatus Unknown(string requestId) => new(requestId, WindowBridgeOperationKind.Unknown, null, null);
    internal static WindowBridgeOperationStatus Expired(string requestId) => new(requestId, WindowBridgeOperationKind.Expired, null, null);
    internal static WindowBridgeOperationStatus Rejected(string requestId, string error) => new(requestId, WindowBridgeOperationKind.Rejected, null, error);
}

internal sealed record WindowBridgeOperationAcceptance(string RequestId, bool Accepted, WindowBridgeOperationStatus Status);

/// <summary>Host-close admission. Completion retains window-owned cleanup after a bounded native close wait.</summary>
internal sealed record WindowBridgeCloseAdmission(int RemainingOperations, Task Completion);
