using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// NET-2: a configured ILoggerFactory receives structured failure entries, and the
// Views ActivitySource and Meter report calls, failures and snapshot frames.
internal static class TelemetryTests
{
    public static async Task RunAsync()
    {
        var previous = BridgeDiagnostics.IncludeFailureDetail;
        try
        {
            BridgeDiagnostics.IncludeFailureDetail = false;
            await FailingCommandIsLoggedTracedAndMeasuredAsync(development: false);
            BridgeDiagnostics.IncludeFailureDetail = true;
            await FailingCommandIsLoggedTracedAndMeasuredAsync(development: true);
            BridgeDiagnostics.IncludeFailureDetail = false;
            await FailingOperationIsLoggedAndTracedAsync();
            await SnapshotFramesAreMeasuredAsync();
            ModelContextLogsUnhandledTurns();
            ReactiveRoutedRegionLogsFailures();
            ReactiveRoutedRegionTracesFailuresWithoutLoggerFactory();
        }
        finally
        {
            BridgeDiagnostics.IncludeFailureDetail = previous;
        }
    }

    private static async Task FailingCommandIsLoggedTracedAndMeasuredAsync(bool development)
    {
        using var telemetry = new TelemetryCapture();
        var logs = new LogCapture();
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        using var content = new WindowContentSession(transport, rootModel: model, loggerFactory: logs);
        using var bridge = new FailingBridge(transport, model, content);

        using var reply = JsonDocument.Parse(transport.Call("failingThrow"));
        var error = reply.RootElement.GetProperty("error");
        Require(error.GetProperty("kind").GetString() == "failed", $"The command did not fail: {reply.RootElement}");
        // D-12: the reply follows D-1, but the operator's log always has the exception.
        Require(error.TryGetProperty("detail", out _) == development,
            $"The reply detail did not follow BridgeDiagnostics: {reply.RootElement}");

        var entry = logs.Entries.SingleOrDefault(entry => entry.EventId.Id == 1000)
            ?? throw new InvalidOperationException($"No BridgeCommandFailed entry was logged: {string.Join(", ", logs.Entries.Select(e => e.EventId))}");
        Require(entry.Category == RunicViewsTelemetry.LogCategory, $"The entry used category {entry.Category}.");
        Require(entry.Level == LogLevel.Error && entry.EventId.Name == "BridgeCommandFailed", "The entry has the wrong level or name.");
        Require(entry.State["Model"] as string == nameof(Model) && entry.State["Member"] as string == "Throw"
            && entry.State["Route"] as string == "failing"
            && entry.State["ErrorType"] as string == typeof(InvalidOperationException).FullName,
            $"The entry was not structured: {string.Join(", ", entry.State)}");
        Require(!entry.Message.Contains("customer-secret", StringComparison.Ordinal),
            $"The log message leaked the exception message: {entry.Message}");
        Require(entry.Exception is InvalidOperationException { Message: "Disk full for customer-secret." },
            "The entry did not carry its exception.");

        var activity = telemetry.Activities.SingleOrDefault(activity => activity.OperationName == "runic.bridge.command")
            ?? throw new InvalidOperationException("No runic.bridge.command activity was recorded.");
        Require(activity.Status == ActivityStatusCode.Error, "The failed command span was not an error.");
        Require(activity.GetTagItem("runic.bridge.member") as string == "Throw"
            && activity.GetTagItem("runic.bridge.model") as string == nameof(Model)
            && activity.GetTagItem("runic.bridge.outcome") as string == "failed"
            && activity.GetTagItem("error.type") as string == typeof(InvalidOperationException).FullName,
            $"The command span tags were wrong: {string.Join(", ", activity.TagObjects)}");

        Require(telemetry.Sum("runic.bridge.calls", ("runic.bridge.outcome", "failed"), ("runic.bridge.member", "Throw")) == 1,
            "runic.bridge.calls did not count the failed command.");
        Require(telemetry.Sum("runic.bridge.failures", ("runic.bridge.kind", "command"),
            ("error.type", typeof(InvalidOperationException).FullName)) == 1,
            "runic.bridge.failures did not count the failed command.");
        Require(telemetry.Count("runic.bridge.call.duration") >= 1, "runic.bridge.call.duration was not recorded.");
        await Task.CompletedTask;
    }

    private static async Task FailingOperationIsLoggedAndTracedAsync()
    {
        using var telemetry = new TelemetryCapture();
        var logs = new LogCapture();
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        using var content = new WindowContentSession(transport, rootModel: model, loggerFactory: logs);
        using var bridge = new FailingBridge(transport, model, content);

        using var admission = JsonDocument.Parse(transport.Call("failingStartFailAsync", new(StringValue: "telemetry-failure")));
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        using var terminal = JsonDocument.Parse(await transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId = "telemetry-failure" }))));

        var entry = logs.Entries.SingleOrDefault(entry => entry.EventId.Id == 1004)
            ?? throw new InvalidOperationException("No BridgeOperationFailed entry was logged.");
        Require(entry.State["Member"] as string == "FailAsync" && entry.Exception is InvalidOperationException,
            $"The operation entry was wrong: {string.Join(", ", entry.State)}");
        Require(telemetry.Activities.Any(activity => activity.OperationName == "runic.bridge.operation.start"
            && activity.GetTagItem("runic.bridge.outcome") as string == "ok"),
            "The operation admission span was not recorded.");
        Require(telemetry.Activities.Any(activity => activity.OperationName == "runic.bridge.operation"
            && activity.Status == ActivityStatusCode.Error),
            "The failed operation span was not recorded.");

        using var duplicate = JsonDocument.Parse(transport.Call("failingStartFailAsync", new(StringValue: "telemetry-failure")));
        Require(telemetry.Activities.Any(activity => activity.OperationName == "runic.bridge.operation.start"
            && activity.GetTagItem("runic.bridge.outcome") as string != "ok"),
            $"A repeated admission was reported as ok: {duplicate.RootElement}");
    }

    private static async Task SnapshotFramesAreMeasuredAsync()
    {
        using var telemetry = new TelemetryCapture();
        var model = new Model();
        using var transport = new InMemoryViewTransport();
        using var content = new WindowContentSession(transport, rootModel: model, loggerFactory: new LogCapture());
        using var bridge = new FailingBridge(transport, model, content);
        _ = transport.Call("failingSetCount", new(Int64Value: 3));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (telemetry.Sum("runic.bridge.snapshot.frames", ("runic.bridge.frame", "state")) == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Require(telemetry.Sum("runic.bridge.snapshot.frames", ("runic.bridge.frame", "state"), ("runic.bridge.model", nameof(Model))) >= 1,
            "runic.bridge.snapshot.frames did not count the delivered state.");
        Require(telemetry.Count("runic.bridge.snapshot.size") >= 1, "runic.bridge.snapshot.size was not recorded.");
        Require(telemetry.Count("runic.bridge.snapshot.delivery.duration") >= 1, "The delivery duration was not recorded.");
        Require(telemetry.Sum("runic.bridge.calls", ("runic.bridge.kind", "set"), ("runic.bridge.outcome", "ok")) == 1,
            "The setter call was not counted.");
        telemetry.Observe();
        Require(telemetry.Count("runic.bridge.snapshot.queue.depth") == 1
            && telemetry.Sum("runic.bridge.snapshot.queue.depth") >= 0,
            "A listener could not observe the delivery queue depth.");
    }

    private static void ModelContextLogsUnhandledTurns()
    {
        var logs = new LogCapture();
        var context = new RunicModelContext(logs.CreateLogger<RunicModelContext>());
        var done = new ManualResetEventSlim();
        context.TryPost(() => throw new InvalidOperationException("customer-secret"));
        context.TryPost(done.Set);
        Require(done.Wait(TimeSpan.FromSeconds(5)), "The model context did not run its posted turns.");
        context.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var entry = logs.Entries.SingleOrDefault(entry => entry.EventId.Id == 1030)
            ?? throw new InvalidOperationException("No ModelTurnFailed entry was logged.");
        Require(entry.Category == typeof(RunicModelContext).FullName && entry.Exception is InvalidOperationException,
            $"The model context entry was wrong: {entry.Category}");
    }

    // W130-047: a ReactiveUI routed region logs an incompatible route and a
    // failed router as Views events, with the router's exception attached.
    private static void ReactiveRoutedRegionLogsFailures()
    {
        var logs = new LogCapture();
        var router = new RoutingState();
        using (var region = new ReactiveRoutedRegion<PageViewModel>(router, logs))
        {
            router.Navigate.Execute(new OtherViewModel()).Subscribe(_ => { });
            Require(region.Current is null, "An incompatible route was presented.");
        }
        var incompatible = logs.Entries.SingleOrDefault(entry => entry.EventId.Id == 1040)
            ?? throw new InvalidOperationException($"No RoutedRegionRouteIncompatible entry was logged: {string.Join(", ", logs.Entries.Select(e => e.EventId))}");
        Require(incompatible.Category == RunicViewsTelemetry.LogCategory && incompatible.Level == LogLevel.Error
            && incompatible.EventId.Name == "RoutedRegionRouteIncompatible"
            && incompatible.State["Region"] as string == nameof(PageViewModel)
            && incompatible.State["Model"] as string == nameof(OtherViewModel),
            $"The incompatible route entry was wrong: {incompatible.Category} {string.Join(", ", incompatible.State)}");

        var routes = new FailingRoutes();
        using (new ReactiveRoutedRegion<PageViewModel>(routes, logs))
            routes.Fail(new InvalidOperationException("Router failed for customer-secret."));
        var failed = logs.Entries.SingleOrDefault(entry => entry.EventId.Id == 1041)
            ?? throw new InvalidOperationException("No RoutedRegionRouterFailed entry was logged.");
        Require(failed.Category == RunicViewsTelemetry.LogCategory && failed.Level == LogLevel.Error
            && failed.EventId.Name == "RoutedRegionRouterFailed"
            && failed.State["Region"] as string == nameof(PageViewModel)
            && failed.State["ErrorType"] as string == typeof(InvalidOperationException).FullName,
            $"The router failure entry was not structured: {string.Join(", ", failed.State)}");
        // D-12: the exception is attached in every environment.
        Require(failed.Exception is InvalidOperationException { Message: "Router failed for customer-secret." },
            "The router failure entry did not carry the exception.");
    }

    // W130-050: without an ILoggerFactory the region writes the same messages,
    // and the router's exception, to Trace.
    private static void ReactiveRoutedRegionTracesFailuresWithoutLoggerFactory()
    {
        var trace = new TraceCapture();
        Trace.Listeners.Add(trace);
        try
        {
            var router = new RoutingState();
            using (var region = new ReactiveRoutedRegion<PageViewModel>(router))
            {
                router.Navigate.Execute(new OtherViewModel()).Subscribe(_ => { });
                Require(region.Current is null, "An incompatible route was presented.");
            }
            var routes = new FailingRoutes();
            using (new ReactiveRoutedRegion<PageViewModel>(routes, loggerFactory: null))
                routes.Fail(new InvalidOperationException("Router failed for customer-secret."));
        }
        finally
        {
            Trace.Listeners.Remove(trace);
        }
        // The listener is global, so keep only this region's entries; earlier
        // suites can still write unrelated Trace output in the background.
        var entries = trace.Entries
            .Where(entry => entry.Message.Contains($"routed region for {nameof(PageViewModel)}", StringComparison.Ordinal))
            .ToArray();
        Require(entries.Length == 2 && entries.All(entry => entry.Type == TraceEventType.Error),
            $"The Trace fallback wrote unexpected entries: {string.Join(" | ", entries)}");
        Require(entries[0].Message == $"A routed region for {nameof(PageViewModel)} received {nameof(OtherViewModel)}, " +
                "which it cannot present; the region presents no content.",
            $"The incompatible route Trace entry was wrong: {entries[0].Message}");
        Require(entries[1].Message.StartsWith($"The router of a routed region for {nameof(PageViewModel)} failed with " +
                $"{typeof(InvalidOperationException).FullName}; the region keeps its last content. " +
                $"{typeof(InvalidOperationException).FullName}: Router failed for customer-secret.", StringComparison.Ordinal),
            $"The router failure Trace entry did not carry the message and exception: {entries[1].Message}");
    }

    private sealed class TraceCapture : TraceListener
    {
        public ConcurrentQueue<(TraceEventType Type, string Message)> Entries { get; } = new();
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message) =>
            Entries.Enqueue((eventType, message ?? string.Empty));
        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
            string? format, params object?[]? args) =>
            Entries.Enqueue((eventType, args is null ? format ?? string.Empty : string.Format(CultureInfo.InvariantCulture, format ?? string.Empty, args)));
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { }
    }
    private sealed class PageViewModel : ReactiveObject, IRoutableViewModel
    {
        public string UrlPathSegment => "page";
        public IScreen HostScreen => throw new NotSupportedException();
    }

    private sealed class OtherViewModel : ReactiveObject, IRoutableViewModel
    {
        public string UrlPathSegment => "other";
        public IScreen HostScreen => throw new NotSupportedException();
    }

    private sealed class FailingRoutes : IObservable<IRoutableViewModel?>
    {
        private IObserver<IRoutableViewModel?>? _observer;
        public IDisposable Subscribe(IObserver<IRoutableViewModel?> observer)
        {
            _observer = observer;
            return new Unsubscriber(this);
        }
        public void Fail(Exception error) => _observer?.OnError(error);
        private sealed class Unsubscriber(FailingRoutes owner) : IDisposable
        {
            public void Dispose() => owner._observer = null;
        }
    }

    private sealed class Model : INotifyPropertyChanged
    {
        private int _count;
        public Model() => Throw = new DelegateCommand(() => throw new InvalidOperationException("Disk full for customer-secret."));
        public ICommand Throw { get; }
        public int Count
        {
            get => _count;
            set { _count = value; PropertyChanged?.Invoke(this, new(nameof(Count))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class FailingBridge(IBridgeTransport transport, Model model, WindowContentSession content)
        : ViewModelBridge<Model>(transport, model, "failing", (writer, vm, revision) =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("revision", revision);
            writer.WriteNumber("count", vm.Count);
            writer.WriteEndObject();
        },
        [new("Count", vm => vm.Count, (vm, e) => vm.Count = checked((int)e.GetInt64()))],
        [
            new("Throw", vm => vm.Throw),
            new("FailAsync", vm => vm.Throw,
                ExecuteAsync: (_, _, _) => Task.FromException(new InvalidOperationException("Operation failed for customer-secret."))),
        ],
        contractFingerprint: "telemetry", content: content);

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message,
        Exception? Exception, IReadOnlyDictionary<string, object?> State);

    private sealed class LogCapture : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose()
        {
        }

        private sealed class Logger(LogCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
                owner.Entries.Enqueue(new(category, logLevel, eventId, formatter(state, exception), exception,
                    values.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }

    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

        public TelemetryCapture()
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == RunicViewsTelemetry.ActivitySourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = Activities.Enqueue,
            };
            ActivitySource.AddActivityListener(_activities);
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RunicViewsTelemetry.MeterName) listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                _measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                _measurements.Enqueue((instrument.Name, value, tags.ToArray())));
            _meters.Start();
        }

        public ConcurrentQueue<Activity> Activities { get; } = new();

        public double Sum(string instrument, params (string Key, string? Value)[] tags) =>
            Matching(instrument, tags).Sum(measurement => measurement.Value);

        public void Observe() => _meters.RecordObservableInstruments();

        public int Count(string instrument) => Matching(instrument, []).Count();

        private IEnumerable<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> Matching(
            string instrument, (string Key, string? Value)[] tags) =>
            _measurements.Where(measurement => measurement.Name == instrument && tags.All(tag =>
                measurement.Tags.Any(pair => pair.Key == tag.Key && Equals(pair.Value as string, tag.Value))));

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
