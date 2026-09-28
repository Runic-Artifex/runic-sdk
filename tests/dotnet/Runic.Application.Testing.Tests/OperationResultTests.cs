using System.Text.Json;
using Runic.Application.Testing;
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

        // Commands on one generated bridge share a contract. A recovery route
        // must still bind its request ID to the command member, even where two
        // commands happen to return the same wire shape.
        Require(registry.Lookup(request.Identity.RegistryKey, "Delete").Kind is BridgeOperationStatusKind.Unknown,
            "A recovery lookup for a different same-shaped command observed the original operation.");
        Require((await registry.WaitForTerminalAsync(request.Identity.RegistryKey, "Delete")).Kind is BridgeOperationStatusKind.Unknown,
            "A wait for a different command member attached to the original operation.");

        AssertMemberBoundRecoveryRoutes();

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

        await AssertStreamRetentionBudgetsAsync();
    }

    private static async Task AssertStreamRetentionBudgetsAsync()
    {
        using var retained = new BridgeOperationRegistry("stream-retention-test", maximumRetainedResultBytes: 4);
        var scalar = retained.Accept(new BridgeOperationRequest("contract", "Scalar", "scalar-retained", "scalar"), () => true,
            _ => Task.FromResult(BridgeOperationResult.Succeeded("\"s\"")));
        await retained.WaitForTerminalAsync(scalar.RequestId);
        var firstStream = new BridgeOperationStream(maximumBytes: 4);
        Require(firstStream.TryPublish("\"a\""), "The first aggregate-budget stream did not publish.");
        var first = retained.Accept(new BridgeOperationRequest("contract", "Save", "stream-retained-one", "one"), () => true,
            _ => Task.FromResult(BridgeOperationResult.Stream(firstStream)));
        await retained.WaitForTerminalAsync(first.RequestId);

        var secondStream = new BridgeOperationStream(maximumBytes: 4);
        Require(secondStream.TryPublish("\"b\""), "The second aggregate-budget stream did not publish.");
        var second = retained.Accept(new BridgeOperationRequest("contract", "Delete", "stream-retained-two", "two"), () => true,
            _ => Task.FromResult(BridgeOperationResult.Stream(secondStream)));
        await retained.WaitForTerminalAsync(second.RequestId);
        Require(retained.Lookup(scalar.RequestId).Kind is BridgeOperationStatusKind.Expired &&
                retained.Lookup(first.RequestId).Kind is BridgeOperationStatusKind.Expired &&
                retained.ReadStream(second.RequestId, 0).Stream is { Items.Count: 1 },
            "Scalar results and stream replay did not share one aggregate retention budget.");

        using var overflow = new BridgeOperationRegistry("stream-retention-overflow", maximumRetainedResultBytes: 2);
        var oversized = new BridgeOperationStream(maximumBytes: 4);
        Require(oversized.TryPublish("\"x\""), "The oversized aggregate-budget stream did not publish.");
        var oversizeAdmission = overflow.Accept(new BridgeOperationRequest("contract", "Save", "stream-overflow", "overflow"), () => true,
            _ => Task.FromResult(BridgeOperationResult.Stream(oversized)));
        var oversizeTerminal = await overflow.WaitForTerminalAsync(oversizeAdmission.RequestId);
        Require(oversizeTerminal.Result?.DeliveryFailure?.Kind is BridgeOperationDeliveryFailureKind.StreamRetentionTooLarge &&
                overflow.ReadStream(oversizeAdmission.RequestId, 0).Stream is { Items.Count: 0, Failure.Kind: BridgeOperationDeliveryFailureKind.StreamRetentionTooLarge },
            "A stream above the aggregate budget was not cleared with an explicit delivery failure.");

        using var running = new BridgeOperationRegistry("running-stream-budget", maximumRunningStreamBytes: 4);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRunningStream = new BridgeOperationStream(maximumBytes: 4);
        var firstRunning = running.Accept(new BridgeOperationRequest("contract", "Save", "running-one", "one"), () => true,
            firstRunningStream, async (_, _) =>
            {
                await release.Task.ConfigureAwait(false);
                return BridgeOperationResult.None;
            });
        var secondRunning = running.Accept(new BridgeOperationRequest("contract", "Delete", "running-two", "two"), () => true,
            new BridgeOperationStream(maximumBytes: 4), (_, _) => Task.FromResult(BridgeOperationResult.None));
        Require(firstRunning.Kind is BridgeOperationAdmissionKind.Accepted &&
                secondRunning.Kind is BridgeOperationAdmissionKind.Rejected && secondRunning.Reason == "stream-capacity",
            "Concurrent streams were admitted beyond the running stream memory budget.");
        release.SetResult();
        await running.WaitForTerminalAsync(firstRunning.RequestId);
        var afterRelease = running.Accept(new BridgeOperationRequest("contract", "Delete", "running-three", "three"), () => true,
            new BridgeOperationStream(maximumBytes: 4), (_, _) => Task.FromResult(BridgeOperationResult.None));
        Require(afterRelease.Kind is BridgeOperationAdmissionKind.Accepted,
            "A completed stream did not release its running-memory reservation.");
        await running.WaitForTerminalAsync(afterRelease.RequestId);
    }

    private static void AssertMemberBoundRecoveryRoutes()
    {
        using var transport = new InMemoryViewTransport();
        using var router = new BridgeOperationRouter(transport, "member-bound-router");
        var request = new BridgeOperationRequest("same-result-contract", "Save", "same-result-id", "same-result-input");
        var admitted = router.Accept(request, () => true,
            _ => Task.FromResult(BridgeOperationResult.Succeeded("7")));
        Require(admitted.Kind is BridgeOperationAdmissionKind.Accepted,
            "The member-bound recovery fixture did not admit its Save operation.");

        using var wrong = JsonDocument.Parse(transport.Call(BridgeOperationRouter.StatusRoute,
            new(StringValue: JsonSerializer.Serialize(new
            {
                contract = request.Identity.Contract,
                member = "Delete",
                requestId = request.Identity.RequestId,
            }))));
        Require(wrong.RootElement.GetProperty("kind").GetString() == "unknown",
            "A recovery request for Delete read Save's same-shaped numeric result.");

        // Existing hand-written clients omit member and retain their established
        // contract/requestId recovery behavior.
        using var legacy = JsonDocument.Parse(transport.Call(BridgeOperationRouter.StatusRoute,
            new(StringValue: JsonSerializer.Serialize(new
            {
                contract = request.Identity.Contract,
                requestId = request.Identity.RequestId,
            }))));
        Require(legacy.RootElement.GetProperty("kind").GetString() == "succeeded" &&
                legacy.RootElement.GetProperty("result").GetInt32() == 7,
            "The optional member binding broke legacy operation recovery.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
