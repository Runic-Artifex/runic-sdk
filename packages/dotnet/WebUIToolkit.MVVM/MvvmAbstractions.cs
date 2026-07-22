namespace WebUIToolkit.MVVM;

/// <summary>Stable BCL instrumentation names emitted by the MVVM runtime.</summary>
/// <remarks>
/// Consumers can subscribe with <see cref="ActivityListener"/>, <see cref="MeterListener"/>,
/// or an OpenTelemetry bridge. Instrumentation never includes capability tokens, JSON payloads,
/// consumer exception details, or other unbounded application data.
/// </remarks>
public static class MvvmDiagnostics
{
    /// <summary>The name shared by the runtime <see cref="ActivitySource"/> and <see cref="Meter"/>.</summary>
    public const string InstrumentationName = "WebUIToolkit.MVVM";

    /// <summary>The activity emitted while a session is activated.</summary>
    public const string SessionOpenActivityName = "mvvm.session.open";

    /// <summary>The activity emitted while a request is dispatched.</summary>
    public const string RequestActivityName = "mvvm.request.dispatch";
}

/// <summary>Internal zero-dependency instruments shared by the factory and session runtime.</summary>
internal static class MvvmTelemetry
{
    private static readonly ActivitySource? ActivitySource = CreateActivitySource();
    private static readonly Meter? Meter = CreateMeter();

    private static readonly Counter<long>? SessionsOpened = CreateCounter(
        "mvvm.sessions.opened",
        "Sessions activated successfully.");

    private static readonly Counter<long>? SessionsClosed = CreateCounter(
        "mvvm.sessions.closed",
        "Sessions whose owned lifetime completed.");

    private static readonly Counter<long>? SessionOpenFailures = CreateCounter(
        "mvvm.session.open.failures",
        "Session activation attempts that failed.");

    private static readonly UpDownCounter<long>? ActiveSessions = CreateUpDownCounter(
        "mvvm.sessions.active",
        "Currently owned sessions.");

    private static readonly Histogram<double>? SessionOpenDuration = CreateHistogram(
        "mvvm.session.open.duration",
        "Session activation duration.");

    private static readonly Counter<long>? Requests = CreateCounter(
        "mvvm.requests",
        "Terminal request outcomes.");

    private static readonly Counter<long>? RequestFaults = CreateCounter(
        "mvvm.request.faults",
        "Terminal protocol fault responses.");

    private static readonly Counter<long>? BackpressureRejections = CreateCounter(
        "mvvm.backpressure.rejections",
        "Work rejected by a configured capacity limit.");

    private static readonly UpDownCounter<long>? ActiveRequests = CreateUpDownCounter(
        "mvvm.requests.active",
        "Dispatch calls that have not published a terminal outcome.");

    private static readonly Histogram<double>? RequestDuration = CreateHistogram(
        "mvvm.request.duration",
        "Request dispatch duration through terminal publication.");

    internal static MvvmActivity StartSessionOpen() =>
        MvvmActivity.Start(ActivitySource, MvvmDiagnostics.SessionOpenActivityName);

    internal static MvvmActivity StartRequest(string requestKind)
    {
        MvvmActivity activity = MvvmActivity.Start(ActivitySource, MvvmDiagnostics.RequestActivityName);
        activity.SetTag("mvvm.request.kind", requestKind);
        return activity;
    }

    internal static void SessionOpenSucceeded(MvvmActivity activity, long startedTimestamp)
    {
        activity.SetTag("mvvm.outcome", "success");
        TryRecord(SessionOpenDuration,
            Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,
            OutcomeTag("success"));
        TryAdd(SessionsOpened, 1);
        TryAdd(ActiveSessions, 1);
    }

    internal static void SessionOpenFailed(MvvmActivity activity, long startedTimestamp, string reason)
    {
        activity.SetTag("mvvm.outcome", reason);
        activity.SetError();
        TryRecord(SessionOpenDuration,
            Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,
            OutcomeTag(reason));
        TryAdd(SessionOpenFailures, 1, OutcomeTag(reason));
    }

    internal static long RequestAdmitted()
    {
        TryAdd(ActiveRequests, 1);
        return Stopwatch.GetTimestamp();
    }

    internal static void RequestCompleted(
        MvvmActivity activity,
        long startedTimestamp,
        string requestKind,
        string outcome,
        string? faultCode = null)
    {
        activity.SetTag("mvvm.outcome", outcome);
        if (faultCode is not null)
        {
            activity.SetTag("mvvm.fault.code", faultCode);
            activity.SetError();
        }

        var kindTag = new KeyValuePair<string, object?>("mvvm.request.kind", requestKind);
        KeyValuePair<string, object?> outcomeTag = OutcomeTag(outcome);
        TryAdd(Requests, 1, kindTag, outcomeTag);
        if (faultCode is not null)
        {
            var faultTag = new KeyValuePair<string, object?>("mvvm.fault.code", faultCode);
            TryAdd(RequestFaults, 1, kindTag, faultTag);
        }

        TryRecord(RequestDuration,
            Stopwatch.GetElapsedTime(startedTimestamp).TotalSeconds,
            kindTag,
            outcomeTag);
        TryAdd(ActiveRequests, -1);
    }

    internal static void RequestRejected(
        MvvmActivity activity,
        string requestKind,
        string outcome,
        string faultCode,
        string? capacityLimit = null)
    {
        activity.SetTag("mvvm.outcome", outcome);
        activity.SetTag("mvvm.fault.code", faultCode);
        activity.SetError();
        var kindTag = new KeyValuePair<string, object?>("mvvm.request.kind", requestKind);
        var faultTag = new KeyValuePair<string, object?>("mvvm.fault.code", faultCode);
        TryAdd(Requests, 1, kindTag, OutcomeTag(outcome));
        TryAdd(RequestFaults, 1, kindTag, faultTag);
        if (capacityLimit is not null)
        {
            BackpressureRejected(capacityLimit, requestKind);
        }
    }

    internal static void BackpressureRejected(string capacityLimit, string? requestKind = null)
    {
        var limitTag = new KeyValuePair<string, object?>("mvvm.limit", capacityLimit);
        if (requestKind is null)
        {
            TryAdd(BackpressureRejections, 1, limitTag);
            return;
        }

        var kindTag = new KeyValuePair<string, object?>("mvvm.request.kind", requestKind);
        TryAdd(BackpressureRejections, 1, limitTag, kindTag);
    }

    internal static void SessionClosed()
    {
        TryAdd(SessionsClosed, 1);
        TryAdd(ActiveSessions, -1);
    }

    private static KeyValuePair<string, object?> OutcomeTag(string outcome) =>
        new("mvvm.outcome", outcome);

    private static ActivitySource? CreateActivitySource()
    {
        try
        {
            return new ActivitySource(MvvmDiagnostics.InstrumentationName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Meter? CreateMeter()
    {
        try
        {
            return new Meter(MvvmDiagnostics.InstrumentationName);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Counter<long>? CreateCounter(string name, string description)
    {
        try
        {
            return Meter?.CreateCounter<long>(name, description: description);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static UpDownCounter<long>? CreateUpDownCounter(string name, string description)
    {
        try
        {
            return Meter?.CreateUpDownCounter<long>(name, description: description);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Histogram<double>? CreateHistogram(string name, string description)
    {
        try
        {
            return Meter?.CreateHistogram<double>(name, "s", description);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryAdd(Counter<long>? counter, long value)
    {
        try
        {
            counter?.Add(value);
        }
        catch (Exception)
        {
            // Diagnostic listeners and exporters are consumer code and cannot affect the runtime.
        }
    }

    private static void TryAdd(
        Counter<long>? counter,
        long value,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            counter?.Add(value, tag);
        }
        catch (Exception)
        {
            // Diagnostic listeners and exporters are consumer code and cannot affect the runtime.
        }
    }

    private static void TryAdd(
        Counter<long>? counter,
        long value,
        KeyValuePair<string, object?> firstTag,
        KeyValuePair<string, object?> secondTag)
    {
        try
        {
            counter?.Add(value, firstTag, secondTag);
        }
        catch (Exception)
        {
            // Diagnostic listeners and exporters are consumer code and cannot affect the runtime.
        }
    }

    private static void TryAdd(UpDownCounter<long>? counter, long value)
    {
        try
        {
            counter?.Add(value);
        }
        catch (Exception)
        {
            // Diagnostic listeners and exporters are consumer code and cannot affect the runtime.
        }
    }

    private static void TryRecord(
        Histogram<double>? histogram,
        double value,
        KeyValuePair<string, object?> tag)
    {
        try
        {
            histogram?.Record(value, tag);
        }
        catch (Exception)
        {
            // Diagnostic listeners and exporters are consumer code and cannot affect the runtime.
        }
    }

    private static void TryRecord(
        Histogram<double>? histogram,
        double value,
        KeyValuePair<string, object?> firstTag,
        KeyValuePair<string, object?> secondTag)
    {
        try
        {
            histogram?.Record(value, firstTag, secondTag);
        }
        catch (Exception)
        {
            // Diagnostic listeners and exporters are consumer code and cannot affect the runtime.
        }
    }
}

/// <summary>Contains an Activity and isolates all listener callbacks from runtime behavior.</summary>
internal sealed class MvvmActivity : IDisposable
{
    private Activity? _activity;

    private MvvmActivity(Activity? activity)
    {
        _activity = activity;
    }

    internal static MvvmActivity Start(ActivitySource? source, string name)
    {
        try
        {
            return new MvvmActivity(source?.StartActivity(name));
        }
        catch (Exception)
        {
            return new MvvmActivity(null);
        }
    }

    internal void SetTag(string name, string value)
    {
        try
        {
            _activity?.SetTag(name, value);
        }
        catch (Exception)
        {
            // A diagnostic listener cannot affect the runtime.
        }
    }

    internal void SetError()
    {
        try
        {
            _activity?.SetStatus(ActivityStatusCode.Error);
        }
        catch (Exception)
        {
            // A diagnostic listener cannot affect the runtime.
        }
    }

    public void Dispose()
    {
        Activity? activity = Interlocked.Exchange(ref _activity, null);
        try
        {
            activity?.Dispose();
        }
        catch (Exception)
        {
            // ActivityStopped callbacks and exporters are consumer code.
        }
    }
}

/// <summary>A closed, generated adapter for one explicitly registered ViewModel contract.</summary>
public interface IMvvmBindingAdapter : IAsyncDisposable
{
    /// <summary>Creates an authoritative full state snapshot.</summary>
    ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Validates and commits one generated-member mutation.</summary>
    /// <remarks>
    /// Committing consumer state and completing the corresponding <see cref="MvvmBindingResult"/>
    /// is one atomic adapter operation. A fault after a commit must complete as
    /// <see cref="MvvmBindingResult.CommittedFailure"/> with the complete patch transaction.
    /// If cancellation wins before result completion, the adapter must not mutate afterward; a
    /// violating late result is discarded and the session is quarantined. An adapter must validate
    /// result limits before mutating because the runtime cannot roll back.
    /// </remarks>
    ValueTask<MvvmBindingResult> DispatchAsync(MvvmMutationRequest request, CancellationToken cancellationToken);
}

/// <summary>Owns one ViewModel, its adapter, ordered dispatch, revisions, and teardown.</summary>
public interface IMvvmSession : IAsyncDisposable
{
    /// <summary>Gets the runtime session identifier.</summary>
    MvvmSessionId Id { get; }

    /// <summary>Gets the registered logical contract.</summary>
    MvvmContract Contract { get; }

    /// <summary>Gets the random per-session invocation capability.</summary>
    string CapabilityToken { get; }

    /// <summary>Compares an invocation capability without data-dependent byte comparison.</summary>
    bool Authorizes(string capabilityToken);

    /// <summary>Gets the current authoritative state revision.</summary>
    long Revision { get; }

    /// <summary>Gets the greatest monotonically acknowledged revision.</summary>
    long? AcknowledgedRevision { get; }

    /// <summary>Dispatches a request using per-session ordering and cancellation semantics.</summary>
    ValueTask<MvvmResponse> DispatchAsync(MvvmRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Creates and owns explicitly registered sessions.</summary>
public interface IMvvmSessionFactory : IAsyncDisposable
{
    /// <summary>Opens a contract in an independently owned lifetime.</summary>
    ValueTask<IMvvmSession> OpenAsync(MvvmContract contract, CancellationToken cancellationToken = default);

    /// <summary>Closes a session. Repeated or unknown closes are harmless.</summary>
    ValueTask<bool> CloseAsync(MvvmSessionId sessionId);
}

/// <summary>The result of activating one explicitly registered contract.</summary>
public sealed class MvvmSessionActivation
{
    private readonly object[] _ownedResources;

    /// <summary>Creates an activation and records resources in creation order.</summary>
    /// <param name="adapter">The generated closed binding adapter.</param>
    /// <param name="ownedResources">
    /// Resources such as scope and ViewModel, in creation order. Each item must implement
    /// <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>.
    /// </param>
    public MvvmSessionActivation(IMvvmBindingAdapter adapter, params object[] ownedResources)
    {
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ArgumentNullException.ThrowIfNull(ownedResources);
        _ownedResources = ownedResources.ToArray();

        if (_ownedResources.Any(static resource => resource is not IAsyncDisposable and not IDisposable))
        {
            throw new ArgumentException("Every session resource must be disposable.", nameof(ownedResources));
        }

        if (_ownedResources.Any(resource => ReferenceEquals(resource, adapter)))
        {
            throw new ArgumentException("The adapter is already owned separately and cannot also be an activation resource.", nameof(ownedResources));
        }
    }

    /// <summary>Gets the generated adapter, which owns binding subscriptions.</summary>
    public IMvvmBindingAdapter Adapter { get; }

    internal object[] OwnedResources => _ownedResources;
}

/// <summary>Activates one registered contract without reflection or runtime discovery.</summary>
/// <param name="cancellationToken">Cancels activation before ownership transfers to the runtime.</param>
public delegate ValueTask<MvvmSessionActivation> MvvmSessionActivator(CancellationToken cancellationToken);
