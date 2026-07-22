using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.MVVM.Tests;

internal static class RuntimeQueueTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    internal static int AssertionCount { get; private set; }

    internal static async Task<int> RunAllAsync()
    {
        AssertionCount = 0;
        int passed = 0;
        await RunAsync(nameof(BclTelemetryIsBoundedAndReportsLifecycle), BclTelemetryIsBoundedAndReportsLifecycle);
        passed++;
        await RunAsync(nameof(ThrowingTelemetrySubscribersAreContained), ThrowingTelemetrySubscribersAreContained);
        passed++;
        await RunAsync(nameof(ConcurrentBurstHonorsExactPendingCapacity), ConcurrentBurstHonorsExactPendingCapacity);
        passed++;
        await RunAsync(nameof(QueuedCancellationPreservesOrderingAndThenReleasesCapacity), QueuedCancellationPreservesOrderingAndThenReleasesCapacity);
        passed++;
        await RunAsync(nameof(CancellationControlBypassesSaturatedQueue), CancellationControlBypassesSaturatedQueue);
        passed++;
        return passed;
    }

    private static async Task BclTelemetryIsBoundedAndReportsLifecycle()
    {
        const string sensitivePayload = "telemetry-secret-payload";
        var activities = new ConcurrentQueue<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == MvvmDiagnostics.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new ConcurrentQueue<Measurement>();
        var instrumentNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MvvmDiagnostics.InstrumentationName)
            {
                instrumentNames.TryAdd(instrument.Name, 0);
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, CopyTags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, CopyTags(tags))));
        meterListener.Start();

        Equal("WebUIToolkit.MVVM", MvvmDiagnostics.InstrumentationName, "instrumentation name");
        Equal("mvvm.session.open", MvvmDiagnostics.SessionOpenActivityName, "session activity name");
        Equal("mvvm.request.dispatch", MvvmDiagnostics.RequestActivityName, "request activity name");

        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new QueueAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                activeEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        IMvvmSessionFactory factory = Factory(adapter, maxPendingRequests: 1);
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("queue"));
        string capabilityToken = session.CapabilityToken;
        MvvmRequestId targetId = Id();
        Task<MvvmResponse> active = session.DispatchAsync(new MvvmMutationRequest(
            targetId,
            MvvmMutationKind.ExecuteCommand,
            0,
            1,
            Json($"\"{sensitivePayload}\""))).AsTask();
        await activeEntered.Task.WaitAsync(TestTimeout);

        MvvmResponse overflow = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.LimitExceeded, overflow.Fault?.Code, "telemetry backpressure response");
        MvvmResponse cancellation = await session.DispatchAsync(new MvvmCancelRequest(Id(), targetId));
        Equal<bool?>(true, cancellation.CancellationAccepted, "telemetry cancellation accepted");
        Equal(MvvmFaultCodes.RequestCancelled, (await active.WaitAsync(TestTimeout)).Fault?.Code, "telemetry cancellation outcome");
        await session.DisposeAsync();
        await factory.DisposeAsync();

        string[] expectedInstruments =
        [
            "mvvm.sessions.opened",
            "mvvm.sessions.closed",
            "mvvm.session.open.failures",
            "mvvm.sessions.active",
            "mvvm.session.open.duration",
            "mvvm.requests",
            "mvvm.request.faults",
            "mvvm.backpressure.rejections",
            "mvvm.requests.active",
            "mvvm.request.duration",
        ];
        foreach (string instrument in expectedInstruments)
        {
            True(instrumentNames.ContainsKey(instrument), $"published instrument {instrument}");
        }

        Measurement[] observedMeasurements = measurements.ToArray();
        True(Sum(observedMeasurements, "mvvm.sessions.opened") >= 1, "opened session counter");
        True(Sum(observedMeasurements, "mvvm.sessions.closed") >= 1, "closed session counter");
        Equal(0d, Sum(observedMeasurements, "mvvm.sessions.active"), "active sessions returns to zero");
        Equal(0d, Sum(observedMeasurements, "mvvm.requests.active"), "active requests returns to zero");
        True(Sum(observedMeasurements, "mvvm.requests") >= 3, "terminal request counter");
        True(Sum(observedMeasurements, "mvvm.request.faults") >= 2, "request fault counter");
        True(
            observedMeasurements.Any(static measurement =>
                measurement.Instrument == "mvvm.backpressure.rejections" &&
                measurement.Value == 1 &&
                measurement.Tags.TryGetValue("mvvm.limit", out string? value) &&
                value == "requests"),
            "request backpressure measurement");
        True(
            observedMeasurements
                .Where(static measurement => measurement.Instrument.EndsWith(".duration", StringComparison.Ordinal))
                .All(static measurement => double.IsFinite(measurement.Value) && measurement.Value >= 0),
            "finite nonnegative durations");

        Activity[] observedActivities = activities.ToArray();
        Activity sessionOpen = Single(
            observedActivities,
            static activity => activity.OperationName == MvvmDiagnostics.SessionOpenActivityName,
            "session-open activity");
        Equal("success", Tag(sessionOpen, "mvvm.outcome"), "session outcome tag");
        True(
            observedActivities.Count(static activity => activity.OperationName == MvvmDiagnostics.RequestActivityName) >= 3,
            "request activities");

        string[] allowedTagNames =
        [
            "mvvm.request.kind",
            "mvvm.outcome",
            "mvvm.fault.code",
            "mvvm.limit",
        ];
        foreach (Activity activity in observedActivities)
        {
            Equal(MvvmDiagnostics.InstrumentationName, activity.Source.Name, "activity source name");
            foreach (KeyValuePair<string, string?> tag in activity.Tags)
            {
                True(allowedTagNames.Contains(tag.Key, StringComparer.Ordinal), $"bounded activity tag {tag.Key}");
                ValidateBoundedTelemetryTag(tag.Key, tag.Value ?? string.Empty);
                False(string.Equals(tag.Value, capabilityToken, StringComparison.Ordinal), "capability token must not be observed");
                False(tag.Value?.Contains(sensitivePayload, StringComparison.Ordinal) == true, "payload must not be observed");
                False(string.Equals(tag.Value, "queue", StringComparison.Ordinal), "contract identity must not be observed");
            }
        }

        foreach (Measurement measurement in observedMeasurements)
        {
            foreach (KeyValuePair<string, string> tag in measurement.Tags)
            {
                True(allowedTagNames.Contains(tag.Key, StringComparer.Ordinal), $"bounded metric tag {tag.Key}");
                ValidateBoundedTelemetryTag(tag.Key, tag.Value);
                False(string.Equals(tag.Value, capabilityToken, StringComparison.Ordinal), "metric must not expose capability");
                False(tag.Value.Contains(sensitivePayload, StringComparison.Ordinal), "metric must not expose payload");
            }
        }
    }

    private static async Task ThrowingTelemetrySubscribersAreContained()
    {
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == MvvmDiagnostics.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = static _ => throw new InvalidOperationException("listener secret"),
            ActivityStopped = static _ => throw new InvalidOperationException("listener secret"),
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == MvvmDiagnostics.InstrumentationName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>(static (_, _, _, _) =>
            throw new InvalidOperationException("listener secret"));
        meterListener.SetMeasurementEventCallback<double>(static (_, _, _, _) =>
            throw new InvalidOperationException("listener secret"));
        meterListener.Start();

        var adapter = new QueueAdapter();
        IMvvmSessionFactory factory = Factory(adapter, maxPendingRequests: 1);
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("queue"));
        MvvmResponse response = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        True(response.Succeeded, "throwing subscribers must not fail dispatch");
        await session.DisposeAsync();
        await factory.DisposeAsync();
    }

    private static async Task ConcurrentBurstHonorsExactPendingCapacity()
    {
        const int capacity = 4;
        const int requestCount = 32;
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new QueueAdapter();
        adapter.Snapshot = async cancellationToken =>
        {
            int concurrent = Interlocked.Increment(ref adapter.Concurrent);
            UpdateMaximum(ref adapter.MaxConcurrent, concurrent);
            int call = Interlocked.Increment(ref adapter.SnapshotCount);
            try
            {
                if (call == 1)
                {
                    firstEntered.TrySetResult();
                    await releaseFirst.Task.WaitAsync(cancellationToken);
                }

                return new MvvmSnapshot(Json("{\"count\":0}"));
            }
            finally
            {
                Interlocked.Decrement(ref adapter.Concurrent);
            }
        };

        await using IMvvmSessionFactory factory = Factory(adapter, capacity);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("queue"));
        var requests = new List<Task<MvvmResponse>>(requestCount)
        {
            session.DispatchAsync(new MvvmSnapshotRequest(Id())).AsTask(),
        };
        await firstEntered.Task.WaitAsync(TestTimeout);

        for (int index = 1; index < requestCount; index++)
        {
            requests.Add(session.DispatchAsync(new MvvmSnapshotRequest(Id())).AsTask());
        }

        // DispatchAsync performs admission synchronously up to the serialized gate. The first
        // capacity requests are therefore admitted before this loop can observe them.
        for (int index = 0; index < capacity; index++)
        {
            False(requests[index].IsCompleted, $"request {index} should be admitted");
        }

        for (int index = capacity; index < requestCount; index++)
        {
            MvvmResponse response = await requests[index].WaitAsync(TestTimeout);
            Equal(MvvmFaultCodes.LimitExceeded, response.Fault?.Code, $"overflow request {index}");
        }

        releaseFirst.TrySetResult();
        MvvmResponse[] admitted = await Task.WhenAll(requests.Take(capacity)).WaitAsync(TestTimeout);
        Equal(capacity, admitted.Count(static response => response.Succeeded), "admitted successes");
        Equal(capacity, adapter.SnapshotCount, "adapter call count");
        Equal(1, adapter.MaxConcurrent, "serialized adapter concurrency");
    }

    private static async Task QueuedCancellationPreservesOrderingAndThenReleasesCapacity()
    {
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new QueueAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                activeEntered.TrySetResult();
                await releaseActive.Task.WaitAsync(cancellationToken);
                return MvvmBindingResult.Success();
            },
        };

        await using IMvvmSessionFactory factory = Factory(adapter, maxPendingRequests: 2);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("queue"));
        Task<MvvmResponse> active = session.DispatchAsync(Mutation(0)).AsTask();
        await activeEntered.Task.WaitAsync(TestTimeout);

        using var queuedCancellation = new CancellationTokenSource();
        Task<MvvmResponse> queued = session.DispatchAsync(
            new MvvmSnapshotRequest(Id()),
            queuedCancellation.Token).AsTask();
        False(queued.IsCompleted, "queued request should consume the second slot");

        MvvmResponse overflow = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.LimitExceeded, overflow.Fault?.Code, "full queue response");

        queuedCancellation.Cancel();
        False(queued.IsCompleted, "queued cancellation must await its FIFO publication point");
        MvvmResponse stillFull = await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 0));
        Equal(MvvmFaultCodes.LimitExceeded, stillFull.Fault?.Code, "cancelled queue entry retains capacity until publication");

        releaseActive.TrySetResult();
        True((await active.WaitAsync(TestTimeout)).Succeeded, "active request should complete");
        MvvmResponse cancelled = await queued.WaitAsync(TestTimeout);
        Equal(MvvmFaultCodes.RequestCancelled, cancelled.Fault?.Code, "queued cancellation response");

        MvvmResponse replacement = await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 1));
        True(replacement.Succeeded, "capacity should release after ordered cancellation publication");
    }

    private static async Task CancellationControlBypassesSaturatedQueue()
    {
        var activeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new QueueAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                activeEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MvvmBindingResult.Success();
            },
        };

        await using IMvvmSessionFactory factory = Factory(adapter, maxPendingRequests: 1);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("queue"));
        MvvmRequestId targetId = Id();
        Task<MvvmResponse> active = session.DispatchAsync(Mutation(0, targetId)).AsTask();
        await activeEntered.Task.WaitAsync(TestTimeout);

        MvvmResponse overflow = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.LimitExceeded, overflow.Fault?.Code, "saturated data request");

        MvvmResponse cancellation = await session.DispatchAsync(new MvvmCancelRequest(Id(), targetId));
        True(cancellation.Succeeded, "cancellation control response");
        Equal<bool?>(true, cancellation.CancellationAccepted, "cancellation accepted");

        MvvmResponse cancelled = await active.WaitAsync(TestTimeout);
        Equal(MvvmFaultCodes.RequestCancelled, cancelled.Fault?.Code, "target cancellation response");
        Equal(0L, session.Revision, "cancelled request revision");
    }

    private static IMvvmSessionFactory Factory(QueueAdapter adapter, int maxPendingRequests)
    {
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("queue"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter)));
        return registry.Build(MvvmLimits.Default with { MaxPendingRequests = maxPendingRequests });
    }

    private static MvvmMutationRequest Mutation(long revision, MvvmRequestId? requestId = null) =>
        new(requestId ?? Id(), MvvmMutationKind.ExecuteCommand, revision, 1, Json("null"));

    private static MvvmRequestId Id() => new(Guid.NewGuid());

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Dictionary<string, string> CopyTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            copy[tag.Key] = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        return copy;
    }

    private static double Sum(IEnumerable<Measurement> measurements, string instrument) =>
        measurements
            .Where(measurement => string.Equals(measurement.Instrument, instrument, StringComparison.Ordinal))
            .Sum(static measurement => measurement.Value);

    private static string? Tag(Activity activity, string name) =>
        activity.Tags.FirstOrDefault(tag => string.Equals(tag.Key, name, StringComparison.Ordinal)).Value;

    private static void ValidateBoundedTelemetryTag(string name, string value)
    {
        switch (name)
        {
            case "mvvm.request.kind":
                True(
                    value is "setProperty" or "execute" or "requestSnapshot" or "ack" or "cancel" or "invalid",
                    $"bounded request kind {value}");
                break;
            case "mvvm.outcome":
                True(
                    value is "success" or "fault" or "cancelled" or "closed" or "rejected" or
                        "contract_unknown" or "limit_exceeded" or "activation_failed" or "disposed",
                    $"bounded outcome {value}");
                break;
            case "mvvm.fault.code":
                True(MvvmFaultCodes.IsDefined(value), $"bounded fault code {value}");
                break;
            case "mvvm.limit":
                True(value is "requests" or "sessions", $"bounded limit {value}");
                break;
            default:
                throw new InvalidOperationException($"Unexpected telemetry tag '{name}'.");
        }
    }

    private static T Single<T>(IEnumerable<T> values, Func<T, bool> predicate, string description)
    {
        T[] matches = values.Where(predicate).ToArray();
        Equal(1, matches.Length, description);
        return matches[0];
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        int observed = target;
        while (value > observed)
        {
            int exchanged = Interlocked.CompareExchange(ref target, value, observed);
            if (exchanged == observed)
            {
                return;
            }

            observed = exchanged;
        }
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        Console.WriteLine($"PASS {name}");
    }

    private static void True(bool value, string description)
    {
        AssertionCount++;
        if (!value)
        {
            throw new InvalidOperationException($"Expected true: {description}.");
        }
    }

    private static void False(bool value, string description) => True(!value, description);

    private static void Equal<T>(T expected, T actual, string description)
    {
        AssertionCount++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Expected '{expected}', got '{actual}': {description}.");
        }
    }

    private sealed class QueueAdapter : IMvvmBindingAdapter
    {
        internal Func<CancellationToken, ValueTask<MvvmSnapshot>> Snapshot { get; set; } =
            _ => ValueTask.FromResult(new MvvmSnapshot(Json("{}")));

        internal Func<MvvmMutationRequest, CancellationToken, ValueTask<MvvmBindingResult>> Dispatch { get; set; } =
            (_, _) => ValueTask.FromResult(MvvmBindingResult.Success());

        internal int SnapshotCount;
        internal int Concurrent;
        internal int MaxConcurrent;

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Snapshot(cancellationToken);

        public ValueTask<MvvmBindingResult> DispatchAsync(
            MvvmMutationRequest request,
            CancellationToken cancellationToken) => Dispatch(request, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record Measurement(
        string Instrument,
        double Value,
        IReadOnlyDictionary<string, string> Tags);
}
