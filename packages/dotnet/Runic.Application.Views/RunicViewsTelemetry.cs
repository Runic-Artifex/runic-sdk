using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>Names of the Views runtime's <see cref="ActivitySource"/>, <see cref="Meter"/> and log category.</summary>
/// <remarks>
/// Subscribe with OpenTelemetry, for example <c>tracing.AddSource(RunicViewsTelemetry.ActivitySourceName)</c>
/// and <c>metrics.AddMeter(RunicViewsTelemetry.MeterName)</c>. The package README lists the spans,
/// instruments and log event IDs.
/// </remarks>
public static class RunicViewsTelemetry
{
    /// <summary>The name of the <see cref="ActivitySource"/> that traces Bridge calls and operations.</summary>
    public const string ActivitySourceName = "Runic.Application.Views";

    /// <summary>The name of the <see cref="Meter"/> that measures Bridge calls and snapshot delivery.</summary>
    public const string MeterName = "Runic.Application.Views";

    /// <summary>The category of the Views runtime's log entries.</summary>
    public const string LogCategory = "Runic.Application.Views";
}

// Tags carry generated contract names and exception types only, never argument,
// state or exception message values. Route names of content presentations are
// per-instance ("content12"), so they are span tags but not metric dimensions.
internal static class BridgeTelemetry
{
    internal const string ModelTag = "runic.bridge.model";
    internal const string MemberTag = "runic.bridge.member";
    internal const string RouteTag = "runic.bridge.route";
    internal const string KindTag = "runic.bridge.kind";
    internal const string OutcomeTag = "runic.bridge.outcome";
    internal const string FrameTag = "runic.bridge.frame";
    internal const string ErrorTypeTag = "error.type";

    // 0.5 ms to 10 s: Bridge calls are interactive, but operations and slow
    // hosts can take seconds. Declared before the histograms that use it.
    private static readonly InstrumentAdvice<double> SecondsAdvice = new()
    {
        HistogramBucketBoundaries = [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10],
    };

    internal static readonly ActivitySource Source = new(RunicViewsTelemetry.ActivitySourceName, Version());
    internal static readonly Meter Meter = new(RunicViewsTelemetry.MeterName, Version());

    internal static readonly Counter<long> Calls = Meter.CreateCounter<long>(
        "runic.bridge.calls", "{call}", "Bridge commands, property writes and operations handled, by outcome.");
    internal static readonly Counter<long> Failures = Meter.CreateCounter<long>(
        "runic.bridge.failures", "{failure}", "Bridge calls, snapshot captures and deliveries and View mounts that failed.");
    internal static readonly Histogram<double> CallDuration = Meter.CreateHistogram<double>(
        "runic.bridge.call.duration", "s", "Duration of Bridge commands, property writes and operations.",
        advice: SecondsAdvice);
    internal static readonly Counter<long> SnapshotFrames = Meter.CreateCounter<long>(
        "runic.bridge.snapshot.frames", "{frame}", "State and collection delta frames delivered to a host.");
    internal static readonly Histogram<long> SnapshotSize = Meter.CreateHistogram<long>(
        "runic.bridge.snapshot.size", "By", "UTF-8 size of delivered state and delta frames.",
        advice: new InstrumentAdvice<long>
        {
            // 256 B to 4 MiB in powers of four.
            HistogramBucketBoundaries = [256, 1024, 4096, 16_384, 65_536, 262_144, 1_048_576, 4_194_304],
        });
    internal static readonly Histogram<double> DeliveryDuration = Meter.CreateHistogram<double>(
        "runic.bridge.snapshot.delivery.duration", "s", "Time a host took to accept one delivered frame.",
        advice: SecondsAdvice);
    internal static readonly Counter<long> RecoverySnapshots = Meter.CreateCounter<long>(
        "runic.bridge.snapshot.recoveries", "{snapshot}", "Full states sent because a slow host fell behind its delta frames.");
    // Observed rather than added, so a listener that subscribes late reads the
    // current depth instead of the changes after it subscribed.
    private static long _queuedFrames;
    internal static readonly ObservableUpDownCounter<long> DeliveryQueueDepth = Meter.CreateObservableUpDownCounter(
        "runic.bridge.snapshot.queue.depth", static () => Interlocked.Read(ref _queuedFrames), "{frame}",
        "Frames waiting in snapshot delivery queues.");

    internal static void AddQueuedFrames(long change) => Interlocked.Add(ref _queuedFrames, change);

    internal static string ErrorType(Exception error) => error.GetType().FullName ?? error.GetType().Name;

    internal static void RecordFailure(string kind, string? model, string? member, Exception error)
    {
        if (!Failures.Enabled) return;
        Failures.Add(1, new(KindTag, kind), new(ModelTag, model), new(MemberTag, member), new(ErrorTypeTag, ErrorType(error)));
    }

    internal static void RecordFrame(string model, string frame, string payload, long startedTimestamp)
    {
        var frameTag = new KeyValuePair<string, object?>(FrameTag, frame);
        var modelTag = new KeyValuePair<string, object?>(ModelTag, model);
        if (SnapshotFrames.Enabled) SnapshotFrames.Add(1, modelTag, frameTag);
        if (SnapshotSize.Enabled) SnapshotSize.Record(Encoding.UTF8.GetByteCount(payload), modelTag, frameTag);
        if (DeliveryDuration.Enabled && startedTimestamp != 0)
            DeliveryDuration.Record(Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds, modelTag, frameTag);
    }

    internal static long Timestamp() =>
        DeliveryDuration.Enabled || CallDuration.Enabled ? Stopwatch.GetTimestamp() : 0;

    private static string? Version() =>
        typeof(BridgeTelemetry).Assembly.GetName().Version?.ToString();
}

internal static class BridgeCallKind
{
    internal const string Command = "command";
    internal const string Set = "set";
    internal const string Write = "write";
    internal const string OperationStart = "operation.start";
    internal const string Operation = "operation";
}

internal static class BridgeCallOutcome
{
    internal const string Ok = "ok";
    internal const string Rejected = "rejected";
    internal const string Cancelled = "cancelled";
    internal const string Failed = "failed";
    internal const string Disconnected = "disconnected";
    internal const string Unavailable = "unavailable";
    internal const string Capacity = "capacity";
    internal const string Duplicate = "duplicate";
    internal const string Expired = "expired";
    internal const string DeliveryFailed = "delivery_failed";

    // An operation admission names why it did not start; only Accepted is ok.
    internal static string Of(BridgeOperationAdmissionKind kind, string? reason) => kind switch
    {
        BridgeOperationAdmissionKind.Accepted => Ok,
        BridgeOperationAdmissionKind.Duplicate => Duplicate,
        BridgeOperationAdmissionKind.Expired => Expired,
        _ => reason switch
        {
            "unavailable" => Unavailable,
            "capacity" or "stream-capacity" => Capacity,
            _ => Rejected,
        },
    };
}

// One traced and measured Bridge call. Start before the work and Complete it
// once; a mutable local, so a second Complete is ignored.
internal struct BridgeCall
{
    private readonly string _kind;
    private readonly string _model;
    private readonly string _member;
    private readonly Activity? _activity;
    private readonly long _started;
    private bool _completed;

    private BridgeCall(string kind, string model, string member, Activity? activity, long started)
    {
        _kind = kind;
        _model = model;
        _member = member;
        _activity = activity;
        _started = started;
        _completed = false;
    }

    internal static BridgeCall Start(string kind, string model, string route, string member)
    {
        var activity = BridgeTelemetry.Source.HasListeners()
            ? BridgeTelemetry.Source.StartActivity("runic.bridge." + kind)
            : null;
        if (activity is not null)
        {
            activity.DisplayName = $"{kind} {model}.{member}";
            if (activity.IsAllDataRequested)
            {
                activity.SetTag(BridgeTelemetry.KindTag, kind);
                activity.SetTag(BridgeTelemetry.ModelTag, model);
                activity.SetTag(BridgeTelemetry.RouteTag, route);
                activity.SetTag(BridgeTelemetry.MemberTag, member);
            }
        }
        var started = BridgeTelemetry.Calls.Enabled || BridgeTelemetry.CallDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        return new(kind, model, member, activity, started);
    }

    internal void Complete(string outcome, Exception? error = null)
    {
        if (_kind is null || _completed) return;
        _completed = true;
        if (_activity is not null)
        {
            _activity.SetTag(BridgeTelemetry.OutcomeTag, outcome);
            if (error is not null)
            {
                _activity.SetTag(BridgeTelemetry.ErrorTypeTag, BridgeTelemetry.ErrorType(error));
                _activity.SetStatus(ActivityStatusCode.Error);
            }
            _activity.Dispose();
        }
        if (error is not null) BridgeTelemetry.RecordFailure(_kind, _model, _member, error);
        if (_started == 0) return;
        var tags = new TagList
        {
            { BridgeTelemetry.KindTag, _kind },
            { BridgeTelemetry.ModelTag, _model },
            { BridgeTelemetry.MemberTag, _member },
            { BridgeTelemetry.OutcomeTag, outcome },
        };
        if (error is not null) tags.Add(BridgeTelemetry.ErrorTypeTag, BridgeTelemetry.ErrorType(error));
        BridgeTelemetry.Calls.Add(1, tags);
        BridgeTelemetry.CallDuration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
    }
}

// Event IDs are part of the documented diagnostics surface (README, Telemetry).
internal static partial class ViewsLog
{
    [LoggerMessage(EventId = 1000, EventName = "BridgeCommandFailed", Level = LogLevel.Error,
        Message = "Bridge command {Model}.{Member} on route {Route} failed with {ErrorType}.")]
    internal static partial void CommandFailed(ILogger logger, Exception? exception, string model, string member, string route, string errorType);

    [LoggerMessage(EventId = 1001, EventName = "BridgeSetterFailed", Level = LogLevel.Error,
        Message = "Bridge setter {Model}.{Member} on route {Route} failed with {ErrorType}.")]
    internal static partial void SetterFailed(ILogger logger, Exception? exception, string model, string member, string route, string errorType);

    [LoggerMessage(EventId = 1002, EventName = "BridgeFieldWriteFailed", Level = LogLevel.Error,
        Message = "Bridge checked field write {Model}.{Member} on route {Route} failed with {ErrorType}.")]
    internal static partial void FieldWriteFailed(ILogger logger, Exception? exception, string model, string member, string route, string errorType);

    [LoggerMessage(EventId = 1003, EventName = "BridgeOperationAdmissionFailed", Level = LogLevel.Error,
        Message = "Bridge operation {Model}.{Member} on route {Route} could not start: {ErrorType}.")]
    internal static partial void OperationAdmissionFailed(ILogger logger, Exception? exception, string model, string member, string route, string errorType);

    [LoggerMessage(EventId = 1004, EventName = "BridgeOperationFailed", Level = LogLevel.Error,
        Message = "Bridge operation {Member} failed with {ErrorType}.")]
    internal static partial void OperationFailed(ILogger logger, Exception? exception, string member, string errorType);

    [LoggerMessage(EventId = 1005, EventName = "BridgeOperationCancellationCallbackFailed", Level = LogLevel.Warning,
        Message = "A Bridge operation cancellation callback failed with {ErrorType}.")]
    internal static partial void OperationCancellationCallbackFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1010, EventName = "BridgeSnapshotCaptureFailed", Level = LogLevel.Error,
        Message = "Bridge snapshot capture for {Model} on route {Route} failed with {ErrorType}.")]
    internal static partial void SnapshotCaptureFailed(ILogger logger, Exception? exception, string model, string route, string errorType);

    [LoggerMessage(EventId = 1011, EventName = "BridgeSnapshotDeliveryFailed", Level = LogLevel.Error,
        Message = "Bridge snapshot delivery for {Model} on route {Route} failed with {ErrorType}.")]
    internal static partial void SnapshotDeliveryFailed(ILogger logger, Exception? exception, string model, string route, string errorType);

    [LoggerMessage(EventId = 1012, EventName = "BridgeCollectionKeysRejected", Level = LogLevel.Error,
        Message = "Bridge collection {Model}.{Field} on route {Route} has an invalid key {Key}; the route publishes no state until the keys are valid.")]
    internal static partial void CollectionKeysRejected(ILogger logger, Exception? exception, string model, string field, string key, string route);

    [LoggerMessage(EventId = 1020, EventName = "ViewMountFailed", Level = LogLevel.Error,
        Message = "Runic View mount on route {Route} failed with {ErrorType}.")]
    internal static partial void MountFailed(ILogger logger, Exception? exception, string route, string errorType);

    [LoggerMessage(EventId = 1021, EventName = "ViewRemountFailed", Level = LogLevel.Error,
        Message = "Runic View remount on route {Route} failed with {ErrorType}.")]
    internal static partial void RemountFailed(ILogger logger, Exception? exception, string route, string errorType);

    [LoggerMessage(EventId = 1030, EventName = "ModelTurnFailed", Level = LogLevel.Error,
        Message = "A model context turn failed with {ErrorType}.")]
    internal static partial void ModelTurnFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1031, EventName = "ModelTurnDropped", Level = LogLevel.Warning,
        Message = "A model context dropped a posted turn at shutdown: {ErrorType}.")]
    internal static partial void ModelTurnDropped(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1032, EventName = "UnhandledTurnHandlerFailed", Level = LogLevel.Error,
        Message = "An UnhandledTurnException handler failed with {ErrorType}.")]
    internal static partial void UnhandledTurnHandlerFailed(ILogger logger, Exception? exception, string errorType);

    [LoggerMessage(EventId = 1033, EventName = "ModelContextReleaseFailed", Level = LogLevel.Error,
        Message = "Releasing a model context failed with {ErrorType}.")]
    internal static partial void ModelContextReleaseFailed(ILogger logger, Exception? exception, string errorType);
}

// Without an ILoggerFactory the runtime keeps its earlier System.Diagnostics.Trace
// output, so a failure stays visible to a debugger or trace listener.
internal sealed class TraceFallbackLogger : ILogger
{
    internal static readonly TraceFallbackLogger Instance = new();

    private TraceFallbackLogger()
    {
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        if (exception is not null) message = $"{message} {exception}";
        if (logLevel >= LogLevel.Error) Trace.TraceError(message);
        else Trace.TraceWarning(message);
    }
}
