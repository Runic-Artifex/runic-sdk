using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>
/// Calls the generated routes of one <typeparamref name="TModel"/> presentation by ViewModel
/// member, as the generated TypeScript client does, instead of by raw route name.
/// </summary>
/// <example>
/// <code>
/// var editor = host.Root.View&lt;DocumentViewModel&gt;(vm => vm.Main).View&lt;EditorViewModel&gt;(vm => vm.CurrentPane);
/// editor.Set(vm => vm.Title, "Groceries").EnsureOk();
/// (await editor.ExecuteAsync(vm => vm.SaveCommand)).EnsureOk();
/// </code>
/// </example>
public sealed class RunicViewDriver<TModel> where TModel : class
{
    private readonly RunicPublicationLog _publications;
    private readonly InMemoryViewTransport _transport;
    private readonly Func<string> _nextId;

    internal RunicViewDriver(RunicPublicationLog publications, InMemoryViewTransport transport, string route,
        PageReference? reference, Func<string> nextId)
    {
        _publications = publications;
        _transport = transport;
        _nextId = nextId;
        Route = route;
        Reference = reference;
    }

    /// <summary>The route prefix: the root name, or <c>content{id}</c> for presented content.</summary>
    public string Route { get; }

    /// <summary>The content reference this driver presents, or null for the root route.</summary>
    public PageReference? Reference { get; }

    /// <summary>Reads the current generated state.</summary>
    public RunicViewState<TModel> Snapshot()
    {
        var route = $"{Route}Snapshot";
        var reply = new RunicCallReply<TModel>(route, _transport.Call(route));
        return reply.EnsureOk().State ?? throw new InvalidOperationException($"{route} returned no state.");
    }

    /// <summary>
    /// Calls the generated setter of a string, Boolean or integer property, as the client's
    /// <c>set{Property}(value)</c> does. Use <see cref="SetJson{TValue}"/> for other values.
    /// </summary>
    public RunicCallReply<TModel> Set<TValue>(Expression<Func<TModel, TValue>> property, TValue value)
    {
        var member = RunicMembers.Property(property);
        var type = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        ViewTestArguments arguments = value switch
        {
            int or long or short or byte or sbyte or ushort or uint => new(Int64Value: System.Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            bool flag => new(BooleanValue: flag),
            string text when !IsNullable(member) => new(StringValue: text),
            null or string when type == typeof(string) => new(StringValue: RunicJson.Write(writer => writer.WriteStringValue((string?)(object?)value))),
            _ => throw new NotSupportedException($"Set supports string, Boolean and integer properties; call SetJson with the wire JSON of {typeof(TValue).Name}."),
        };
        var route = $"{Route}Set{member.Name}";
        return new(route, _transport.Call(route, arguments));
    }

    /// <summary>Calls the generated setter of a property with a value already encoded as wire JSON.</summary>
    public RunicCallReply<TModel> SetJson<TValue>(Expression<Func<TModel, TValue>> property, string wireJson)
    {
        ArgumentNullException.ThrowIfNull(wireJson);
        var route = $"{Route}Set{RunicMembers.Property(property).Name}";
        return new(route, _transport.Call(route, new(StringValue: wireJson)));
    }

    /// <summary>
    /// Executes a command, as the client's <c>{command}(input)</c> does, and waits for an
    /// asynchronous command to finish.
    /// </summary>
    /// <param name="command">The command property, such as <c>vm => vm.SaveCommand</c>.</param>
    /// <param name="argumentJson">The command input as wire JSON, for a command with an argument.</param>
    /// <param name="cancellationToken">Cancels an awaited command, as a closed client connection does.</param>
    public async ValueTask<RunicCallReply<TModel>> ExecuteAsync(Expression<Func<TModel, object?>> command, string? argumentJson = null,
        CancellationToken cancellationToken = default)
    {
        var route = $"{Route}{RunicMembers.CommandName(RunicMembers.Property(command))}";
        var arguments = argumentJson is null ? new ViewTestArguments() : new ViewTestArguments(StringValue: argumentJson);
        var json = _transport.IsAsync(route)
            ? await _transport.CallAsync(route, arguments, cancellationToken).ConfigureAwait(false)
            : _transport.Call(route, arguments);
        return new(route, json);
    }

    /// <summary>Asks whether a command with an argument can execute (<c>can{Command}(input)</c>).</summary>
    public bool CanExecute(Expression<Func<TModel, object?>> command, string argumentJson)
    {
        ArgumentNullException.ThrowIfNull(argumentJson);
        var route = $"{Route}Can{RunicMembers.CommandName(RunicMembers.Property(command))}";
        var reply = _transport.Call(route, new(StringValue: argumentJson));
        return reply switch
        {
            "true" => true,
            "false" => false,
            _ => throw new InvalidOperationException($"{route} returned '{reply}'."),
        };
    }

    /// <summary>
    /// Starts an asynchronous command as a recoverable operation (<c>start{Command}()</c>).
    /// </summary>
    /// <param name="command">The command property.</param>
    /// <param name="argumentJson">The command input as wire JSON, for a command with an argument.</param>
    /// <param name="requestId">The idempotency key; the host issues <c>request{n}</c> when omitted.</param>
    public RunicTestOperation Start(Expression<Func<TModel, object?>> command, string? argumentJson = null, string? requestId = null)
    {
        var member = RunicMembers.CommandName(RunicMembers.Property(command));
        requestId ??= $"request{_nextId()}";
        var payload = argumentJson is null ? requestId : RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", requestId);
            writer.WritePropertyName("input");
            writer.WriteRawValue(argumentJson);
            writer.WriteEndObject();
        });
        var admission = _transport.Call($"{Route}Start{member}", new(StringValue: payload));
        return new(_transport, $"{typeof(TModel).FullName}:{Fingerprint.Value}:{Route}", member, requestId, admission);
    }

    /// <summary>
    /// Writes a string, Boolean or integer checked field, as the client's
    /// <c>write{Property}(value, {{ requestId, baseline }})</c> does. The baseline is the
    /// current value and, unless given, its current version.
    /// </summary>
    public RunicFieldWriteReceipt<TModel> Write<TValue>(Expression<Func<TModel, TValue>> property, TValue value, long? expectedVersion = null)
    {
        var member = RunicMembers.Property(property);
        var snapshot = Snapshot();
        var field = RunicMembers.WireName(member);
        var version = expectedVersion ?? snapshot.FieldVersion(property);
        var payload = RunicJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("requestId", $"write{_nextId()}");
            writer.WriteNumber("expectedVersion", version);
            writer.WritePropertyName("expectedValue");
            snapshot[field].WriteTo(writer);
            writer.WritePropertyName("value");
            switch (value)
            {
                case null: writer.WriteNullValue(); break;
                case string text: writer.WriteStringValue(text); break;
                case bool flag: writer.WriteBooleanValue(flag); break;
                case int or long or short or byte: writer.WriteNumberValue(System.Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
                default: throw new NotSupportedException($"Write supports string, Boolean and integer fields, not {typeof(TValue).Name}.");
            }
            writer.WriteEndObject();
        });
        var route = $"{Route}Write{member.Name}";
        return new(route, _transport.Call(route, new(StringValue: payload)));
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

    /// <summary>Acknowledges a browser presentation, as a frontend outlet does when it mounts this View.</summary>
    /// <param name="token">The mount token, `{session}:{presentation}`; the host issues `test:mount{n}` when omitted.</param>
    /// <param name="clientKey">The client identity, for tests of several browser clients.</param>
    /// <param name="connectionKey">The connection identity, for tests of a client disconnect.</param>
    /// <returns>The mount token; pass it to <see cref="Unmount"/>.</returns>
    public string Mount(string? token = null, string? clientKey = null, string? connectionKey = null)
    {
        token ??= $"test:mount{_nextId()}";
        var reply = _transport.Call($"{Route}Mount", new(StringValue: token, ClientKey: clientKey, ConnectionKey: connectionKey));
        return reply == "ok" ? token : throw new InvalidOperationException($"{Route}Mount returned '{reply}'.");
    }

    /// <summary>Releases a browser presentation.</summary>
    public string Unmount(string token, string? clientKey = null, string? connectionKey = null) =>
        _transport.Call($"{Route}Unmount", new(StringValue: token, ClientKey: clientKey, ConnectionKey: connectionKey));

    /// <summary>Returns and removes the frames pushed to this route so far.</summary>
    public IReadOnlyList<RunicViewPublication> TakePublications() => _publications.Take(Route);

    /// <summary>Waits for the next frame pushed to this route.</summary>
    /// <param name="timeout">Real time to wait; defaults to the host's publication timeout.</param>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="TimeoutException">Nothing was pushed in time.</exception>
    public ValueTask<RunicViewPublication> NextPublicationAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        _publications.NextAsync(Route, timeout, cancellationToken);

    /// <summary>
    /// Follows this route's state from a snapshot through its pushed frames, applying collection
    /// deltas as the client does and failing on a frame the client could not apply.
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

    private static bool IsNullable(PropertyInfo property) =>
        new NullabilityInfoContext().Create(property).WriteState == NullabilityState.Nullable;

    private static class Fingerprint
    {
        // The generated operation contract includes the ViewModel's contract fingerprint.
        internal static readonly string Value = Compute();

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The test host reflects over the application's ViewModels in an untrimmed test process.")]
        [UnconditionalSuppressMessage("Trimming", "IL2087", Justification = "The test host reflects over the application's ViewModels in an untrimmed test process.")]
        private static string Compute() => BridgeContractShape.Compute(typeof(TModel));
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
