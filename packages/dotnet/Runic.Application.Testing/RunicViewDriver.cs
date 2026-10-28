using System.Buffers;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>
/// Sends the Views protocol 4 requests of one <typeparamref name="TModel"/> route by ViewModel member, as the generated
/// TypeScript client does, on the transport's test connection.
/// </summary>
/// <example>
/// <code>
/// var editor = host.Root.View&lt;DocumentViewModel&gt;(vm => vm.Main).View&lt;EditorViewModel&gt;(vm => vm.CurrentPane);
/// editor.Set(vm => vm.Title, "Groceries").ThrowIfFailed();
/// (await editor.ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
/// </code>
/// </example>
public sealed class RunicViewDriver<TModel> where TModel : class
{
    private readonly RunicPublicationLog _publications;
    private readonly InMemoryBridgeTransport _transport;
    private readonly Func<string> _nextId;

    internal RunicViewDriver(RunicPublicationLog publications, InMemoryBridgeTransport transport, string route,
        PageReference? reference, Func<string> nextId)
    {
        _publications = publications;
        _transport = transport;
        _nextId = nextId;
        Route = route;
        Reference = reference;
    }

    /// <summary>The route: the root name, or <c>content{id}</c> for presented content.</summary>
    public string Route { get; }

    /// <summary>The content reference this driver presents, or null for the root route.</summary>
    public PageReference? Reference { get; }

    // Sends one request of this route on the test connection, after its hello.
    internal string Send(Action<Utf8JsonWriter> writeMembers, string? connectionKey = null, string? clientKey = null) =>
        SendAsync(writeMembers, connectionKey, clientKey).AsTask().GetAwaiter().GetResult();

    internal async ValueTask<string> SendAsync(Action<Utf8JsonWriter> writeMembers, string? connectionKey = null,
        string? clientKey = null, CancellationToken cancellationToken = default)
    {
        var connection = connectionKey ?? InMemoryBridgeTransport.DefaultConnectionKey;
        _transport.EnsureHello(connection, clientKey);
        var json = RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writeMembers(writer);
            writer.WriteString("route", Route);
            writer.WriteEndObject();
        });
        return await _transport.RequestAsync(json, connection, clientKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the current state with a <c>snapshot</c> request.</summary>
    /// <exception cref="InvalidOperationException">The route answered with a failure, such as <c>released</c>.</exception>
    public RunicViewState<TModel> Snapshot() => ReadState(Send(writer => writer.WriteString("kind", "snapshot")), "snapshot");

    // The state of an open or snapshot reply.
    private RunicViewState<TModel> ReadState(string reply, string kind)
    {
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean() || !root.TryGetProperty("state", out var state))
            throw new InvalidOperationException($"{Route}: {kind} failed: {reply}");
        return new(state, root.GetProperty("revision").GetInt64(), root.TryGetProperty("fields", out var fields) ? fields : null);
    }

    // The reply of a request that changes state, with the state the route has after it.
    private RunicCallReply<TModel> Reply(string kind, string member, string json)
    {
        RunicViewState<TModel>? state = null;
        using (var document = JsonDocument.Parse(json))
        {
            var root = document.RootElement;
            var released = root.TryGetProperty("released", out var flag) && flag.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("revision", out _) && !released)
            {
                var snapshot = Send(writer => writer.WriteString("kind", "snapshot"));
                if (snapshot.StartsWith("{\"ok\":true", StringComparison.Ordinal)) state = ReadState(snapshot, "snapshot");
            }
        }
        return new($"{Route} {kind} {member}", json, state);
    }

    private int NextEdit(string member) => _transport.NextEdit(InMemoryBridgeTransport.DefaultConnectionKey, Route, member);

    /// <summary>
    /// Sends the generated setter's <c>set</c> request with the next edit number of the member. The value is encoded as
    /// its wire type: an <c>Int64</c> or <c>decimal</c> as a string, an enum by name, <c>null</c> for a nullable value.
    /// Use <see cref="SetJson{TValue}"/> for DTOs, lists and unions.
    /// </summary>
    public RunicCallReply<TModel> Set<TValue>(Expression<Func<TModel, TValue>> property, TValue value)
    {
        var member = RunicMembers.Property(property);
        return SetJson(property, RunicWireValue.Encode(value, member.PropertyType));
    }

    /// <summary>Sends a <c>set</c> request with a value already encoded as wire JSON.</summary>
    public RunicCallReply<TModel> SetJson<TValue>(Expression<Func<TModel, TValue>> property, string wireJson)
    {
        ArgumentNullException.ThrowIfNull(wireJson);
        var member = RunicMembers.WireName(RunicMembers.Property(property));
        var edit = NextEdit(member);
        return Reply("set", member, Send(writer =>
        {
            writer.WriteString("kind", "set");
            writer.WriteString("member", member);
            writer.WritePropertyName("value");
            writer.WriteRawValue(wireJson);
            writer.WriteNumber("edit", edit);
        }));
    }

    private static string CommandMember(Expression<Func<TModel, object?>> command) =>
        RunicMembers.LowerFirst(RunicMembers.CommandName(RunicMembers.Property(command)));

    private static void WriteCommand(Utf8JsonWriter writer, string kind, string member, string? inputJson)
    {
        writer.WriteString("kind", kind);
        writer.WriteString("member", member);
        if (inputJson is null) return;
        writer.WritePropertyName("input");
        writer.WriteRawValue(inputJson);
    }

    /// <summary>
    /// Executes a command, as the client's <c>{command}(input)</c> does, and waits for an
    /// asynchronous command to finish.
    /// </summary>
    /// <remarks>
    /// A command that waits on the host's fake clock finishes only after the test advances
    /// it: start the call, advance <see cref="RunicWindowTestHost{TViewModel}.Time"/>, then
    /// await the call.
    /// </remarks>
    /// <param name="command">The command property, such as <c>vm => vm.SaveCommand</c>.</param>
    /// <param name="argumentJson">The command input as wire JSON, for a command with an input.</param>
    /// <param name="cancellationToken">Cancels an awaited command, as a closed client connection does.</param>
    public async Task<RunicCallReply<TModel>> ExecuteAsync(Expression<Func<TModel, object?>> command, string? argumentJson = null,
        CancellationToken cancellationToken = default)
    {
        var member = CommandMember(command);
        var json = await SendAsync(writer => WriteCommand(writer, "command", member, argumentJson),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Reply("command", member, json);
    }

    private static bool CanReply(string route, string reply)
    {
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        return root.GetProperty("ok").GetBoolean() && root.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidOperationException($"{route} can returned '{reply}'.");
    }

    /// <summary>Asks whether a command with an input can execute (<c>can{Command}(input)</c>).</summary>
    /// <remarks>
    /// A command with an asynchronous key resolver answers only after the resolver finishes. A resolver that waits on
    /// the host's fake clock blocks this call; use <see cref="CanExecuteAsync"/>.
    /// </remarks>
    public bool CanExecute(Expression<Func<TModel, object?>> command, string argumentJson)
    {
        ArgumentNullException.ThrowIfNull(argumentJson);
        var member = CommandMember(command);
        return CanReply(Route, Send(writer => WriteCommand(writer, "can", member, argumentJson)));
    }

    /// <summary>
    /// Asks whether a command with an input can execute (<c>can{Command}(input)</c>), waiting for an asynchronous key
    /// resolver.
    /// </summary>
    /// <param name="command">The command property, such as <c>vm => vm.DeleteCommand</c>.</param>
    /// <param name="argumentJson">The command input or key as wire JSON.</param>
    /// <param name="cancellationToken">Cancels the resolution, as a closed client connection does.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public async Task<bool> CanExecuteAsync(Expression<Func<TModel, object?>> command, string argumentJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argumentJson);
        var member = CommandMember(command);
        var reply = await SendAsync(writer => WriteCommand(writer, "can", member, argumentJson),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // A cancelled resolution answers false, as for a closed connection; the caller asked to stop instead.
        cancellationToken.ThrowIfCancellationRequested();
        return CanReply(Route, reply);
    }

    // The operation contract: {view}:{fingerprint}.
    private static string Contract => $"{typeof(TModel).FullName}:{Fingerprint.Value}";

    /// <summary>Starts an asynchronous command as a recoverable operation with a <c>start</c> request.</summary>
    /// <param name="command">The command property.</param>
    /// <param name="argumentJson">The command input as wire JSON, for a command with an input.</param>
    /// <param name="requestId">The operation id; the host issues <c>request{n}</c> when omitted.</param>
    public RunicTestOperation Start(Expression<Func<TModel, object?>> command, string? argumentJson = null, string? requestId = null) =>
        StartAsync(command, argumentJson, requestId).GetAwaiter().GetResult();

    /// <summary>
    /// Starts an asynchronous command as a recoverable operation with a <c>start</c> request, and completes with the
    /// admission. Await <see cref="RunicTestOperation.WaitAsync"/> for the outcome.
    /// </summary>
    /// <param name="command">The command property.</param>
    /// <param name="argumentJson">The command input or key as wire JSON, for a command with an input.</param>
    /// <param name="requestId">The operation id; the host issues <c>request{n}</c> when omitted.</param>
    /// <param name="cancellationToken">Cancels the resolution, as a closed client connection does.</param>
    public async Task<RunicTestOperation> StartAsync(Expression<Func<TModel, object?>> command, string? argumentJson = null,
        string? requestId = null, CancellationToken cancellationToken = default)
    {
        var member = CommandMember(command);
        requestId ??= $"request{_nextId()}";
        var reply = await SendAsync(writer =>
        {
            WriteCommand(writer, "start", member, argumentJson);
            writer.WriteString("operation", requestId);
            writer.WriteString("contract", Contract);
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(_transport, Route, Contract, member, requestId, reply);
    }

    /// <summary>
    /// Writes a scalar checked field, encoded as <see cref="Set{TValue}"/> encodes values, as the client's
    /// <c>write{Property}(value, { requestId, baseline })</c> does. The baseline is the current value and, unless given,
    /// its current version.
    /// </summary>
    public RunicFieldWriteReceipt<TModel> Write<TValue>(Expression<Func<TModel, TValue>> property, TValue value, long? expectedVersion = null)
    {
        var member = RunicMembers.Property(property);
        var snapshot = Snapshot();
        var field = RunicMembers.WireName(member);
        var version = expectedVersion ?? snapshot.FieldVersion(property);
        var reply = Send(writer =>
        {
            writer.WriteString("kind", "write");
            writer.WriteString("member", field);
            writer.WriteString("requestId", $"write{_nextId()}");
            writer.WriteNumber("expectedVersion", version);
            writer.WritePropertyName("expectedValue");
            snapshot[field].WriteTo(writer);
            writer.WritePropertyName("value");
            RunicWireValue.Write(writer, value, member.PropertyType);
        });
        RunicViewState<TModel>? state = null;
        if (reply.Contains("\"revision\"", StringComparison.Ordinal))
        {
            var after = Send(writer => writer.WriteString("kind", "snapshot"));
            if (after.StartsWith("{\"ok\":true", StringComparison.Ordinal)) state = ReadState(after, "snapshot");
        }
        return new($"{Route} write {field}", reply, state);
    }

    /// <summary>Drives the content that a ViewModel content property presents now.</summary>
    /// <typeparam name="TContent">The presented ViewModel type, checked against the reference's kind.</typeparam>
    public RunicViewDriver<TContent> View<TContent>(Expression<Func<TModel, object?>> property) where TContent : class
    {
        var member = RunicMembers.Property(property);
        var reference = Snapshot().Reference(property)
            ?? throw new InvalidOperationException($"{typeof(TModel).Name}.{member.Name} presents nothing.");
        return Content<TContent>(reference, member);
    }

    /// <summary>Drives each item that a ViewModel content collection presents now.</summary>
    public IReadOnlyList<RunicViewDriver<TContent>> Views<TContent>(Expression<Func<TModel, object?>> property) where TContent : class
    {
        var member = RunicMembers.Property(property);
        return Snapshot().References(property).Select(reference => Content<TContent>(reference, member)).ToArray();
    }

    /// <summary>Opens a presentation of this route, as a page that shows the View sends <c>open</c>.</summary>
    /// <param name="presentation">The presentation id; the host issues <c>test-presentation{n}</c> when omitted.</param>
    /// <param name="clientKey">The client identity, for tests of several browser clients.</param>
    /// <param name="connectionKey">The connection identity, for tests of a client disconnect.</param>
    /// <returns>The presentation id; pass it to <see cref="Unmount"/>.</returns>
    /// <exception cref="InvalidOperationException">The open was not answered <c>ok</c>; the message names the reply.</exception>
    public string Mount(string? presentation = null, string? clientKey = null, string? connectionKey = null)
    {
        presentation ??= $"test-presentation{_nextId()}";
        var id = presentation;
        var reply = Send(writer =>
        {
            writer.WriteString("kind", "open");
            writer.WriteString("presentation", id);
        }, connectionKey, clientKey);
        return reply.StartsWith("{\"ok\":true", StringComparison.Ordinal) && reply.Contains("\"presentation\":\"ok\"", StringComparison.Ordinal)
            ? presentation : throw new InvalidOperationException($"{Route} open returned '{reply}'.");
    }

    /// <summary>Closes a presentation, as a page that stops showing the View sends <c>close</c>.</summary>
    /// <param name="presentation">The id <see cref="Mount"/> returned.</param>
    /// <param name="clientKey">The client identity the presentation was opened with.</param>
    /// <param name="connectionKey">The connection identity the presentation was opened with.</param>
    /// <exception cref="InvalidOperationException">
    /// The route did not close it, for example because another client opened it (<c>ignored</c>) or the route was released
    /// (<c>released</c>). The message names the reply.
    /// </exception>
    public void Unmount(string presentation, string? clientKey = null, string? connectionKey = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        var reply = Send(writer =>
        {
            writer.WriteString("kind", "close");
            writer.WriteString("presentation", presentation);
        }, connectionKey, clientKey);
        if (reply != "{\"ok\":true,\"presentation\":\"ok\"}") throw new InvalidOperationException($"{Route} close returned '{reply}'.");
    }

    /// <summary>Returns and removes the states and frames pushed to this route so far.</summary>
    public IReadOnlyList<RunicViewPublication> TakePublications() => _publications.Take(Route);

    /// <summary>Waits for the next state or frame pushed to this route.</summary>
    /// <param name="timeout">Real time to wait; defaults to the host's publication timeout.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="TimeoutException">Nothing was pushed in time.</exception>
    public Task<RunicViewPublication> NextPublicationAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _publications.NextAsync(Route, timeout, cancellationToken);

    /// <summary>
    /// Follows this route's state from a snapshot through its pushed states and frames, applying their changes as the
    /// client does and failing on a frame the client could not apply.
    /// </summary>
    public RunicStateTracker<TModel> Track() => new(this, _publications);

    private RunicViewDriver<TContent> Content<TContent>(PageReference reference, PropertyInfo member) where TContent : class
    {
        var kind = RunicMembers.RouteOf(typeof(TContent));
        if (!reference.Kind.StartsWith(kind, StringComparison.Ordinal)
            || reference.Kind.Length > kind.Length && !char.IsUpper(reference.Kind[kind.Length]))
            throw new InvalidOperationException(
                $"{typeof(TModel).Name}.{member.Name} presents '{reference.Kind}', not {typeof(TContent).Name}.");
        return new(_publications, _transport, $"content{reference.Id}", reference, _nextId);
    }

    private static class Fingerprint
    {
        // The generated operation contract includes the ViewModel's contract fingerprint.
        internal static readonly string Value = BridgeContractShape.Compute(typeof(TModel));
    }
}

internal static class RunicJson
{
    internal static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
