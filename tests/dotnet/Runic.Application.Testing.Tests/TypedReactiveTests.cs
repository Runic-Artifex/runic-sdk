using System.Text.Json;
using ReactiveUI;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

internal static class TypedReactiveTests
{
    internal static async Task RunAsync()
    {
        await using var context = new RunicModelContext();
        var model = new TypedReactiveViewModel(context);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<TypedReactiveViewModel>(model, "typedReactive",
            (transport, content, vm) => new TypedReactiveBridge(transport, vm, content: content),
            new TestViewLocator());

        const string requestId = "typed-save-1";
        var payload = Payload(requestId, "first body");
        using var accepted = JsonDocument.Parse(host.Transport.Call("typedReactiveStartSave", new(StringValue: payload)));
        Require(accepted.RootElement.GetProperty("kind").GetString() == "accepted",
            "The typed ReactiveUI command was not admitted.");
        var contract = accepted.RootElement.GetProperty("contract").GetString()!;

        // An identical retry sees the operation despite current availability
        // becoming false. It must not run the command or fresh CanExecute.
        model.SaveEnabled = false;
        using var duplicate = JsonDocument.Parse(host.Transport.Call("typedReactiveStartSave", new(StringValue: payload)));
        Require(duplicate.RootElement.GetProperty("kind").GetString() == "duplicate",
            "A typed command retry did not recover the admitted operation.");
        Require(model.Executions == 0, "The duplicate typed command started a second execution.");

        using var conflict = JsonDocument.Parse(host.Transport.Call("typedReactiveStartSave",
            new(StringValue: Payload(requestId, "changed body"))));
        Require(conflict.RootElement.GetProperty("kind").GetString() == "rejected"
            && conflict.RootElement.GetProperty("reason").GetString() == "identity-conflict",
            "A request ID could be reused with a different typed command payload.");

        using var unavailable = JsonDocument.Parse(host.Transport.Call("typedReactiveStartSave",
            new(StringValue: Payload("typed-save-disabled", "new body"))));
        Require(unavailable.RootElement.GetProperty("kind").GetString() == "rejected"
            && unavailable.RootElement.GetProperty("reason").GetString() == "unavailable",
            "A new typed command ignored its current availability.");

        model.ReleaseSave();
        using var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))));
        var root = completion.RootElement;
        Require(root.GetProperty("kind").GetString() == "succeeded", "The typed command did not complete successfully.");
        var result = root.GetProperty("result");
        Require(result.GetProperty("documentId").GetString() == "document-1"
            && result.GetProperty("savedVersion").GetInt32() == 8
            && result.GetProperty("contentLength").GetInt32() == "first body".Length,
            "The typed command result was not encoded through the generated result codec.");
        Require(model.Executions == 1, "The typed command did not execute exactly once.");

        using var unavailableAfterCompletion = JsonDocument.Parse(host.Transport.Call("typedReactiveStartSave",
            new(StringValue: Payload("typed-save-disabled-after-completion", "new body"))));
        Require(unavailableAfterCompletion.RootElement.GetProperty("kind").GetString() == "rejected"
            && unavailableAfterCompletion.RootElement.GetProperty("reason").GetString() == "unavailable",
            "A genuine CanExecute=false source was not honored after command completion.");

        // The execution helper makes cardinality explicit: a command with two
        // observable values is rejected instead of silently retaining its last
        // value. Last-value and stream contracts require their own opt-in API.
        await RequireThrowsAsync<InvalidOperationException>(async () =>
            await ReactiveCommandExecution.Execute(model.MultiResultCommand,
                new TypedSaveRequest("document-1", "body", 1), CancellationToken.None),
            "A multi-result ReactiveUI command was accepted as a single result.");

        await AssertLastAndStreamAsync(host);
        await AssertEmptyCompletionAsync(host);
        await AssertImmediateCompletionTurnAsync(host);
        await AssertFailureAndCancellationAsync(host, model);
        await AssertInterfaceOnlyCommandLifecycleAsync(host, model);
        AssertPlainTypedCommand(host, model);
    }

    private static async Task AssertEmptyCompletionAsync(RunicWindowTestHost<TypedReactiveViewModel> host)
    {
        const string requestId = "typed-empty-completion";
        using var admission = JsonDocument.Parse(host.Transport.Call("typedReactiveStartEmptyCompletion",
            new(StringValue: requestId)));
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        using var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))));
        Require(completion.RootElement.GetProperty("kind").GetString() == "succeeded"
            && !completion.RootElement.TryGetProperty("result", out _),
            "An empty RxVoid command did not complete as a no-result operation.");
    }

    private static async Task AssertImmediateCompletionTurnAsync(RunicWindowTestHost<TypedReactiveViewModel> host)
    {
        for (var index = 0; index < 1_000; index++)
        {
            using var reply = JsonDocument.Parse(await host.Transport.CallAsync("typedReactiveImmediateCompletion",
                new(StringValue: JsonSerializer.Serialize("repeat"))));
            Require(reply.RootElement.GetProperty("ok").GetBoolean(),
                $"An immediately completed ReactiveUI command was unavailable after {index} sequential bridge calls.");
        }
    }

    private static async Task AssertFailureAndCancellationAsync(
        RunicWindowTestHost<TypedReactiveViewModel> host, TypedReactiveViewModel model)
    {
        var observed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var exceptions = model.FailCommand.ThrownExceptions.Subscribe(new ExceptionObserver(error => observed.TrySetResult(error)));
        const string failedRequest = "typed-failure";
        using var failedAdmission = JsonDocument.Parse(host.Transport.Call("typedReactiveStartFail",
            new(StringValue: Payload(failedRequest, "failure"))));
        var failedContract = failedAdmission.RootElement.GetProperty("contract").GetString()!;
        using var failed = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract = failedContract, requestId = failedRequest }))));
        var observedError = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Require(failed.RootElement.GetProperty("kind").GetString() == "failed"
            && failed.RootElement.GetProperty("error").GetProperty("message").GetString() == "The operation failed."
            && observedError is InvalidOperationException,
            "A ReactiveUI command failure was not sanitized at the operation boundary and observed through ThrownExceptions.");

        const string cancelledRequest = "typed-cancelled";
        using var cancelledAdmission = JsonDocument.Parse(host.Transport.Call("typedReactiveStartCancel",
            new(StringValue: Payload(cancelledRequest, "cancel"))));
        var cancelledContract = cancelledAdmission.RootElement.GetProperty("contract").GetString()!;
        using var cancel = JsonDocument.Parse(host.Transport.Call("__runicOperationCancel",
            new(StringValue: JsonSerializer.Serialize(new { contract = cancelledContract, requestId = cancelledRequest }))));
        Require(cancel.RootElement.GetProperty("kind").GetString() == "cancellation-requested",
            "The operation cancellation request was not delivered to ReactiveUI command execution.");
        using var cancelled = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract = cancelledContract, requestId = cancelledRequest }))));
        Require(cancelled.RootElement.GetProperty("kind").GetString() == "cancelled",
            "A canceled ReactiveUI command did not produce a cancelled operation terminal.");
    }

    private static async Task AssertInterfaceOnlyCommandLifecycleAsync(
        RunicWindowTestHost<TypedReactiveViewModel> host, TypedReactiveViewModel model)
    {
        // The first CanExecute subscriber is the bridge Observe lease. A later
        // sample has no synchronous value, so this admission proves generated
        // descriptors use their lease for interface-only commands.
        const string requestId = "typed-interface-only";
        using var admission = JsonDocument.Parse(host.Transport.Call("typedReactiveStartInterfaceOnly",
            new(StringValue: JsonSerializer.Serialize(new { requestId, input = "interface" }))));
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        using var completed = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))));
        Require(completed.RootElement.GetProperty("kind").GetString() == "succeeded"
            && completed.RootElement.GetProperty("result").GetInt32() == "interface".Length,
            "An interface-only ReactiveUI command did not use the bridge availability lease.");

        model.SetInterfaceOnlyAvailability(false);
        using var disabled = JsonDocument.Parse(host.Transport.Call("typedReactiveStartInterfaceOnly",
            new(StringValue: JsonSerializer.Serialize(new { requestId = "typed-interface-only-disabled", input = "interface" }))));
        Require(disabled.RootElement.GetProperty("kind").GetString() == "rejected"
            && disabled.RootElement.GetProperty("reason").GetString() == "unavailable",
            "An interface-only ReactiveUI command ignored an observed false availability value.");

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
        using var reply = JsonDocument.Parse(host.Transport.Call("typedReactivePlainApply",
            new(StringValue: JsonSerializer.Serialize(new
            {
                documentId = "document-1", content = "plain", expectedVersion = 9,
            }))));
        Require(reply.RootElement.GetProperty("ok").GetBoolean()
            && reply.RootElement.GetProperty("state").GetProperty("plainApplyCount").GetInt32() == 1
            && model.PlainApplyCount == 1,
            "The typed plain ICommand input did not decode and execute with normal snapshot semantics.");
    }

    private static async Task AssertLastAndStreamAsync(RunicWindowTestHost<TypedReactiveViewModel> host)
    {
        const string lastRequestId = "typed-last-1";
        using var lastAdmission = JsonDocument.Parse(host.Transport.Call("typedReactiveStartLastResult",
            new(StringValue: Payload(lastRequestId, "last"))));
        var lastContract = lastAdmission.RootElement.GetProperty("contract").GetString()!;
        using var last = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract = lastContract, requestId = lastRequestId }))));
        Require(last.RootElement.GetProperty("kind").GetString() == "succeeded"
            && last.RootElement.GetProperty("result").GetInt32() == 2,
            "The explicit Last cardinality did not retain the final observable value.");

        const string streamRequestId = "typed-stream-1";
        using var streamAdmission = JsonDocument.Parse(host.Transport.Call("typedReactiveStartStreamResult",
            new(StringValue: Payload(streamRequestId, "stream"))));
        var streamContract = streamAdmission.RootElement.GetProperty("contract").GetString()!;
        using var stream = JsonDocument.Parse(host.Transport.Call("__runicOperationStream",
            new(StringValue: JsonSerializer.Serialize(new { contract = streamContract, requestId = streamRequestId, cursor = 0 }))));
        var page = stream.RootElement;
        Require(page.GetProperty("kind").GetString() == "succeeded"
            && page.GetProperty("items").GetArrayLength() == 128
            && page.GetProperty("items")[0].GetProperty("value").GetInt32() == 1
            && page.GetProperty("items")[127].GetProperty("value").GetInt32() == 128
            && page.GetProperty("delivery").GetProperty("kind").GetString() == "stream-overflow",
            "The explicit Stream cardinality did not preserve bounded replay and visible overflow.");
    }

    private static string Payload(string requestId, string content) => JsonSerializer.Serialize(new
    {
        requestId,
        input = new { documentId = "document-1", content, expectedVersion = 7 },
    });

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
