using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.MVVM;

namespace WebUIToolkit.MVVM.Tests;

internal static class Program
{
    private static int _passed;

    public static async Task<int> Main()
    {
        try
        {
            Run(nameof(ContractIdentityIsStrict), ContractIdentityIsStrict);
            Run(nameof(FaultAndLimitContractsAreBounded), FaultAndLimitContractsAreBounded);
            await RunAsync(nameof(RevisionAndSnapshotSemantics), RevisionAndSnapshotSemantics);
            await RunAsync(nameof(AdapterRejectionAndExceptionDoNotAdvance), AdapterRejectionAndExceptionDoNotAdvance);
            await RunAsync(nameof(CommittedFailureAdvancesExactlyOnce), CommittedFailureAdvancesExactlyOnce);
            await RunAsync(nameof(AcknowledgementsAreMonotonic), AcknowledgementsAreMonotonic);
            await RunAsync(nameof(OneSessionSerializesMutations), OneSessionSerializesMutations);
            await RunAsync(nameof(CancellationBypassesTheDispatchGate), CancellationBypassesTheDispatchGate);
            await RunAsync(nameof(ThrowingCancellationCallbacksAreContained), ThrowingCancellationCallbacksAreContained);
            await RunAsync(nameof(TimeoutHasAStableFault), TimeoutHasAStableFault);
            await RunAsync(nameof(TimeoutWinsOverLateCancellationAndSuccess), TimeoutWinsOverLateCancellationAndSuccess);
            await RunAsync(nameof(PendingAndSessionLimitsAreEnforced), PendingAndSessionLimitsAreEnforced);
            await RunAsync(nameof(FactoryRecoversAfterActivationFailure), FactoryRecoversAfterActivationFailure);
            await RunAsync(nameof(FactoryDisposalDrainsConcurrentOpen), FactoryDisposalDrainsConcurrentOpen);
            await RunAsync(nameof(DisposalIsIdempotentAndReverseOrdered), DisposalIsIdempotentAndReverseOrdered);
            await RunAsync(nameof(ConcurrentDisposalWaitsForTheSameCompletion), ConcurrentDisposalWaitsForTheSameCompletion);

            Console.WriteLine($"PASS: {_passed} runtime contract tests");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {_passed} tests: {exception}");
            return 1;
        }
    }

    private static void ContractIdentityIsStrict()
    {
        Equal("webuitoolkit.mvvm/1", MvvmProtocol.Identity);
        Throws<ArgumentException>(() => _ = new MvvmContract(""));
        Throws<ArgumentException>(() => _ = new MvvmContract(new string('x', 129)));

        var registry = new MvvmSessionRegistry();
        registry.Map(new MvvmContract("Settings"), NewActivation);
        registry.Map(new MvvmContract("settings"), NewActivation);
        Throws<InvalidOperationException>(() => registry.Map(new MvvmContract("settings"), NewActivation));
    }

    private static void FaultAndLimitContractsAreBounded()
    {
        True(MvvmFaultCodes.IsDefined(MvvmFaultCodes.RequestTimeout));
        False(MvvmFaultCodes.IsDefined("consumer.secret"));
        Throws<ArgumentException>(() => _ = new MvvmFault("consumer.secret", "message"));

        var fault = new MvvmFault(MvvmFaultCodes.RequestInvalid, "line\twith\0controls " + new string('é', 300));
        False(fault.Message.Any(char.IsControl));
        True(System.Text.Encoding.UTF8.GetByteCount(fault.Message) <= 256);

        Equal(64, MvvmLimits.Default.MaxPendingRequests);
        Equal(1_024, MvvmLimits.Default.MaxPatchOperations);
        (MvvmLimits.Default with { MaxCommandDuration = TimeSpan.FromMinutes(5) }).Validate();
        Throws<ArgumentOutOfRangeException>(() =>
            (MvvmLimits.Default with { MaxPendingRequests = MvvmLimits.MaximumPendingRequests + 1 }).Validate());
        Throws<ArgumentOutOfRangeException>(() =>
            (MvvmLimits.Default with { MaxCommandDuration = TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1) }).Validate());
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmMutationRequest(Id(), (MvvmMutationKind)99, 0, 1, Json("null")));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmCollectionPatch(1, (MvvmCollectionOperation)99, 0, []));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmCollectionPatch(1, MvvmCollectionOperation.Insert, 10_000, []));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmCollectionMovePatch(1, 0, 10_000, 1));
    }

    private static async Task RevisionAndSnapshotSemantics()
    {
        var adapter = new DelegateAdapter();
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        True(session.Authorizes(session.CapabilityToken));
        False(session.Authorizes(session.CapabilityToken + "x"));

        MvvmResponse snapshot = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        True(snapshot.Succeeded);
        Equal(0L, snapshot.Revision);
        Equal(0L, snapshot.Payload!.Value.GetProperty("count").GetInt64());

        MvvmResponse first = await session.DispatchAsync(Mutation(0));
        True(first.Succeeded);
        Equal(1L, first.Revision);
        Equal(1L, session.Revision);
        Equal(1, adapter.DispatchCount);

        MvvmResponse stale = await session.DispatchAsync(Mutation(0));
        False(stale.Succeeded);
        Equal(MvvmFaultCodes.RevisionStale, stale.Fault!.Code);
        Equal(1L, stale.Revision);
        Equal(1, adapter.DispatchCount);
    }

    private static async Task AdapterRejectionAndExceptionDoNotAdvance()
    {
        var adapter = new DelegateAdapter
        {
            Dispatch = static (_, _) => ValueTask.FromResult(
                MvvmBindingResult.Rejected(new MvvmFault(MvvmFaultCodes.MemberUnknown, "Unknown member."))),
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        MvvmResponse rejected = await session.DispatchAsync(Mutation(0));
        False(rejected.Succeeded);
        Equal(MvvmFaultCodes.MemberUnknown, rejected.Fault!.Code);
        Equal(0L, session.Revision);

        adapter.Dispatch = static (_, _) => throw new SensitiveFailure("secret path C:\\private\\token.txt");
        MvvmResponse failed = await session.DispatchAsync(Mutation(0));
        False(failed.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, failed.Fault!.Code);
        False(failed.Fault.Message.Contains("secret", StringComparison.Ordinal));
        Equal(0L, session.Revision);
    }

    private static async Task CommittedFailureAdvancesExactlyOnce()
    {
        var patches = new List<MvvmPatch> { new MvvmPropertyPatch(1, Json("7")) };
        var result = MvvmBindingResult.CommittedFailure(
            new MvvmFault(MvvmFaultCodes.RequestInvalid, "secret C:\\private\\token.txt"),
            patches);
        patches.Clear();
        Equal(1, result.Patches.Count);

        var adapter = new DelegateAdapter
        {
            Dispatch = (_, _) => ValueTask.FromResult(result),
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        MvvmResponse response = await session.DispatchAsync(Mutation(0, kind: MvvmMutationKind.ExecuteCommand));
        False(response.Succeeded);
        Equal(1L, response.Revision);
        Equal(1L, session.Revision);
        Equal(1, response.Patches.Count);
        Equal(MvvmFaultCodes.RequestInvalid, response.Fault!.Code);
        False(response.Fault.Message.Contains("secret", StringComparison.Ordinal));
    }

    private static async Task AcknowledgementsAreMonotonic()
    {
        var adapter = new DelegateAdapter();
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        await session.DispatchAsync(Mutation(0));
        await session.DispatchAsync(Mutation(1));
        True(session.AcknowledgedRevision is null);

        True((await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 1))).Succeeded);
        True((await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 0))).Succeeded);
        Equal<long?>(1L, session.AcknowledgedRevision);

        MvvmResponse future = await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 3));
        False(future.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, future.Fault!.Code);
        Equal<long?>(1L, session.AcknowledgedRevision);
    }

    private static async Task OneSessionSerializesMutations()
    {
        var adapter = new DelegateAdapter();
        adapter.Dispatch = async (_, cancellationToken) =>
        {
            int concurrent = Interlocked.Increment(ref adapter.Concurrent);
            UpdateMaximum(ref adapter.MaxConcurrent, concurrent);
            await Task.Delay(30, cancellationToken);
            Interlocked.Decrement(ref adapter.Concurrent);
            Interlocked.Increment(ref adapter.DispatchCount);
            return MvvmBindingResult.Success();
        };

        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        Task<MvvmResponse> first = session.DispatchAsync(Mutation(0)).AsTask();
        Task<MvvmResponse> second = session.DispatchAsync(Mutation(0)).AsTask();
        MvvmResponse[] responses = await Task.WhenAll(first, second);

        Equal(1, adapter.MaxConcurrent);
        Equal(1, responses.Count(static response => response.Succeeded));
        Equal(1, responses.Count(static response => response.Fault?.Code == MvvmFaultCodes.RevisionStale));
        Equal(1L, session.Revision);
    }

    private static async Task CancellationBypassesTheDispatchGate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId targetId = Id();
        Task<MvvmResponse> target = session.DispatchAsync(Mutation(0, targetId, MvvmMutationKind.ExecuteCommand)).AsTask();
        await started.Task;

        MvvmResponse cancellation = await session.DispatchAsync(new MvvmCancelRequest(Id(), targetId));
        MvvmResponse cancelled = await target;
        True(cancellation.Succeeded);
        Equal<bool?>(true, cancellation.CancellationAccepted);
        False(cancelled.Succeeded);
        Equal(MvvmFaultCodes.RequestCancelled, cancelled.Fault!.Code);
        Equal(0L, session.Revision);
    }

    private static async Task TimeoutHasAStableFault()
    {
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        var limits = MvvmLimits.Default with { MaxCommandDuration = TimeSpan.FromMilliseconds(30) };
        await using IMvvmSessionFactory factory = Factory(adapter, limits);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        MvvmResponse response = await session.DispatchAsync(Mutation(0, kind: MvvmMutationKind.ExecuteCommand));
        False(response.Succeeded);
        Equal(MvvmFaultCodes.RequestTimeout, response.Fault!.Code);
        Equal(0L, session.Revision);
    }

    private static async Task ThrowingCancellationCallbacksAreContained()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    static () => throw new SensitiveFailure("callback secret"));
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId targetId = Id();
        Task<MvvmResponse> target = session.DispatchAsync(Mutation(0, targetId)).AsTask();
        await started.Task;

        MvvmResponse cancellation = await session.DispatchAsync(new MvvmCancelRequest(Id(), targetId));
        MvvmResponse cancelled = await target;
        True(cancellation.Succeeded);
        Equal<bool?>(true, cancellation.CancellationAccepted);
        Equal(MvvmFaultCodes.RequestCancelled, cancelled.Fault!.Code);
    }

    private static async Task TimeoutWinsOverLateCancellationAndSuccess()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return MvvmBindingResult.Success(patches: [new MvvmPropertyPatch(1, Json("9"))]);
            },
        };
        var limits = MvvmLimits.Default with { MaxCommandDuration = TimeSpan.FromMilliseconds(30) };
        await using IMvvmSessionFactory factory = Factory(adapter, limits);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId targetId = Id();
        Task<MvvmResponse> target = session.DispatchAsync(
            Mutation(0, targetId, MvvmMutationKind.ExecuteCommand)).AsTask();
        await started.Task;
        await Task.Delay(80);

        Task<MvvmResponse> lateCancelTask = session.DispatchAsync(new MvvmCancelRequest(Id(), targetId)).AsTask();
        False(lateCancelTask.IsCompleted);
        release.SetResult();
        MvvmResponse lateCancel = await lateCancelTask;
        MvvmResponse timedOut = await target;

        True(lateCancel.Succeeded);
        Equal<bool?>(false, lateCancel.CancellationAccepted);
        False(timedOut.Succeeded);
        Equal(MvvmFaultCodes.RequestTimeout, timedOut.Fault!.Code);
        Equal(1L, timedOut.Revision);
        Equal(1L, session.Revision);
        Equal(1, timedOut.Patches.Count);

        MvvmResponse completedCancel = await session.DispatchAsync(new MvvmCancelRequest(Id(), targetId));
        True(completedCancel.Succeeded);
        Equal<bool?>(false, completedCancel.CancellationAccepted);
    }

    private static async Task PendingAndSessionLimitsAreEnforced()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        var limits = MvvmLimits.Default with { MaxPendingRequests = 1, MaxSessions = 1 };
        await using IMvvmSessionFactory factory = Factory(adapter, limits);
        await using IMvvmSession firstSession = await factory.OpenAsync(new MvvmContract("counter"));
        await ThrowsAsync<InvalidOperationException>(async () =>
        {
            await factory.OpenAsync(new MvvmContract("counter"));
        });

        Task<MvvmResponse> pending = firstSession.DispatchAsync(Mutation(0)).AsTask();
        await started.Task;
        MvvmResponse overflow = await firstSession.DispatchAsync(new MvvmSnapshotRequest(Id()));
        False(overflow.Succeeded);
        Equal(MvvmFaultCodes.LimitExceeded, overflow.Fault!.Code);
        release.SetResult();
        True((await pending).Succeeded);
    }

    private static async Task FactoryRecoversAfterActivationFailure()
    {
        int attempts = 0;
        var registry = new MvvmSessionRegistry();
        registry.Map(new MvvmContract("counter"), _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new SensitiveFailure("activation failed");
            }

            return NewActivation(default);
        });

        await using IMvvmSessionFactory factory = registry.Build(MvvmLimits.Default with { MaxSessions = 1 });
        await ThrowsAsync<SensitiveFailure>(async () =>
        {
            await factory.OpenAsync(new MvvmContract("counter"));
        });
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        True(session.CapabilityToken.Length >= 43);
    }

    private static async Task FactoryDisposalDrainsConcurrentOpen()
    {
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter();
        var registry = new MvvmSessionRegistry();
        registry.Map(new MvvmContract("counter"), async _ =>
        {
            activationStarted.SetResult();
            await releaseActivation.Task;
            return new MvvmSessionActivation(adapter);
        });
        IMvvmSessionFactory factory = registry.Build();

        Task<IMvvmSession> opening = factory.OpenAsync(new MvvmContract("counter")).AsTask();
        await activationStarted.Task;
        Task disposing = factory.DisposeAsync().AsTask();
        False(disposing.IsCompleted);
        releaseActivation.SetResult();
        await ThrowsAsync<ObjectDisposedException>(async () => await opening);
        await disposing;
        True(adapter.Disposed);
    }

    private static async Task DisposalIsIdempotentAndReverseOrdered()
    {
        var order = new List<string>();
        var adapter = new DelegateAdapter { DisposeAction = () => order.Add("adapter") };
        var scope = new TrackedResource("scope", order);
        var viewModel = new TrackedResource("viewModel", order);
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("counter"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter, scope, viewModel)));
        await using IMvvmSessionFactory factory = registry.Build();
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        await session.DisposeAsync();
        await session.DisposeAsync();
        Equal("adapter,viewModel,scope", string.Join(',', order));

        MvvmResponse closed = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.SessionClosed, closed.Fault!.Code);
        False(await factory.CloseAsync(session.Id));
    }

    private static async Task ConcurrentDisposalWaitsForTheSameCompletion()
    {
        var disposeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter
        {
            Dispose = async () =>
            {
                disposeStarted.SetResult();
                await releaseDispose.Task;
            },
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        Task first = session.DisposeAsync().AsTask();
        await disposeStarted.Task;
        Task second = session.DisposeAsync().AsTask();
        False(second.IsCompleted);
        releaseDispose.SetResult();
        await Task.WhenAll(first, second);
        True(adapter.Disposed);
    }

    private static IMvvmSessionFactory Factory(DelegateAdapter adapter, MvvmLimits? limits = null)
    {
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("counter"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter)));
        return registry.Build(limits);
    }

    private static ValueTask<MvvmSessionActivation> NewActivation(CancellationToken _) =>
        ValueTask.FromResult(new MvvmSessionActivation(new DelegateAdapter()));

    private static MvvmMutationRequest Mutation(
        long revision,
        MvvmRequestId? requestId = null,
        MvvmMutationKind kind = MvvmMutationKind.SetProperty) =>
        new(requestId ?? Id(), kind, revision, 1, Json("42"));

    private static MvvmRequestId Id() => new(Guid.NewGuid());

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
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

    private static void Run(string name, Action test)
    {
        test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine($"PASS {name}");
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class DelegateAdapter : IMvvmBindingAdapter
    {
        internal Func<MvvmMutationRequest, CancellationToken, ValueTask<MvvmBindingResult>> Dispatch { get; set; }

        internal Action? DisposeAction { get; init; }

        internal Func<ValueTask>? Dispose { get; init; }

        internal bool Disposed { get; private set; }

        internal int DispatchCount;

        internal int Concurrent;

        internal int MaxConcurrent;

        internal DelegateAdapter()
        {
            Dispatch = (_, _) =>
            {
                Interlocked.Increment(ref DispatchCount);
                return ValueTask.FromResult(MvvmBindingResult.Success(patches:
                [
                    new MvvmPropertyPatch(1, Json("42")),
                ]));
            };
        }

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MvvmSnapshot(Json("{\"count\":0}")));

        public ValueTask<MvvmBindingResult> DispatchAsync(
            MvvmMutationRequest request,
            CancellationToken cancellationToken) => Dispatch(request, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            DisposeAction?.Invoke();
            if (Dispose is not null)
            {
                await Dispose();
            }

            Disposed = true;
        }
    }

    private sealed class TrackedResource(string name, List<string> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            order.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SensitiveFailure(string message) : Exception(message);
}
