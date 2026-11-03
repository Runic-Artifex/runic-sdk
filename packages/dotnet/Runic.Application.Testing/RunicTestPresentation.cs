using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Runic.Application.Views;
using Runic.Application.Views.Generated;

namespace Runic.Application.Testing;

/// <summary>A view request that a <see cref="RunicTestPresentation{TModel}"/> received, as a page's handler would.</summary>
/// <param name="Name">The request's generated name, such as <c>focus</c> for a <c>Focus</c> property.</param>
/// <param name="Route">The route of the presentation.</param>
/// <param name="Presentation">The presentation id.</param>
/// <param name="Revision">The route's revision that includes everything the ViewModel changed before raising it.</param>
/// <param name="InputJson">The request's input, as wire JSON.</param>
public sealed record RunicViewRequest(string Name, string Route, string Presentation, long Revision, string InputJson)
{
    /// <summary>
    /// Reads <see cref="InputJson"/> as <typeparamref name="T"/> with System.Text.Json's web defaults and enum names,
    /// which read the wire form of records and classes with camel-case members; compare <see cref="InputJson"/> for
    /// other values.
    /// </summary>
    /// <typeparam name="T">The request's input type.</typeparam>
    public T Input<T>() => JsonSerializer.Deserialize<T>(InputJson, RunicTestPresentationJson.Options)!;
}

/// <summary>
/// A presentation of a route that a test opened as a page does, which answers the ViewModel's interactions and
/// questions with the handlers the test registers and receives its view requests. Create it with
/// <see cref="RunicViewDriver{TModel}.Present"/>.
/// </summary>
/// <remarks>
/// <para>
/// It registers every <see cref="ViewRequest{T}"/> of <typeparamref name="TModel"/> when it opens, so the ViewModel's
/// requests reach it by their <see cref="ViewRequestDelivery"/>; a presentation that <see cref="RunicViewDriver{TModel}.Mount"/>
/// opened registers none. Each <see cref="Handle{TInput, TOutput}(Expression{Func{TModel, object}}, Func{TInput, TOutput})"/>
/// registers one more interaction handler, as the generated client's <c>interactions.&lt;name&gt;.handle</c> does.
/// </para>
/// <code>
/// var page = await editor.PresentAsync();
/// page.Handle&lt;DiscardRequest, bool&gt;(vm =&gt; vm.ConfirmDiscard, request =&gt; true);
/// (await editor.ExecuteAsync(vm =&gt; vm.DiscardCommand)).ThrowIfFailed();
/// var focus = Assert.Single(page.TakeViewRequests(vm =&gt; vm.Focus));
/// </code>
/// </remarks>
/// <typeparam name="TModel">The ViewModel of the route.</typeparam>
public sealed class RunicTestPresentation<TModel> : IDisposable where TModel : class
{
    private readonly RunicViewDriver<TModel> _driver;
    private readonly InMemoryBridgeTransport _transport;
    private readonly string _connection;
    private readonly object _gate = new();
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, ValueTask<string>>> _handlers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _contracts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _active = new(StringComparer.Ordinal);
    private readonly List<RunicViewRequest> _requests = [];
    private TaskCompletionSource _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IDisposable _listener;
    private long _generation;
    private bool _disposed;

    internal RunicTestPresentation(RunicViewDriver<TModel> driver, InMemoryBridgeTransport transport, string id,
        string? clientKey, string? connectionKey)
    {
        _driver = driver;
        _transport = transport;
        Id = id;
        ClientKey = clientKey;
        ConnectionKey = connectionKey;
        _connection = connectionKey ?? InMemoryBridgeTransport.DefaultConnectionKey;
        _listener = transport.OnTargetedPush(OnPush);
    }

    /// <summary>The presentation id.</summary>
    public string Id { get; }

    /// <summary>The route of the presentation.</summary>
    public string Route => _driver.Route;

    /// <summary>The client identity the presentation was opened with, or <see langword="null"/> for the default.</summary>
    public string? ClientKey { get; }

    /// <summary>The connection identity the presentation was opened with, or <see langword="null"/> for the default.</summary>
    public string? ConnectionKey { get; }

    /// <summary>
    /// Answers a ViewModel's interaction, such as a ReactiveUI <c>Interaction&lt;TInput, TOutput&gt;</c>, with
    /// <paramref name="answer"/>, as a page's handler does, from now on; it replaces an earlier handler of the member.
    /// Blocks the calling thread until the window registered it.
    /// </summary>
    /// <typeparam name="TInput">The interaction's input type.</typeparam>
    /// <typeparam name="TOutput">The interaction's output type.</typeparam>
    /// <param name="interaction">The interaction property, such as <c>vm =&gt; vm.ConfirmDiscard</c>.</param>
    /// <param name="answer">Answers each request; an exception answers <c>failed</c>.</param>
    /// <returns>This presentation.</returns>
    /// <exception cref="InvalidOperationException">The window did not register the handler; the message names its reply.</exception>
    public RunicTestPresentation<TModel> Handle<TInput, TOutput>(Expression<Func<TModel, object?>> interaction, Func<TInput, TOutput> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return HandleAsync<TInput, TOutput>(interaction, (input, _) => ValueTask.FromResult(answer(input))).GetAwaiter().GetResult();
    }

    /// <summary>Answers a <see cref="ViewQuestion{TInput, TOutput}"/> with <paramref name="answer"/>, as a page's handler does.</summary>
    /// <inheritdoc cref="Handle{TInput, TOutput}(Expression{Func{TModel, object}}, Func{TInput, TOutput})"/>
    public RunicTestPresentation<TModel> Handle<TInput, TOutput>(Expression<Func<TModel, ViewQuestion<TInput, TOutput>>> question,
        Func<TInput, TOutput> answer)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(answer);
        return HandleAsync<TInput, TOutput>(RunicMembers.Property(question), (input, _) => ValueTask.FromResult(answer(input)))
            .GetAwaiter().GetResult();
    }

    /// <summary>
    /// Answers a ViewModel's interaction with an asynchronous handler, which is cancelled when .NET cancels the request,
    /// without blocking a thread.
    /// </summary>
    /// <typeparam name="TInput">The interaction's input type.</typeparam>
    /// <typeparam name="TOutput">The interaction's output type.</typeparam>
    /// <param name="interaction">The interaction or <see cref="ViewQuestion{TInput, TOutput}"/> property.</param>
    /// <param name="answer">Answers each request; an exception answers <c>failed</c>, a cancellation <c>cancelled</c>.</param>
    /// <param name="cancellationToken">Stops waiting for the registration's reply.</param>
    /// <returns>This presentation, once the window registered the handler.</returns>
    /// <exception cref="InvalidOperationException">The window did not register the handler; the message names its reply.</exception>
    public Task<RunicTestPresentation<TModel>> HandleAsync<TInput, TOutput>(Expression<Func<TModel, object?>> interaction,
        Func<TInput, CancellationToken, ValueTask<TOutput>> answer, CancellationToken cancellationToken = default) =>
        HandleAsync(RunicMembers.Property(interaction), answer, cancellationToken);

    private async Task<RunicTestPresentation<TModel>> HandleAsync<TInput, TOutput>(PropertyInfo property,
        Func<TInput, CancellationToken, ValueTask<TOutput>> answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var name = RunicMembers.LowerFirst(property.Name);
        string[] names;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _contracts[name] = $"{RunicViewDriver<TModel>.ContractFingerprint}:interaction:{property.Name}";
            _handlers[name] = async (input, token) =>
            {
                var output = await answer(input.Deserialize<TInput>(RunicTestPresentationJson.Options)!, token).ConfigureAwait(false);
                return RunicWireValue.Encode(output, typeof(TOutput));
            };
            names = [.. _handlers.Keys];
            generation = ++_generation;
        }
        var reply = await _driver.SendAsync(writer =>
        {
            writer.WriteString("kind", "interactions");
            writer.WriteString("presentation", Id);
            writer.WriteNumber("generation", generation);
            writer.WriteStartArray("handlers");
            foreach (var handler in names)
            {
                writer.WriteStartObject();
                writer.WriteString("name", handler);
                lock (_gate) writer.WriteString("contract", _contracts[handler]);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ConnectionKey, ClientKey, cancellationToken).ConfigureAwait(false);
        if (!reply.Contains("\"registration\":\"ok\"", StringComparison.Ordinal) && !reply.Contains("\"registration\":\"stale\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Route} interactions returned '{reply}'.");
        return this;
    }

    /// <summary>Returns and removes the view requests this presentation received, in order.</summary>
    /// <param name="request">Only the requests of this <see cref="ViewRequest{T}"/> property, such as <c>vm =&gt; vm.Focus</c>; by default all.</param>
    public IReadOnlyList<RunicViewRequest> TakeViewRequests(Expression<Func<TModel, object?>>? request = null)
    {
        var name = request is null ? null : RunicMembers.LowerFirst(RunicMembers.Property(request).Name);
        lock (_gate)
        {
            var taken = _requests.Where(value => name is null || value.Name == name).ToArray();
            foreach (var value in taken) _requests.Remove(value);
            return taken;
        }
    }

    /// <summary>Waits for the next view request this presentation receives, and removes it.</summary>
    /// <param name="timeout">Real time to wait; five seconds by default.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="TimeoutException">No view request arrived in time.</exception>
    public async Task<RunicViewRequest> NextViewRequestAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            Task next;
            lock (_gate)
            {
                if (_requests.Count > 0)
                {
                    var first = _requests[0];
                    _requests.RemoveAt(0);
                    return first;
                }
                next = _requested.Task;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException($"{Route}: no view request arrived.");
            try { await next.WaitAsync(remaining, cancellationToken).ConfigureAwait(false); }
            catch (TimeoutException) { throw new TimeoutException($"{Route}: no view request arrived."); }
        }
    }

    // Registers every ViewRequest of the model, as a page whose components handle all of them.
    internal async Task RegisterViewRequestsAsync(CancellationToken cancellationToken)
    {
        var requests = typeof(TModel).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.PropertyType.IsGenericType && property.PropertyType.GetGenericTypeDefinition() == typeof(ViewRequest<>))
            .ToArray();
        if (requests.Length == 0) return;
        var reply = await _driver.SendAsync(writer =>
        {
            writer.WriteString("kind", "view-requests");
            writer.WriteString("presentation", Id);
            writer.WriteNumber("generation", 1);
            writer.WriteStartArray("requests");
            foreach (var property in requests)
            {
                writer.WriteStartObject();
                writer.WriteString("name", RunicMembers.LowerFirst(property.Name));
                writer.WriteString("contract", $"{RunicViewDriver<TModel>.ContractFingerprint}:request:{property.Name}");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }, ConnectionKey, ClientKey, cancellationToken).ConfigureAwait(false);
        if (!reply.Contains("\"registration\":\"ok\"", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Route} view-requests returned '{reply}'.");
    }

    private void OnPush(RunicBridgeMessage message)
    {
        if (message.ConnectionKey != _connection) return;
        try
        {
            using var document = JsonDocument.Parse(message.Json);
            var root = document.RootElement;
            if (!root.TryGetProperty("presentation", out var presentation) || presentation.GetString() != Id
                || root.GetProperty("route").GetString() != Route) return;
            switch (root.GetProperty("kind").GetString())
            {
                case "view-request":
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _requests.Add(new(root.GetProperty("name").GetString()!, Route, Id, root.GetProperty("revision").GetInt64(),
                            root.GetProperty("input").GetRawText()));
                        _requested.TrySetResult();
                        _requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    break;
                case "interaction":
                    _ = AnswerAsync(root.Clone());
                    break;
                case "interaction-cancelled":
                    lock (_gate)
                        if (_active.TryGetValue(root.GetProperty("requestId").GetString()!, out var active)) active.Cancel();
                    break;
            }
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    private async Task AnswerAsync(JsonElement request)
    {
        var requestId = request.GetProperty("requestId").GetString()!;
        var name = request.GetProperty("name").GetString()!;
        Func<JsonElement, CancellationToken, ValueTask<string>>? handler;
        using var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed) return;
            _handlers.TryGetValue(name, out handler);
            _active[requestId] = cancellation;
        }
        string outcome;
        string? output = null;
        try
        {
            if (handler is null) outcome = "cancelled";
            else
            {
                output = await handler(request.GetProperty("input"), cancellation.Token).ConfigureAwait(false);
                outcome = "answered";
            }
        }
        catch (OperationCanceledException) { outcome = "cancelled"; }
        catch (Exception) { outcome = "failed"; }
        finally
        {
            lock (_gate) _active.Remove(requestId);
        }
        try
        {
            await _driver.SendAsync(writer =>
            {
                writer.WriteString("kind", "interaction-reply");
                writer.WriteString("presentation", Id);
                writer.WriteString("requestId", requestId);
                writer.WriteNumber("ownerEpoch", request.GetProperty("ownerEpoch").GetInt64());
                writer.WriteString("name", name);
                writer.WriteString("contract", request.GetProperty("contract").GetString());
                writer.WriteString("outcome", outcome);
                if (output is not null)
                {
                    writer.WritePropertyName("output");
                    writer.WriteRawValue(output);
                }
            }, ConnectionKey, ClientKey).ConfigureAwait(false);
        }
        // The window or its transport closed while the handler ran.
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
    }

    /// <summary>Closes the presentation, as a page that stops showing the View sends <c>close</c>, and stops answering.</summary>
    /// <exception cref="InvalidOperationException">The route did not close it; the message names the reply.</exception>
    public void Unmount()
    {
        Dispose();
        _driver.Unmount(Id, ClientKey, ConnectionKey);
    }

    /// <summary>Closes the presentation without blocking a thread, and stops answering.</summary>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <exception cref="InvalidOperationException">The route did not close it; the message names the reply.</exception>
    public Task UnmountAsync(CancellationToken cancellationToken = default)
    {
        Dispose();
        return _driver.UnmountAsync(Id, ClientKey, ConnectionKey, cancellationToken);
    }

    /// <summary>Stops answering interactions and taking view requests; the presentation stays open.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var active in _active.Values) active.Cancel();
        }
        _listener.Dispose();
    }
}

internal static class RunicTestPresentationJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
