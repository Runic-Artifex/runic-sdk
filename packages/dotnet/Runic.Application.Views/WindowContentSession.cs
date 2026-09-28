using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using System.ComponentModel;

namespace Runic.Application.Views;

/// <summary>A window-local reference to a ViewModel presented as content.</summary>
public sealed record PageReference(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("id")] string Id);

/// <summary>
/// Experimental instance routing for content ViewModels. The window owns this
/// session and the application or DI scope continues to own the ViewModels.
/// </summary>
public sealed class WindowContentSession : IDisposable
{
    private readonly object _gate = new();
    private readonly IBridgeTransport _transport;
    private readonly IRunicViewLocator? _viewLocator;
    private readonly BridgeOperationRouter _operations;
    private readonly BridgeInteractionRouter _interactions;
    private readonly BridgeFieldWriteRegistryProvider _fieldWrites;
    private readonly RunicModelContextRegistry _modelContexts;
    private IRunicModelContextLease? _rootModelLease;
    // Sessions constructed for a standalone content host still need one graph
    // lane before their first Expose. It has no registry identity of its own;
    // every exposed model receives an ordinary, releasable binding lease.
    private IRunicModelContext? _sessionOwnedModelContext;
    private readonly HashSet<IRunicModelContextLease> _contentModelLeases = [];
    private ConditionalWeakTable<object, Dictionary<string, Entry>> _entries = new();
    private readonly HashSet<Entry> _activeEntries = [];
    private readonly Dictionary<object, Dictionary<string, Entry>> _slots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<InteractionMountAttachment> _rootInteractionMounts = [];
    // A logical View instance has one mounted presentation owner at a time.
    // The factory overload can be useful for custom composition, so enforce the
    // same rule even when a caller accidentally returns a singleton View.
    private readonly HashSet<IRunicView> _ownedViews = new(ReferenceEqualityComparer.Instance);
    // User lifetime callbacks run while an attachment is releasing, outside
    // _gate. A synchronous Expose from that same stack cannot wait for the
    // release without waiting for itself. Keep that narrow state thread-local so
    // concurrent callers still wait for the retired route normally.
    private readonly ThreadLocal<DetachmentFrame?> _detachingOnCurrentFlow = new();
    private bool _disposed;

    public WindowContentSession(IBridgeTransport transport, IRunicViewLocator? viewLocator = null,
        CancellationToken operationShutdown = default, object? rootModel = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _viewLocator = viewLocator;
        _modelContexts = RunicModelContextRegistry.Shared;
        if (rootModel is not null)
            _rootModelLease = _modelContexts.Acquire(static () => new RunicModelContext(), rootModel);
        else
            _sessionOwnedModelContext = new RunicModelContext();
        BridgeOperationRouter? operations = null;
        BridgeInteractionRouter? interactions = null;
        BridgeFieldWriteRegistryProvider? fieldWrites = null;
        try
        {
            operations = new BridgeOperationRouter(_transport, Guid.NewGuid().ToString("N"), operationShutdown);
            interactions = new BridgeInteractionRouter(this, _transport);
            fieldWrites = new BridgeFieldWriteRegistryProvider(Guid.NewGuid().ToString("N"));
            _operations = operations;
            _interactions = interactions;
            _fieldWrites = fieldWrites;
        }
        catch
        {
            try { fieldWrites?.Dispose(); }
            finally
            {
                try { interactions?.Dispose(); }
                finally
                {
                    try { operations?.Dispose(); }
                    finally
                    {
                        try { _rootModelLease?.Dispose(); }
                        finally
                        {
                            if (_sessionOwnedModelContext is { } context)
                                RunicModelContextDisposal.DisposeSynchronously(context);
                        }
                    }
                }
            }
            throw;
        }
    }

    /// <summary>
    /// Gets the execution context shared by this window's ViewModel graph. A model already
    /// owned by an application-wide graph keeps that existing context when another window
    /// attaches to it.
    /// </summary>
    public IRunicModelContext? ModelContext => _rootModelLease?.Context ?? _sessionOwnedModelContext;

    // Generated Bridges remain in this assembly through their host-neutral
    // base class. The router is intentionally internal: applications do not
    // author operation routes directly.
    internal BridgeOperationRouter Operations => _operations;

    /// <summary>Window-owned interaction request router used by optional UI adapters.</summary>
    public BridgeInteractionRouter Interactions => _interactions;

    /// <summary>
    /// Adds trusted presentation acknowledgement for a root bridge route which
    /// exposes interactions. Content routes already own this lifecycle through
    /// their normal mount and unmount endpoints.
    /// </summary>
    public IDisposable AttachRootInteractionPresentation(string route)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        lock (_gate)
        {
            ThrowIfDisposed();
            var attachment = new InteractionMountAttachment(this, _transport, route);
            _rootInteractionMounts.Add(attachment);
            return new RootInteractionMountLease(this, attachment);
        }
    }

    // Registry ownership follows this browser Window/session. Generated
    // Bridges use it internally; application code never receives or disposes
    // a shared field registry.
    internal BridgeFieldWriteRegistryProvider FieldWrites => _fieldWrites;

    /// <summary>
    /// Stops new window-owned operation admission and waits for accepted work
    /// to reach a terminal result. It leaves content, fields, and routes owned
    /// by this session intact until the owner later disposes the session.
    /// </summary>
    public async ValueTask<WindowContentSessionCloseResult> BeginCloseAsync(TimeSpan timeout)
    {
        lock (_gate) ThrowIfDisposed();
        var result = await _operations.BeginCloseAsync(timeout).ConfigureAwait(false);
        return new(result.Drained, result.RemainingRunningOperations);
    }

    /// <summary>
    /// Attaches one route Bridge while resolving a fresh logical View for every
    /// mounted web presentation. The ViewModel remains application-owned; each
    /// mounted View owns only its own attached, mounted, and disposable lifetime.
    /// </summary>
    public IDisposable AttachPresentation<TView, TViewModel>(TViewModel viewModel, string route,
        Func<IBridgeTransport, TViewModel, string, IDisposable> attachBridge, string? contract = null)
        where TView : class, IRunicView
        where TViewModel : class
    {
        if (_viewLocator is null)
            throw new InvalidOperationException($"No .NET view locator is registered for {typeof(TView).FullName}.");
        return AttachPresentation(viewModel, route, attachBridge, () =>
            _viewLocator.Locate<TView, TViewModel>(contract)
            ?? throw new InvalidOperationException($"The view locator returned no {typeof(TView).FullName}."));
    }

    /// <summary>
    /// Variant for an application-owned factory. It must produce a distinct
    /// View for concurrently mounted presentations. Returning one already
    /// mounted View is rejected with an ownership error.
    /// </summary>
    public IDisposable AttachPresentation<TView, TViewModel>(TViewModel viewModel, string route,
        Func<IBridgeTransport, TViewModel, string, IDisposable> attachBridge, Func<TView> createView)
        where TView : class, IRunicView
        where TViewModel : class
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(attachBridge);
        ArgumentNullException.ThrowIfNull(createView);
        lock (_gate) ThrowIfDisposed();

        IDisposable? bridge = null;
        ViewPresentationAttachment? attachment = null;
        try
        {
            bridge = attachBridge(_transport, viewModel, route);
            attachment = new ViewPresentationAttachment(
                bridge!,
                () =>
                {
                    var view = createView()
                        ?? throw new InvalidOperationException($"The View factory returned no {typeof(TView).FullName}.");
                    return view;
                },
                view => view.DataContext = viewModel,
                ClaimView,
                ReleaseView);
            lock (_gate)
            {
                if (!_disposed) return attachment;
            }
            attachment.Dispose();
            throw new ObjectDisposedException(nameof(WindowContentSession));
        }
        catch
        {
            if (attachment is not null) attachment.Dispose();
            else bridge?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Exposes an object in this window. Re-exposing the same object returns
    /// the same reference, including after Suspend. The factory is expected
    /// to attach a generated ViewModel Bridge using the supplied route prefix.
    /// </summary>
    public PageReference Expose<T>(string kind, T viewModel,
        Func<IBridgeTransport, T, string, IDisposable> attach) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(attach);
        // Bridge construction itself creates field/data subscriptions which run model
        // turns. Reserve and construct content on that same owner so one thread cannot
        // wait for an Attaching entry while its construction waits behind that thread.
        if (ModelContext is IRunicSynchronousModelContext synchronous)
            return synchronous.IsExecuting
                ? ExposeCore(kind, viewModel, attach)
                : synchronous.Run(() => ExposeCore(kind, viewModel, attach));
        if (ModelContext is { } context)
            return context.InvokeAsync(() => ExposeCore(kind, viewModel, attach)).AsTask().GetAwaiter().GetResult();
        return ExposeCore(kind, viewModel, attach);
    }

    private PageReference ExposeCore<T>(string kind, T viewModel,
        Func<IBridgeTransport, T, string, IDisposable> attach) where T : class
    {
        Entry entry;
        var created = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            BridgeSnapshotPublication.ThrowIfInactive();
            while (true)
            {
                if (_entries.TryGetValue(viewModel, out var variants) && variants.TryGetValue(kind, out var existing))
                {
                    WaitForDetachment(existing);
                    // Waiting releases the session gate. Forget may have
                    // removed this identity, or a later Expose may have made
                    // a new one while the prior attachment was retiring.
                    // Never revive that detached Entry outside the table.
                    if (!_entries.TryGetValue(viewModel, out var currentVariants)
                        || !currentVariants.TryGetValue(kind, out var current)
                        || !ReferenceEquals(existing, current)) continue;
                    if (existing.Attachment is not null) return existing.Reference;
                    if (existing.Attaching)
                    {
                        if (IsExecutingOnModelContext())
                            throw new InvalidOperationException(
                                "A content bridge recursively exposed itself while its attachment was being constructed.");
                        Monitor.Wait(_gate);
                        ThrowIfDisposed();
                        continue;
                    }
                    BridgeSnapshotPublication.ThrowIfInactive();
                    existing.Attaching = true;
                    entry = existing;
                    break;
                }

                var reference = new PageReference(kind, Guid.NewGuid().ToString("N"));
                BridgeSnapshotPublication.ThrowIfInactive();
                entry = new Entry(reference, AcquireContentModelLease(viewModel));
                entry.Attaching = true;
                if (variants is null) _entries.Add(viewModel, variants = new Dictionary<string, Entry>(StringComparer.Ordinal));
                variants.Add(kind, entry);
                created = true;
                break;
            }
        }

        IDisposable? attachment = null;
        try
        {
            attachment = AttachContent(viewModel, Prefix(entry.Reference), attach);
        }
        catch
        {
            CompleteFailedAttachment(viewModel, kind, entry, created);
            throw;
        }

        lock (_gate)
        {
            entry.Attaching = false;
            if (!_disposed && _entries.TryGetValue(viewModel, out var variants)
                && variants.TryGetValue(kind, out var current) && ReferenceEquals(current, entry))
            {
                entry.Attachment = attachment;
                _activeEntries.Add(entry);
                Monitor.PulseAll(_gate);
                return entry.Reference;
            }
            Monitor.PulseAll(_gate);
        }
        attachment.Dispose();
        entry.ContextLease?.Dispose();
        throw new ObjectDisposedException(nameof(WindowContentSession));
    }

    /// <summary>
    /// Presents a ViewModel in one content property. Replacing or clearing the
    /// property suspends its old endpoint only if no other property in this
    /// window still presents that same object.
    /// </summary>
    public PageReference Present<T>(object owner, string property, string kind, T viewModel,
        Func<IBridgeTransport, T, string, IDisposable> attach) where T : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        var reference = Expose(kind, viewModel, attach);
        IDisposable? previousAttachment = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var current = _entries.GetValue(viewModel, _ => throw new InvalidOperationException())[kind];
            if (!_slots.TryGetValue(owner, out var properties))
                _slots.Add(owner, properties = new Dictionary<string, Entry>(StringComparer.Ordinal));
            properties.TryGetValue(property, out var previous);
            properties[property] = current;
            if (previous is not null && !ReferenceEquals(previous, current) && !IsPresented(previous))
                previousAttachment = SuspendCore(previous);
        }
        previousAttachment?.Dispose();
        return reference;
    }

    /// <summary>Retains a collection item by ViewModel identity, so reordering does not detach its route.</summary>
    public PageReference PresentItem<T>(object owner, string property, string kind, T viewModel,
        Func<IBridgeTransport, T, string, IDisposable> attach) where T : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        var reference = Expose(kind, viewModel, attach);
        lock (_gate)
        {
            ThrowIfDisposed();
            var entry = _entries.GetValue(viewModel, _ => throw new InvalidOperationException())[kind];
            if (!_slots.TryGetValue(owner, out var properties))
                _slots.Add(owner, properties = new Dictionary<string, Entry>(StringComparer.Ordinal));
            properties[CollectionSlot(property, reference.Id)] = entry;
            return reference;
        }
    }

    /// <summary>Releases collection items omitted by the latest published snapshot.</summary>
    public void PruneCollection(object owner, string property, IReadOnlySet<string> activeIds)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        ArgumentNullException.ThrowIfNull(activeIds);
        List<IDisposable> attachments = [];
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_slots.TryGetValue(owner, out var properties)) return;
            var prefix = property + "\0";
            foreach (var (slot, entry) in properties.ToArray())
            {
                if (!slot.StartsWith(prefix, StringComparison.Ordinal) || activeIds.Contains(slot[prefix.Length..]))
                    continue;
                properties.Remove(slot);
                if (!IsPresented(entry) && SuspendCore(entry) is { } attachment)
                    attachments.Add(attachment);
            }
            if (properties.Count == 0) _slots.Remove(owner);
        }
        foreach (var attachment in attachments) attachment.Dispose();
    }

    private static string CollectionSlot(string property, string id) => property + "\0" + id;

    public void Clear(object owner, string property)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        IDisposable? attachment = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_slots.TryGetValue(owner, out var properties)
                || !properties.Remove(property, out var previous)) return;
            if (properties.Count == 0) _slots.Remove(owner);
            if (!IsPresented(previous)) attachment = SuspendCore(previous);
        }
        attachment?.Dispose();
    }

    public void ClearOwner(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        List<IDisposable> attachments = [];
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_slots.Remove(owner, out var properties)) return;
            foreach (var previous in properties.Values.Distinct())
                if (!IsPresented(previous) && SuspendCore(previous) is { } attachment)
                    attachments.Add(attachment);
        }
        foreach (var attachment in attachments) attachment.Dispose();
    }

    /// <summary>Detaches routes while retaining the object's window identity.</summary>
    public void Suspend(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        List<IDisposable> attachments = [];
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_entries.TryGetValue(viewModel, out var variants))
                foreach (var entry in variants.Values)
                    if (SuspendCore(entry) is { } attachment) attachments.Add(attachment);
        }
        foreach (var attachment in attachments) attachment.Dispose();
    }

    /// <summary>Releases only mounts owned by a disconnected host client.</summary>
    public void ReleaseConnection(string connectionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionKey);
        ContentAttachment[] attachments;
        InteractionMountAttachment[] rootMounts;
        lock (_gate)
        {
            if (_disposed) return;
            attachments = _activeEntries.Select(entry => entry.Attachment)
                .OfType<ContentAttachment>().ToArray();
            rootMounts = _rootInteractionMounts.ToArray();
        }
        _interactions.ReleaseConnection(connectionKey);
        foreach (var attachment in attachments) attachment.ReleaseConnection(connectionKey);
        foreach (var attachment in rootMounts) attachment.ReleaseConnection(connectionKey);
    }

    /// <summary>Releases identity and routes for an object no longer retained by this window.</summary>
    public void Forget(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        List<IDisposable> attachments = [];
        List<IRunicModelContextLease> modelLeases = [];
        var forgetFields = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_entries.TryGetValue(viewModel, out var variants)) return;
            _entries.Remove(viewModel);
            forgetFields = viewModel is INotifyPropertyChanged;
            foreach (var (owner, properties) in _slots.ToArray())
            {
                foreach (var property in properties.Where(pair => variants.Values.Contains(pair.Value))
                    .Select(pair => pair.Key).ToArray()) properties.Remove(property);
                if (properties.Count == 0) _slots.Remove(owner);
            }
            foreach (var entry in variants.Values)
            {
                if (SuspendCore(entry) is { } attachment) attachments.Add(attachment);
                if (entry.ContextLease is { } lease && _contentModelLeases.Remove(lease)) modelLeases.Add(lease);
            }
        }
        foreach (var attachment in attachments) attachment.Dispose();
        foreach (var lease in modelLeases) lease.Dispose();
        // Do not hold the session gate while the provider detaches its
        // PropertyChanged observers. Those observers participate in the model
        // turn and may be running user code synchronously.
        if (forgetFields)
        {
            try { _fieldWrites.Forget((INotifyPropertyChanged)viewModel); }
            // A concurrent window close owns the same provider shutdown.
            // Forget has already released all session-owned routes above.
            catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        IDisposable[] attachments;
        InteractionMountAttachment[] rootMounts;
        IRunicModelContextLease[] modelLeases;
        IRunicModelContextLease? rootModelLease;
        IRunicModelContext? sessionOwnedModelContext;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            attachments = _activeEntries.Select(entry => entry.Attachment).OfType<IDisposable>().ToArray();
            _activeEntries.Clear();
            _entries = new();
            _slots.Clear();
            modelLeases = _contentModelLeases.ToArray();
            _contentModelLeases.Clear();
            rootModelLease = _rootModelLease;
            _rootModelLease = null;
            sessionOwnedModelContext = _sessionOwnedModelContext;
            _sessionOwnedModelContext = null;
            rootMounts = _rootInteractionMounts.ToArray();
            _rootInteractionMounts.Clear();
            // A re-exposure can be waiting for a concurrently suspended
            // attachment to finish disposal. Window shutdown must release that
            // wait immediately; it cannot require a new page to wait for
            // application-owned disposal that the closing window no longer
            // needs.
            Monitor.PulseAll(_gate);
        }
        try
        {
            foreach (var attachment in attachments) attachment.Dispose();
            foreach (var attachment in rootMounts) attachment.Dispose();
        }
        finally
        {
            // The operation router is window-owned, rather than presentation-owned:
            // a suspended page can still observe a save, while closing the window
            // removes all three fixed operation endpoints.
            try { _operations.Dispose(); }
            finally
            {
                try { _interactions.Dispose(); }
                finally
                {
                    try { _fieldWrites.Dispose(); }
                    finally
                    {
                        foreach (var lease in modelLeases) lease.Dispose();
                        try { rootModelLease?.Dispose(); }
                        finally
                        {
                            if (sessionOwnedModelContext is { } context)
                                RunicModelContextDisposal.DisposeSynchronously(context);
                        }
                    }
                }
            }
        }
    }

    private IDisposable? SuspendCore(Entry entry)
    {
        while (entry.Attaching)
        {
            Monitor.Wait(_gate);
            ThrowIfDisposed();
        }
        var attachment = entry.Attachment;
        if (attachment is null) return null;
        (attachment as IBridgeDetachmentSignal)?.BeginDetaching();
        entry.Attachment = null;
        _activeEntries.Remove(entry);
        entry.Detaching = true;
        return new DetachingAttachment(this, entry, attachment);
    }

    // `Suspend` releases an attachment outside the session gate so user-owned
    // disposal does not run under it. A concurrent `Expose` must nevertheless
    // wait for that release: RebindableBridgeTransport cannot install the new
    // handler until the former attachment has relinquished its route lease.
    private void WaitForDetachment(Entry entry)
    {
        if (IsDetachingOnCurrentFlow(entry))
            throw new InvalidOperationException(
                "A content lifetime callback cannot synchronously re-expose its own ViewModel while its route is detaching. " +
                "Schedule the re-exposure after the callback returns.");
        while (entry.Detaching)
        {
            Monitor.Wait(_gate);
            ThrowIfDisposed();
        }
    }

    private void CompleteDetachment(Entry entry)
    {
        lock (_gate)
        {
            entry.Detaching = false;
            Monitor.PulseAll(_gate);
        }
    }

    private bool IsDetachingOnCurrentFlow(Entry entry)
    {
        for (var frame = _detachingOnCurrentFlow.Value; frame is not null; frame = frame.Previous)
            if (ReferenceEquals(frame.Entry, entry)) return true;
        return false;
    }

    private IDisposable EnterDetachment(Entry entry)
    {
        var previous = _detachingOnCurrentFlow.Value;
        _detachingOnCurrentFlow.Value = new DetachmentFrame(entry, previous);
        return new DetachmentScope(_detachingOnCurrentFlow, previous);
    }

    private static string Prefix(PageReference reference) => $"content{reference.Id}";

    private IDisposable AttachContent<T>(T viewModel, string route,
        Func<IBridgeTransport, T, string, IDisposable> attach) where T : class
    {
        var inner = attach(_transport, viewModel, route);
        try { return new ContentAttachment(this, inner, _transport, route); }
        catch { inner.Dispose(); throw; }
    }

    // Must run under the session gate. Content can be independently exposed later than
    // its root, but it still joins the session graph's one execution owner.
    private IRunicModelContextLease? AcquireContentModelLease(object viewModel)
    {
        var context = ModelContext ?? throw new ObjectDisposedException(nameof(WindowContentSession));
        var lease = _modelContexts.Bind(context, viewModel);
        _contentModelLeases.Add(lease);
        return lease;
    }

    private void CompleteFailedAttachment(object viewModel, string kind, Entry entry, bool created)
    {
        IRunicModelContextLease? modelLease = null;
        lock (_gate)
        {
            entry.Attaching = false;
            if (created && _entries.TryGetValue(viewModel, out var variants)
                && variants.TryGetValue(kind, out var current) && ReferenceEquals(current, entry))
            {
                variants.Remove(kind);
                if (variants.Count == 0) _entries.Remove(viewModel);
                if (entry.ContextLease is { } lease && _contentModelLeases.Remove(lease)) modelLease = lease;
            }
            Monitor.PulseAll(_gate);
        }
        modelLease?.Dispose();
    }

    private bool IsExecutingOnModelContext() =>
        ModelContext is IRunicSynchronousModelContext synchronous && synchronous.IsExecuting;

    private bool IsPresented(Entry entry) =>
        _slots.Values.Any(properties => properties.Values.Any(value => ReferenceEquals(value, entry)));

    private void ClaimView(IRunicView view)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_ownedViews.Add(view))
                throw new InvalidOperationException(
                    $"The {view.GetType().FullName} instance is already owned by another mounted presentation.");
        }
    }

    private void ReleaseView(IRunicView view)
    {
        lock (_gate) _ownedViews.Remove(view);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowContentSession));
    }

    private sealed class Entry(PageReference reference, IRunicModelContextLease? contextLease)
    {
        public PageReference Reference { get; } = reference;
        public IRunicModelContextLease? ContextLease { get; } = contextLease;
        public IDisposable? Attachment { get; set; }
        public bool Attaching { get; set; }
        public bool Detaching { get; set; }
    }

    private sealed class DetachingAttachment(WindowContentSession session, Entry entry, IDisposable attachment) : IDisposable
    {
        private IDisposable? _attachment = attachment;

        public void Dispose()
        {
            var attachment = Interlocked.Exchange(ref _attachment, null);
            if (attachment is null) return;
            using var detachment = session.EnterDetachment(entry);
            try { attachment.Dispose(); }
            finally { session.CompleteDetachment(entry); }
        }
    }

    private sealed record DetachmentFrame(Entry Entry, DetachmentFrame? Previous);

    private void RemoveRootInteractionMount(InteractionMountAttachment attachment)
    {
        lock (_gate) _rootInteractionMounts.Remove(attachment);
    }

    private sealed class RootInteractionMountLease(WindowContentSession owner, InteractionMountAttachment attachment) : IDisposable
    {
        private InteractionMountAttachment? _attachment = attachment;
        public void Dispose()
        {
            var attachment = Interlocked.Exchange(ref _attachment, null);
            if (attachment is null) return;
            owner.RemoveRootInteractionMount(attachment);
            attachment.Dispose();
        }
    }

    /// <summary>Mount owner tracking for a root bridge with no logical .NET View.</summary>
    private sealed class InteractionMountAttachment : IDisposable
    {
        private readonly WindowContentSession _owner;
        private readonly string _route;
        private readonly object _gate = new();
        private readonly IDisposable _mount;
        private readonly IDisposable _unmount;
        private readonly Dictionary<string, MountOwner> _presentations = new(StringComparer.Ordinal);
        private bool _disposed;

        public InteractionMountAttachment(WindowContentSession owner, IBridgeTransport transport, string route)
        {
            _owner = owner;
            _route = route;
            _mount = transport.Bind($"{route}Mount", Mount);
            try { _unmount = transport.Bind($"{route}Unmount", Unmount); }
            catch { _mount.Dispose(); throw; }
        }

        private string Mount(IBridgeArguments arguments)
        {
            var token = arguments.GetString();
            if (token.Length is 0 or > 256 || token.IndexOf(':') <= 0) return "invalid";
            lock (_gate)
            {
                if (_disposed) return "disconnected";
                var owner = new MountOwner(arguments.ClientKey, arguments.ConnectionKey);
                if (!_presentations.TryAdd(token, owner)) return _presentations[token].Matches(arguments) ? "ok" : "ignored";
                _owner._interactions.OnPresentationMounted(_route, token, owner.ClientKey, owner.ConnectionKey);
                return "ok";
            }
        }

        private string Unmount(IBridgeArguments arguments)
        {
            var token = arguments.GetString();
            lock (_gate)
            {
                if (_disposed) return "disconnected";
                if (!_presentations.TryGetValue(token, out var owner)) return "ok";
                if (!owner.Matches(arguments)) return "ignored";
                _presentations.Remove(token);
                _owner._interactions.OnPresentationUnmounted(token);
                return "ok";
            }
        }

        public void ReleaseConnection(string connectionKey)
        {
            lock (_gate)
                foreach (var token in _presentations.Where(pair => pair.Value.ConnectionKey == connectionKey)
                    .Select(pair => pair.Key).ToArray())
                {
                    _presentations.Remove(token);
                    _owner._interactions.OnPresentationUnmounted(token, "The browser connection was disconnected.");
                }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (var token in _presentations.Keys.ToArray())
                    _owner._interactions.OnPresentationUnmounted(token, "The browser presentation was disposed.");
                _presentations.Clear();
            }
            try { _mount.Dispose(); }
            finally { _unmount.Dispose(); }
        }

        private sealed record MountOwner(string? ClientKey, string? ConnectionKey)
        {
            public bool Matches(IBridgeArguments arguments) =>
                string.Equals(ClientKey, arguments.ClientKey, StringComparison.Ordinal) &&
                string.Equals(ConnectionKey, arguments.ConnectionKey, StringComparison.Ordinal);
        }
    }

    private sealed class DetachmentScope(ThreadLocal<DetachmentFrame?> state, DetachmentFrame? previous) : IDisposable
    {
        private ThreadLocal<DetachmentFrame?>? _state = state;
        private readonly DetachmentFrame? _previous = previous;

        public void Dispose()
        {
            var state = Interlocked.Exchange(ref _state, null);
            if (state is not null) state.Value = _previous;
        }
    }

    private sealed class ContentAttachment : IDisposable, IBridgeDetachmentSignal
    {
        private readonly WindowContentSession _session;
        private readonly string _route;
        private readonly object _gate = new();
        private readonly IDisposable _inner;
        private readonly IDisposable _mountBinding;
        private readonly IDisposable _unmountBinding;
        private readonly Dictionary<string, MountOwner> _mounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sessions = new(StringComparer.Ordinal);
        // WebUI may reuse a connection identifier after reload. A closed
        // connection therefore rejects only its former browser session.
        private readonly Dictionary<string, string> _connectionSessions = new(StringComparer.Ordinal);
        private readonly HashSet<(string Connection, string Session)> _closedSessions = [];
        // A transport mount can be removed before its serialized model turn gets
        // to create the View. Keep acknowledgement separate from the transport
        // table so an unmount cannot be followed by a late mounted callback.
        private readonly HashSet<string> _activatedMounts = new(StringComparer.Ordinal);
        private string? _browserSession;
        private bool _viewModelLifetimeActive;
        private bool _disposed;

        public ContentAttachment(WindowContentSession session, IDisposable inner, IBridgeTransport transport, string route)
        {
            _session = session;
            _route = route;
            _inner = inner;
            _mountBinding = transport.Bind($"{route}Mount", Mount);
            try { _unmountBinding = transport.Bind($"{route}Unmount", Unmount); }
            catch { _mountBinding.Dispose(); throw; }
        }

        private string Mount(IBridgeArguments arguments)
        {
            var token = arguments.GetString();
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1)
                return "invalid";
            var releases = new List<MountRelease>();
            MountOwner owner;
            lock (_gate)
            {
                if (_disposed) return "disconnected";
                var session = token[..separator];
                var clientKey = arguments.ClientKey;
                if (arguments.ConnectionKey is { } connectionKey)
                {
                    if (_closedSessions.Contains((connectionKey, session))) return "disconnected";
                    _connectionSessions[connectionKey] = session;
                }
                if (clientKey is not null)
                {
                    if (!_sessions.TryGetValue(clientKey, out var previousSession) || previousSession != session)
                    {
                        _sessions[clientKey] = session;
                        foreach (var oldToken in _mounts.Where(pair => pair.Value.ClientKey == clientKey)
                            .Select(pair => pair.Key).ToArray())
                            if (SupersedeMountCore(oldToken) is { } release) releases.Add(release);
                    }
                }
                else if (_browserSession != session)
                {
                    _browserSession = session;
                    foreach (var oldToken in _mounts.Keys.ToArray())
                        if (SupersedeMountCore(oldToken) is { } release) releases.Add(release);
                }
                owner = new MountOwner(clientKey, arguments.ConnectionKey);
                if (!_mounts.TryAdd(token, owner))
                {
                    // Exact duplicate delivery from the same browser is
                    // idempotent. Another client or connection may not claim
                    // its token merely because the string collided.
                    return _mounts[token].Matches(arguments) ? "ok" : "ignored";
                }
            }
            foreach (var release in releases) CompleteRelease(release);
            try
            {
                RunLifecycle(() => CompleteMount(token, owner));
                return "ok";
            }
            catch (Exception exception)
            {
                MountRelease? release;
                lock (_gate) release = ReleaseMountCore(token, "The browser presentation could not be mounted.");
                if (release is { } value) CompleteRelease(value);
                Console.Error.WriteLine($"Runic View mount failed: {exception}");
                throw;
            }
        }

        private string Unmount(IBridgeArguments arguments)
        {
            var token = arguments.GetString();
            MountRelease? release;
            lock (_gate)
            {
                if (_disposed) return "disconnected";
                if (_mounts.TryGetValue(token, out var owner) && !owner.Matches(arguments))
                    return "ignored";
                release = ReleaseMountCore(token, "The browser presentation was unmounted.");
            }
            if (release is { } value) CompleteRelease(value);
            return "ok";
        }

        public void ReleaseConnection(string connectionKey)
        {
            List<MountRelease> releases = [];
            lock (_gate)
            {
                if (_disposed) return;
                if (_connectionSessions.Remove(connectionKey, out var session))
                    _closedSessions.Add((connectionKey, session));
                foreach (var token in _mounts.Where(pair => pair.Value.ConnectionKey == connectionKey)
                    .Select(pair => pair.Key).ToArray())
                    if (ReleaseMountCore(token, "The browser connection was disconnected.") is { } release)
                        releases.Add(release);
                foreach (var client in _sessions.Keys.Where(client =>
                    !_mounts.Values.Any(owner => owner.ClientKey == client)).ToArray()) _sessions.Remove(client);
            }
            foreach (var release in releases) CompleteRelease(release);
        }

        public void BeginDetaching() => (_inner as IBridgeDetachmentSignal)?.BeginDetaching();

        public void Dispose()
        {
            var shouldDispose = false;
            List<MountRelease> releases = [];
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    shouldDispose = true;
                    foreach (var token in _mounts.Keys.ToArray())
                        if (ReleaseMountCore(token, "The browser presentation was disposed.") is { } release)
                            releases.Add(release);
                    _sessions.Clear();
                    _connectionSessions.Clear();
                    _closedSessions.Clear();
                }
            }
            finally
            {
                if (shouldDispose)
                {
                    try { _mountBinding.Dispose(); }
                    finally
                    {
                        try { _unmountBinding.Dispose(); }
                        finally
                        {
                            foreach (var release in releases) CompleteRelease(release);
                            _inner.Dispose();
                        }
                    }
                }
            }
        }

        private void CompleteMount(string token, MountOwner owner)
        {
            var notifyView = false;
            lock (_gate)
            {
                if (_disposed || !_mounts.TryGetValue(token, out var current) || !ReferenceEquals(current, owner)
                    || !_activatedMounts.Add(token)) return;
                if (_inner is IRunicWebPresentationLifetime)
                    notifyView = true;
                else if (!_viewModelLifetimeActive)
                {
                    _viewModelLifetimeActive = true;
                    notifyView = true;
                }
            }

            _session._interactions.OnPresentationMounted(_route, token, owner.ClientKey, owner.ConnectionKey);
            if (notifyView) NotifyMounted(token);
        }

        private void NotifyMounted(string token)
        {
            if (_inner is IRunicWebPresentationLifetime presentation)
                presentation.OnWebMounted(token);
            else
                (_inner as IRunicWebMountLifetime)?.OnWebMounted();
        }

        private MountRelease? ReleaseMountCore(string token, string reason)
        {
            if (!_mounts.Remove(token)) return null;
            var notifyInteraction = _activatedMounts.Remove(token);
            var notifyView = _inner is IRunicWebPresentationLifetime
                ? notifyInteraction
                : _mounts.Count == 0 && _viewModelLifetimeActive;
            if (notifyView && _inner is not IRunicWebPresentationLifetime)
                _viewModelLifetimeActive = false;
            return new(token, notifyInteraction, notifyView, reason);
        }

        private MountRelease? SupersedeMountCore(string token)
        {
            if (!_mounts.Remove(token)) return null;
            // ViewModel-only endpoints retain one lifetime while a browser session
            // replaces its presentation token.
            var notifyInteraction = _activatedMounts.Remove(token);
            return new(token, notifyInteraction,
                _inner is IRunicWebPresentationLifetime && notifyInteraction,
                "The browser presentation was superseded.");
        }

        private void CompleteRelease(MountRelease release)
        {
            RunLifecycle(() =>
            {
                if (release.NotifyInteractionUnmount)
                    _session._interactions.OnPresentationUnmounted(release.Token, release.Reason);
                if (release.NotifyViewUnmount) NotifyUnmounted(release.Token);
            });
        }

        private void NotifyUnmounted(string token)
        {
            if (_inner is IRunicWebPresentationLifetime presentation)
                presentation.OnWebUnmounted(token);
            else
                (_inner as IRunicWebMountLifetime)?.OnWebUnmounted();
        }

        // User presentation lifetimes can mutate a ReactiveObject. Run them on the
        // graph owner, but never while the attachment gate is held.
        private void RunLifecycle(Action callback)
        {
            if (_session.ModelContext is IRunicSynchronousModelContext synchronous) synchronous.Run(callback);
            else if (_session.ModelContext is { } context) context.InvokeAsync(callback).AsTask().GetAwaiter().GetResult();
            else callback();
        }

        private readonly record struct MountRelease(
            string Token,
            bool NotifyInteractionUnmount,
            bool NotifyViewUnmount,
            string Reason);

        private sealed record MountOwner(string? ClientKey, string? ConnectionKey)
        {
            public bool Matches(IBridgeArguments arguments) =>
                string.Equals(ClientKey, arguments.ClientKey, StringComparison.Ordinal) &&
                string.Equals(ConnectionKey, arguments.ConnectionKey, StringComparison.Ordinal);
        }
    }

    private sealed class ViewPresentationAttachment(
        IDisposable bridge,
        Func<IRunicView> createView,
        Action<IRunicView> bindView,
        Action<IRunicView> claimView,
        Action<IRunicView> releaseView) : IDisposable, IRunicWebPresentationLifetime, IBridgeDetachmentSignal
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, IRunicView> _presentations = new(StringComparer.Ordinal);
        private bool _disposed;

        public void OnWebMounted(string presentationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_presentations.ContainsKey(presentationId)) return;

                var view = createView();
                claimView(view);
                var attached = false;
                var mounted = false;
                try
                {
                    bindView(view);
                    if (view is IRunicViewLifetime lifetime)
                    {
                        attached = true;
                        lifetime.OnAttached();
                    }
                    if (view is IRunicWebMountLifetime mountLifetime)
                    {
                        mounted = true;
                        mountLifetime.OnWebMounted();
                    }
                    _presentations.Add(presentationId, view);
                }
                catch
                {
                    ReleasePresentation(view, attached, mounted);
                    throw;
                }
            }
        }

        public void OnWebUnmounted(string presentationId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(presentationId);
            lock (_gate)
            {
                if (!_presentations.Remove(presentationId, out var view)) return;
                ReleasePresentation(view, attached: true, mounted: true);
            }
        }

        public void BeginDetaching() => (bridge as IBridgeDetachmentSignal)?.BeginDetaching();

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    foreach (var view in _presentations.Values.ToArray())
                        ReleasePresentation(view, attached: true, mounted: true);
                    _presentations.Clear();
                }
                finally { bridge.Dispose(); }
            }
        }

        private void ReleasePresentation(IRunicView view, bool attached, bool mounted)
        {
            try
            {
                if (mounted && view is IRunicWebMountLifetime mountLifetime)
                    mountLifetime.OnWebUnmounted();
            }
            finally
            {
                try
                {
                    if (attached && view is IRunicViewLifetime lifetime)
                        lifetime.OnDetached();
                }
                finally
                {
                    try { if (view is IDisposable disposable) disposable.Dispose(); }
                    finally { releaseView(view); }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ViewPresentationAttachment));
        }
    }
}

/// <summary>Result of requesting graceful shutdown for one window session.</summary>
public sealed record WindowContentSessionCloseResult(bool Drained, int RemainingOperations);
