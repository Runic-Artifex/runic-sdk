using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Application.Views;

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
        Require(reply.RootElement.GetProperty("error").GetProperty("kind").GetString() == "failed",
            $"The command did not fail: {reply.RootElement}");

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
        if (development)
            Require(entry.Exception is InvalidOperationException, "A development entry did not carry its exception.");
        else
            Require(entry.Exception is null, "A production entry carried exception detail.");

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
        Require(entry.State["Member"] as string == "FailAsync" && entry.Exception is null,
            $"The operation entry was wrong: {string.Join(", ", entry.State)}");
        Require(telemetry.Activities.Any(activity => activity.OperationName == "runic.bridge.operation.start"
            && activity.GetTagItem("runic.bridge.outcome") as string == "ok"),
            "The operation admission span was not recorded.");
        Require(telemetry.Activities.Any(activity => activity.OperationName == "runic.bridge.operation"
            && activity.Status == ActivityStatusCode.Error),
            "The failed operation span was not recorded.");
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
        Require(entry.Category == typeof(RunicModelContext).FullName && entry.Exception is null,
            $"The model context entry was wrong: {entry.Category}");
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
