using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W250-042: while a generated Bridge presents a ReactiveUI ViewModel, it logs each bridged command's unexpected
// exception exactly once through the Window's logger factory, without a ThrownExceptions subscription in the
// ViewModel. An exception that a Bridge call received is the Bridge's to report (event 1000 or 1004), a declared
// failure is reported to the client, and ObserveBridgeExceptions replaces the logging.
internal sealed class ReactiveCommandFailureLoggingTests
{
    private const int BridgeCommandFailed = 1000;
    private const int BridgeOperationFailed = 1004;
    private const int ReactiveCommandFailed = 1042;

    // With operations: the same, with a failing operation as well.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BridgedCommandLogsAnUnexpectedExceptionOnceWithoutASubscriptionAsync(bool operations)
    {
        await using var context = new RunicModelContext();
        var model = new CommandLoggingViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        var logs = new CategoryLog();
        using (var host = Host(model, context, logs))
        {
            // An execution started in .NET, which no Bridge call observes.
            await ExecuteInModelAsync(context, model.FailCommand);
            var logged = await WaitForAsync(logs, 1);
            Require(logged.Category == "Runic.Application.Views" && logged.Level == LogLevel.Error
                    && logged.Error is InvalidOperationException { Message: "fail 1" }
                    && Equals(logged.State.GetValueOrDefault("Source"), "CommandLoggingViewModel.FailCommand"),
                $"The unexpected exception was not logged as event 1042 through the Window's logger factory: {logged}");

            // A Bridge call reports its own failure: the awaited route as event 1000, an operation as event 1004.
            var reply = await host.Root.ExecuteAsync(vm => vm.FailCommand);
            Require(!reply.Succeeded, "The awaited Bridge call of a failing command succeeded.");
            if (operations)
            {
                var operation = await host.Root.Start(vm => vm.FailCommand, requestId: "fail-op").WaitAsync();
                Require(operation.Kind == RunicOperationStatusKind.Failed, $"The failing operation did not fail: {operation.Json}");
            }

            // A declared failure reaches the client and is not logged, from the Bridge or from .NET.
            var declared = await host.Root.ExecuteAsync(vm => vm.DeclinedCommand);
            Require(!declared.Succeeded, "The declared failure succeeded.");
            await ExecuteInModelAsync(context, model.DeclinedCommand);

            // The model context runs scheduled deliveries in order: once this execution is logged, every earlier
            // ThrownExceptions delivery has run.
            await ExecuteInModelAsync(context, model.FailCommand);
            await WaitForAsync(logs, 2);
            Require(logs.Count(BridgeCommandFailed) == 1 && logs.Count(BridgeOperationFailed) == (operations ? 1 : 0),
                $"A Bridge call's failure was not logged exactly once by the Bridge: {logs.Describe()}");
            Require(logs.Count(ReactiveCommandFailed) == 2,
                $"An exception was logged twice, or a reported one again: {logs.Describe()}");

            // Two Bridges that present one ViewModel log an exception once.
            using (Host(model, context, logs))
            {
                await ExecuteInModelAsync(context, model.FailCommand);
                await WaitForAsync(logs, 3);
                await ExecuteInModelAsync(context, model.FailCommand);
                await WaitForAsync(logs, 4);
            }
            await ExecuteInModelAsync(context, model.FailCommand);
            await WaitForAsync(logs, 5);
            Require(logs.Count(ReactiveCommandFailed) == 5, $"A mirrored Bridge logged an exception twice: {logs.Describe()}");

            // ObserveBridgeExceptions replaces the logging while it observes the command.
            var handled = new ConcurrentQueue<Exception>();
            using (model.FailCommand.ObserveBridgeExceptions(handled.Enqueue))
            {
                await ExecuteInModelAsync(context, model.FailCommand);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (handled.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
                Require(handled.Count == 1, "ObserveBridgeExceptions did not receive the exception.");
            }
            await ExecuteInModelAsync(context, model.FailCommand);
            await WaitForAsync(logs, 6);
            Require(logs.Count(ReactiveCommandFailed) == 6,
                $"The Bridge logged an exception that ObserveBridgeExceptions handled: {logs.Describe()}");
        }

        // A detached Bridge no longer observes the command.
        using var detached = model.FailCommand.ObserveBridgeExceptions(_ => { });
        await ExecuteInModelAsync(context, model.FailCommand);
        await context.InvokeAsync(() => { });
        await context.InvokeAsync(() => { });
        Require(logs.Count(ReactiveCommandFailed) == 6, $"A detached Bridge logged an exception: {logs.Describe()}");
    }

    private static RunicWindowTestHost<CommandLoggingViewModel> Host(CommandLoggingViewModel model, RunicModelContext context,
        CategoryLog logs) =>
        new(model, (transport, content, vm) => new CommandLoggingBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions
            {
                RootRoute = "commandLogging", ModelContext = context, LoggerFactory = logs, ViewLocator = new TestViewLocator(),
            });

    private static async Task ExecuteInModelAsync(RunicModelContext context, ReactiveCommand<RxVoid, RxVoid> command) =>
        await context.InvokeAsync(() => command.Execute().Subscribe(new IgnoredResult()));

    private static async Task<LogEntry> WaitForAsync(CategoryLog logs, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (logs.Count(ReactiveCommandFailed) < count && DateTime.UtcNow < deadline) await Task.Delay(10);
        var entries = logs.Entries.Where(entry => entry.Id == ReactiveCommandFailed).ToArray();
        Require(entries.Length >= count, $"Event 1042 was logged {entries.Length} times, expected {count}: {logs.Describe()}");
        return entries[count - 1];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IgnoredResult : IObserver<RxVoid>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(RxVoid value) { }
    }

    private sealed record LogEntry(string Category, LogLevel Level, int Id, Exception? Error, IReadOnlyDictionary<string, object?> State);

    private sealed class CategoryLog : ILoggerFactory
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public int Count(int id) => Entries.Count(entry => entry.Id == id);
        public string Describe() => string.Join(", ", Entries.Select(entry => $"{entry.Id}/{entry.Category}/{entry.Error?.Message}"));
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
        public void Dispose() { }

        private sealed class Logger(CategoryLog owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
                owner.Entries.Enqueue(new(category, logLevel, eventId.Id, exception, values.ToDictionary(pair => pair.Key, pair => pair.Value)));
            }
        }
    }
}
