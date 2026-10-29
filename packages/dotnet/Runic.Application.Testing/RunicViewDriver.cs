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
/// <remarks>
/// Each request has an asynchronous method, such as <see cref="SnapshotAsync"/> and <see cref="SetAsync{TValue}"/>,
/// that never blocks a thread. The synchronous methods, such as <see cref="Snapshot"/> and <see cref="Set{TValue}"/>,
/// block the calling thread until the reply, while the window's own asynchronous work continues on thread-pool threads.
/// In a suite that runs many tests in parallel on thread-pool threads, each blocked test holds a thread that the replies
/// it waits for need, which can starve the pool and slow the suite down; use the asynchronous methods there.
/// </remarks>
/// <example>
/// <code>
/// var editor = await (await host.Root.ViewAsync&lt;DocumentViewModel&gt;(vm => vm.Main)).ViewAsync&lt;EditorViewModel&gt;(vm => vm.CurrentPane);
/// (await editor.SetAsync(vm => vm.Title, "Groceries")).ThrowIfFailed();
/// (await editor.ExecuteAsync(vm => vm.SaveCommand)).ThrowIfFailed();
/// (await editor.ExecuteAsync(vm => vm.RenameCommand, new RenameInput("Groceries", Pinned: true))).ThrowIfFailed();
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

    // Sends one request of this route on the test connection, after its hello, blocking the calling thread.
    internal string Send(Action<Utf8JsonWriter> writeMembers, string? connectionKey = null, string? clientKey = null) =>
        SendAsync(writeMembers, connectionKey, clientKey).AsTask().GetAwaiter().GetResult();

    internal async ValueTask<string> SendAsync(Action<Utf8JsonWriter> writeMembers, string? connectionKey = null,
        string? clientKey = null, CancellationToken cancellationToken = default)
    {
        var connection = connectionKey ?? InMemoryBridgeTransport.DefaultConnectionKey;
        await _transport.EnsureHelloAsync(connection, clientKey, cancellationToken).ConfigureAwait(false);
        var json = RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writeMembers(writer);
            writer.WriteString("route", Route);
            writer.WriteEndObject();
        });
        return await _transport.RequestAsync(json, connection, clientKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the current state with a <c>snapshot</c> request, blocking the calling thread until the reply.</summary>
    /// <remarks>Prefer <see cref="SnapshotAsync"/> in suites that run tests in parallel; see the class remarks.</remarks>
    /// <exception cref="InvalidOperationException">The route answered with a failure, such as <c>released</c>.</exception>
    public RunicViewState<TModel> Snapshot() => ReadState(Send(WriteSnapshot), "snapshot");

    /// <summary>Reads the current state with a <c>snapshot</c> request, without blocking a thread.</summary>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <exception cref="InvalidOperationException">The route answered with a failure, such as <c>released</c>.</exception>
    public async Task<RunicViewState<TModel>> SnapshotAsync(CancellationToken cancellationToken = default) =>
        ReadState(await SendAsync(WriteSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false), "snapshot");

    private static void WriteSnapshot(Utf8JsonWriter writer) => writer.WriteString("kind", "snapshot");

    // The state after a reply that names a revision, or null when the route was released or answers no snapshot.
    private RunicViewState<TModel>? StateAfter(string snapshot) =>
        snapshot.StartsWith("{\"ok\":true", StringComparison.Ordinal) ? ReadState(snapshot, "snapshot") : null;

    // The state of an open or snapshot reply.
    private RunicViewState<TModel> ReadState(string reply, string kind)
    {
        using var document = JsonDocument.Parse(reply);
        var root = document.RootElement;
        if (!root.GetProperty("ok").GetBoolean() || !root.TryGetProperty("state", out var state))
            throw new InvalidOperationException($"{Route}: {kind} failed: {reply}");
        return new(state, root.GetProperty("revision").GetInt64(), root.TryGetProperty("fields", out var fields) ? fields : null);
    }

    // Whether a reply of a request that changes state names a revision to read the state at.
    private static bool HasState(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var released = root.TryGetProperty("released", out var flag) && flag.ValueKind == JsonValueKind.True;
        return root.TryGetProperty("revision", out _) && !released;
    }

    // The reply of a request that changes state, with the state the route has after it.
    private RunicCallReply<TModel> Reply(string kind, string member, string json) =>
        new($"{Route} {kind} {member}", json, HasState(json) ? StateAfter(Send(WriteSnapshot)) : null);

    private async Task<RunicCallReply<TModel>> ReplyAsync(string kind, string member, string json, CancellationToken cancellationToken) =>
        new($"{Route} {kind} {member}", json, HasState(json)
            ? StateAfter(await SendAsync(WriteSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false))
            : null);

    private int NextEdit(string member) => _transport.NextEdit(InMemoryBridgeTransport.DefaultConnectionKey, Route, member);

    /// <summary>
    /// Sends the generated setter's <c>set</c> request with the next edit number of the member, blocking the calling
    /// thread until the reply. The value is encoded as its wire type, as <see cref="SetAsync{TValue}"/> encodes it.
    /// </summary>
    /// <remarks>Prefer <see cref="SetAsync{TValue}"/> in suites that run tests in parallel; see the class remarks.</remarks>
    public RunicCallReply<TModel> Set<TValue>(Expression<Func<TModel, TValue>> property, TValue value)
    {
        var member = RunicMembers.Property(property);
        return SetJson(property, RunicWireValue.Encode(value, member.PropertyType));
    }

    /// <summary>
    /// Sends the generated setter's <c>set</c> request with the next edit number of the member, without blocking a
    /// thread. The value is encoded as its wire type: an <c>Int64</c> or <c>decimal</c> as a string, an enum by name,
    /// <c>null</c> for a nullable value, and a DTO, list or union with the Bridge's JSON rules (see
    /// <see cref="ExecuteAsync{TArgument}"/>). Use <see cref="SetJsonAsync{TValue}"/> for a value already encoded.
    /// </summary>
    /// <param name="property">The state property, such as <c>vm => vm.Title</c>.</param>
    /// <param name="value">The value the page sets.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    public Task<RunicCallReply<TModel>> SetAsync<TValue>(Expression<Func<TModel, TValue>> property, TValue value,
        CancellationToken cancellationToken = default)
    {
        var member = RunicMembers.Property(property);
        return SetJsonAsync(property, RunicWireValue.Encode(value, member.PropertyType), cancellationToken);
    }

    /// <summary>Sends a <c>set</c> request with a value already encoded as wire JSON, blocking the calling thread.</summary>
    /// <remarks>Prefer <see cref="SetJsonAsync{TValue}"/> in suites that run tests in parallel; see the class remarks.</remarks>
    public RunicCallReply<TModel> SetJson<TValue>(Expression<Func<TModel, TValue>> property, string wireJson)
    {
        var (member, write) = SetRequest(property, wireJson);
        return Reply("set", member, Send(write));
    }

    /// <summary>Sends a <c>set</c> request with a value already encoded as wire JSON, without blocking a thread.</summary>
    /// <param name="property">The state property, such as <c>vm => vm.Title</c>.</param>
    /// <param name="wireJson">The value as wire JSON.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    public async Task<RunicCallReply<TModel>> SetJsonAsync<TValue>(Expression<Func<TModel, TValue>> property, string wireJson,
        CancellationToken cancellationToken = default)
    {
        var (member, write) = SetRequest(property, wireJson);
        var reply = await SendAsync(write, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReplyAsync("set", member, reply, cancellationToken).ConfigureAwait(false);
    }

    private (string Member, Action<Utf8JsonWriter> Write) SetRequest<TValue>(Expression<Func<TModel, TValue>> property, string wireJson)
    {
        ArgumentNullException.ThrowIfNull(wireJson);
        var member = RunicMembers.WireName(RunicMembers.Property(property));
        var value = RunicJson.Validate(wireJson, nameof(wireJson), "SetAsync");
        var edit = NextEdit(member);
        return (member, writer =>
        {
            writer.WriteString("kind", "set");
            writer.WriteString("member", member);
            writer.WritePropertyName("value");
            writer.WriteRawValue(value);
            writer.WriteNumber("edit", edit);
        });
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

    // A command input written by a typed overload.
    private static string Input<TArgument>(TArgument argument) =>
        typeof(TArgument) == typeof(CancellationToken)
            ? throw new ArgumentException(
                "Pass the cancellation token by name (cancellationToken: token); this parameter is the command input.",
                nameof(argument))
            : RunicWireValue.Encode(argument, typeof(TArgument));

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
    /// <param name="argumentJson">
    /// The command input as wire JSON, for a command with an input. Use <see cref="ExecuteAsync{TArgument}"/> to pass
    /// the input as a .NET value.
    /// </param>
    /// <param name="cancellationToken">Cancels an awaited command, as a closed client connection does.</param>
    /// <exception cref="ArgumentException"><paramref name="argumentJson"/> is not one JSON value.</exception>
    public async Task<RunicCallReply<TModel>> ExecuteAsync(Expression<Func<TModel, object?>> command, string? argumentJson = null,
        CancellationToken cancellationToken = default)
    {
        var member = CommandMember(command);
        var input = argumentJson is null ? null : RunicJson.Validate(argumentJson, nameof(argumentJson), "ExecuteAsync");
        var json = await SendAsync(writer => WriteCommand(writer, "command", member, input),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReplyAsync("command", member, json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes a command with an input, as the client's <c>{command}(input)</c> does, and waits for an asynchronous
    /// command to finish. The input is encoded as the generated client encodes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The input follows the Bridge's JSON rules: scalars as <see cref="SetAsync{TValue}"/> encodes them, a record or
    /// class as an object of its public properties by wire name (camelCase, or its <c>[RunicAlias]</c> or
    /// <c>[JsonPropertyName]</c>, without <c>[RunicIgnore]</c> and, unless the assembly opts out, <c>[JsonIgnore]</c>
    /// members), a case of a <c>[RunicUnion]</c> with its <c>$case</c>, a list or array as an array, a dictionary with
    /// string keys as an object, and a type with a <c>[RunicCodec]</c> by its codec.
    /// </para>
    /// <para>
    /// A <see cref="string"/> input selects the overload that takes wire JSON unless you name the type:
    /// <c>ExecuteAsync&lt;string&gt;(vm => vm.SearchCommand, "groceries")</c>.
    /// </para>
    /// </remarks>
    /// <param name="command">The command property, such as <c>vm => vm.RenameCommand</c>.</param>
    /// <param name="argument">The command input.</param>
    /// <param name="cancellationToken">Cancels an awaited command, as a closed client connection does.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The typed input overload requires its input, so a call without one keeps binding to the wire JSON overload.")]
    public async Task<RunicCallReply<TModel>> ExecuteAsync<TArgument>(Expression<Func<TModel, object?>> command, TArgument argument,
        CancellationToken cancellationToken = default)
    {
        var member = CommandMember(command);
        var input = Input(argument);
        var json = await SendAsync(writer => WriteCommand(writer, "command", member, input),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return await ReplyAsync("command", member, json, cancellationToken).ConfigureAwait(false);
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
        var input = RunicJson.Validate(argumentJson, nameof(argumentJson), "CanExecuteAsync");
        return CanReply(Route, Send(writer => WriteCommand(writer, "can", member, input)));
    }

    /// <summary>
    /// Asks whether a command with an input can execute (<c>can{Command}(input)</c>), waiting for an asynchronous key
    /// resolver.
    /// </summary>
    /// <param name="command">The command property, such as <c>vm => vm.DeleteCommand</c>.</param>
    /// <param name="argumentJson">The command input or key as wire JSON.</param>
    /// <param name="cancellationToken">Cancels the resolution, as a closed client connection does.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public Task<bool> CanExecuteAsync(Expression<Func<TModel, object?>> command, string argumentJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(argumentJson);
        return CanExecuteCoreAsync(command, RunicJson.Validate(argumentJson, nameof(argumentJson), "CanExecuteAsync"),
            cancellationToken);
    }

    /// <summary>
    /// Asks whether a command with an input can execute (<c>can{Command}(input)</c>), with the input encoded as
    /// <see cref="ExecuteAsync{TArgument}"/> encodes it, waiting for an asynchronous key resolver.
    /// </summary>
    /// <param name="command">The command property, such as <c>vm => vm.DeleteCommand</c>.</param>
    /// <param name="argument">The command input or key.</param>
    /// <param name="cancellationToken">Cancels the resolution, as a closed client connection does.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The typed input overload requires its input, so a call without one keeps binding to the wire JSON overload.")]
    public Task<bool> CanExecuteAsync<TArgument>(Expression<Func<TModel, object?>> command, TArgument argument,
        CancellationToken cancellationToken = default) =>
        CanExecuteCoreAsync(command, Input(argument), cancellationToken);

    private async Task<bool> CanExecuteCoreAsync(Expression<Func<TModel, object?>> command, string input,
        CancellationToken cancellationToken)
    {
        var member = CommandMember(command);
        var reply = await SendAsync(writer => WriteCommand(writer, "can", member, input),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        // A cancelled resolution answers false, as for a closed connection; the caller asked to stop instead.
        cancellationToken.ThrowIfCancellationRequested();
        return CanReply(Route, reply);
    }

    // The operation contract: {view}:{fingerprint}.
    private static string Contract => $"{typeof(TModel).FullName}:{Fingerprint.Value}";

    /// <summary>
    /// Starts an asynchronous command as a recoverable operation with a <c>start</c> request, blocking the calling
    /// thread until the admission.
    /// </summary>
    /// <remarks>Prefer <see cref="StartAsync(Expression{Func{TModel, object}}, string?, string?, CancellationToken)"/> in suites that run tests in parallel; see the class remarks.</remarks>
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
    public Task<RunicTestOperation> StartAsync(Expression<Func<TModel, object?>> command, string? argumentJson = null,
        string? requestId = null, CancellationToken cancellationToken = default) =>
        StartCoreAsync(command, argumentJson is null ? null : RunicJson.Validate(argumentJson, nameof(argumentJson), "StartAsync"),
            requestId, cancellationToken);

    /// <summary>
    /// Starts an asynchronous command with an input as a recoverable operation with a <c>start</c> request, with the
    /// input encoded as <see cref="ExecuteAsync{TArgument}"/> encodes it, and completes with the admission.
    /// </summary>
    /// <param name="command">The command property.</param>
    /// <param name="argument">The command input or key.</param>
    /// <param name="requestId">The operation id; the host issues <c>request{n}</c> when omitted.</param>
    /// <param name="cancellationToken">Cancels the resolution, as a closed client connection does.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
        Justification = "The typed input overload requires its input, so a call without one keeps binding to the wire JSON overload.")]
    public Task<RunicTestOperation> StartAsync<TArgument>(Expression<Func<TModel, object?>> command, TArgument argument,
        string? requestId = null, CancellationToken cancellationToken = default) =>
        StartCoreAsync(command, Input(argument), requestId, cancellationToken);

    private async Task<RunicTestOperation> StartCoreAsync(Expression<Func<TModel, object?>> command, string? argumentJson,
        string? requestId, CancellationToken cancellationToken)
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
    /// Writes a checked field, encoded as <see cref="Set{TValue}"/> encodes values, as the client's
    /// <c>write{Property}(value, { requestId, baseline })</c> does, blocking the calling thread. The baseline is the
    /// current value and, unless given, its current version.
    /// </summary>
    /// <remarks>Prefer <see cref="WriteAsync{TValue}"/> in suites that run tests in parallel; see the class remarks.</remarks>
    public RunicFieldWriteReceipt<TModel> Write<TValue>(Expression<Func<TModel, TValue>> property, TValue value, long? expectedVersion = null)
    {
        var (field, write) = WriteRequest(property, value, expectedVersion, Snapshot());
        var reply = Send(write);
        var state = reply.Contains("\"revision\"", StringComparison.Ordinal) ? StateAfter(Send(WriteSnapshot)) : null;
        return new($"{Route} write {field}", reply, state);
    }

    /// <summary>
    /// Writes a checked field, encoded as <see cref="SetAsync{TValue}"/> encodes values, as the client's
    /// <c>write{Property}(value, { requestId, baseline })</c> does, without blocking a thread. The baseline is the
    /// current value and, unless given, its current version.
    /// </summary>
    /// <param name="property">The checked state property.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="expectedVersion">The baseline version; by default the current one.</param>
    /// <param name="cancellationToken">Stops waiting for the replies.</param>
    public async Task<RunicFieldWriteReceipt<TModel>> WriteAsync<TValue>(Expression<Func<TModel, TValue>> property, TValue value,
        long? expectedVersion = null, CancellationToken cancellationToken = default)
    {
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var (field, write) = WriteRequest(property, value, expectedVersion, snapshot);
        var reply = await SendAsync(write, cancellationToken: cancellationToken).ConfigureAwait(false);
        var state = reply.Contains("\"revision\"", StringComparison.Ordinal)
            ? StateAfter(await SendAsync(WriteSnapshot, cancellationToken: cancellationToken).ConfigureAwait(false))
            : null;
        return new($"{Route} write {field}", reply, state);
    }

    private (string Field, Action<Utf8JsonWriter> Write) WriteRequest<TValue>(Expression<Func<TModel, TValue>> property,
        TValue value, long? expectedVersion, RunicViewState<TModel> snapshot)
    {
        var member = RunicMembers.Property(property);
        var field = RunicMembers.WireName(member);
        var version = expectedVersion ?? snapshot.FieldVersion(property);
        var requestId = $"write{_nextId()}";
        var encoded = RunicWireValue.Encode(value, member.PropertyType);
        return (field, writer =>
        {
            writer.WriteString("kind", "write");
            writer.WriteString("member", field);
            writer.WriteString("requestId", requestId);
            writer.WriteNumber("expectedVersion", version);
            writer.WritePropertyName("expectedValue");
            snapshot[field].WriteTo(writer);
            writer.WritePropertyName("value");
            writer.WriteRawValue(encoded);
        });
    }

    /// <summary>
    /// Drives the content that a ViewModel content property presents now, reading it with a snapshot that blocks the
    /// calling thread.
    /// </summary>
    /// <typeparam name="TContent">The presented ViewModel type, checked against the reference's kind.</typeparam>
    /// <remarks>Prefer <see cref="ViewAsync{TContent}"/> in suites that run tests in parallel; see the class remarks.</remarks>
    public RunicViewDriver<TContent> View<TContent>(Expression<Func<TModel, object?>> property) where TContent : class =>
        Presented<TContent>(Snapshot(), property);

    /// <summary>Drives the content that a ViewModel content property presents now, without blocking a thread.</summary>
    /// <typeparam name="TContent">The presented ViewModel type, checked against the reference's kind.</typeparam>
    /// <param name="property">The content property, such as <c>vm => vm.Main</c>.</param>
    /// <param name="cancellationToken">Stops waiting for the snapshot.</param>
    public async Task<RunicViewDriver<TContent>> ViewAsync<TContent>(Expression<Func<TModel, object?>> property,
        CancellationToken cancellationToken = default) where TContent : class =>
        Presented<TContent>(await SnapshotAsync(cancellationToken).ConfigureAwait(false), property);

    private RunicViewDriver<TContent> Presented<TContent>(RunicViewState<TModel> snapshot, Expression<Func<TModel, object?>> property)
        where TContent : class
    {
        var member = RunicMembers.Property(property);
        var reference = snapshot.Reference(property)
            ?? throw new InvalidOperationException($"{typeof(TModel).Name}.{member.Name} presents nothing.");
        return Content<TContent>(reference, member);
    }

    /// <summary>Drives each item that a ViewModel content collection presents now, blocking the calling thread.</summary>
    /// <remarks>Prefer <see cref="ViewsAsync{TContent}"/> in suites that run tests in parallel; see the class remarks.</remarks>
    public IReadOnlyList<RunicViewDriver<TContent>> Views<TContent>(Expression<Func<TModel, object?>> property) where TContent : class =>
        PresentedItems<TContent>(Snapshot(), property);

    /// <summary>Drives each item that a ViewModel content collection presents now, without blocking a thread.</summary>
    /// <param name="property">The content collection property, such as <c>vm => vm.Tabs</c>.</param>
    /// <param name="cancellationToken">Stops waiting for the snapshot.</param>
    public async Task<IReadOnlyList<RunicViewDriver<TContent>>> ViewsAsync<TContent>(Expression<Func<TModel, object?>> property,
        CancellationToken cancellationToken = default) where TContent : class =>
        PresentedItems<TContent>(await SnapshotAsync(cancellationToken).ConfigureAwait(false), property);

    private RunicViewDriver<TContent>[] PresentedItems<TContent>(RunicViewState<TModel> snapshot,
        Expression<Func<TModel, object?>> property) where TContent : class
    {
        var member = RunicMembers.Property(property);
        return snapshot.References(property).Select(reference => Content<TContent>(reference, member)).ToArray();
    }

    /// <summary>Opens a presentation of this route, as a page that shows the View sends <c>open</c>.</summary>
    /// <param name="presentation">The presentation id; the host issues <c>test-presentation{n}</c> when omitted.</param>
    /// <param name="clientKey">The client identity, for tests of several browser clients.</param>
    /// <param name="connectionKey">The connection identity, for tests of a client disconnect.</param>
    /// <returns>The presentation id; pass it to <see cref="Unmount"/>.</returns>
    /// <exception cref="InvalidOperationException">The open was not answered <c>ok</c>; the message names the reply.</exception>
    public string Mount(string? presentation = null, string? clientKey = null, string? connectionKey = null)
    {
        var id = presentation ?? $"test-presentation{_nextId()}";
        return Mounted(id, Send(writer => WriteOpen(writer, id), connectionKey, clientKey));
    }

    /// <summary>Opens a presentation of this route, as a page that shows the View sends <c>open</c>, without blocking a thread.</summary>
    /// <param name="presentation">The presentation id; the host issues <c>test-presentation{n}</c> when omitted.</param>
    /// <param name="clientKey">The client identity, for tests of several browser clients.</param>
    /// <param name="connectionKey">The connection identity, for tests of a client disconnect.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <returns>The presentation id; pass it to <see cref="UnmountAsync"/>.</returns>
    /// <exception cref="InvalidOperationException">The open was not answered <c>ok</c>; the message names the reply.</exception>
    public async Task<string> MountAsync(string? presentation = null, string? clientKey = null, string? connectionKey = null,
        CancellationToken cancellationToken = default)
    {
        var id = presentation ?? $"test-presentation{_nextId()}";
        return Mounted(id, await SendAsync(writer => WriteOpen(writer, id), connectionKey, clientKey, cancellationToken)
            .ConfigureAwait(false));
    }

    private static void WriteOpen(Utf8JsonWriter writer, string presentation)
    {
        writer.WriteString("kind", "open");
        writer.WriteString("presentation", presentation);
    }

    private string Mounted(string presentation, string reply) =>
        reply.StartsWith("{\"ok\":true", StringComparison.Ordinal) && reply.Contains("\"presentation\":\"ok\"", StringComparison.Ordinal)
            ? presentation : throw new InvalidOperationException($"{Route} open returned '{reply}'.");

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
        Unmounted(Send(writer => WriteClose(writer, presentation), connectionKey, clientKey));
    }

    /// <summary>Closes a presentation, as a page that stops showing the View sends <c>close</c>, without blocking a thread.</summary>
    /// <param name="presentation">The id <see cref="MountAsync"/> returned.</param>
    /// <param name="clientKey">The client identity the presentation was opened with.</param>
    /// <param name="connectionKey">The connection identity the presentation was opened with.</param>
    /// <param name="cancellationToken">Stops waiting for the reply.</param>
    /// <exception cref="InvalidOperationException">The route did not close it; the message names the reply.</exception>
    public async Task UnmountAsync(string presentation, string? clientKey = null, string? connectionKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        Unmounted(await SendAsync(writer => WriteClose(writer, presentation), connectionKey, clientKey, cancellationToken)
            .ConfigureAwait(false));
    }

    private static void WriteClose(Utf8JsonWriter writer, string presentation)
    {
        writer.WriteString("kind", "close");
        writer.WriteString("presentation", presentation);
    }

    private void Unmounted(string reply)
    {
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
    /// <remarks>Its first snapshot blocks the calling thread; prefer <see cref="TrackAsync"/> in suites that run tests in parallel.</remarks>
    public RunicStateTracker<TModel> Track()
    {
        // Frames queued before the snapshot are older than it.
        _ = _publications.Take(Route);
        return new(this, _publications, Snapshot());
    }

    /// <summary>
    /// Follows this route's state from a snapshot through its pushed states and frames, as <see cref="Track"/> does,
    /// reading the first snapshot without blocking a thread.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the snapshot.</param>
    public async Task<RunicStateTracker<TModel>> TrackAsync(CancellationToken cancellationToken = default)
    {
        _ = _publications.Take(Route);
        return new(this, _publications, await SnapshotAsync(cancellationToken).ConfigureAwait(false));
    }

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

    // Wire JSON a test passed as a string: one JSON value, or a message that points at the typed overload.
    internal static string Validate(string json, string parameter, string typedMethod)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return json;
        }
        catch (JsonException error)
        {
            var hint = typedMethod == "SetAsync"
                ? "To pass a .NET value, call SetAsync."
                : $"To pass a .NET value, call the typed {typedMethod} overload; for a string, name its type: {typedMethod}<string>(..., \"text\").";
            throw new ArgumentException($"The value is not wire JSON: {error.Message} {hint}", parameter, error);
        }
    }
}
