using System.Text.Json;
using ReactiveUI;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class TypedReactiveTests
{
    [Test]
    [Skip("P5: needs start, watch, cancel and stream served")]
    public async Task TypedReactiveCommandsAdmitRecoverAndCompleteAsync()
    {
        await using var context = new RunicModelContext();
        var model = new TypedReactiveViewModel(context);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = Host(model);
        var driver = host.Root;

        const string requestId = "typed-save-1";
        var accepted = driver.Start(vm => vm.SaveCommand, Input("first body"), requestId);
        Require(accepted.Admission == RunicOperationAdmissionKind.Accepted,
            $"The typed ReactiveUI command was not admitted: {accepted.AdmissionJson}");

        // An identical retry sees the operation despite current availability
        // becoming false. It must not run the command or fresh CanExecute.
        model.SaveEnabled = false;
        var duplicate = driver.Start(vm => vm.SaveCommand, Input("first body"), requestId);
        Require(duplicate.Admission == RunicOperationAdmissionKind.Duplicate,
            $"A typed command retry did not recover the admitted operation: {duplicate.AdmissionJson}");
        Require(model.Executions == 0, "The duplicate typed command started a second execution.");

        var conflict = driver.Start(vm => vm.SaveCommand, Input("changed body"), requestId);
        Require(conflict.Admission == RunicOperationAdmissionKind.Rejected && conflict.Reason == "identity-conflict",
            "A request ID could be reused with a different typed command payload.");

        var unavailable = driver.Start(vm => vm.SaveCommand, Input("new body"), "typed-save-disabled");
        Require(unavailable.Admission == RunicOperationAdmissionKind.Rejected && unavailable.Reason == "unavailable",
            "A new typed command ignored its current availability.");

        model.ReleaseSave();
        var completion = await accepted.WaitAsync();
        Require(completion.Kind == RunicOperationStatusKind.Succeeded, $"The typed command did not complete successfully: {completion.Json}");
        var result = completion.Result!.Value;
        Require(result.GetProperty("documentId").GetString() == "document-1"
            && result.GetProperty("savedVersion").GetInt32() == 8
            && result.GetProperty("contentLength").GetInt32() == "first body".Length,
            "The typed command result was not encoded through the generated result codec.");
        Require(model.Executions == 1, "The typed command did not execute exactly once.");

        var unavailableAfterCompletion = driver.Start(vm => vm.SaveCommand, Input("new body"), "typed-save-disabled-after-completion");
        Require(unavailableAfterCompletion.Admission == RunicOperationAdmissionKind.Rejected && unavailableAfterCompletion.Reason == "unavailable",
            "A genuine CanExecute=false source was not honored after command completion.");

        await AssertLastAndStreamAsync(driver);
        await AssertEmptyCompletionAsync(driver);
        await AssertFailureAndCancellationAsync(driver, model);
        await AssertInterfaceOnlyOperationAsync(driver, model);
    }

    // The same commands sent as command requests, and the execution helper.
    [Test]
    public async Task TypedReactiveCommandsExecuteAsync()
    {
        await using var context = new RunicModelContext();
        var model = new TypedReactiveViewModel(context);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = Host(model);

        // The execution helper makes cardinality explicit: a command with two
        // observable values is rejected instead of silently retaining its last
        // value. Last-value and stream contracts require their own opt-in API.
        await RequireThrowsAsync<InvalidOperationException>(async () =>
            await ReactiveCommandExecution.Execute(model.MultiResultCommand,
                new TypedSaveRequest("document-1", "body", 1), CancellationToken.None),
            "A multi-result ReactiveUI command was accepted as a single result.");

        await AssertImmediateCompletionTurnAsync(host);
        await AssertInterfaceOnlyCommandLifecycleAsync(host, model);
        AssertPlainTypedCommand(host, model);
    }

    private static RunicWindowTestHost<TypedReactiveViewModel> Host(TypedReactiveViewModel model) =>
        new(model, (transport, content, vm) => new TypedReactiveBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "typedReactive", ViewLocator = new TestViewLocator() });

    private static async Task AssertEmptyCompletionAsync(RunicViewDriver<TypedReactiveViewModel> driver)
    {
        var completion = await driver.Start(vm => vm.EmptyCompletionCommand, requestId: "typed-empty-completion").WaitAsync();
        using var status = JsonDocument.Parse(completion.Json);
        Require(completion.Kind == RunicOperationStatusKind.Succeeded && !status.RootElement.TryGetProperty("result", out _),
            "An empty RxVoid command did not complete as a no-result operation.");
    }

    private static async Task AssertImmediateCompletionTurnAsync(RunicWindowTestHost<TypedReactiveViewModel> host)
    {
        for (var index = 0; index < 1_000; index++)
        {
            using var reply = JsonDocument.Parse(await host.Transport.CommandAsync("typedReactive", "immediateCompletion",
                JsonSerializer.Serialize("repeat")));
            Require(reply.RootElement.GetProperty("ok").GetBoolean(),
                $"An immediately completed ReactiveUI command was unavailable after {index} sequential bridge calls.");
        }
    }

    private static async Task AssertFailureAndCancellationAsync(
        RunicViewDriver<TypedReactiveViewModel> driver, TypedReactiveViewModel model)
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var exceptions = model.FailCommand.ThrownExceptions.Subscribe(new ExceptionObserver(error => observed.TrySetResult(error)));
        var failed = await driver.Start(vm => vm.FailCommand, Input("failure"), "typed-failure").WaitAsync();
        var observedError = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(failed.Kind == RunicOperationStatusKind.Failed && failed.ErrorMessage == "The operation failed."
            && observedError is InvalidOperationException,
            "A ReactiveUI command failure was not sanitized at the operation boundary and observed through ThrownExceptions.");

        var cancelling = driver.Start(vm => vm.CancelCommand, Input("cancel"), "typed-cancelled");
        Require(cancelling.Cancel() == RunicOperationCancelKind.CancellationRequested,
            "The operation cancellation request was not delivered to ReactiveUI command execution.");
        Require((await cancelling.WaitAsync()).Kind == RunicOperationStatusKind.Cancelled,
            "A canceled ReactiveUI command did not produce a cancelled operation terminal.");
    }

    private static async Task AssertInterfaceOnlyOperationAsync(
        RunicViewDriver<TypedReactiveViewModel> driver, TypedReactiveViewModel model)
    {
        model.SetInterfaceOnlyAvailability(true);
        var completed = await driver.Start(vm => vm.InterfaceOnlyCommand, JsonSerializer.Serialize("interface"), "typed-interface-only").WaitAsync();
        Require(completed.Kind == RunicOperationStatusKind.Succeeded && completed.Result?.GetInt32() == "interface".Length,
            "An interface-only ReactiveUI command did not use the bridge availability lease.");
        model.SetInterfaceOnlyAvailability(false);
        var disabled = driver.Start(vm => vm.InterfaceOnlyCommand, JsonSerializer.Serialize("interface"), "typed-interface-only-disabled");
        Require(disabled.Admission == RunicOperationAdmissionKind.Rejected && disabled.Reason == "unavailable",
            "An interface-only ReactiveUI command ignored an observed false availability value.");
    }

    private static async Task AssertInterfaceOnlyCommandLifecycleAsync(
        RunicWindowTestHost<TypedReactiveViewModel> host, TypedReactiveViewModel model)
    {
        // The first CanExecute subscriber is the bridge Observe lease. A later
        // sample has no synchronous value, so this command proves generated
        // descriptors use their lease for interface-only commands.
        using var completed = JsonDocument.Parse(await host.Transport.CommandAsync("typedReactive", "interfaceOnly",
            JsonSerializer.Serialize("interface")));
        Require(completed.RootElement.GetProperty("ok").GetBoolean(),
            $"An interface-only ReactiveUI command did not use the bridge availability lease: {completed.RootElement}");

        model.SetInterfaceOnlyAvailability(false);
        using var disabled = JsonDocument.Parse(await host.Transport.CommandAsync("typedReactive", "interfaceOnly",
            JsonSerializer.Serialize("interface")));
        Require(!disabled.RootElement.GetProperty("ok").GetBoolean()
            && disabled.RootElement.GetProperty("error").GetProperty("reason").GetString() == "unavailable",
            $"An interface-only ReactiveUI command ignored an observed false availability value: {disabled.RootElement}");

        var direct = new TypedReactiveViewModel.InterfaceOnlyReactiveCommand();
        Require(ReactiveCommandExecution.CanExecute(direct, "direct"),
            "Direct interface-only availability did not sample a synchronous CanExecute value.");
        Require(await ReactiveCommandExecution.Execute(direct, "direct", CancellationToken.None) == "direct".Length,
            "Direct ReactiveUI execution without an Observe lease did not complete.");

        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(async () =>
                await ReactiveCommandExecution.Execute(direct, "never", preCancelled.Token),
                "A pre-cancelled execution subscribed to the command.");
        }
        Require(direct.DisposedNeverExecutions == 0,
            "A pre-cancelled execution caused command work before cancellation was checked.");

        using (var cancellation = new CancellationTokenSource())
        {
            var pending = ReactiveCommandExecution.Execute(direct, "never", cancellation.Token);
            cancellation.Cancel();
            await RequireThrowsAsync<OperationCanceledException>(async () => await pending,
                "A running direct execution did not honor cancellation.");
        }
        Require(direct.DisposedNeverExecutions == 1,
            "Cancelling a direct execution did not dispose its command subscription.");
    }

    private static void AssertPlainTypedCommand(RunicWindowTestHost<TypedReactiveViewModel> host,
        TypedReactiveViewModel model)
    {
        using var reply = JsonDocument.Parse(host.Transport.Command("typedReactive", "plainApply",
            JsonSerializer.Serialize(new
            {
                documentId = "document-1", content = "plain", expectedVersion = 9,
            })));
        // The state at the reply's revision or later includes the command's effects.
        using var state = host.RawSnapshot();
        Require(reply.RootElement.GetProperty("ok").GetBoolean() && reply.RootElement.TryGetProperty("revision", out _)
            && state.RootElement.GetProperty("state").GetProperty("plainApplyCount").GetInt32() == 1
            && model.PlainApplyCount == 1,
            "The typed plain ICommand input did not decode and execute with normal snapshot semantics.");
    }

    private static async Task AssertLastAndStreamAsync(RunicViewDriver<TypedReactiveViewModel> driver)
    {
        var last = await driver.Start(vm => vm.LastResultCommand, Input("last"), "typed-last-1").WaitAsync();
        Require(last.Kind == RunicOperationStatusKind.Succeeded && last.Result?.GetInt32() == 2,
            "The explicit Last cardinality did not retain the final observable value.");

        var stream = driver.Start(vm => vm.StreamResultCommand, Input("stream"), "typed-stream-1");
        _ = await stream.WaitAsync();
        using var page = JsonDocument.Parse(stream.ReadStream().Json);
        var root = page.RootElement;
        Require(root.GetProperty("kind").GetString() == "succeeded"
            && root.GetProperty("items").GetArrayLength() == 128
            && root.GetProperty("items")[0].GetProperty("value").GetInt32() == 1
            && root.GetProperty("items")[127].GetProperty("value").GetInt32() == 128
            && root.GetProperty("delivery").GetProperty("kind").GetString() == "stream-overflow",
            "The explicit Stream cardinality did not preserve bounded replay and visible overflow.");
    }

    private static string Input(string content) =>
        JsonSerializer.Serialize(new { documentId = "document-1", content, expectedVersion = 7 });

    private static async Task RequireThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class ExceptionObserver(Action<Exception> observed) : IObserver<Exception>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => throw error;
        public void OnNext(Exception value) => observed(value);
    }
}
