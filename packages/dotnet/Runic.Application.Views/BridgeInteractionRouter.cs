using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

// The ReactiveUI conformance fixtures enter interaction scopes and call the interaction routes as a browser would.
[assembly: InternalsVisibleTo("Runic.Application.Views.ReactiveUI.Conformance.Tests")]
[assembly: InternalsVisibleTo("Runic.Application.Views.ReactiveUI.Reactive.Conformance.Tests")]

namespace Runic.Application.Views;

/// <summary>
/// The trusted origin of work which may ask a browser to handle an interaction or a view request.
/// The bridge runtime establishes a command invocation around a generated command or operation, with the browser
/// identity its transport authenticated. The window scope has no browser identity: a
/// <see cref="RunicWindow{TViewModel}"/> enters it around its close decision, a menu-bar command runs in it, and
/// application code enters it with <see cref="EnterWindow"/> for other work of a window, such as a timer or a
/// notification's activation. Application code reads the scope through <see cref="Current"/> but cannot enter a command
/// invocation, so it cannot claim a client or connection identity.
/// </summary>
public sealed class RunicInteractionInvocation
{
    // The route of the scope a Window enters while it decides a close; no bridge route has this name.
    internal const string WindowCloseRoute = "__runicWindowClose";
    // The route of the window scope that a menu-bar command or EnterWindow enters; no bridge route has this name.
    internal const string WindowRoute = "__runicWindow";

    private static readonly AsyncLocal<RunicInteractionInvocation?> CurrentScope = new();

    private RunicInteractionInvocation(WindowContentSession session, string route, string? clientKey,
        string? connectionKey, string? commandName, CancellationToken cancellationToken, bool windowClose = false)
    {
        Session = session;
        Route = route;
        ClientKey = clientKey;
        ConnectionKey = connectionKey;
        CommandName = commandName;
        CancellationToken = cancellationToken;
        IsWindowScope = windowClose;
    }

    /// <summary>The window content session that owns the invocation.</summary>
    public WindowContentSession Session { get; }
    /// <summary>
    /// The bridge route whose browser client may handle interactions, <c>__runicWindowClose</c> while the window
    /// decides a close, or <c>__runicWindow</c> in the window scope.
    /// </summary>
    public string Route { get; }
    /// <summary>The trusted browser client identity, if the invocation came from one.</summary>
    /// <remarks>
    /// The key the window's transport authenticated for the request, never a value in its JSON. A Window's
    /// <see cref="IRunicConnections.Caller"/> reads it.
    /// </remarks>
    public string? ClientKey { get; }
    /// <summary>The trusted transport connection identity, if the invocation came from one.</summary>
    /// <remarks>
    /// The key the window's transport authenticated for the request, never a value in its JSON. A Window's
    /// <see cref="IRunicConnections.Caller"/> reads it.
    /// The window gives each connection a key that is unique for its lifetime: the transport's key, or, when the transport
    /// reused the key of a closed connection, that key with a <c>#n</c> suffix. A command that runs on after its
    /// connection closed therefore never reaches the connection that reused its transport key.
    /// </remarks>
    public string? ConnectionKey { get; }
    /// <summary>The generated command that started the invocation, if any.</summary>
    public string? CommandName { get; }
    /// <summary>Cancels interactions requested by this invocation.</summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>The invocation scope on the current asynchronous flow, or <see langword="null"/>.</summary>
    public static RunicInteractionInvocation? Current => CurrentScope.Value;

    // The window scope: the window decides a close, after its close button or RequestCloseAsync, or runs a menu-bar
    // command or work the application entered it for. No browser client started it, so it may ask the one presentation
    // in this window that has mounted a handler for the interaction.
    internal bool IsWindowScope { get; }

    // Enters a trusted bridge invocation. Only the bridge runtime enters one, with the identity its transport
    // authenticated (the request's connection), never values supplied inside request JSON: the client and connection
    // keys select the browser presentation that answers, so application code must not be able to claim them
    // (W319-003 Views-02). Tests select the identity themselves.
    internal static IDisposable Enter(WindowContentSession session, string route, string? clientKey,
        string? connectionKey, string? commandName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var previous = CurrentScope.Value;
        CurrentScope.Value = new RunicInteractionInvocation(session, route, clientKey, connectionKey,
            commandName, cancellationToken);
        return new Scope(previous);
    }

    // RunicWindow enters it around its close decision, whatever started the close; the token ends the decision.
    internal static IDisposable EnterWindowClose(WindowContentSession session, CancellationToken cancellationToken)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = new RunicInteractionInvocation(session, WindowCloseRoute, null, null, null,
            cancellationToken, windowClose: true);
        return new Scope(previous);
    }

    // The identity of a scope that no flow enters: a request kept for an object no window presented yet is delivered on
    // behalf of the scope that raised it (BridgeInteractionRouter.TakeUnattachedViewRequest).
    internal static RunicInteractionInvocation Detached(WindowContentSession session, string route, string? clientKey,
        string? connectionKey, bool windowScope) =>
        new(session, route, clientKey, connectionKey, null, CancellationToken.None, windowScope);

    // A menu-bar command of the window runs in the window scope; the token ends its interactions.
    internal static IDisposable EnterWindowScope(WindowContentSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var previous = CurrentScope.Value;
        CurrentScope.Value = new RunicInteractionInvocation(session, WindowRoute, null, null, null,
            cancellationToken, windowClose: true);
        return new Scope(previous);
    }

    /// <summary>
    /// Enters the window scope of the one window that presents <paramref name="viewModel"/>, until the returned scope
    /// is disposed, for work of that window that no page started, such as a timer or a notification's activation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In the scope, a ReactiveUI interaction or a <see cref="ViewQuestion{TInput, TOutput}"/> asks the one
    /// presentation of the window that handles it, as during the window's close decision, and a
    /// <see cref="ViewRequest{T}"/> goes to the one presentation of the window that handles it. With none or several,
    /// the interaction uses its .NET fallback and the request follows its <see cref="ViewRequestDelivery"/>.
    /// </para>
    /// <para>
    /// It enters no scope, and the current one stays, when no window or several present the ViewModel, and inside a
    /// command that a page of that window invoked: that command already reaches its own page, and the window scope
    /// would widen it to another page. <see cref="TryEnterWindow"/> tells which happened. The scope has no client or
    /// connection identity, so it never reaches one page's presentation on behalf of another.
    /// </para>
    /// <code>
    /// using (RunicInteractionInvocation.EnterWindow(this))
    ///     if (await ConfirmReload.Handle(path)) await ReloadAsync();
    /// </code>
    /// </remarks>
    /// <param name="viewModel">A ViewModel that a route of the window presents.</param>
    /// <param name="cancellationToken">Ends the interactions asked in the scope.</param>
    /// <returns>The scope; dispose it to restore the previous one. It does nothing when no scope was entered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public static IDisposable EnterWindow(object viewModel, CancellationToken cancellationToken = default)
    {
        TryEnterWindow(viewModel, out var scope, cancellationToken);
        return scope;
    }

    /// <summary>
    /// Enters the window scope of the one window that presents <paramref name="viewModel"/>, as
    /// <see cref="EnterWindow"/> does, and returns whether it entered it.
    /// </summary>
    /// <param name="viewModel">A ViewModel that a route of the window presents.</param>
    /// <param name="scope">The scope to dispose; it does nothing when the method returns <see langword="false"/>.</param>
    /// <param name="cancellationToken">Ends the interactions asked in the scope.</param>
    /// <returns>
    /// <see langword="true"/> when it entered the window scope; <see langword="false"/> when no window or several present
    /// the ViewModel, or a command that a page of that window invoked is current and stays.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> is <see langword="null"/>.</exception>
    public static bool TryEnterWindow(object viewModel, out IDisposable scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        var windows = BridgePresentationRegistry.Routes(viewModel).Select(route => route.Content).OfType<WindowContentSession>()
            .Distinct().ToArray();
        scope = NoScope.Instance;
        if (windows.Length != 1) return false;
        // A page's command keeps its own, narrower scope.
        if (Current is { IsWindowScope: false } command && ReferenceEquals(command.Session, windows[0])) return false;
        scope = EnterWindowScope(windows[0], cancellationToken);
        return true;
    }

    private sealed class NoScope : IDisposable
    {
        internal static readonly NoScope Instance = new();
        public void Dispose() { }
    }

    private sealed class Scope(RunicInteractionInvocation? previous) : IDisposable
    {
        private RunicInteractionInvocation? _previous = previous;
        public void Dispose()
        {
            var previous = Interlocked.Exchange(ref _previous, null);
            if (previous is not null || CurrentScope.Value is not null) CurrentScope.Value = previous;
        }
    }
}

/// <summary>Raised when a selected mounted interaction endpoint disappears.</summary>
public sealed class RunicInteractionCancelledException : OperationCanceledException
{
    /// <summary>Creates the exception with a default message.</summary>
    public RunicInteractionCancelledException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RunicInteractionCancelledException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the exception that caused it.</summary>
    public RunicInteractionCancelledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a browser interaction exceeded its configured lifetime.</summary>
public sealed class RunicInteractionTimeoutException : TimeoutException
{
    /// <summary>Creates the exception with a default message.</summary>
    public RunicInteractionTimeoutException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RunicInteractionTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the exception that caused it.</summary>
    public RunicInteractionTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Raised when a browser answered an interaction with output its contract cannot decode.</summary>
public sealed class RunicInteractionOutputException : InvalidOperationException
{
    /// <summary>Creates the exception with a default message.</summary>
    public RunicInteractionOutputException()
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/>.</summary>
    public RunicInteractionOutputException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with <paramref name="message"/> and the exception that caused it.</summary>
    public RunicInteractionOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Window-owned request/reply broker for typed interaction adapters. A presentation that a page opened over Views
/// protocol 4 registers its handlers with <c>open</c> or <c>interactions</c>, receives <c>interaction</c> and
/// <c>interaction-cancelled</c> pushes on its connection, and answers with <c>interaction-reply</c>. With
/// <c>view-requests/1</c> it also registers view request handlers with <c>view-requests</c> and receives
/// <c>view-request</c> pushes, which it does not answer.
/// </summary>
// A presentation is identified by its route and presentation id.
public sealed class BridgeInteractionRouter : IDisposable
{
    private const int MaximumPending = 32;
    private const int MaximumPendingPerPresentation = 8;
    private const int MaximumReceipts = 64;
    private const int MaximumRouteLength = 512;
    private const int MaximumNameLength = 128;
    private const int MaximumContractLength = 256;
    private const int MaximumPresentationLength = 256;

    private readonly object _gate = new();
    private readonly WindowContentSession _session;
    private readonly IBridgeTransport _transport;
    private BridgeTargetedPushes? _pushes;
    private readonly Dictionary<string, Presentation> _presentations = new(StringComparer.Ordinal);
    private readonly Dictionary<DefinitionKey, int> _definitions = [];
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
    // view-requests/1: the view requests of the window's routes, and the NextMounted requests kept for the next
    // presentation that registers them, one per raising ViewRequest and name.
    //
    // A kept entry whose object the window never presents again (Keys empty, or emptied by RemoveViewRequest only for
    // routes it had) lives until the window closes, as does an unattached one below. That is bounded: an entry holds
    // only the latest request of one ViewRequest member of one object that work of this window raised, so their number
    // is at most the number of such members, and the window's disposal releases them all. A kept command request also
    // goes when its connection closes (ReleaseConnection).
    private readonly Dictionary<DefinitionKey, int> _requestDefinitions = [];
    private readonly Dictionary<(object Owner, string Name), KeptViewRequest> _kept = [];
    // NextMounted requests that work in a scope of this window raised for an object that no window presented yet, by
    // their ViewRequest: the input and the identity of the scope, which the first route of this window that presents
    // the object takes (TakeUnattachedViewRequest).
    private readonly Dictionary<object, UnattachedViewRequest> _unattached = [];
    private bool _disposed;

    internal BridgeInteractionRouter(WindowContentSession session, IBridgeTransport transport)
    {
        _session = session;
        _transport = transport;
    }

    /// <summary>
    /// Registers a typed interaction surface for one generated bridge route.
    /// The route lease is normally owned by the generated bridge attachment.
    /// </summary>
    public IDisposable Register<TInput, TOutput>(string route, string name, string contract,
        Func<TInput, string> encodeInput, Func<JsonElement, TOutput> decodeOutput,
        int priority = 0, TimeSpan? timeout = null)
    {
        ValidateDefinition(route, name, contract, encodeInput, decodeOutput);
        _ = ResolveTimeout(timeout);
        var key = new DefinitionKey(route, name, contract);
        lock (_gate)
        {
            ThrowIfDisposed();
            _definitions.TryGetValue(key, out var count);
            _definitions[key] = checked(count + 1);
        }
        return new Registration(this, key);
    }

    // A view request of a route (ViewRequest.Attach); the lease removes it with the route's presentation of its object.
    internal IDisposable RegisterViewRequest(object owner, string route, string name, string contract)
    {
        ValidateDefinition<object?, object?>(route, name, contract, static _ => "null", static _ => null);
        var key = new DefinitionKey(route, name, contract);
        lock (_gate)
        {
            ThrowIfDisposed();
            _requestDefinitions.TryGetValue(key, out var count);
            _requestDefinitions[key] = checked(count + 1);
            // A request kept for the object waits for this route too.
            if (_kept.TryGetValue((owner, name), out var kept) && kept.Contract == contract) kept.Keys.Add(key);
        }
        return new ViewRequestRegistration(this, key);
    }

    private void RemoveViewRequest(DefinitionKey key)
    {
        lock (_gate)
        {
            if (!_requestDefinitions.Remove(key, out var count)) return;
            if (count > 1) { _requestDefinitions[key] = count - 1; return; }
            // A kept request waits only for routes of the window that still present its object.
            foreach (var (owner, kept) in _kept.ToArray())
                if (kept.Keys.Remove(key) && kept.Keys.Count == 0) _kept.Remove(owner);
        }
    }

    // Raises a view request of an object that the window presents on routes (specs/application/README.md, "View
    // requests"). Selects the presentations by the delivery and the invoking scope, keeps a NextMounted request that
    // finds none, and pushes it to each selected presentation with the revision of its route after what was pending
    // was published.
    internal void RaiseViewRequest(object owner, IReadOnlyCollection<string> routes, string name, string contract, string inputJson,
        ViewRequestDelivery delivery, RunicInteractionInvocation? invocation)
    {
        List<ViewRequestTarget> targets = [];
        lock (_gate)
        {
            if (_disposed) return;
            var keys = routes.Select(route => new DefinitionKey(route, name, contract)).Where(_requestDefinitions.ContainsKey).ToHashSet();
            if (!ReferenceEquals(invocation?.Session, _session)) invocation = null;
            if (delivery is ViewRequestDelivery.Broadcast)
                // Every presentation of the window that handles it, on every connection, whatever raised it.
                targets.AddRange(_presentations.Values.Where(presentation => presentation.Requests.Overlaps(keys))
                    .Select(presentation => Target(presentation, name, contract, inputJson)));
            else if (SelectPresentation(keys, invocation, static presentation => presentation.Requests) is { } selected)
            {
                targets.Add(Target(selected, name, contract, inputJson));
                _kept.Remove((owner, name));
                _unattached.Remove(owner);
            }
            else if (delivery is ViewRequestDelivery.NextMounted)
                Keep(owner, keys, name, contract, inputJson, invocation);
            else ViewsLog.ViewRequestDropped(_session.Logger, name, "no single presentation handles it");
        }
        Deliver(targets);
    }

    // Keeps a NextMounted request (the latest one wins); one that a command raised stays with its client and connection.
    // keys is empty for a request of an object that no route of this window presents yet. The gate is held.
    private void Keep(object owner, HashSet<DefinitionKey> keys, string name, string contract, string inputJson,
        RunicInteractionInvocation? invocation)
    {
        var command = invocation is { IsWindowScope: false } ? invocation : null;
        _kept[(owner, name)] = new KeptViewRequest(keys, name, contract, inputJson, command?.ClientKey, command?.ConnectionKey);
        ViewsLog.ViewRequestKept(_session.Logger, name);
    }

    // A NextMounted request raised in a scope of this window for an object that no window presents yet. Its name, contract
    // and encoding are only known once a route presents the object, so the input stays as it is; the latest one wins.
    internal void KeepUnattachedViewRequest(object owner, object? input, long sequence, RunicInteractionInvocation invocation)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(invocation.Session, _session)) return;
            _unattached[owner] = new UnattachedViewRequest(input, sequence, invocation.Route,
                invocation.IsWindowScope ? null : invocation.ClientKey, invocation.IsWindowScope ? null : invocation.ConnectionKey,
                invocation.IsWindowScope);
        }
    }

    // Takes the unattached request of owner, with an invocation of its scope's identity that no flow entered, or null.
    internal (object? Input, long Sequence, RunicInteractionInvocation Identity)? TakeUnattachedViewRequest(object owner)
    {
        lock (_gate)
        {
            if (_disposed || !_unattached.Remove(owner, out var request)) return null;
            return (request.Input, request.Sequence, RunicInteractionInvocation.Detached(_session, request.Route,
                request.ClientKey, request.ConnectionKey, request.WindowScope));
        }
    }

    // A NextMounted request raised in a scope of this window for an object that no route of it presents yet: kept here,
    // so it is gone with the window, until a route of the window presents the object (RegisterViewRequest).
    internal void KeepViewRequest(object owner, string name, string contract, string inputJson, RunicInteractionInvocation? invocation)
    {
        lock (_gate)
        {
            if (_disposed) return;
            Keep(owner, [], name, contract, inputJson, invocation is not null && ReferenceEquals(invocation.Session, _session) ? invocation : null);
        }
    }

    private static ViewRequestTarget Target(Presentation presentation, string name, string contract, string inputJson) =>
        new(presentation.Route, presentation.Id, presentation.ConnectionKey, name, contract, inputJson);

    // Pushes view requests outside the gate. Each route first publishes what is pending in its model turn, and the push
    // carries that revision: inline inside the turn, otherwise in a later turn, so a Raise from another thread returns at
    // once instead of waiting for the model.
    private void Deliver(List<ViewRequestTarget> targets)
    {
        if (targets.Count == 0) return;
        var dispatcher = BridgeDispatcher.For(_transport);
        foreach (var group in targets.Where(target => target.ConnectionKey is not null).GroupBy(target => target.Route, StringComparer.Ordinal))
        {
            // A released route has no state to show the request with.
            if (dispatcher.FindRoute(group.Key) is not IBridgeViewRequestRoute route) continue;
            var routeTargets = group.ToArray();
            route.PublishForViewRequest(revision =>
            {
                if (revision is not { } published) return;
                lock (_gate) if (_disposed) return;
                foreach (var target in routeTargets)
                    (_pushes ??= BridgeTargetedPushes.For(_transport)).Send(target.ConnectionKey!, EncodeViewRequestPush(target, published));
            });
        }
    }

    /// <summary>
    /// Attempts browser delivery for the current trusted bridge invocation.
    /// A null result preserves ReactiveUI fallback/unhandled behavior.
    /// </summary>
    /// <remarks>
    /// The invocation must belong to this window. A bridged command reaches the presentation of
    /// <paramref name="route"/> mounted by the client and connection that invoked it: its own route, or another
    /// route of the window, such as a content View whose interaction a shell's command asks, or the departure guard
    /// of the page that the shell's Back leaves. The window scope (a close decision, a menu-bar command,
    /// <see cref="RunicInteractionInvocation.EnterWindow"/>) reaches the one presentation of the window that handles
    /// the interaction.
    /// </remarks>
    public Task<TOutput>? TryRequest<TInput, TOutput>(string route, string name, string contract,
        TInput input, Func<TInput, string> encodeInput, Func<JsonElement, TOutput> decodeOutput,
        TimeSpan? timeout = null) =>
        TryRequest([route], name, contract, input, encodeInput, decodeOutput, timeout, CancellationToken.None);

    // TryRequest for an interaction object that the window presents on routes, with the caller's own token, such as
    // ViewQuestion.AskAsync's. The presentation is selected among all of them (SelectPresentation), so the window scope
    // asks only when exactly one presentation of the window handles the object's interaction.
    internal Task<TOutput>? TryRequest<TInput, TOutput>(IReadOnlyCollection<string> routes, string name, string contract,
        TInput input, Func<TInput, string> encodeInput, Func<JsonElement, TOutput> decodeOutput,
        TimeSpan? timeout, CancellationToken callerCancellation)
    {
        ArgumentNullException.ThrowIfNull(routes);
        var invocation = RunicInteractionInvocation.Current;
        if (invocation is null || !ReferenceEquals(invocation.Session, _session)) return null;
        // A navigation hook, such as a leave confirmation's guard, asks on behalf of its transition: the request ends
        // when that transition is superseded, cancelled or closed.
        var hook = NavigationHookCancellation();
        // Only a command invocation the transport authenticated carries a connection; SelectPresentation keeps to it.
        if (!invocation.IsWindowScope && string.IsNullOrWhiteSpace(invocation.ConnectionKey)) return null;
        if (invocation.CancellationToken.IsCancellationRequested)
            return Task.FromCanceled<TOutput>(invocation.CancellationToken);
        if (hook.IsCancellationRequested) return Task.FromCanceled<TOutput>(hook);
        if (callerCancellation.IsCancellationRequested) return Task.FromCanceled<TOutput>(callerCancellation);
        var lifetime = ResolveTimeout(timeout);

        string inputJson;
        try
        {
            inputJson = encodeInput(input);
            ValidateJsonPayload(inputJson);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException)
        {
            return Task.FromException<TOutput>(new ArgumentException("The interaction input is invalid.", error));
        }

        lock (_gate)
        {
            var keys = routes.Select(route => new DefinitionKey(route, name, contract)).Where(_definitions.ContainsKey).ToHashSet();
            if (_disposed || keys.Count == 0) return null;
            var presentation = SelectPresentation(keys, invocation, static presentation => presentation.Capabilities);
            // A mounted endpoint advertises its support separately from its
            // current poll. This keeps an absent component on the normal
            // ReactiveUI fallback path while a short polling gap stays queued.
            if (presentation is null || _pending.Count >= MaximumPending
                || presentation.Pending.Count >= MaximumPendingPerPresentation) return null;

            var requestId = _session.CreateId();
            var key = new DefinitionKey(presentation.Route, name, contract);
            var request = new PendingRequest(requestId, key, presentation.Id, presentation.Key,
                presentation.ConnectionKey, presentation.ClientKey, 0, inputJson,
                value => decodeOutput(value), lifetime, _session.TimeProvider.GetUtcNow().Add(lifetime));
            _pending.Add(requestId, request);
            presentation.Pending.Enqueue(requestId);
            var cancellation = RegisterCancellation(requestId, request.Timeout, invocation.CancellationToken, hook, callerCancellation);
            request.Cancellation = cancellation;
            // Register invokes synchronously for an already-cancelled token.
            // Never deliver a request whose admission was cancelled that way.
            if (!_pending.ContainsKey(requestId))
            {
                cancellation.Dispose();
                return request.AsTask<TOutput>();
            }
            TryDeliverNext(presentation);
            return request.AsTask<TOutput>();
        }
    }

    // A presentation of the route opened by the connection; its requests are pushed to that connection.
    internal void OnPresentationMounted(string route, string presentationId, string? clientKey, string? connectionKey)
    {
        if (presentationId.Length > MaximumPresentationLength) return;
        lock (_gate)
        {
            if (_disposed) return;
            var key = PresentationKey(route, presentationId);
            if (_presentations.TryGetValue(key, out var existing))
            {
                // A repeated open of the same presentation keeps its registration and pending requests.
                if (existing.Matches(route, connectionKey, clientKey)) return;
                ReleasePresentation(key, "The browser presentation was replaced.");
            }
            _presentations[key] = new Presentation(route, presentationId, clientKey, connectionKey);
        }
    }

    // Releases the presentation id on every route of the window.
    internal void OnPresentationUnmounted(string presentationId, string reason = "The browser presentation was unmounted.")
    {
        lock (_gate)
            foreach (var key in _presentations.Values.Where(value => value.Id == presentationId).Select(value => value.Key).ToArray())
                ReleasePresentation(key, reason);
    }

    internal void OnPresentationUnmounted(string route, string presentationId, string reason)
    {
        lock (_gate) ReleasePresentation(PresentationKey(route, presentationId), reason);
    }

    internal void ReleaseConnection(string connectionKey)
    {
        lock (_gate)
        {
            // A request kept for a page whose connection closed never reaches it, nor its next connection.
            foreach (var (owner, kept) in _kept.ToArray())
                if (string.Equals(kept.ConnectionKey, connectionKey, StringComparison.Ordinal)) _kept.Remove(owner);
            foreach (var (owner, unattached) in _unattached.ToArray())
                if (string.Equals(unattached.ConnectionKey, connectionKey, StringComparison.Ordinal)) _unattached.Remove(owner);
            foreach (var key in _presentations.Values
                .Where(value => string.Equals(value.ConnectionKey, connectionKey, StringComparison.Ordinal))
                .Select(value => value.Key).ToArray())
                ReleasePresentation(key, "The browser connection was disconnected.");
        }
    }

    // Registers the handlers of an open presentation as generation 0 (open's handlers), and returns the registration
    // outcome for the open reply.
    internal string RegisterOpenHandlers(string route, string presentationId, BridgeConnection connection, JsonElement handlers) =>
        Register(route, presentationId, connection, generation: 0, handlers);

    // interactions and interaction-reply (protocol 4) of a route of this window. Returns the reply.
    internal ReadOnlyMemory<byte> Handle(BridgeRouteRequest request)
    {
        var root = request.Root;
        var presentationId = root.GetProperty("presentation").GetString()!;
        if (request.Kind == "interactions")
        {
            BridgeRequestEnvelope.TryReadInteger(root.GetProperty("generation"), out var generation);
            var registration = Register(request.Route, presentationId, request.Connection, generation, root.GetProperty("handlers"));
            return BridgeMessages.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteString("registration", registration);
                writer.WriteEndObject();
            });
        }
        if (request.Kind == "view-requests")
        {
            BridgeRequestEnvelope.TryReadInteger(root.GetProperty("generation"), out var requestGeneration);
            var requestOutcome = RegisterViewRequests(request.Route, presentationId, request.Connection, requestGeneration,
                root.GetProperty("requests"), out var kept);
            Deliver(kept);
            return BridgeMessages.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("ok", true);
                writer.WriteString("registration", requestOutcome);
                writer.WriteEndObject();
            });
        }
        if (request.Kind != "interaction-reply")
            throw new ArgumentException($"'{request.Kind}' is not an interaction request.", nameof(request));
        BridgeRequestEnvelope.TryReadInteger(root.GetProperty("ownerEpoch"), out var ownerEpoch);
        var outcome = root.GetProperty("outcome").GetString()!;
        var reply = new InteractionReply(outcome, root.GetProperty("requestId").GetString()!, request.Route, presentationId,
            ownerEpoch, root.GetProperty("name").GetString()!, root.GetProperty("contract").GetString()!,
            outcome == "answered" ? root.GetProperty("output").Clone() : null);
        string result;
        try
        {
            // The canonical form makes a repeat with the same output in another layout already-completed.
            if (reply.Output is { } output)
                reply = reply with { OutputSignature = BridgeCanonicalJson.ToString(JsonMarshal.GetRawUtf8Value(output)) };
            result = Settle(reply, request.Connection.Key, request.Connection.ClientKey);
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            result = "invalid-output";
        }
        return BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("result", result);
            writer.WriteEndObject();
        });
    }

    // A protocol-4 registration (specs/application/README.md, "Interactions"): ignored when the presentation is not
    // open on this connection; unsupported when a handler names an interaction or contract the route does not have;
    // stale for a lower generation, or the same generation with other handlers; otherwise the handlers replace the
    // registration.
    private string Register(string route, string presentationId, BridgeConnection connection, long generation, JsonElement handlers)
    {
        var identities = handlers.EnumerateArray()
            .Select(handler => new HandlerIdentity(handler.GetProperty("name").GetString()!, handler.GetProperty("contract").GetString()!))
            .ToArray();
        lock (_gate)
        {
            if (_disposed || !_presentations.TryGetValue(PresentationKey(route, presentationId), out var presentation)
                || !presentation.Matches(route, connection.Key, connection.ClientKey)) return "ignored";
            var supported = identities.Select(handler => new DefinitionKey(route, handler.Name, handler.Contract)).ToHashSet();
            if (supported.Any(key => !_definitions.ContainsKey(key))) return "unsupported";
            if (presentation.RegisteredGeneration is { } registered
                && (generation < registered || (generation == registered && !presentation.Capabilities.SetEquals(supported))))
                return "stale";
            presentation.RegisteredGeneration = generation;
            presentation.Capabilities.Clear();
            presentation.Capabilities.UnionWith(supported);
            TryDeliverNext(presentation);
            return "ok";
        }
    }

    // A view-requests registration (view-requests/1), with the outcomes of an interaction registration. A request that
    // the registration adds and that a NextMounted request was kept for is delivered to it, once.
    private string RegisterViewRequests(string route, string presentationId, BridgeConnection connection, long generation,
        JsonElement requests, out List<ViewRequestTarget> kept)
    {
        kept = [];
        var identities = requests.EnumerateArray()
            .Select(handler => new DefinitionKey(route, handler.GetProperty("name").GetString()!, handler.GetProperty("contract").GetString()!))
            .ToHashSet();
        lock (_gate)
        {
            if (_disposed || !_presentations.TryGetValue(PresentationKey(route, presentationId), out var presentation)
                || !presentation.Matches(route, connection.Key, connection.ClientKey)) return "ignored";
            if (identities.Any(key => !_requestDefinitions.ContainsKey(key))) return "unsupported";
            if (presentation.RequestGeneration is { } registered
                && (generation < registered || (generation == registered && !presentation.Requests.SetEquals(identities))))
                return "stale";
            presentation.RequestGeneration = generation;
            presentation.Requests.Clear();
            presentation.Requests.UnionWith(identities);
            foreach (var (owner, request) in _kept.ToArray())
            {
                if (!request.Keys.Overlaps(identities) || (request.ConnectionKey is not null
                    && (!string.Equals(request.ConnectionKey, presentation.ConnectionKey, StringComparison.Ordinal)
                        || !string.Equals(request.ClientKey, presentation.ClientKey, StringComparison.Ordinal)))) continue;
                _kept.Remove(owner);
                kept.Add(Target(presentation, request.Name, request.Contract, request.InputJson));
            }
            return "ok";
        }
    }

    // Settles a pending request with a page's reply: ok, already-completed for a repeat of the reply received,
    // stale for a request that is not pending or an identity that does not match, invalid-output when the output does
    // not decode (the waiting interaction then fails with RunicInteractionOutputException), or released.
    private string Settle(InteractionReply reply, string? connectionKey, string? clientKey)
    {
        PendingRequest request;
        lock (_gate)
        {
            if (_disposed) return Generated.BridgeFailure.Released;
            if (!_pending.TryGetValue(reply.RequestId, out var pending))
            {
                return _receipts.TryGetValue(reply.RequestId, out var receipt) && receipt.Matches(reply, connectionKey, clientKey)
                    ? "already-completed" : "stale";
            }
            if (!pending.Matches(reply, connectionKey, clientKey)) return "stale";
            request = pending;
            // The reply settles this request whatever its output decodes to.
            CompleteRequest(request, reply);
        }

        if (reply.Kind == "cancelled")
            request.CompleteException(new RunicInteractionCancelledException("The browser cancelled the interaction."));
        else if (reply.Kind == "failed")
            request.CompleteException(new InvalidOperationException("The browser interaction handler failed."));
        else
        {
            // The generated decoder is application code: run it outside the
            // router gate, and fail the waiting interaction with a typed
            // error instead of leaving it pending until its timeout.
            try { request.CompleteOutput(request.Decode(reply.Output!.Value)); }
            catch (Exception error)
            {
                request.CompleteException(new RunicInteractionOutputException(
                    "The browser interaction output did not match its contract.", error));
                return "invalid-output";
            }
        }
        return "ok";
    }

    // The one presentation that handles keys (one per route that presents the object), for interactions and view
    // requests alike. The window scope, and work outside any scope, has no invoking client: any presentation of this
    // window may handle it, if it is the only one on all of the object's routes. A command invocation keeps to the
    // invoking client and connection, and prefers its own route's single presentation. The gate is held.
    private Presentation? SelectPresentation(HashSet<DefinitionKey> keys, RunicInteractionInvocation? invocation,
        Func<Presentation, HashSet<DefinitionKey>> handles)
    {
        var command = invocation is { IsWindowScope: false } ? invocation : null;
        var matches = _presentations.Values.Where(presentation => handles(presentation).Overlaps(keys)
            && (command is null
                || (string.Equals(presentation.ConnectionKey, command.ConnectionKey, StringComparison.Ordinal)
                    && string.Equals(presentation.ClientKey, command.ClientKey, StringComparison.Ordinal)))).ToArray();
        if (command is not null && matches.Where(presentation => presentation.Route == command.Route).ToArray() is [var own])
            return own;
        return matches.Length == 1 ? matches[0] : null;
    }

    private void TryDeliverNext(Presentation presentation) => PushPending(presentation);

    // Pushes the presentation's requests to its connection, each with its owner epoch. The gate is held.
    private void PushPending(Presentation presentation)
    {
        while (presentation.Pending.TryDequeue(out var requestId))
        {
            if (!_pending.TryGetValue(requestId, out var request)) continue;
            if (!presentation.Capabilities.Contains(request.Key))
            {
                CancelRequest(requestId, new RunicInteractionCancelledException("The browser handler was removed."));
                continue;
            }
            request.OwnerEpoch = ++presentation.Generation;
            Push(presentation, EncodeInteractionPush(request));
        }
    }

    // The gate is held.
    private void Push(Presentation presentation, byte[] message)
    {
        if (presentation.ConnectionKey is not { } connectionKey) return;
        (_pushes ??= BridgeTargetedPushes.For(_transport)).Send(connectionKey, message);
    }

    // Only a request the page received needs its cancellation. The gate is held.
    private void NotifyCancelled(PendingRequest request, string reason)
    {
        if (request.OwnerEpoch > 0 && _presentations.TryGetValue(request.PresentationKey, out var presentation))
            Push(presentation, EncodeCancelledPush(request, reason));
    }

    private void ReleasePresentation(string presentationKey, string reason)
    {
        _presentations.Remove(presentationKey);
        foreach (var request in _pending.Values.Where(value => value.PresentationKey == presentationKey).ToArray())
        {
            request.CompleteException(new RunicInteractionCancelledException(reason));
            CompleteRequest(request, CancelledReply(request));
        }
    }

    private void CancelRequest(string requestId, Exception error)
    {
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out var request)) return;
            request.CompleteException(error);
            AddReceipt(request, CancelledReply(request));
            NotifyCancelled(request, error.Message);
            request.Dispose();
        }
    }

    private static CancellationToken NavigationHookCancellation() => Runic.Navigation.RunicNavigator.HookCancellationToken;

    private CancellationLease RegisterCancellation(string requestId, TimeSpan timeout, CancellationToken cancellationToken,
        CancellationToken hookCancellation, CancellationToken callerCancellation)
    {
        var source = new CancellationTokenSource(timeout, _session.TimeProvider);
        var linked = CancellationTokenSource.CreateLinkedTokenSource([cancellationToken, hookCancellation, callerCancellation, source.Token]);
        var registration = linked.Token.Register(() =>
        {
            Exception error = cancellationToken.IsCancellationRequested
                ? new RunicInteractionCancelledException("The invoking command was cancelled.")
                : hookCancellation.IsCancellationRequested
                    ? new RunicInteractionCancelledException("The navigation that asked was superseded or cancelled.")
                    : callerCancellation.IsCancellationRequested
                        ? new RunicInteractionCancelledException("The question was cancelled.")
                        : new RunicInteractionTimeoutException("The browser interaction timed out.");
            CancelRequest(requestId, error);
        });
        return new CancellationLease(registration, linked, source);
    }

    private void CompleteRequest(PendingRequest request, InteractionReply reply)
    {
        _pending.Remove(request.RequestId);
        AddReceipt(request, reply);
        request.Dispose();
    }

    private void AddReceipt(PendingRequest request, InteractionReply reply)
    {
        while (_receipts.Count >= MaximumReceipts) _receipts.Remove(_receipts.Keys.First());
        _receipts[request.RequestId] = new Receipt(request, reply);
    }

    private static InteractionReply CancelledReply(PendingRequest request) => new("cancelled", request.RequestId,
        request.Key.Route, request.PresentationId, request.OwnerEpoch, request.Key.Name, request.Key.Contract, null);

    private static void ValidateDefinition<TInput, TOutput>(string route, string name, string contract,
        Func<TInput, string> encodeInput, Func<JsonElement, TOutput> decodeOutput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract);
        ArgumentNullException.ThrowIfNull(encodeInput);
        ArgumentNullException.ThrowIfNull(decodeOutput);
        if (route.Length > MaximumRouteLength || name.Length > MaximumNameLength || contract.Length > MaximumContractLength)
            throw new ArgumentException("The interaction identity is too long.");
    }

    private void ValidateJsonPayload(string json)
    {
        if (!BridgeInputLimit.Fits(json, _session.MaximumInputBytes)) throw new ArgumentException("The interaction payload is too large.");
        using var _ = JsonDocument.Parse(json);
    }

    private static TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? TimeSpan.FromMinutes(2);
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Interaction timeouts must be between zero and ten minutes.");
        return value;
    }

    // {"kind":"interaction", route, presentation, requestId, ownerEpoch, name, contract, expiresAt, input}
    private static byte[] EncodeInteractionPush(PendingRequest request) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "interaction");
        writer.WriteString("route", request.Key.Route);
        writer.WriteString("presentation", request.PresentationId);
        writer.WriteString("requestId", request.RequestId);
        writer.WriteNumber("ownerEpoch", request.OwnerEpoch);
        writer.WriteString("name", request.Key.Name);
        writer.WriteString("contract", request.Key.Contract);
        writer.WriteString("expiresAt", OffsetDateTime(request.ExpiresAt));
        writer.WritePropertyName("input");
        writer.WriteRawValue(request.InputJson);
        writer.WriteEndObject();
    });

    // {"kind":"view-request", route, presentation, revision, name, contract, input}
    private static byte[] EncodeViewRequestPush(ViewRequestTarget target, long revision) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "view-request");
        writer.WriteString("route", target.Route);
        writer.WriteString("presentation", target.PresentationId);
        writer.WriteNumber("revision", revision);
        writer.WriteString("name", target.Name);
        writer.WriteString("contract", target.Contract);
        writer.WritePropertyName("input");
        writer.WriteRawValue(target.InputJson);
        writer.WriteEndObject();
    });

    // {"kind":"interaction-cancelled", route, presentation, requestId, ownerEpoch, message}
    private static byte[] EncodeCancelledPush(PendingRequest request, string message) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "interaction-cancelled");
        writer.WriteString("route", request.Key.Route);
        writer.WriteString("presentation", request.PresentationId);
        writer.WriteString("requestId", request.RequestId);
        writer.WriteNumber("ownerEpoch", request.OwnerEpoch);
        writer.WriteString("message", message);
        writer.WriteEndObject();
    });

    // The normal form of an offset-date-time (specs/application/values.md): UTC with Z, and the fraction of a second
    // without trailing zeros.
    internal static string OffsetDateTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static string PresentationKey(string route, string presentationId) => $"{route}\u0000{presentationId}";

    /// <summary>Cancels pending interactions and forgets the window's presentations.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var request in _pending.Values) { request.CompleteException(new RunicInteractionCancelledException("The window was closed.")); request.Dispose(); }
            _pending.Clear();
            _presentations.Clear();
            _definitions.Clear();
            _receipts.Clear();
            _requestDefinitions.Clear();
            _kept.Clear();
            _unattached.Clear();
        }
    }

    private void RemoveRegistration(DefinitionKey key)
    {
        lock (_gate)
        {
            if (!_definitions.Remove(key, out var count)) return;
            if (count > 1) _definitions[key] = count - 1;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private sealed class Registration(BridgeInteractionRouter owner, DefinitionKey key) : IDisposable
    { private BridgeInteractionRouter? _owner = owner; public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveRegistration(key); }
    private sealed class ViewRequestRegistration(BridgeInteractionRouter owner, DefinitionKey key) : IDisposable
    { private BridgeInteractionRouter? _owner = owner; public void Dispose() => Interlocked.Exchange(ref _owner, null)?.RemoveViewRequest(key); }
    // A NextMounted request kept for the next presentation that registers it on one of the routes of Keys.
    private sealed record KeptViewRequest(HashSet<DefinitionKey> Keys, string Name, string Contract, string InputJson,
        string? ClientKey, string? ConnectionKey);
    private sealed record UnattachedViewRequest(object? Input, long Sequence, string Route, string? ClientKey,
        string? ConnectionKey, bool WindowScope);
    private sealed record ViewRequestTarget(string Route, string PresentationId, string? ConnectionKey, string Name,
        string Contract, string InputJson);
    private sealed class Presentation(string route, string id, string? clientKey, string? connectionKey)
    {
        public string Route { get; } = route;
        public string Id { get; } = id;
        public string Key { get; } = PresentationKey(route, id);
        // The generation of the registration; null until open or interactions registered handlers.
        public long? RegisteredGeneration { get; set; }
        public string? ClientKey { get; } = clientKey;
        public string? ConnectionKey { get; } = connectionKey;
        // The owner epoch of the presentation's last pushed request.
        public long Generation { get; set; }
        public HashSet<DefinitionKey> Capabilities { get; } = [];
        public Queue<string> Pending { get; } = [];
        // view-requests/1: the generation of the view request registration, null until one, and its requests.
        public long? RequestGeneration { get; set; }
        public HashSet<DefinitionKey> Requests { get; } = [];
        public bool Matches(string route, string? connectionKey, string? clientKey) =>
            string.Equals(Route, route, StringComparison.Ordinal)
            && string.Equals(ClientKey, clientKey, StringComparison.Ordinal)
            && string.Equals(ConnectionKey, connectionKey, StringComparison.Ordinal);
    }
    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<JsonElement, object?> _decodeOutput;
        public PendingRequest(string requestId, DefinitionKey key, string presentationId, string presentationKey, string? connectionKey, string? clientKey, long ownerEpoch, string inputJson, Func<JsonElement, object?> decodeOutput, TimeSpan timeout, DateTimeOffset expiresAt)
        { RequestId = requestId; Key = key; PresentationId = presentationId; PresentationKey = presentationKey; ConnectionKey = connectionKey; ClientKey = clientKey; OwnerEpoch = ownerEpoch; InputJson = inputJson; Timeout = timeout; ExpiresAt = expiresAt; _decodeOutput = decodeOutput; }
        public string PresentationKey { get; }
        public object? Decode(JsonElement value) => _decodeOutput(value);
        public string RequestId { get; } public DefinitionKey Key { get; } public string PresentationId { get; } public string? ConnectionKey { get; } public string? ClientKey { get; } public long OwnerEpoch { get; set; } public string InputJson { get; } public TimeSpan Timeout { get; } public DateTimeOffset ExpiresAt { get; } public CancellationLease? Cancellation { get; set; }
        public Task<T> AsTask<T>() => _completion.Task.ContinueWith(task => (T)task.GetAwaiter().GetResult()!, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        public void CompleteOutput(object? value) => _completion.TrySetResult(value); public void CompleteException(Exception error) => _completion.TrySetException(error); public bool Matches(InteractionReply reply, string? connectionKey, string? clientKey) => reply.RequestId == RequestId && reply.Route == Key.Route && reply.PresentationId == PresentationId && reply.OwnerEpoch == OwnerEpoch && reply.Name == Key.Name && reply.Contract == Key.Contract && string.Equals(ConnectionKey,connectionKey,StringComparison.Ordinal) && string.Equals(ClientKey,clientKey,StringComparison.Ordinal); public void Dispose() => Cancellation?.Dispose();
    }
    private sealed class CancellationLease(CancellationTokenRegistration registration, CancellationTokenSource linked,
        CancellationTokenSource timeout) : IDisposable
    {
        private CancellationTokenRegistration _registration = registration;
        private CancellationTokenSource? _linked = linked;
        private CancellationTokenSource? _timeout = timeout;
        public void Dispose()
        {
            _registration.Unregister();
            Interlocked.Exchange(ref _linked, null)?.Dispose();
            Interlocked.Exchange(ref _timeout, null)?.Dispose();
        }
    }
    private sealed record Receipt(string RequestId, string Route, string PresentationId, long OwnerEpoch, string Name, string Contract, string? ConnectionKey, string? ClientKey, string ReplySignature)
    {
        public Receipt(PendingRequest request, InteractionReply reply) : this(request.RequestId, request.Key.Route,
            request.PresentationId, request.OwnerEpoch, request.Key.Name, request.Key.Contract,
            request.ConnectionKey, request.ClientKey, Signature(reply)) { }

        public bool Matches(InteractionReply reply, string? connectionKey, string? clientKey) =>
            reply.RequestId == RequestId && reply.Route == Route && reply.PresentationId == PresentationId
            && reply.OwnerEpoch == OwnerEpoch && reply.Name == Name && reply.Contract == Contract
            && ReplySignature == Signature(reply)
            && string.Equals(ConnectionKey, connectionKey, StringComparison.Ordinal)
            && string.Equals(ClientKey, clientKey, StringComparison.Ordinal);

        private static string Signature(InteractionReply reply) => reply.Kind + ":" + reply.OutputSignature;
    }

    private readonly record struct DefinitionKey(string Route, string Name, string Contract);
    private sealed record HandlerIdentity(string Name, string Contract);
    private sealed record InteractionReply(string Kind, string RequestId, string Route, string PresentationId, long OwnerEpoch, string Name, string Contract, JsonElement? Output)
    {
        public string OutputSignature { get; init; } = "";
    }
}
