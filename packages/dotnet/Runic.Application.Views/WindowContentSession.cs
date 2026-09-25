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
    private readonly BridgeFieldWriteRegistryProvider _fieldWrites;
    private ConditionalWeakTable<object, Dictionary<string, Entry>> _entries = new();
    private readonly HashSet<Entry> _activeEntries = [];
    private readonly Dictionary<object, Dictionary<string, Entry>> _slots = new(ReferenceEqualityComparer.Instance);
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
        CancellationToken operationShutdown = default)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _viewLocator = viewLocator;
        _operations = new BridgeOperationRouter(_transport, Guid.NewGuid().ToString("N"), operationShutdown);
        _fieldWrites = new BridgeFieldWriteRegistryProvider(Guid.NewGuid().ToString("N"));
    }

    // Generated Bridges remain in this assembly through their host-neutral
    // base class. The router is intentionally internal: applications do not
    // author operation routes directly.
    internal BridgeOperationRouter Operations => _operations;

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
                    if (existing.Attachment is null)
                    {
                        BridgeSnapshotPublication.ThrowIfInactive();
                        existing.Attachment = AttachContent(viewModel, Prefix(existing.Reference), attach);
                        _activeEntries.Add(existing);
                    }
                    return existing.Reference;
                }

                var reference = new PageReference(kind, Guid.NewGuid().ToString("N"));
                BridgeSnapshotPublication.ThrowIfInactive();
                var attachment = AttachContent(viewModel, Prefix(reference), attach);
                var entry = new Entry(reference, attachment);
                if (variants is null) _entries.Add(viewModel, variants = new Dictionary<string, Entry>(StringComparer.Ordinal));
                variants.Add(kind, entry);
                _activeEntries.Add(entry);
                return reference;
            }
        }
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
        IDisposable? previousAttachment = null;
        PageReference reference;
        lock (_gate)
        {
            ThrowIfDisposed();
            reference = Expose(kind, viewModel, attach);
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
        lock (_gate)
        {
            ThrowIfDisposed();
            var reference = Expose(kind, viewModel, attach);
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
        lock (_gate)
        {
            if (_disposed) return;
            attachments = _activeEntries.Select(entry => entry.Attachment)
                .OfType<ContentAttachment>().ToArray();
        }
        foreach (var attachment in attachments) attachment.ReleaseConnection(connectionKey);
    }

    /// <summary>Releases identity and routes for an object no longer retained by this window.</summary>
    public void Forget(object viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        List<IDisposable> attachments = [];
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
                if (SuspendCore(entry) is { } attachment) attachments.Add(attachment);
        }
        foreach (var attachment in attachments) attachment.Dispose();
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            attachments = _activeEntries.Select(entry => entry.Attachment).OfType<IDisposable>().ToArray();
            _activeEntries.Clear();
            _entries = new();
            _slots.Clear();
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
        }
        finally
        {
            // The operation router is window-owned, rather than presentation-owned:
            // a suspended page can still observe a save, while closing the window
            // removes all three fixed operation endpoints.
            _operations.Dispose();
            _fieldWrites.Dispose();
        }
    }

    private IDisposable? SuspendCore(Entry entry)
    {
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
        try { return new ContentAttachment(inner, _transport, route); }
        catch { inner.Dispose(); throw; }
    }

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

    private sealed class Entry(PageReference reference, IDisposable attachment)
    {
        public PageReference Reference { get; } = reference;
        public IDisposable? Attachment { get; set; } = attachment;
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
        private string? _session;
        private bool _disposed;

        public ContentAttachment(IDisposable inner, IBridgeTransport transport, string route)
        {
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
            lock (_gate)
            {
                if (_disposed) return "disconnected";
                var session = token[..separator];
                var wasMounted = _mounts.Count > 0;
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
                            .Select(pair => pair.Key).ToArray()) SupersedeMount(oldToken);
                    }
                }
                else if (_session != session)
                {
                    _session = session;
                    foreach (var oldToken in _mounts.Keys.ToArray()) SupersedeMount(oldToken);
                }
                var owner = new MountOwner(clientKey, arguments.ConnectionKey);
                if (!_mounts.TryAdd(token, owner))
                {
                    // Exact duplicate delivery from the same browser is
                    // idempotent. Another client or connection may not claim
                    // its token merely because the string collided.
                    return _mounts[token].Matches(arguments) ? "ok" : "ignored";
                }
                try
                {
                    NotifyMounted(token, wasMounted);
                }
                catch (Exception exception)
                {
                    _mounts.Remove(token);
                    Console.Error.WriteLine($"Runic View mount failed: {exception}");
                    throw;
                }
                return "ok";
            }
        }

        private string Unmount(IBridgeArguments arguments)
        {
            var token = arguments.GetString();
            lock (_gate)
            {
                if (_disposed) return "disconnected";
                if (_mounts.TryGetValue(token, out var owner) && !owner.Matches(arguments))
                    return "ignored";
                ReleaseMount(token);
                return "ok";
            }
        }

        public void ReleaseConnection(string connectionKey)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_connectionSessions.Remove(connectionKey, out var session))
                    _closedSessions.Add((connectionKey, session));
                foreach (var token in _mounts.Where(pair => pair.Value.ConnectionKey == connectionKey)
                    .Select(pair => pair.Key).ToArray())
                    ReleaseMount(token);
                foreach (var client in _sessions.Keys.Where(client =>
                    !_mounts.Values.Any(owner => owner.ClientKey == client)).ToArray()) _sessions.Remove(client);
            }
        }

        public void BeginDetaching() => (_inner as IBridgeDetachmentSignal)?.BeginDetaching();

        public void Dispose()
        {
            var shouldDispose = false;
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    shouldDispose = true;
                    foreach (var token in _mounts.Keys.ToArray()) ReleaseMount(token);
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
                        finally { _inner.Dispose(); }
                    }
                }
            }
        }

        private void NotifyMounted(string token, bool wasMounted)
        {
            if (_inner is IRunicWebPresentationLifetime presentation)
                presentation.OnWebMounted(token);
            else if (!wasMounted)
                (_inner as IRunicWebMountLifetime)?.OnWebMounted();
        }

        private bool ReleaseMount(string token)
        {
            if (!_mounts.Remove(token)) return false;
            if (_inner is IRunicWebPresentationLifetime presentation)
                presentation.OnWebUnmounted(token);
            else if (_mounts.Count == 0)
                (_inner as IRunicWebMountLifetime)?.OnWebUnmounted();
            return true;
        }

        // A presentation-aware attachment has one View per token and must
        // release each displaced presentation. ViewModel-only endpoints have
        // no presentation lifetime to end.
        private void SupersedeMount(string token)
        {
            if (_inner is IRunicWebPresentationLifetime) ReleaseMount(token);
            else _mounts.Remove(token);
        }

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
