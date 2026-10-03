using System.Text;
using System.Text.Json;

namespace Runic.Application.Views;

/// <summary>
/// The trusted origin of work which may ask a browser to handle an interaction.
/// Generated bridges establish this scope around a command or operation. It is
/// deliberately absent for arbitrary background work, which must use a .NET
/// handler or opt into an explicit scope.
/// </summary>
public sealed class RunicInteractionInvocation
{
    private static readonly AsyncLocal<RunicInteractionInvocation?> CurrentScope = new();

    private RunicInteractionInvocation(WindowContentSession session, string route, string? clientKey,
        string? connectionKey, string? commandName, CancellationToken cancellationToken)
    {
        Session = session;
        Route = route;
        ClientKey = clientKey;
        ConnectionKey = connectionKey;
        CommandName = commandName;
        CancellationToken = cancellationToken;
    }

    /// <summary>The window content session that owns the invocation.</summary>
    public WindowContentSession Session { get; }
    /// <summary>The bridge route whose browser client may handle interactions.</summary>
    public string Route { get; }
    /// <summary>The trusted browser client identity, if the invocation came from one.</summary>
    public string? ClientKey { get; }
    /// <summary>The trusted transport connection identity, if the invocation came from one.</summary>
    public string? ConnectionKey { get; }
    /// <summary>The generated command that started the invocation, if any.</summary>
    public string? CommandName { get; }
    /// <summary>Cancels interactions requested by this invocation.</summary>
    public CancellationToken CancellationToken { get; }
    /// <summary>The invocation scope on the current asynchronous flow, or <see langword="null"/>.</summary>
    public static RunicInteractionInvocation? Current => CurrentScope.Value;

    /// <summary>
    /// Enters a trusted bridge invocation. Callers must use identity supplied by
    /// <see cref="IBridgeArguments"/>, never values supplied inside request JSON.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
        Justification = "Published API called by generated bridges; reordering its optional parameters would break them.")]
    public static IDisposable Enter(WindowContentSession session, string route, IBridgeArguments arguments,
        CancellationToken cancellationToken = default, string? commandName = null) =>
        Enter(session, route, arguments.ClientKey, arguments.ConnectionKey, cancellationToken, commandName);

    /// <summary>Enters an explicitly selected interaction scope.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068:CancellationToken parameters must come last",
        Justification = "Published API called by generated bridges; reordering its optional parameters would break them.")]
    public static IDisposable Enter(WindowContentSession session, string route, string? clientKey,
        string? connectionKey, CancellationToken cancellationToken = default, string? commandName = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var previous = CurrentScope.Value;
        CurrentScope.Value = new RunicInteractionInvocation(session, route, clientKey, connectionKey,
            commandName, cancellationToken);
        return new Scope(previous);
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

/// <summary>Neutral generated interaction descriptor. Optional adapters implement its attachment behavior.</summary>
public abstract class BridgeInteractionDescriptor<TModel>
{
    /// <summary>Attaches the interaction of <paramref name="model"/> to the browser route.</summary>
    /// <returns>A lease that detaches the interaction.</returns>
    public abstract IDisposable Attach(WindowContentSession session, TModel model, string route);
}

/// <summary>Raised when a selected mounted interaction endpoint disappears.</summary>
public sealed class RunicInteractionCancelledException(string message) : OperationCanceledException(message);

/// <summary>Raised when a browser interaction exceeded its configured lifetime.</summary>
public sealed class RunicInteractionTimeoutException(string message) : TimeoutException(message);

/// <summary>Raised when a browser answered an interaction with output its contract cannot decode.</summary>
public sealed class RunicInteractionOutputException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Window-owned request/reply broker for typed interaction adapters. Browser
/// delivery is pull-based: a mounted endpoint receives input only as the reply
/// to its own authenticated wait route.
/// </summary>
public sealed class BridgeInteractionRouter : IDisposable
{
    /// <summary>The route on which a mounted endpoint waits for interaction input.</summary>
    public const string WaitRoute = "__runicInteractionWait";
    /// <summary>The route on which an endpoint answers an interaction.</summary>
    public const string ReplyRoute = "__runicInteractionReply";
    /// <summary>The route on which an endpoint reports the interaction handlers it has mounted.</summary>
    public const string ControlRoute = "__runicInteractionControl";
    /// <summary>The route on which an endpoint waits for control events.</summary>
    public const string ControlWaitRoute = "__runicInteractionControlWait";
    private const int MaximumPending = 32;
    private const int MaximumPendingPerPresentation = 8;
    private const int MaximumControlEvents = 32;
    private const int MaximumReceipts = 64;
    private const int MaximumRouteLength = 512;
    private const int MaximumNameLength = 128;
    private const int MaximumContractLength = 256;
    private const int MaximumPresentationLength = 256;
    private const int MaximumPayloadBytes = 64 * 1024;

    private readonly object _gate = new();
    private readonly WindowContentSession _session;
    private readonly Dictionary<string, Presentation> _presentations = new(StringComparer.Ordinal);
    private readonly Dictionary<DefinitionKey, int> _definitions = [];
    private readonly Dictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ControlWaiter> _controlWaiters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.Ordinal);
    private readonly IDisposable[] _bindings;
    private bool _disposed;

    internal BridgeInteractionRouter(WindowContentSession session, IBridgeTransport transport)
    {
        _session = session;
        var bindings = new List<IDisposable>();
        try
        {
            bindings.Add(transport.BindAsync(WaitRoute, WaitAsync));
            bindings.Add(transport.Bind(ReplyRoute, Reply));
            bindings.Add(transport.Bind(ControlRoute, Control));
            bindings.Add(transport.BindAsync(ControlWaitRoute, ControlWaitAsync));
            _bindings = [.. bindings];
        }
        catch
        {
            foreach (var binding in bindings) binding.Dispose();
            throw;
        }
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

    /// <summary>
    /// Attempts browser delivery for the current trusted bridge invocation.
    /// A null result preserves ReactiveUI fallback/unhandled behavior.
    /// </summary>
    public Task<TOutput>? TryRequest<TInput, TOutput>(string route, string name, string contract,
        TInput input, Func<TInput, string> encodeInput, Func<JsonElement, TOutput> decodeOutput,
        TimeSpan? timeout = null)
    {
        var invocation = RunicInteractionInvocation.Current;
        if (invocation is null || !ReferenceEquals(invocation.Session, _session)
            || !string.Equals(invocation.Route, route, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(invocation.ConnectionKey)) return null;
        if (invocation.CancellationToken.IsCancellationRequested)
            return Task.FromCanceled<TOutput>(invocation.CancellationToken);
        var lifetime = ResolveTimeout(timeout);

        var key = new DefinitionKey(route, name, contract);
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
            if (_disposed || !_definitions.ContainsKey(key)) return null;
            var presentation = SelectPresentation(key, invocation);
            // A mounted endpoint advertises its support separately from its
            // current poll. This keeps an absent component on the normal
            // ReactiveUI fallback path while a short polling gap stays queued.
            if (presentation is null || _pending.Count >= MaximumPending
                || presentation.Pending.Count >= MaximumPendingPerPresentation) return null;

            var requestId = Guid.NewGuid().ToString("N");
            var request = new PendingRequest(requestId, key, presentation.Id,
                presentation.ConnectionKey, presentation.ClientKey, 0, inputJson,
                value => decodeOutput(value), lifetime);
            _pending.Add(requestId, request);
            presentation.Pending.Enqueue(requestId);
            var cancellation = RegisterCancellation(requestId, request.Timeout, invocation.CancellationToken);
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

    internal void OnPresentationMounted(string route, string presentationId, string? clientKey, string? connectionKey)
    {
        if (presentationId.Length > MaximumPresentationLength) return;
        lock (_gate)
        {
            if (_disposed) return;
            _presentations[presentationId] = new Presentation(route, presentationId, clientKey, connectionKey);
        }
    }

    internal void OnPresentationUnmounted(string presentationId, string reason = "The browser presentation was unmounted.")
    {
        lock (_gate) ReleasePresentation(presentationId, reason);
    }

    internal void ReleaseConnection(string connectionKey)
    {
        lock (_gate)
            foreach (var presentationId in _presentations.Values
                .Where(value => string.Equals(value.ConnectionKey, connectionKey, StringComparison.Ordinal))
                .Select(value => value.Id).ToArray())
                ReleasePresentation(presentationId, "The browser connection was disconnected.");
    }

    private async ValueTask<string> WaitAsync(IBridgeArguments arguments, CancellationToken observerCancellation)
    {
        WaitRequest request;
        try { request = ParseWait(arguments.GetString()); }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return EncodeKind("invalid-request"); }

        Waiter? waiter;
        lock (_gate)
        {
            if (_disposed) return EncodeKind("disconnected");
            if (!_presentations.TryGetValue(request.PresentationId, out var presentation)
                || !presentation.Matches(request.Route, arguments)
                || _waiters.ContainsKey(request.PresentationId)) return EncodeKind("ignored");

            var capability = UpdateCapabilities(presentation, request.Route, request.Handlers, request.Generation);
            if (capability != "ok") return EncodeKind(capability);
            if (presentation.Capabilities.Count == 0) return EncodeKind("unsupported");
            waiter = new Waiter(request.PresentationId, presentation, presentation.ClientKey, presentation.ConnectionKey);
            _waiters.Add(request.PresentationId, waiter);
            var cancellation = observerCancellation.Register(() => CancelWaiter(request.PresentationId, waiter));
            waiter.Cancellation = cancellation;
            if (!_waiters.ContainsKey(request.PresentationId)) cancellation.Unregister();
            TryDeliverNext(presentation);
        }

        try { return await waiter.Completion.Task.ConfigureAwait(false); }
        finally { CancelWaiter(request.PresentationId, waiter); }
    }

    private string Control(IBridgeArguments arguments)
    {
        ControlRequest request;
        try { request = ParseControl(arguments.GetString()); }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return EncodeKind("invalid-request"); }

        lock (_gate)
        {
            if (_disposed) return EncodeKind("disconnected");
            if (!_presentations.TryGetValue(request.PresentationId, out var presentation)
                || !presentation.Matches(request.Route, arguments)) return EncodeKind("ignored");
            var result = UpdateCapabilities(presentation, request.Route, request.Handlers, request.Generation);
            if (result == "ok") TryDeliverNext(presentation);
            return EncodeKind(result);
        }
    }

    private async ValueTask<string> ControlWaitAsync(IBridgeArguments arguments, CancellationToken observerCancellation)
    {
        ControlWaitRequest request;
        try { request = ParseControlWait(arguments.GetString()); }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return EncodeKind("invalid-request"); }

        ControlWaiter? waiter;
        lock (_gate)
        {
            if (_disposed) return EncodeKind("disconnected");
            if (!_presentations.TryGetValue(request.PresentationId, out var presentation)
                || !presentation.Matches(request.Route, arguments)
                || _controlWaiters.ContainsKey(request.PresentationId)) return EncodeKind("ignored");
            waiter = new ControlWaiter(request.PresentationId);
            _controlWaiters.Add(request.PresentationId, waiter);
            waiter.Cancellation = observerCancellation.Register(() => CancelControlWaiter(request.PresentationId, waiter));
            if (!_controlWaiters.ContainsKey(request.PresentationId)) waiter.Cancellation.Unregister();
            TryDeliverControl(presentation);
        }
        try { return await waiter.Completion.Task.ConfigureAwait(false); }
        finally { CancelControlWaiter(request.PresentationId, waiter); }
    }

    private string Reply(IBridgeArguments arguments)
    {
        InteractionReply reply;
        try { reply = ParseReply(arguments.GetString()); }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        { return EncodeKind("invalid-request"); }

        PendingRequest request;
        lock (_gate)
        {
            if (_disposed) return EncodeKind("disconnected");
            if (!_pending.TryGetValue(reply.RequestId, out var pending))
            {
                return _receipts.TryGetValue(reply.RequestId, out var receipt) && receipt.Matches(reply, arguments)
                    ? EncodeKind("already-completed") : EncodeKind("stale");
            }
            if (!pending.Matches(reply, arguments)) return EncodeKind("stale");
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
                return EncodeKind("invalid-output");
            }
        }
        return EncodeKind("ok");
    }

    private Presentation? SelectPresentation(DefinitionKey key, RunicInteractionInvocation invocation)
    {
        var matches = _presentations.Values.Where(presentation => presentation.Capabilities.Contains(key)
            && string.Equals(presentation.ConnectionKey, invocation.ConnectionKey, StringComparison.Ordinal)
            && string.Equals(presentation.ClientKey, invocation.ClientKey, StringComparison.Ordinal)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private string UpdateCapabilities(Presentation presentation, string route, HandlerIdentity[] handlers, long generation)
    {
        var supported = handlers
            .Where(handler => _definitions.ContainsKey(new DefinitionKey(route, handler.Name, handler.Contract)))
            .Select(handler => new DefinitionKey(route, handler.Name, handler.Contract))
            .ToHashSet();
        if (handlers.Length != supported.Count) return "unsupported";
        // Wait requests from pre-control generated clients carry generation 0.
        // Keep their old replace-on-poll semantics without letting them roll a
        // modern control registration backwards.
        if (generation == 0)
        {
            if (presentation.CapabilityGeneration != 0) return "ok";
            presentation.Capabilities.Clear();
            presentation.Capabilities.UnionWith(supported);
            return "ok";
        }
        if (generation < presentation.CapabilityGeneration) return "stale";
        if (generation == presentation.CapabilityGeneration)
            return presentation.Capabilities.SetEquals(supported) ? "ok" : "stale";
        presentation.Capabilities.Clear();
        presentation.Capabilities.UnionWith(supported);
        presentation.CapabilityGeneration = generation;
        return "ok";
    }

    private void TryDeliverNext(Presentation presentation)
    {
        if (!_waiters.TryGetValue(presentation.Id, out var waiter)) return;
        while (presentation.Pending.TryDequeue(out var requestId))
        {
            if (!_pending.TryGetValue(requestId, out var request)) continue;
            if (!presentation.Capabilities.Contains(request.Key))
            {
                CancelRequest(requestId, new RunicInteractionCancelledException("The browser handler was removed."));
                continue;
            }
            request.OwnerEpoch = ++presentation.Generation;
            waiter.DisposeRegistration();
            _waiters.Remove(presentation.Id);
            waiter.Completion.TrySetResult(EncodeRequest(request));
            return;
        }
    }

    private void CancelWaiter(string presentationId, Waiter expected)
    {
        lock (_gate)
        {
            if (_waiters.TryGetValue(presentationId, out var current) && ReferenceEquals(current, expected))
            {
                _waiters.Remove(presentationId);
                current.DisposeRegistration();
                current.Completion.TrySetResult(EncodeKind("cancelled"));
            }
        }
    }

    private void CancelControlWaiter(string presentationId, ControlWaiter expected)
    {
        lock (_gate)
        {
            if (_controlWaiters.TryGetValue(presentationId, out var current) && ReferenceEquals(current, expected))
            {
                _controlWaiters.Remove(presentationId);
                current.DisposeRegistration();
                current.Completion.TrySetResult(EncodeKind("cancelled"));
            }
        }
    }

    private void TryDeliverControl(Presentation presentation)
    {
        if (!_controlWaiters.TryGetValue(presentation.Id, out var waiter)
            || !presentation.ControlEvents.TryDequeue(out var control)) return;
        waiter.DisposeRegistration();
        _controlWaiters.Remove(presentation.Id);
        waiter.Completion.TrySetResult(EncodeControl(control));
    }

    private void NotifyCancelled(PendingRequest request, string reason)
    {
        if (!_presentations.TryGetValue(request.PresentationId, out var presentation)) return;
        while (presentation.ControlEvents.Count >= MaximumControlEvents) presentation.ControlEvents.Dequeue();
        presentation.ControlEvents.Enqueue(new ControlEvent("cancelled", request.RequestId, request.Key.Route,
            request.PresentationId, request.OwnerEpoch, reason));
        TryDeliverControl(presentation);
    }

    private void ReleasePresentation(string presentationId, string reason)
    {
        _presentations.Remove(presentationId);
        if (_waiters.Remove(presentationId, out var waiter))
        {
            waiter.DisposeRegistration();
            waiter.Completion.TrySetResult(EncodeKind("disconnected"));
        }
        if (_controlWaiters.Remove(presentationId, out var controlWaiter))
        {
            controlWaiter.DisposeRegistration();
            controlWaiter.Completion.TrySetResult(EncodeKind("disconnected"));
        }
        foreach (var request in _pending.Values.Where(value => value.PresentationId == presentationId).ToArray())
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

    private CancellationLease RegisterCancellation(string requestId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = new CancellationTokenSource(timeout);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, source.Token);
        var registration = linked.Token.Register(() =>
        {
            Exception error = cancellationToken.IsCancellationRequested
                ? new RunicInteractionCancelledException("The invoking command was cancelled.")
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

    private static void ValidateJsonPayload(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new ArgumentException("The interaction payload is too large.");
        using var _ = JsonDocument.Parse(json);
    }

    private static TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? TimeSpan.FromMinutes(2);
        if (value <= TimeSpan.Zero || value > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Interaction timeouts must be between zero and ten minutes.");
        return value;
    }

    private static WaitRequest ParseWait(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new ArgumentException("The wait request is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var route = root.GetProperty("route").GetString() ?? "";
        var presentationId = root.GetProperty("presentationId").GetString() ?? "";
        if (route.Length is 0 or > MaximumRouteLength || presentationId.Length is 0 or > MaximumPresentationLength)
            throw new ArgumentException("Invalid presentation identity.");
        var handlers = root.GetProperty("handlers").EnumerateArray().Select(element => new HandlerIdentity(
            element.GetProperty("name").GetString() ?? "", element.GetProperty("contract").GetString() ?? "")).ToArray();
        if (handlers.Length == 0 || handlers.Any(value => value.Name.Length is 0 or > MaximumNameLength
            || value.Contract.Length is 0 or > MaximumContractLength)) throw new ArgumentException("Invalid handler identity.");
        var generation = root.TryGetProperty("generation", out var value) ? value.GetInt64() : 0;
        if (generation < 0) throw new ArgumentException("Invalid capability generation.");
        return new WaitRequest(route, presentationId, handlers, generation);
    }

    private static ControlRequest ParseControl(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new ArgumentException("The control request is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var route = root.GetProperty("route").GetString() ?? "";
        var presentationId = root.GetProperty("presentationId").GetString() ?? "";
        var generation = root.GetProperty("generation").GetInt64();
        if (route.Length is 0 or > MaximumRouteLength || presentationId.Length is 0 or > MaximumPresentationLength || generation < 1)
            throw new ArgumentException("Invalid presentation identity.");
        var handlers = root.GetProperty("handlers").EnumerateArray().Select(element => new HandlerIdentity(
            element.GetProperty("name").GetString() ?? "", element.GetProperty("contract").GetString() ?? "")).ToArray();
        if (handlers.Any(value => value.Name.Length is 0 or > MaximumNameLength || value.Contract.Length is 0 or > MaximumContractLength))
            throw new ArgumentException("Invalid handler identity.");
        return new ControlRequest(route, presentationId, generation, handlers);
    }

    private static ControlWaitRequest ParseControlWait(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new ArgumentException("The control wait request is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var route = root.GetProperty("route").GetString() ?? "";
        var presentationId = root.GetProperty("presentationId").GetString() ?? "";
        if (route.Length is 0 or > MaximumRouteLength || presentationId.Length is 0 or > MaximumPresentationLength)
            throw new ArgumentException("Invalid presentation identity.");
        return new ControlWaitRequest(route, presentationId);
    }

    private static InteractionReply ParseReply(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) throw new ArgumentException("The interaction reply is too large.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var kind = root.GetProperty("kind").GetString() ?? "";
        if (kind is not ("answered" or "cancelled" or "failed")) throw new ArgumentException("Invalid reply kind.");
        var reply = new InteractionReply(kind,
            root.GetProperty("requestId").GetString() ?? "",
            root.GetProperty("route").GetString() ?? "",
            root.GetProperty("presentationId").GetString() ?? "",
            root.GetProperty("ownerEpoch").GetInt64(),
            root.GetProperty("name").GetString() ?? "",
            root.GetProperty("contract").GetString() ?? "",
            kind == "answered" ? root.GetProperty("output").Clone() : null);
        if (reply.RequestId.Length is 0 or > 128 || reply.Route.Length is 0 or > MaximumRouteLength
            || reply.PresentationId.Length is 0 or > MaximumPresentationLength || reply.Name.Length is 0 or > MaximumNameLength
            || reply.Contract.Length is 0 or > MaximumContractLength) throw new ArgumentException("Invalid reply identity.");
        return reply;
    }

    private static string EncodeRequest(PendingRequest request) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "request");
        writer.WriteString("requestId", request.RequestId);
        writer.WriteString("route", request.Key.Route);
        writer.WriteString("presentationId", request.PresentationId);
        writer.WriteNumber("ownerEpoch", request.OwnerEpoch);
        writer.WriteString("name", request.Key.Name);
        writer.WriteString("contract", request.Key.Contract);
        writer.WriteString("expiresAt", request.ExpiresAt);
        writer.WritePropertyName("input");
        using var input = JsonDocument.Parse(request.InputJson);
        input.RootElement.WriteTo(writer);
        writer.WriteEndObject();
    });

    private static string EncodeControl(ControlEvent control) => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", control.Kind);
        writer.WriteString("requestId", control.RequestId);
        writer.WriteString("route", control.Route);
        writer.WriteString("presentationId", control.PresentationId);
        writer.WriteNumber("ownerEpoch", control.OwnerEpoch);
        writer.WriteString("reason", control.Reason);
        writer.WriteEndObject();
    });

    private static string EncodeKind(string kind) => WriteJson(writer => { writer.WriteStartObject(); writer.WriteString("kind", kind); writer.WriteEndObject(); });
    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    /// <summary>Unbinds the interaction routes and cancels pending interactions.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var waiter in _waiters.Values) { waiter.DisposeRegistration(); waiter.Completion.TrySetResult(EncodeKind("disconnected")); }
            _waiters.Clear();
            foreach (var waiter in _controlWaiters.Values) { waiter.DisposeRegistration(); waiter.Completion.TrySetResult(EncodeKind("disconnected")); }
            _controlWaiters.Clear();
            foreach (var request in _pending.Values) { request.CompleteException(new RunicInteractionCancelledException("The window was closed.")); request.Dispose(); }
            _pending.Clear();
            _presentations.Clear();
            _definitions.Clear();
            _receipts.Clear();
        }
        foreach (var binding in _bindings) binding.Dispose();
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
    private sealed class Presentation(string route, string id, string? clientKey, string? connectionKey)
    {
        public string Route { get; } = route;
        public string Id { get; } = id;
        public string? ClientKey { get; } = clientKey;
        public string? ConnectionKey { get; } = connectionKey;
        public long Generation { get; set; }
        public long CapabilityGeneration { get; set; }
        public HashSet<DefinitionKey> Capabilities { get; } = [];
        public Queue<string> Pending { get; } = [];
        public Queue<ControlEvent> ControlEvents { get; } = [];
        public bool Matches(string route, IBridgeArguments arguments) =>
            string.Equals(Route, route, StringComparison.Ordinal)
            && string.Equals(ClientKey, arguments.ClientKey, StringComparison.Ordinal)
            && string.Equals(ConnectionKey, arguments.ConnectionKey, StringComparison.Ordinal);
    }
    private sealed class Waiter(string presentationId, Presentation presentation, string? clientKey, string? connectionKey)
    { public string PresentationId { get; } = presentationId; public Presentation Presentation { get; } = presentation; public string? ClientKey { get; } = clientKey; public string? ConnectionKey { get; } = connectionKey; public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public CancellationTokenRegistration Cancellation { get; set; } public void DisposeRegistration() => Cancellation.Unregister(); }
    private sealed class ControlWaiter(string presentationId)
    { public string PresentationId { get; } = presentationId; public TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public CancellationTokenRegistration Cancellation { get; set; } public void DisposeRegistration() => Cancellation.Unregister(); }
    private sealed class PendingRequest
    {
        private readonly TaskCompletionSource<object?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<JsonElement, object?> _decodeOutput;
        public PendingRequest(string requestId, DefinitionKey key, string presentationId, string? connectionKey, string? clientKey, long ownerEpoch, string inputJson, Func<JsonElement, object?> decodeOutput, TimeSpan timeout)
        { RequestId = requestId; Key = key; PresentationId = presentationId; ConnectionKey = connectionKey; ClientKey = clientKey; OwnerEpoch = ownerEpoch; InputJson = inputJson; Timeout = timeout; ExpiresAt = DateTimeOffset.UtcNow.Add(timeout); _decodeOutput = decodeOutput; }
        public object? Decode(JsonElement value) => _decodeOutput(value);
        public string RequestId { get; } public DefinitionKey Key { get; } public string PresentationId { get; } public string? ConnectionKey { get; } public string? ClientKey { get; } public long OwnerEpoch { get; set; } public string InputJson { get; } public TimeSpan Timeout { get; } public DateTimeOffset ExpiresAt { get; } public CancellationLease? Cancellation { get; set; }
        public Task<T> AsTask<T>() => _completion.Task.ContinueWith(task => (T)task.GetAwaiter().GetResult()!, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        public void CompleteOutput(object? value) => _completion.TrySetResult(value); public void CompleteException(Exception error) => _completion.TrySetException(error); public bool Matches(InteractionReply reply, IBridgeArguments args) => reply.RequestId == RequestId && reply.Route == Key.Route && reply.PresentationId == PresentationId && reply.OwnerEpoch == OwnerEpoch && reply.Name == Key.Name && reply.Contract == Key.Contract && string.Equals(ConnectionKey,args.ConnectionKey,StringComparison.Ordinal) && string.Equals(ClientKey,args.ClientKey,StringComparison.Ordinal); public void Dispose() => Cancellation?.Dispose();
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

        public bool Matches(InteractionReply reply, IBridgeArguments args) =>
            reply.RequestId == RequestId && reply.Route == Route && reply.PresentationId == PresentationId
            && reply.OwnerEpoch == OwnerEpoch && reply.Name == Name && reply.Contract == Contract
            && ReplySignature == Signature(reply)
            && string.Equals(ConnectionKey, args.ConnectionKey, StringComparison.Ordinal)
            && string.Equals(ClientKey, args.ClientKey, StringComparison.Ordinal);

        private static string Signature(InteractionReply reply) => reply.Kind + ":" +
            (reply.Output is { } output ? CanonicalJson(output) : string.Empty);
    }

    private static string CanonicalJson(JsonElement value) => WriteJson(writer => WriteCanonical(value, writer));

    private static void WriteCanonical(JsonElement value, Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(item, writer);
                writer.WriteEndArray();
                break;
            default: value.WriteTo(writer); break;
        }
    }
    private readonly record struct DefinitionKey(string Route, string Name, string Contract);
    private sealed record HandlerIdentity(string Name, string Contract);
    private sealed record WaitRequest(string Route, string PresentationId, HandlerIdentity[] Handlers, long Generation);
    private sealed record ControlRequest(string Route, string PresentationId, long Generation, HandlerIdentity[] Handlers);
    private sealed record ControlWaitRequest(string Route, string PresentationId);
    private sealed record ControlEvent(string Kind, string RequestId, string Route, string PresentationId, long OwnerEpoch, string Reason);
    private sealed record InteractionReply(string Kind, string RequestId, string Route, string PresentationId, long OwnerEpoch, string Name, string Contract, JsonElement? Output);
}
