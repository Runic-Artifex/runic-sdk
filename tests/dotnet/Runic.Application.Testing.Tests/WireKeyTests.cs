using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Runic.Application.Testing;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// #472: a RunicCommandKey command takes a wire key; the Bridge resolves it to the command's parameter
// (decode key → resolve → CanExecute on the resolved object → execute). A key that names no object is
// rejected with reason unresolved-key and runs nothing.
internal sealed class WireKeyTests
{
    private const int ReactiveCommandFailed = 1042;

    [Test]
    public async Task ReactiveCommandResolvesItsKeyInTheModelTurnAsync()
    {
        await using var context = new RunicModelContext();
        var model = new KeyedReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        var logs = new EventLog();
        using var host = Host(model, context, logs);

        var deleted = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, "\"1\"");
        Require(deleted.Succeeded && model.Last == "deleted first", $"The keyed command did not run with its item: {deleted}");
        Require(model.Store.ResolvedInTurn.Count > 0 && model.Store.ResolvedInTurn.All(inTurn => inTurn),
            "The resolver did not run in the Bridge call's model turn.");

        // The key now names no object.
        var unresolved = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, "\"1\"");
        // A rejected request did not run, so its reply has no revision (and the driver reads no state for it).
        Require(unresolved is { Succeeded: false, ErrorKind: "rejected", ErrorReason: "unresolved-key", Revision: null }
            && unresolved.ErrorMessage == "Delete has no object for this key.",
            $"An unresolved key was not rejected with reason unresolved-key: {unresolved}");
        Require(model.Last == "deleted first", "The command ran for an unresolved key.");

        // CanExecute is evaluated after resolution.
        model.Enabled = false;
        var unavailable = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, "\"3\"");
        Require(unavailable is { ErrorKind: "rejected", ErrorReason: "unavailable" }, $"A disabled keyed command ran: {unavailable}");
        var unavailableUnresolved = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, "\"77\"");
        Require(unavailableUnresolved.ErrorReason == "unresolved-key", "Availability was checked before the key was resolved.");
        Require(!host.Root.CanExecute(vm => vm.DeleteCommand, "\"3\""), "can… ignored the command's CanExecute.");
        model.Enabled = true;
        Require(host.Root.CanExecute(vm => vm.DeleteCommand, "\"3\""), "can… was false for a resolved key.");
        Require(!host.Root.CanExecute(vm => vm.DeleteCommand, "\"77\""), "can… was true for an unresolved key.");

        // Resolver exceptions use the Bridge's usual classification.
        var failed = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, $"\"{KeyedItemStore.Throws}\"");
        Require(failed.ErrorKind == "failed", $"A throwing resolver was not a failure: {failed}");
        var invalid = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, $"\"{KeyedItemStore.Rejects}\"");
        Require(invalid is { ErrorKind: "rejected", ErrorReason: "invalid-input" }, $"An ArgumentException from the resolver was not invalid-input: {invalid}");
        var malformed = await host.Root.ExecuteAsync(vm => vm.DeleteCommand, "\"not a number\"");
        Require(malformed.ErrorReason == "invalid-input", $"A key that does not decode was not invalid-input: {malformed}");

        Require(logs.Count(ReactiveCommandFailed) == 0, $"A keyed command logged event 1042: {logs.Describe()}");
    }

    [Test]
    public async Task AsynchronousResolverRunsBeforeAvailabilityAndExecutionAsync()
    {
        await using var context = new RunicModelContext();
        var model = new KeyedReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        var logs = new EventLog();
        using var host = Host(model, context, logs);

        var archived = await host.Root.ExecuteAsync(vm => vm.ArchiveCommand, "\"1\"");
        Require(archived.Succeeded && model.Last == "archived first", $"The asynchronous resolver did not resolve: {archived}");
        Require(model.Store.ResolvedInTurn.All(inTurn => inTurn), "The asynchronous resolver did not start in a model turn.");
        var unresolved = await host.Root.ExecuteAsync(vm => vm.ArchiveCommand, "\"77\"");
        Require(unresolved.ErrorReason == "unresolved-key", $"An unresolved asynchronous key was not rejected: {unresolved}");
        Require(await host.Root.CanExecuteAsync(vm => vm.ArchiveCommand, "\"3\"") && !await host.Root.CanExecuteAsync(vm => vm.ArchiveCommand, "\"77\""),
            "can… of an asynchronous resolver did not follow the resolution.");
        // Protocol 4: the synchronous can request waits for an asynchronous resolver (it no longer refuses it); a resolver
        // that waits on the fake clock needs CanExecuteAsync.
        Require(host.Root.CanExecute(vm => vm.ArchiveCommand, "\"3\"") && !host.Root.CanExecute(vm => vm.ArchiveCommand, "\"77\""),
            "The synchronous can… of an asynchronous resolver did not follow the resolution.");
        // The asynchronous calls answer a synchronous resolver too.
        Require(await host.Root.CanExecuteAsync(vm => vm.DeleteCommand, "\"3\"") && !await host.Root.CanExecuteAsync(vm => vm.DeleteCommand, "\"77\""),
            "CanExecuteAsync did not answer a synchronous resolver.");
        model.Enabled = false;
        var unavailable = await host.Root.ExecuteAsync(vm => vm.ArchiveCommand, "\"3\"");
        Require(unavailable.ErrorReason == "unavailable", $"A disabled command ran after an asynchronous resolution: {unavailable}");
        model.Enabled = true;

        // Cancelling the call cancels the resolution; the command does not run.
        model.Store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Store.ResetResolutionStarted();
        using (var cancellation = new CancellationTokenSource())
        {
            var call = host.Root.ExecuteAsync(vm => vm.ArchiveCommand, "\"3\"", cancellation.Token);
            await model.Store.ResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            var cancelled = await call.WaitAsync(TimeSpan.FromSeconds(10));
            Require(cancelled.ErrorKind == "cancelled" && model.Store.ResolutionCancelled && model.Last == "archived first",
                $"A cancelled resolution did not cancel the call: {cancelled}");
        }

        // Cancelling CanExecuteAsync cancels the resolution.
        model.Store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        model.Store.ResetResolutionStarted();
        using (var cancellation = new CancellationTokenSource())
        {
            var can = host.Root.CanExecuteAsync(vm => vm.ArchiveCommand, "\"3\"", cancellation.Token);
            await model.Store.ResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await cancellation.CancelAsync();
            var cancelled = false;
            try { await can.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && model.Store.ResolutionCancelled, "A cancelled CanExecuteAsync did not cancel the resolution.");
        }
        model.Store.Gate = null;
        Require(logs.Count(ReactiveCommandFailed) == 0, $"A keyed command logged event 1042: {logs.Describe()}");
    }

    [Test]
    public async Task ClosingTheWindowCancelsAResolutionAsync()
    {
        await using var context = new RunicModelContext();
        var model = new KeyedReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = Host(model, context, new EventLog());
        model.Store.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var call = host.Root.ExecuteAsync(vm => vm.ArchiveCommand, "\"1\"");
        await model.Store.ResolutionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var drain = host.DrainAsync(TimeSpan.Zero);
        var reply = await call.WaitAsync(TimeSpan.FromSeconds(10));
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
        Require(!reply.Succeeded && model.Store.ResolutionCancelled && model.Last.Length == 0,
            $"Closing the window did not cancel the resolution: {reply}");
    }

    [Test]
    public async Task OperationStartsResolveTheirKeyAsync()
    {
        await using var context = new RunicModelContext();
        var model = new KeyedReactiveViewModel(context);
        using var lease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = Host(model, context, new EventLog());

        var unresolved = host.Root.Start(vm => vm.RenameCommand, "\"77\"");
        Require(unresolved is { Admission: RunicOperationAdmissionKind.Rejected, Reason: "unresolved-key" },
            $"An unresolved operation start was admitted: {unresolved.AdmissionJson}");

        // A repeated request id observes its operation, even when the key no longer resolves.
        var started = host.Root.Start(vm => vm.RenameCommand, "\"3\"", requestId: "rename-3");
        Require(started.Admission is RunicOperationAdmissionKind.Accepted, $"The keyed operation was not admitted: {started.AdmissionJson}");
        var status = await started.WaitAsync();
        Require(status.Kind is RunicOperationStatusKind.Succeeded && status.Result?.GetString() == "THIRD",
            $"The keyed operation did not run with its item: {status.Json}");
        model.Store.Remove(3);
        var duplicate = host.Root.Start(vm => vm.RenameCommand, "\"3\"", requestId: "rename-3");
        Require(duplicate.Admission is RunicOperationAdmissionKind.Duplicate, $"A repeated start was resolved again: {duplicate.AdmissionJson}");
        var fresh = host.Root.Start(vm => vm.RenameCommand, "\"3\"", requestId: "rename-3-again");
        Require(fresh.Reason == "unresolved-key", $"A new start with a removed key was admitted: {fresh.AdmissionJson}");

        model.Enabled = false;
        var unavailable = host.Root.Start(vm => vm.RenameCommand, "\"1\"");
        Require(unavailable.Reason == "unavailable", $"A disabled keyed operation was admitted: {unavailable.AdmissionJson}");
        model.Enabled = true;

        // An asynchronous resolver's start route.
        var asyncUnresolved = await host.Root.StartAsync(vm => vm.ArchiveCommand, "\"77\"");
        Require(asyncUnresolved.Reason == "unresolved-key", $"An unresolved asynchronous start was admitted: {asyncUnresolved.AdmissionJson}");
        var asyncStarted = await host.Root.StartAsync(vm => vm.ArchiveCommand, "\"2\"", requestId: "archive-2");
        Require(asyncStarted.Admission is RunicOperationAdmissionKind.Accepted, $"The asynchronous keyed start failed: {asyncStarted.AdmissionJson}");
        Require((await asyncStarted.WaitAsync()).Kind is RunicOperationStatusKind.Succeeded && model.Last == "archived second",
            "The asynchronous keyed operation did not run.");
        model.Store.Remove(2);
        var asyncDuplicate = await host.Root.StartAsync(vm => vm.ArchiveCommand, "\"2\"", requestId: "archive-2");
        Require(asyncDuplicate.Admission is RunicOperationAdmissionKind.Duplicate, $"A repeated asynchronous start was rejected: {asyncDuplicate.AdmissionJson}");

        // A streamed result.
        var export = host.Root.Start(vm => vm.ExportCommand, "\"1\"");
        Require((await export.WaitAsync()).Kind is RunicOperationStatusKind.Succeeded, "The keyed stream did not complete.");
        var letters = string.Concat(export.ReadStream().Items.Select(item => item.GetString()));
        Require(letters == "first", $"The keyed stream produced '{letters}'.");
    }

    [Test]
    public async Task ToolkitAndPlainCommandsSeeTheResolvedObjectAsync()
    {
        var toolkit = new KeyedToolkitViewModel();
        using (var host = new RunicWindowTestHost<KeyedToolkitViewModel>(toolkit,
            (transport, content, vm) => new KeyedToolkitBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() }))
        {
            Require(host.Root.CanExecute(vm => vm.LockCommand, "\"1\""), "CanExecute(item) was false for an unlocked item.");
            Require(!host.Root.CanExecute(vm => vm.LockCommand, "\"2\""), "CanExecute did not see the resolved, locked item.");
            Require(!host.Root.CanExecute(vm => vm.LockCommand, "\"77\""), "can… was true for an unresolved key.");
            var locked = await host.Root.ExecuteAsync(vm => vm.LockCommand, "\"1\"");
            Require(locked.Succeeded && toolkit.Last == "locked first", $"The keyed Toolkit command did not run: {locked}");
            var refused = await host.Root.ExecuteAsync(vm => vm.LockCommand, "\"2\"");
            Require(refused.ErrorReason == "unavailable", $"CanExecute on the resolved item was ignored: {refused}");
            var missing = await host.Root.ExecuteAsync(vm => vm.LockCommand, "\"77\"");
            Require(missing.ErrorReason == "unresolved-key", $"An unresolved Toolkit key was not rejected: {missing}");

            var opened = await host.Root.ExecuteAsync(vm => vm.OpenCommand, "\"3\"");
            Require(opened.Succeeded && toolkit.Last == "opened third", $"The async Toolkit command did not run: {opened}");
            var openLocked = await host.Root.ExecuteAsync(vm => vm.OpenCommand, "\"2\"");
            Require(openLocked.ErrorReason == "unavailable", $"The async Toolkit command ignored CanExecute(item): {openLocked}");

            // The attribute on a [RelayCommand] method, with a static resolver and a string key.
            var selected = await host.Root.ExecuteAsync(vm => vm.SelectCommand, "\"fixed\"");
            Require(selected.Succeeded && toolkit.Last == "selected fixed", $"The [RelayCommand] keyed command did not run: {selected}");
            var other = await host.Root.ExecuteAsync(vm => vm.SelectCommand, "\"other\"");
            Require(other.ErrorReason == "unresolved-key", $"An unresolved [RelayCommand] key was not rejected: {other}");
        }

        var plain = new KeyedPlainViewModel();
        using (var host = new RunicWindowTestHost<KeyedPlainViewModel>(plain,
            (transport, content, vm) => new KeyedPlainBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() }))
        {
            var picked = await host.Root.ExecuteAsync(vm => vm.PickCommand, "2");
            Require(picked.Succeeded && plain.Picked == 20, $"The plain keyed command did not run with its value: {picked}");
            var unavailable = await host.Root.ExecuteAsync(vm => vm.PickCommand, "3");
            Require(unavailable.ErrorReason == "unavailable", $"The plain command's CanExecute did not see the resolved value: {unavailable}");
            var missing = await host.Root.ExecuteAsync(vm => vm.PickCommand, "9");
            Require(missing.ErrorReason == "unresolved-key" && plain.Picked == 20, $"An unresolved plain key was not rejected: {missing}");
        }
    }

    [Test]
    public async Task AnUnresolvedToolkitStartIsRejectedAsync()
    {
        using var host = new RunicWindowTestHost<KeyedToolkitViewModel>(new KeyedToolkitViewModel(),
            (transport, content, vm) => new KeyedToolkitBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var start = await host.Root.StartAsync(vm => vm.OpenCommand, "\"77\"");
        Require(start.Reason == "unresolved-key", $"An unresolved Toolkit start was admitted: {start.AdmissionJson}");
    }

    private static RunicWindowTestHost<KeyedReactiveViewModel> Host(KeyedReactiveViewModel model, RunicModelContext context, EventLog logs) =>
        new(model, (transport, content, vm) => new KeyedReactiveBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, LoggerFactory = logs, ViewLocator = new TestViewLocator() });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class EventLog : ILoggerFactory
    {
        private readonly ConcurrentQueue<(int Id, string Message)> _entries = new();
        public int Count(int id) => _entries.Count(entry => entry.Id == id);
        public string Describe() => string.Join(", ", _entries.Select(entry => $"{entry.Id}: {entry.Message}"));
        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public ILogger CreateLogger(string categoryName) => new Logger(this);
        public void Dispose() { }

        private sealed class Logger(EventLog owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => owner._entries.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }
}
