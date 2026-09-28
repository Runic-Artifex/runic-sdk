using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

internal static class OperationResultTests
{
    internal static async Task RunAsync()
    {
        var firstDigest = BridgeOperationRequest.CanonicalDigest("{\"b\":2,\"a\":1}");
        var reorderedDigest = BridgeOperationRequest.CanonicalDigest("{\"a\":1.0,\"b\":2}");
        Require(firstDigest == reorderedDigest, "Canonical operation input digests changed with property order or numeric spelling.");

        using var registry = new BridgeOperationRegistry("typed-operation-test", maximumRetainedResultBytes: 4);
        var request = new BridgeOperationRequest("contract", "Save", "request-one", firstDigest);
        var starts = 0;
        var first = registry.Accept(request, () => ++starts == 1,
            _ => Task.FromResult(BridgeOperationResult.Succeeded("12345")));
        Require(first.Kind is BridgeOperationAdmissionKind.Accepted, "The typed operation was not admitted.");

        var duplicate = registry.Accept(request, () => throw new InvalidOperationException("Duplicate CanExecute must not run."),
            _ => throw new InvalidOperationException("Duplicate work must not run."));
        Require(duplicate.Kind is BridgeOperationAdmissionKind.Duplicate && starts == 1,
            "A duplicate typed request did not return before availability was reevaluated.");

        var changedInput = registry.Accept(new BridgeOperationRequest("contract", "Save", "request-one", "different"),
            () => true, _ => Task.FromResult(BridgeOperationResult.None));
        Require(changedInput.Kind is BridgeOperationAdmissionKind.Rejected && changedInput.Reason == "identity-conflict",
            "A request ID could be reused for a changed input.");

        var terminal = await registry.WaitForTerminalAsync(request.Identity.RegistryKey);
        Require(terminal.Kind is BridgeOperationStatusKind.Succeeded && terminal.Result?.DeliveryFailure?.Kind is BridgeOperationDeliveryFailureKind.ResultTooLarge,
            "An oversized result did not preserve success with an explicit delivery failure.");

        var codecFailure = registry.Accept(new BridgeOperationRequest("contract", "Encode", "request-codec", "codec"), () => true,
            _ => Task.FromResult(BridgeOperationResult.Encode(() => throw new InvalidOperationException("codec failed"))));
        var codecTerminal = await registry.WaitForTerminalAsync(codecFailure.RequestId);
        Require(codecTerminal.Kind is BridgeOperationStatusKind.Succeeded
            && codecTerminal.Result?.DeliveryFailure?.Kind is BridgeOperationDeliveryFailureKind.ResultEncodingFailed,
            "A result codec failure did not preserve the successful command effect as a delivery outcome.");

        var stream = new BridgeOperationStream(maximumItems: 2, maximumBytes: 16);
        Require(stream.TryPublish("1") && stream.TryPublish("2") && !stream.TryPublish("3"),
            "The bounded stream did not reject overflow explicitly.");
        var streamed = registry.Accept(new BridgeOperationRequest("contract", "Progress", "request-two", "stream"), () => true,
            _ => Task.FromResult(BridgeOperationResult.Stream(stream)));
        Require(streamed.Kind is BridgeOperationAdmissionKind.Accepted, "The stream operation was not admitted.");
        var streamStatus = await registry.WaitForTerminalAsync(streamed.RequestId);
        Require(streamStatus.Result?.DeliveryFailure?.Kind is BridgeOperationDeliveryFailureKind.StreamOverflow,
            "The stream overflow was not retained as a delivery failure.");
        var replay = registry.ReadStream(streamed.RequestId, 0).Stream;
        Require(replay is { Items.Count: 2, NextCursor: 2 } && replay.Failure?.Kind is BridgeOperationDeliveryFailureKind.StreamOverflow,
            "The stream cursor did not retain bounded replay with explicit overflow.");

        var liveStream = new BridgeOperationStream();
        var releaseLiveStream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = registry.Accept(new BridgeOperationRequest("contract", "Progress", "request-three", "live"), () => true,
            liveStream, async (execution, _) =>
            {
                Require(ReferenceEquals(execution.Stream, liveStream), "The stream execution did not receive its admitted stream.");
                Require(execution.Stream!.TryPublish("{\"step\":1}"), "The live stream could not publish its first item.");
                await releaseLiveStream.Task.ConfigureAwait(false);
                return BridgeOperationResult.None;
            });
        Require(live.Kind is BridgeOperationAdmissionKind.Accepted, "The live stream operation was not admitted.");
        var whileRunning = registry.ReadStream(live.RequestId, 0).Stream;
        Require(whileRunning is { Completed: false, Items.Count: 1, NextCursor: 1 },
            "A stream item was not available before the operation completed.");
        releaseLiveStream.SetResult();
        await registry.WaitForTerminalAsync(live.RequestId);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
