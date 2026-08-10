using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RunicFlow;
using RunicFlow.ApplicationBridge;
using RunicFlow.Operations;
using RunicFlow.Processes;
using RunicToolkit.ApplicationBridge;

namespace RunicArtifex.Examples.Flow;

internal static class Program
{
    public static async Task<int> Main()
    {
        try
        {
            var definition = new ProcessDefinition<CanaryState, CanaryCommand, int>(
                new ProcessKey("canary.process"),
                1,
                HandleAsync);
            await using var process = new ProcessSession<CanaryState, CanaryCommand, int>(
                definition,
                new CanaryState(0));

            ProcessTransition<CanaryState, int> incremented = await process
                .DispatchAsync(new Increment(), expectedVersion: 0)
                .ConfigureAwait(false);
            ProcessTransition<CanaryState, int> completed = await process
                .DispatchAsync(new Complete(), expectedVersion: 1)
                .ConfigureAwait(false);

            using var operations = new FakeBridgeOperations(
                Guid.Parse("dc578e9f-cb6b-4efe-a21c-b5ab40966797"));
            var runner = new OperationRunner();
            OperationId observedOperation = default;
            BridgeOperationId bridgeOperation = operations.StartFlowOperation(
                runner,
                new OperationRequest(new OperationKey("canary.operation")),
                (_, context, _) =>
                {
                    observedOperation = context.Id;
                    context.Report(new OperationProgress(1, new OperationStage("complete")));
                    return ValueTask.CompletedTask;
                });
            await operations.Completion.ConfigureAwait(false);

            OperationSnapshot snapshot = runner.GetSnapshots()[0];
            bool valid = incremented.Kind == ProcessTransitionKind.Accepted
                && incremented.Snapshot.Version == 1
                && incremented.Snapshot.State.Count == 1
                && completed.Kind == ProcessTransitionKind.Completed
                && completed.Snapshot.Result == 1
                && bridgeOperation.Value == observedOperation.Value
                && snapshot.Id == observedOperation
                && snapshot.State == OperationState.Succeeded;
            if (!valid)
            {
                Console.Error.WriteLine("FAIL: published headless Runic Flow package canary.");
                return 1;
            }

            Console.WriteLine("Runic Flow headless process and Application Bridge packages passed managed and NativeAOT canaries.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static ValueTask<ProcessDecision<CanaryState, int>> HandleAsync(
        ProcessCommandContext<CanaryState> context,
        CanaryCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessDecision<CanaryState, int> decision = command switch
        {
            Increment => ProcessDecision<CanaryState, int>.Accept(
                context.State with { Count = context.State.Count + 1 }),
            Complete => ProcessDecision<CanaryState, int>.Complete(
                context.State,
                context.State.Count),
            _ => ProcessDecision<CanaryState, int>.Reject("Unsupported canary command."),
        };
        return ValueTask.FromResult(decision);
    }

    private sealed class FakeBridgeOperations(Guid operationId) : IBridgeOperationFactory, IDisposable
    {
        private Task _completion = Task.CompletedTask;

        public Task Completion => _completion;

        public BridgeOperationId Start(
            Func<BridgeOperationId, CancellationToken, ValueTask> operation,
            CancellationToken cancellationToken = default)
        {
            var id = new BridgeOperationId(operationId);
            _completion = operation(id, cancellationToken).AsTask();
            return id;
        }

        public void Dispose()
        {
        }
    }

    private sealed record CanaryState(int Count);

    private abstract record CanaryCommand;

    private sealed record Increment : CanaryCommand;

    private sealed record Complete : CanaryCommand;
}
