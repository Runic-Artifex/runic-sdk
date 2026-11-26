using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Testing;
using Runic.Application.Testing.Tests.Conformance;

namespace Runic.Application.Views.Conformance;

// The conformance adapter of the .NET Views producer (eng/conformance/PROTOCOL.md): one JSON request per line on
// stdin, one response per line on stdout; the rest of the process's output goes to stderr. The driver owns the steps,
// ids, matching and timeouts. This class maps adapter operations onto the same seams the in-repository runner uses
// (ScenarioTransport, FakeTimeProvider, IConformanceWindow, BridgeDispatcher) and onto BridgeWire for vectors.
internal static class AdapterProgram
{
    private const string Protocol = "rax.conformance-adapter/1";

    // What the .NET producer lists in its hello (BridgeDispatcher's features), and the runner capabilities of README
    // "Requirements" this adapter gives.
    private static readonly string[] Features =
    [
        "text-splice/1", "presentation-epoch/1", "presented/1", "field-flush/1", "view-requests/1", "request-session/1",
        "region-collections/1", "query-collections/1", "field-paths/1", "translation-text/1", "navigation-path/1",
        "appearance/1", "client-languages/1", "client-errors/1", "blobs/1", "validation-pending/1", "trace-context/1",
        "handler-timing/1", "hosted-regions/1", "hosted-region-loading/1",
    ];
    private static readonly string[] Capabilities = ["virtual-clock", "held-delivery"];

    private static ScenarioState? s_state;

    public static async Task<int> Main()
    {
        var protocolOut = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        // Nothing but responses may reach stdout.
        Console.SetOut(Console.Error);
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        while (await input.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonObject request;
            try { request = JsonNode.Parse(line)!.AsObject(); }
            catch (JsonException error) { Console.Error.WriteLine($"unreadable request ({error.Message}): {line}"); continue; }
            var seq = request["seq"]?.DeepClone();
            var op = request["op"]?.GetValue<string>() ?? "";
            var args = request["args"] as JsonObject ?? [];
            JsonObject response;
            try { response = new JsonObject { ["seq"] = seq, ["ok"] = await HandleAsync(op, args).ConfigureAwait(false) }; }
            catch (AdapterFailure failure) { response = Error(seq, failure.Code, failure.Message); }
            catch (Exception error) { response = Error(seq, "failed", $"{error.GetType().Name}: {error.Message}"); }
            await protocolOut.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
            if (op == "shutdown") break;
        }
        s_state?.Dispose();
        return 0;
    }

    private static JsonObject Error(JsonNode? seq, string code, string message) =>
        new() { ["seq"] = seq, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static async Task<JsonNode> HandleAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "describe":
                return new JsonObject
                {
                    ["protocol"] = Protocol,
                    ["implementation"] = new JsonObject
                    {
                        ["id"] = "dotnet-views", ["name"] = "Runic.Application.Views",
                        ["version"] = typeof(WindowContentSession).Assembly.GetName().Version?.ToString(),
                    },
                    ["corpora"] = new JsonArray("views/4"),
                    ["roles"] = new JsonArray("producer"),
                    ["features"] = ProducerFeatures(),
                    ["capabilities"] = new JsonArray([.. Capabilities.Select(capability => (JsonNode)capability)]),
                };
            case "shutdown":
                return new JsonObject();
            case "views.canonicalize":
                return Canonicalize(Convert.FromBase64String(Str(args, "input")));
            case "views.decode":
                return Decode(Str(args, "type"), args["parameters"] as JsonObject, Convert.FromBase64String(Str(args, "input")));
            case "views.readRequest":
                return await ReadRequestAsync(Convert.FromBase64String(Str(args, "input")), args["inputBytes"]?.GetValue<int>(),
                    Convert.FromBase64String(Str(args, "hello"))).ConfigureAwait(false);
            case "views.open":
                s_state?.Dispose();
                s_state = ScenarioState.Open(Str(args, "view"), (args["model"] as JsonObject) ?? [], args["inputBytes"]?.GetValue<int>());
                return new JsonObject();
            case "views.dispose":
                s_state?.Dispose();
                s_state = null;
                return new JsonObject();
            default:
                var state = s_state ?? throw new AdapterFailure("failed", "No window is open (views.open).");
                return await state.HandleAsync(op, args).ConfigureAwait(false);
        }
    }

    // The features the producer itself lists: the hello reply to a client that offers every feature name it knows.
    private static JsonArray ProducerFeatures()
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { IncludeFailureDetail = false });
        var hello = new JsonObject
        {
            ["kind"] = "hello", ["protocol"] = 4, ["session"] = "s-describe",
            ["client"] = new JsonObject { ["name"] = "rax-conformance", ["version"] = "0.0.0" },
            ["features"] = new JsonArray([.. Features.Select(feature => (JsonNode)feature)]),
        };
        var reply = JsonNode.Parse(transport.Request(hello.ToJsonString()))!;
        return reply["features"]?.AsArray().DeepClone().AsArray() ?? [];
    }

    internal static string Str(JsonObject args, string name) =>
        args[name]?.GetValue<string>() ?? throw new AdapterFailure("failed", $"Missing argument {name}.");

    // The canonical form. The reader's exceptions carry the corpus's codes (#1340); an exception without one is
    // reported as unmapped, so the case fails instead of the adapter guessing a code.
    private static JsonObject Canonicalize(byte[] input)
    {
        try { return new JsonObject { ["output"] = Encoding.UTF8.GetString(BridgeWire.Canonicalize(input)) }; }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return new JsonObject
            {
                ["error"] = BridgeCanonicalJson.ErrorCode(error) ?? $"unmapped:{error.GetType().Name}",
                ["message"] = $"{error.GetType().Name}: {error.Message}",
            };
        }
    }

    private enum VectorLevel { Low, Normal, High }

    private sealed record WireCodec(Func<JsonElement, object?> Read, Action<Utf8JsonWriter, object?> Write);

    // ValueVectorTests' codec table: the readers and writers generated codecs call (BridgeWire).
    private static WireCodec Codec(string type) => type switch
    {
        "boolean" => new(element => BridgeWire.ReadBoolean(element), (writer, value) => writer.WriteBooleanValue((bool)value!)),
        "int8" => new(element => BridgeWire.ReadInt8(element), (writer, value) => writer.WriteNumberValue((sbyte)value!)),
        "uint8" => new(element => BridgeWire.ReadUInt8(element), (writer, value) => writer.WriteNumberValue((byte)value!)),
        "int16" => new(element => BridgeWire.ReadInt16(element), (writer, value) => writer.WriteNumberValue((short)value!)),
        "uint16" => new(element => BridgeWire.ReadUInt16(element), (writer, value) => writer.WriteNumberValue((ushort)value!)),
        "int32" => new(element => BridgeWire.ReadInt32(element), (writer, value) => writer.WriteNumberValue((int)value!)),
        "uint32" => new(element => BridgeWire.ReadUInt32(element), (writer, value) => writer.WriteNumberValue((uint)value!)),
        "float32" => new(element => BridgeWire.ReadSingle(element), (writer, value) => BridgeWire.WriteSingle(writer, (float)value!)),
        "float64" => new(element => BridgeWire.ReadDouble(element), (writer, value) => BridgeWire.WriteDouble(writer, (double)value!)),
        "int64" => new(element => BridgeWire.ReadInt64(element), (writer, value) => BridgeWire.WriteInt64(writer, (long)value!)),
        "uint64" => new(element => BridgeWire.ReadUInt64(element), (writer, value) => BridgeWire.WriteUInt64(writer, (ulong)value!)),
        "big-integer" => new(element => BridgeWire.ReadBigInteger(element),
            (writer, value) => BridgeWire.WriteBigInteger(writer, (System.Numerics.BigInteger)value!)),
        "decimal" => new(element => BridgeWire.ReadDecimal(element), (writer, value) => BridgeWire.WriteDecimal(writer, (decimal)value!)),
        "string" => new(element => BridgeWire.ReadString(element), (writer, value) => writer.WriteStringValue((string)value!)),
        "uuid" => new(element => BridgeWire.ReadGuid(element), (writer, value) => BridgeWire.WriteGuid(writer, (Guid)value!)),
        "local-date" => new(element => BridgeWire.ReadDateOnly(element), (writer, value) => BridgeWire.WriteDateOnly(writer, (DateOnly)value!)),
        "local-time" => new(element => BridgeWire.ReadTimeOnly(element), (writer, value) => BridgeWire.WriteTimeOnly(writer, (TimeOnly)value!)),
        "date-time" => new(element => BridgeWire.ReadDateTime(element), (writer, value) => BridgeWire.WriteDateTime(writer, (DateTime)value!)),
        "offset-date-time" => new(element => BridgeWire.ReadDateTimeOffset(element),
            (writer, value) => BridgeWire.WriteDateTimeOffset(writer, (DateTimeOffset)value!)),
        "duration" => new(element => BridgeWire.ReadTimeSpan(element), (writer, value) => BridgeWire.WriteTimeSpan(writer, (TimeSpan)value!)),
        "enum" => new(element => BridgeWire.ReadEnum<VectorLevel>(element), (writer, value) => BridgeWire.WriteEnum(writer, (VectorLevel)value!)),
        "json" => new(element => element.ValueKind == JsonValueKind.Null ? throw new FormatException("Expected a value.") : element.Clone(),
            (writer, value) => ((JsonElement)value!).WriteTo(writer)),
        _ => throw new AdapterFailure("unsupported", $"No codec for the wire type {type}."),
    };

    private static JsonObject Decode(string type, JsonObject? parameters, byte[] input)
    {
        var nullable = parameters?["nullable"]?.GetValue<bool>() == true;
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 64 });
            var element = document.RootElement;
            if (element.ValueKind == JsonValueKind.Null && (nullable || type == "json" && parameters?["nullable"]?.GetValue<bool>() != false))
                return new JsonObject { ["value"] = "null" };
            var codec = Codec(type);
            var decoded = codec.Read(element);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 128 })) codec.Write(writer, decoded);
            return new JsonObject { ["value"] = Encoding.UTF8.GetString(stream.ToArray()) };
        }
        catch (Exception error) when (error is FormatException or JsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            return new JsonObject { ["error"] = "invalid-value", ["message"] = error.Message };
        }
    }

    // README "Messages": a fresh window, and the file's hello first unless the case is a hello itself.
    private static readonly string[] EnvelopeReasons = ["invalid-request", "invalid-input", "no-hello"];

    private static async Task<JsonObject> ReadRequestAsync(byte[] input, int? inputBytes, byte[] hello)
    {
        using var transport = new InMemoryBridgeTransport();
        using var session = new WindowContentSession(transport, new WindowContentSessionOptions { MaximumInputBytes = inputBytes, IncludeFailureDetail = false });
        if (Kind(input) != "hello" && JsonNode.Parse(transport.Request(Encoding.UTF8.GetString(hello)))!["ok"]!.GetValue<bool>() is false)
            throw new AdapterFailure("failed", "The hello before the request failed.");
        var reply = await BridgeDispatcher.For(transport)
            .HandleAsync(new BridgeRequest(input, InMemoryBridgeTransport.DefaultClientKey, InMemoryBridgeTransport.DefaultConnectionKey), CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var answer = JsonNode.Parse(Encoding.UTF8.GetString(reply.Span))!.AsObject();
        var error = answer["ok"]!.GetValue<bool>() ? null : answer["error"]!.AsObject();
        var kind = error?["kind"]?.GetValue<string>();
        var reason = error?["reason"]?.GetValue<string>();
        // The corpus's "valid": not rejected for the envelope (a route nothing serves answers released, which is fine).
        if (kind is null || !(kind == "rejected" && EnvelopeReasons.Contains(reason)))
            return new JsonObject { ["result"] = "valid", ["reply"] = answer.ToJsonString() };
        return new JsonObject { ["result"] = "invalid", ["kind"] = kind, ["reason"] = reason };
    }

    private static string? Kind(byte[] input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String ? kind.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}

internal sealed class AdapterFailure(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}

// One scenario's window, transport and clock.
internal sealed class ScenarioState : IDisposable
{
    private readonly ScenarioTransport _transport = new();
    private readonly FakeTimeProvider _clock = new();
    private readonly IConformanceWindow _window;
    private readonly Dictionary<string, ScenarioConnection> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<RunicWindowCloseResult>> _closes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<bool>> _flushes = new(StringComparer.Ordinal);

    private ScenarioState(string view, JsonObject model, int? inputBytes)
    {
        try { _window = ConformanceViews.Producer.CreateWindow(view, model, new(_transport, _clock, inputBytes)); }
        catch (NotSupportedException error) { _transport.Dispose(); throw new AdapterFailure("unsupported", error.Message); }
    }

    internal static ScenarioState Open(string view, JsonObject model, int? inputBytes) => new(view, model, inputBytes);

    internal async Task<JsonNode> HandleAsync(string op, JsonObject args)
    {
        switch (op)
        {
            case "views.connect":
            {
                var name = AdapterProgram.Str(args, "conn");
                _connections.Add(name, _transport.Connect(name));
                return new JsonObject();
            }
            case "views.disconnect":
                _transport.Disconnect(Connection(args));
                return new JsonObject();
            case "views.send":
                _transport.Send(Connection(args), Encoding.UTF8.GetBytes(AdapterProgram.Str(args, "text")), AdapterProgram.Str(args, "tag"));
                return new JsonObject();
            case "views.next":
            {
                var connection = Connection(args);
                var wait = TimeSpan.FromMilliseconds(args["waitMs"]?.GetValue<int>() ?? 0);
                ScenarioMessage? message = null;
                if (!connection.Messages.Reader.TryRead(out message) && wait > TimeSpan.Zero)
                {
                    using var timeout = new CancellationTokenSource(wait);
                    try { message = await connection.Messages.Reader.ReadAsync(timeout.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { message = null; }
                }
                return new JsonObject
                {
                    ["message"] = message is null ? null : new JsonObject { ["replyTo"] = message.ReplyTo, ["text"] = message.Json },
                };
            }
            case "views.settle":
            {
                var wait = TimeSpan.FromMilliseconds(args["waitMs"]?.GetValue<int>() ?? 0);
                return new JsonObject { ["settled"] = await _transport.SettleAsync(_window.RunQueuedTurnsAsync, wait).ConfigureAwait(false) };
            }
            case "views.advance":
                _clock.Advance(TimeSpan.FromMilliseconds(args["ms"]!.GetValue<double>()));
                return new JsonObject();
            case "views.delivery":
                if (args["hold"]!.GetValue<bool>()) _transport.Hold();
                else _transport.Release();
                return new JsonObject();
            case "views.mutate":
                await _window.MutateAsync(args["mutations"]!.AsArray(), args["each"]?.GetValue<bool>() == true).ConfigureAwait(false);
                return new JsonObject();
            case "views.flushFields":
                _flushes.Add(AdapterProgram.Str(args, "id"), _window.FlushFieldsAsync());
                return new JsonObject();
            case "views.closeWindow":
                _closes.Add(AdapterProgram.Str(args, "id"), _window.CloseAsync(TimeSpan.FromMilliseconds(args["timeout"]!.GetValue<int>())));
                return new JsonObject();
            case "views.setLocale":
                BridgeDispatcher.For(_transport).SetLocale(args["locale"]?.GetValue<string>());
                return new JsonObject();
            case "views.setAppearance":
                BridgeDispatcher.For(_transport).SetAppearance(ReadAppearance(args["appearance"] as JsonObject));
                return new JsonObject();
            case "views.readMember":
                return new JsonObject { ["value"] = _window.ReadMember(AdapterProgram.Str(args, "member"))?.DeepClone() };
            case "views.provide":
                return new JsonObject { ["value"] = _window.Provide(AdapterProgram.Str(args, "name"))?.DeepClone() };
            case "views.waitResult":
                return await ResultAsync(_window.Wait(AdapterProgram.Str(args, "id")), args).ConfigureAwait(false);
            case "views.closeResult":
            {
                var close = _closes.GetValueOrDefault(AdapterProgram.Str(args, "id")) ?? throw new AdapterFailure("failed", "No such close.");
                return await ResultAsync(close.ContinueWith(task => task.Result.PagesAcknowledged, TaskScheduler.Default), args).ConfigureAwait(false);
            }
            case "views.flushResult":
                return await ResultAsync(_flushes.GetValueOrDefault(AdapterProgram.Str(args, "id")) ?? throw new AdapterFailure("failed", "No such flush."), args)
                    .ConfigureAwait(false);
            case "views.languages":
            {
                var connection = Connection(args);
                using var lease = BridgeDispatcher.For(_transport).ObserveConnections(static _ => { }, static _ => { }, out var open);
                var found = open.FirstOrDefault(candidate => candidate.TransportKey == connection.Key)
                    ?? throw new AdapterFailure("failed", $"The connection {connection.Name} has not completed its hello.");
                return new JsonObject { ["value"] = new JsonArray([.. found.Languages.Select(language => (JsonNode)language)]) };
            }
            default:
                throw new AdapterFailure("unsupported", $"Operation {op}.");
        }
    }

    // A pending result: null while it has not completed within waitMs, else its value.
    private static async Task<JsonNode> ResultAsync(Task<bool> task, JsonObject args)
    {
        var wait = TimeSpan.FromMilliseconds(args["waitMs"]?.GetValue<int>() ?? 0);
        if (!task.IsCompleted && wait > TimeSpan.Zero)
            try { await task.WaitAsync(wait).ConfigureAwait(false); }
            catch (TimeoutException) { }
        return new JsonObject { ["result"] = task.IsCompletedSuccessfully ? task.Result : null };
    }

    private ScenarioConnection Connection(JsonObject args)
    {
        var name = AdapterProgram.Str(args, "conn");
        return _connections.GetValueOrDefault(name) ?? throw new AdapterFailure("failed", $"Unknown connection {name}.");
    }

    // ProducerScenarioRunner.ReadAppearance: the members of an appearance push as the DesktopAppearance a host sets.
    private static Runic.Platform.DesktopAppearance? ReadAppearance(JsonObject? members)
    {
        if (members is null) return null;
        static Runic.Platform.DesktopAccentColor? Color(JsonNode? node) => node?.GetValue<string>() is { } text
            ? new(Convert.ToInt32(text[1..3], 16) / 255d, Convert.ToInt32(text[3..5], 16) / 255d, Convert.ToInt32(text[5..7], 16) / 255d)
            : null;
        var colors = members["systemColors"] as JsonObject;
        return new Runic.Platform.DesktopAppearance(
            members["colorScheme"]?.GetValue<string>() switch
            {
                "light" => Runic.Platform.DesktopColorScheme.Light,
                "dark" => Runic.Platform.DesktopColorScheme.Dark,
                _ => Runic.Platform.DesktopColorScheme.NoPreference,
            },
            Color(members["accentColor"]), ReducedMotion: members["reducedMotion"]?.GetValue<bool>())
        {
            Contrast = members["contrast"]?.GetValue<string>() switch
            {
                "no-preference" => Runic.Platform.DesktopContrast.NoPreference,
                "more" => Runic.Platform.DesktopContrast.More,
                "forced-colors" => Runic.Platform.DesktopContrast.ForcedColors,
                _ => null,
            },
            TextScale = members["textScale"]?.GetValue<double>(),
            SystemColors = colors is null ? null : new(Color(colors["canvas"])!, Color(colors["canvasText"])!, Color(colors["linkText"])!,
                Color(colors["grayText"])!, Color(colors["highlight"])!, Color(colors["highlightText"])!, Color(colors["buttonFace"])!,
                Color(colors["buttonText"])!),
        };
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values.Where(connection => connection.IsOpen)) _transport.Disconnect(connection);
        _window.Dispose();
        _transport.Dispose();
    }
}
