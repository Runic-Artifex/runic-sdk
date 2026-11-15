using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace Runic.Application.Views;

// What one request asks of the Bridge call it runs (specs/application/README.md, "Trace context and handler timing"):
//
// - trace-context/1: the page's W3C trace context (`trace: { traceparent, tracestate? }`), the parent of the request's
//   runic.bridge.* span, so the page's span, the Bridge span and the ViewModel's own spans form one trace. It is read
//   only while the Views ActivitySource has a listener; a context that does not parse is ignored.
// - handler-timing/1: `timing: true` asks for the C# handler's name and duration in the reply (`handler`). The producer
//   offers the feature only in Development (BridgeDispatcher.HandlerTiming).
//
// The dispatcher creates one only when the request asks for either, and makes it current for the route handler's work
// (an AsyncLocal, which the model turns flow with the ExecutionContext they capture). The first BridgeCall that starts
// in it claims it: that call's span takes the remote parent, and its Complete records the handler's timing. Without a
// context, a BridgeCall reads one AsyncLocal and nothing else.
internal sealed class BridgeRequestTrace
{
    // W3C Trace Context: a version 00 traceparent is 55 characters, and tracestate is at most 32 list members of at most
    // 256 characters each, so 512 characters is the length the specification asks implementations to keep.
    internal const int MaximumTraceparentLength = 256;
    internal const int MaximumTracestateLength = 512;

    private static readonly AsyncLocal<BridgeRequestTrace?> CurrentRequest = new();

    private int _claimed;
    private string? _handlerName;
    private double _handlerMilliseconds;

    private BridgeRequestTrace(ActivityContext parent, bool hasParent, bool timing)
    {
        Parent = parent;
        HasParent = hasParent;
        Timing = timing;
    }

    // The page's span, when the request carried a trace context that parsed and the ActivitySource has a listener.
    internal ActivityContext Parent { get; }
    internal bool HasParent { get; }
    // Whether the reply carries the handler's timing.
    internal bool Timing { get; }

    // The context of a request on a connection, or null when the request asks for nothing (and always without either
    // feature in use, which costs two flag reads).
    internal static BridgeRequestTrace? Read(BridgeConnection connection, JsonElement root)
    {
        if (!connection.UsesTraceContext && !connection.UsesHandlerTiming) return null;
        var timing = connection.UsesHandlerTiming && root.TryGetProperty("timing", out var flag) && flag.ValueKind == JsonValueKind.True;
        var hasParent = false;
        ActivityContext parent = default;
        if (connection.UsesTraceContext && BridgeTelemetry.Source.HasListeners()) hasParent = TryReadParent(root, out parent);
        return timing || hasParent ? new(parent, hasParent, timing) : null;
    }

    // `trace: { traceparent, tracestate? }`; anything else is ignored, so tracing never fails a call.
    internal static bool TryReadParent(JsonElement root, out ActivityContext parent)
    {
        parent = default;
        if (!root.TryGetProperty("trace", out var trace) || trace.ValueKind != JsonValueKind.Object
            || !trace.TryGetProperty("traceparent", out var traceparent) || traceparent.ValueKind != JsonValueKind.String)
            return false;
        var value = traceparent.GetString()!;
        if (value.Length > MaximumTraceparentLength) return false;
        string? state = null;
        if (trace.TryGetProperty("tracestate", out var tracestate) && tracestate.ValueKind == JsonValueKind.String
            && tracestate.GetString() is { Length: > 0 and <= MaximumTracestateLength } text)
            state = text;
        return ActivityContext.TryParse(value, state, isRemote: true, out parent);
    }

    // Makes context current for the work started until the scope is disposed; null leaves everything as it is.
    internal static Scope Enter(BridgeRequestTrace? context)
    {
        if (context is null) return default;
        var previous = CurrentRequest.Value;
        CurrentRequest.Value = context;
        return new(previous, true);
    }

    // The current request's context for the first call that starts in it; null for later calls and without one.
    internal static BridgeRequestTrace? Claim()
    {
        var context = CurrentRequest.Value;
        return context is not null && Interlocked.Exchange(ref context._claimed, 1) == 0 ? context : null;
    }

    internal void RecordHandler(string name, double milliseconds)
    {
        _handlerMilliseconds = milliseconds;
        Volatile.Write(ref _handlerName, name);
    }

    // The reply with `handler: { name, duration }` added, when the request asked for timing and a call recorded it. A
    // reply is one JSON object that the producer wrote, so the member goes before its closing brace.
    internal ReadOnlyMemory<byte> AddHandler(ReadOnlyMemory<byte> reply)
    {
        if (!Timing || Volatile.Read(ref _handlerName) is not { } name) return reply;
        var span = reply.Span;
        if (span.Length < 2 || span[0] != (byte)'{' || span[^1] != (byte)'}') return reply;
        var buffer = new ArrayBufferWriter<byte>(span.Length + name.Length + 48);
        buffer.Write(span[..^1]);
        buffer.Write(span.Length == 2 ? "\"handler\":"u8 : ",\"handler\":"u8);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("name", name);
            // Milliseconds, to the microsecond.
            writer.WriteNumber("duration", Math.Round(_handlerMilliseconds, 3));
            writer.WriteEndObject();
        }
        buffer.Write("}"u8);
        return buffer.WrittenMemory;
    }

    internal readonly struct Scope(BridgeRequestTrace? previous, bool entered) : IDisposable
    {
        public void Dispose()
        {
            if (entered) CurrentRequest.Value = previous;
        }
    }
}
