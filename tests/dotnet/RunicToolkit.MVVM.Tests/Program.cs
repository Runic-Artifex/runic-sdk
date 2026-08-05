using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RunicToolkit.MVVM;

namespace RunicToolkit.MVVM.Tests;

internal static class Program
{
    private static int _passed;
    private static int _assertions;

    public static async Task<int> Main()
    {
        try
        {
            // Isolate delta-based gauges from close callbacks belonging to tests that
            // intentionally exercise deferred consumer cleanup.
            _passed += await RuntimeQueueTests.RunAllAsync();
            _assertions += RuntimeQueueTests.AssertionCount;

            Run(nameof(ContractIdentityIsStrict), ContractIdentityIsStrict);
            Run(nameof(FaultAndLimitContractsAreBounded), FaultAndLimitContractsAreBounded);
            await RunAsync(nameof(RevisionAndSnapshotSemantics), RevisionAndSnapshotSemantics);
            await RunAsync(nameof(AdapterRejectionAndExceptionDoNotAdvance), AdapterRejectionAndExceptionDoNotAdvance);
            await RunAsync(nameof(CommittedFailureAdvancesExactlyOnce), CommittedFailureAdvancesExactlyOnce);
            await RunAsync(nameof(UnsolicitedChangesAdvanceAndPublish), UnsolicitedChangesAdvanceAndPublish);
            await RunAsync(nameof(AcknowledgementsAreMonotonic), AcknowledgementsAreMonotonic);
            await RunAsync(nameof(OneSessionSerializesMutations), OneSessionSerializesMutations);
            await RunAsync(nameof(CancellationBypassesTheDispatchGate), CancellationBypassesTheDispatchGate);
            await RunAsync(nameof(ThrowingCancellationCallbacksAreContained), ThrowingCancellationCallbacksAreContained);
            await RunAsync(nameof(TimeoutHasAStableFault), TimeoutHasAStableFault);
            await RunAsync(nameof(TimeoutPoisonsSessionWhenAdapterIgnoresCancellation), TimeoutPoisonsSessionWhenAdapterIgnoresCancellation);
            await RunAsync(nameof(PendingAndSessionLimitsAreEnforced), PendingAndSessionLimitsAreEnforced);
            await RunAsync(nameof(FactoryRecoversAfterActivationFailure), FactoryRecoversAfterActivationFailure);
            await RunAsync(nameof(FactoryDisposalDrainsConcurrentOpen), FactoryDisposalDrainsConcurrentOpen);
            await RunAsync(nameof(FactoryDisposalBoundsStuckActivation), FactoryDisposalBoundsStuckActivation);
            await RunAsync(nameof(CancelledStuckActivationRetainsAdmissionUntilDeferredCleanup), CancelledStuckActivationRetainsAdmissionUntilDeferredCleanup);
            await RunAsync(nameof(DisposalIsIdempotentAndReverseOrdered), DisposalIsIdempotentAndReverseOrdered);
            await RunAsync(nameof(ConcurrentDisposalWaitsForTheSameCompletion), ConcurrentDisposalWaitsForTheSameCompletion);
            await RunAsync(nameof(SessionDisposalBoundsStuckAdapterWithoutConcurrentResources), SessionDisposalBoundsStuckAdapterWithoutConcurrentResources);
            await RunAsync(nameof(SessionDisposalBoundsStuckResourceWithoutConcurrentDependents), SessionDisposalBoundsStuckResourceWithoutConcurrentDependents);
            Run(nameof(ProjectionSnapshotIsCanonicalAndDetached), ProjectionSnapshotIsCanonicalAndDetached);
            Run(nameof(ProjectionPatchTransactionIsOrderedAndDetached), ProjectionPatchTransactionIsOrderedAndDetached);
            Run(nameof(MvvmValuesAreDetachedAndStrict), MvvmValuesAreDetachedAndStrict);
            Run(nameof(GeneratedBindingReferencesAreStable), GeneratedBindingReferencesAreStable);
            await RunAsync(nameof(BindingVocabularyIsClosedAndDeterministic), BindingVocabularyIsClosedAndDeterministic);
            await RunAsync(nameof(BindingAdapterLifecycleIsDeterministic), BindingAdapterLifecycleIsDeterministic);
            await RunAsync(nameof(ProviderSnapshotViolationPoisonsWithoutRevision), ProviderSnapshotViolationPoisonsWithoutRevision);
            await RunAsync(nameof(ProviderPatchViolationPoisonsWithoutRevision), ProviderPatchViolationPoisonsWithoutRevision);
            await RunAsync(nameof(ReconnectSnapshotWaitsForCommittedMutation), ReconnectSnapshotWaitsForCommittedMutation);
            await RunAsync(nameof(ConcurrentAcknowledgementsConvergeMonotonically), ConcurrentAcknowledgementsConvergeMonotonically);
            await RunAsync(nameof(DuplicateInFlightRequestIdsAreRejected), DuplicateInFlightRequestIdsAreRejected);
            await RunAsync(nameof(CompletedMutationRequestIdsCannotReplay), CompletedMutationRequestIdsCannotReplay);
            await RunAsync(nameof(CancelAfterCompletionReportsPublishedRevision), CancelAfterCompletionReportsPublishedRevision);
            await RunAsync(nameof(CompletedRequestLedgerIsBoundedAndFailsClosed), CompletedRequestLedgerIsBoundedAndFailsClosed);
            await RunAsync(nameof(CancelFloodAgainstStuckAdapterCompletesPromptly), CancelFloodAgainstStuckAdapterCompletesPromptly);
            await RunAsync(nameof(CloseStopsAdmissionAndDrainsQueuedWork), CloseStopsAdmissionAndDrainsQueuedWork);
            Console.WriteLine($"PASS: {_passed} runtime contract tests, {_assertions} assertions");
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
        Equal("runic.toolkit.mvvm/1", MvvmProtocol.Identity);
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
        Equal(TimeSpan.FromSeconds(30), MvvmLimits.Default.MaxShutdownDuration);
        (MvvmLimits.Default with { MaxCommandDuration = TimeSpan.FromMinutes(5) }).Validate();
        Throws<ArgumentOutOfRangeException>(() =>
            (MvvmLimits.Default with { MaxPendingRequests = MvvmLimits.MaximumPendingRequests + 1 }).Validate());
        Throws<ArgumentOutOfRangeException>(() =>
            (MvvmLimits.Default with { MaxCommandDuration = TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1) }).Validate());
        Throws<ArgumentOutOfRangeException>(() =>
            (MvvmLimits.Default with { MaxShutdownDuration = TimeSpan.Zero }).Validate());
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmMutationRequest(Id(), (MvvmMutationKind)99, 0, 1, Json("null")));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmCollectionPatch(1, (MvvmCollectionOperation)99, 0, []));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmCollectionPatch(1, MvvmCollectionOperation.Insert, 10_000, []));
        Throws<ArgumentOutOfRangeException>(() =>
            _ = new MvvmCollectionMovePatch(1, 0, 10_000, 1));
    }

    private static void GeneratedBindingReferencesAreStable()
    {
        MvvmPropertyReference property = MvvmPropertyReference.Create("NewTitle");
        MvvmCollectionReference collection = MvvmCollectionReference.Create("Items");
        MvvmCommandReference command = MvvmCommandReference.Create("AddCommand");

        Equal(1_521_409_795, property.MemberId);
        Equal(404_215_890, collection.MemberId);
        Equal(1_234_597_803, command.MemberId);
        Equal("NewTitle", property.GeneratedMemberName);
        Equal("Items", collection.GeneratedMemberName);
        Equal("AddCommand", command.GeneratedMemberName);
        Equal(property, MvvmPropertyReference.Create("NewTitle"));
        Throws<ArgumentException>(() => MvvmCommandReference.Create(""));
        Throws<ArgumentException>(() => MvvmCommandReference.Create(" "));
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

    private static async Task UnsolicitedChangesAdvanceAndPublish()
    {
        var adapter = new ChangeSourceAdapter();
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        var published = new TaskCompletionSource<MvvmProjectionChangedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.ProjectionChanged += (_, eventArgs) => published.TrySetResult(eventArgs);

        adapter.Value = 17;
        adapter.RaiseChanged();

        MvvmProjectionChangedEventArgs change = await published.Task
            .WaitAsync(TimeSpan.FromSeconds(5));
        Equal(0L, change.FromRevision);
        Equal(1L, change.Response.Revision);
        Equal(1L, session.Revision);
        MvvmPropertyPatch patch = (MvvmPropertyPatch)change.Response.Patches.Single();
        Equal(17, patch.Value.GetInt32());
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
        var limits = MvvmLimits.Default with
        {
            MaxCommandDuration = TimeSpan.FromMilliseconds(30),
            MaxShutdownDuration = TimeSpan.FromMilliseconds(30),
        };
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

    private static async Task TimeoutPoisonsSessionWhenAdapterIgnoresCancellation()
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
        var limits = MvvmLimits.Default with
        {
            MaxCommandDuration = TimeSpan.FromMilliseconds(30),
            MaxShutdownDuration = TimeSpan.FromMilliseconds(30),
        };
        await using IMvvmSessionFactory factory = Factory(adapter, limits);
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        Task<MvvmResponse> target = session.DispatchAsync(
            Mutation(0, kind: MvvmMutationKind.ExecuteCommand)).AsTask();
        await started.Task;

        MvvmResponse timedOut = await target.WaitAsync(TimeSpan.FromSeconds(2));
        False(timedOut.Succeeded);
        Equal(MvvmFaultCodes.RequestTimeout, timedOut.Fault!.Code);
        Equal(0L, timedOut.Revision);
        Equal(0L, session.Revision);
        Equal(0, timedOut.Patches.Count);

        MvvmResponse poisoned = await session.DispatchAsync(new MvvmSnapshotRequest(Id())).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        False(poisoned.Succeeded);
        Equal(MvvmFaultCodes.SessionClosed, poisoned.Fault!.Code);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await Task.Delay(20);
        Equal(0L, session.Revision);
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

    private static async Task FactoryDisposalBoundsStuckActivation()
    {
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapterDisposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter { DisposeAction = adapterDisposed.SetResult };
        var registry = new MvvmSessionRegistry();
        registry.Map(new MvvmContract("counter"), async _ =>
        {
            activationStarted.SetResult();
            await releaseActivation.Task;
            return new MvvmSessionActivation(adapter);
        });
        IMvvmSessionFactory factory = registry.Build(
            MvvmLimits.Default with { MaxShutdownDuration = TimeSpan.FromMilliseconds(30) });

        Task<IMvvmSession> opening = factory.OpenAsync(new MvvmContract("counter")).AsTask();
        await activationStarted.Task;
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await ThrowsAsync<ObjectDisposedException>(async () =>
            await opening.WaitAsync(TimeSpan.FromSeconds(2)));
        await ThrowsAsync<ObjectDisposedException>(async () =>
            await factory.OpenAsync(new MvvmContract("counter")));

        releaseActivation.SetResult();
        await adapterDisposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        True(adapter.Disposed);
    }

    private static async Task CancelledStuckActivationRetainsAdmissionUntilDeferredCleanup()
    {
        var activationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActivation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceDisposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResourceDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateAdapter = new DelegateAdapter();
        var lateResource = new DelegateResource(async () =>
        {
            resourceDisposalStarted.SetResult();
            await releaseResourceDisposal.Task;
        });
        int activations = 0;
        var registry = new MvvmSessionRegistry();
        registry.Map(new MvvmContract("counter"), async _ =>
        {
            int activation = Interlocked.Increment(ref activations);
            if (activation == 1)
            {
                activationStarted.SetResult();
                await releaseActivation.Task;
                return new MvvmSessionActivation(lateAdapter, lateResource);
            }

            return new MvvmSessionActivation(new DelegateAdapter());
        });
        IMvvmSessionFactory factory = registry.Build(MvvmLimits.Default with
        {
            MaxSessions = 1,
            MaxShutdownDuration = TimeSpan.FromMilliseconds(30),
        });

        using var cancellation = new CancellationTokenSource();
        Task<IMvvmSession> cancelledOpen = factory.OpenAsync(
            new MvvmContract("counter"),
            cancellation.Token).AsTask();
        await activationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
            await cancelledOpen.WaitAsync(TimeSpan.FromSeconds(2)));

        for (int attempt = 0; attempt < 8; attempt++)
        {
            await ThrowsAsync<InvalidOperationException>(async () =>
                await factory.OpenAsync(new MvvmContract("counter")));
        }

        Equal(1, Volatile.Read(ref activations));
        releaseActivation.SetResult();
        await resourceDisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        True(lateAdapter.Disposed);
        Equal(1, lateResource.DisposeCalls);
        await ThrowsAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(new MvvmContract("counter")));
        Equal(1, Volatile.Read(ref activations));

        releaseResourceDisposal.SetResult();
        await lateResource.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        IMvvmSession? recovered = null;
        var recoveryDeadline = Stopwatch.StartNew();
        while (recovered is null && recoveryDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            try
            {
                recovered = await factory.OpenAsync(new MvvmContract("counter"));
            }
            catch (InvalidOperationException)
            {
                await Task.Delay(10);
            }
        }

        True(recovered is not null);
        Equal(2, Volatile.Read(ref activations));
        await ThrowsAsync<InvalidOperationException>(async () =>
            await factory.OpenAsync(new MvvmContract("counter")));
        Equal(2, Volatile.Read(ref activations));
        await recovered!.DisposeAsync();
        await factory.DisposeAsync();
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
        await using IMvvmSessionFactory factory = Factory(
            adapter,
            MvvmLimits.Default with { MaxShutdownDuration = TimeSpan.FromMilliseconds(30) });
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        Task first = session.DisposeAsync().AsTask();
        await disposeStarted.Task;
        Task second = session.DisposeAsync().AsTask();
        False(second.IsCompleted);
        releaseDispose.SetResult();
        await Task.WhenAll(first, second);
        True(adapter.Disposed);
    }

    private static async Task SessionDisposalBoundsStuckAdapterWithoutConcurrentResources()
    {
        var adapterDisposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdapter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int adapterDisposals = 0;
        var resource = new DelegateResource();
        var adapter = new DelegateAdapter
        {
            Dispose = async () =>
            {
                Interlocked.Increment(ref adapterDisposals);
                adapterDisposalStarted.SetResult();
                await releaseAdapter.Task;
            },
        };
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("counter"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter, resource)));
        IMvvmSessionFactory factory = registry.Build(
            MvvmLimits.Default with { MaxShutdownDuration = TimeSpan.FromMilliseconds(30) });
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        Task first = session.DisposeAsync().AsTask();
        await adapterDisposalStarted.Task;
        Task second = session.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Equal(1, Volatile.Read(ref adapterDisposals));
        Equal(0, resource.DisposeCalls);
        MvvmResponse closed = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.SessionClosed, closed.Fault!.Code);

        releaseAdapter.SetResult();
        await resource.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(1, resource.DisposeCalls);
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task SessionDisposalBoundsStuckResourceWithoutConcurrentDependents()
    {
        var stuckStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStuck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dependent = new DelegateResource();
        var stuck = new DelegateResource(async () =>
        {
            stuckStarted.SetResult();
            await releaseStuck.Task;
        });
        var adapter = new DelegateAdapter();
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("counter"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter, dependent, stuck)));
        IMvvmSessionFactory factory = registry.Build(
            MvvmLimits.Default with { MaxShutdownDuration = TimeSpan.FromMilliseconds(30) });
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));

        Task disposal = session.DisposeAsync().AsTask();
        await stuckStarted.Task;
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(1, stuck.DisposeCalls);
        Equal(0, dependent.DisposeCalls);
        True(adapter.Disposed);
        releaseStuck.SetResult();
        await stuck.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        await dependent.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(1, dependent.DisposeCalls);
        await factory.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void ProjectionSnapshotIsCanonicalAndDetached()
    {
        var items = new List<JsonElement> { Json("{\"id\":1}") };
        var errors = new List<string> { "line\nsecret" };
        var builder = new MvvmProjectionSnapshotBuilder()
            .AddValidation(4, errors)
            .AddCommand(3, canExecute: true, isExecuting: false)
            .AddCollection(2, items)
            .AddProperty(1, Json("{\"name\":\"Ada\"}"));

        MvvmSnapshot snapshot = builder.Build();
        items.Clear();
        errors.Clear();

        Equal(4, builder.Count);
        Equal(
            "{\"members\":[{\"type\":\"property\",\"member\":1,\"value\":{\"name\":\"Ada\"}},{\"type\":\"collection\",\"member\":2,\"items\":[{\"id\":1}]},{\"type\":\"command\",\"member\":3,\"canExecute\":true,\"isExecuting\":false},{\"type\":\"validation\",\"member\":4,\"errors\":[\"line secret\"]}]}",
            snapshot.State.GetRawText());
        Throws<ArgumentException>(() => builder.AddProperty(1, Json("null")));
        Throws<ArgumentException>(() => _ = new MvvmProjectionProperty(0, Json("null")));
        Throws<ArgumentException>(() => _ = new MvvmProjectionCollectionMember(1, [default]));
    }

    private static void ProjectionPatchTransactionIsOrderedAndDetached()
    {
        var items = new List<JsonElement> { Json("1"), Json("2") };
        var builder = new MvvmProjectionPatchBuilder()
            .Property(5, Json("true"))
            .Collection(6, MvvmCollectionOperation.Insert, 0, items)
            .CollectionMove(6, 0, 1, 1)
            .Command(7, canExecute: false, isExecuting: true)
            .Validation(8, ["bad\rvalue"]);
        IReadOnlyList<MvvmPatch> first = builder.Build();
        MvvmBindingResult result = builder.Success(Json("{\"ok\":true}"));
        items.Clear();
        builder.Property(9, Json("null"));

        Equal(6, builder.Count);
        Equal(5, first.Count);
        Equal(5, result.Patches.Count);
        Equal(MvvmPatchKind.Property, first[0].Kind);
        Equal(MvvmPatchKind.Collection, first[1].Kind);
        Equal(MvvmPatchKind.CollectionMove, first[2].Kind);
        Equal(MvvmPatchKind.Command, first[3].Kind);
        Equal(MvvmPatchKind.Validation, first[4].Kind);
        Equal(2, ((MvvmCollectionPatch)first[1]).Items.Count);
        Equal("bad value", ((MvvmValidationPatch)first[4]).Errors[0]);
        True(result.Succeeded);
        True(result.Committed);
        Equal(true, result.Payload!.Value.GetProperty("ok").GetBoolean());
    }

    private static void MvvmValuesAreDetachedAndStrict()
    {
        JsonElement objectValue = MvvmValue.Create(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("text", "strict");
            writer.WriteEndObject();
        });
        Equal("{\"text\":\"strict\"}", objectValue.GetRawText());
        Equal(JsonValueKind.Null, MvvmValue.Null.ValueKind);
        Equal("hello", MvvmValue.From("hello").GetString());
        Equal(true, MvvmValue.From(true).GetBoolean());
        Equal(-42L, MvvmValue.From(-42L).GetInt64());
        Equal(42UL, MvvmValue.From(42UL).GetUInt64());
        Equal(1.25m, MvvmValue.From(1.25m).GetDecimal());
        Equal(1.5d, MvvmValue.From(1.5d).GetDouble());
        Throws<ArgumentNullException>(() => MvvmValue.Create(null!));
        Throws<JsonException>(() => MvvmValue.Create(static _ => { }));
        Throws<ArgumentException>(() => MvvmValue.From(double.NaN));
    }

    private static async Task BindingVocabularyIsClosedAndDeterministic()
    {
        var vocabulary = new MvvmBindingVocabulary(
        [
            new MvvmBindingMember(3, MvvmBindingMemberKind.Collection, "Items"),
            new MvvmBindingMember(2, MvvmBindingMemberKind.Command, "Save"),
            new MvvmBindingMember(1, MvvmBindingMemberKind.Property, "Name"),
        ]);
        Equal(3, vocabulary.Members.Count);
        Equal(1, vocabulary.Members[0].MemberId);
        Equal(MvvmBindingMemberKind.Property, vocabulary.Members[0].Kind);
        Equal(MvvmBindingMemberKind.Command, vocabulary.Members[1].Kind);
        Equal(MvvmBindingMemberKind.Collection, vocabulary.Members[2].Kind);
        True(vocabulary.TryResolve(new MvvmMutationRequest(Id(), MvvmMutationKind.ExecuteCommand, 0, 2, Json("null")), out MvvmBindingMember? command));
        Equal("Save", command!.DiagnosticName);
        False(vocabulary.TryResolve(new MvvmMutationRequest(Id(), MvvmMutationKind.ExecuteCommand, 0, 1, Json("null")), out _));
        True(vocabulary.TryGetMember(3, out MvvmBindingMember? collection));
        Equal(MvvmBindingMemberKind.Collection, collection!.Kind);
        Throws<ArgumentException>(() => _ = new MvvmBindingVocabulary(
        [
            new MvvmBindingMember(1, MvvmBindingMemberKind.Property),
            new MvvmBindingMember(1, MvvmBindingMemberKind.Command),
        ]));

        MvvmSnapshot projected = new MvvmProjectionSnapshotBuilder(vocabulary)
            .AddCommand(2, canExecute: true, isExecuting: false)
            .AddCollection(3, [Json("1")])
            .AddProperty(1, Json("\"Ada\""))
            .AddValidation(1, ["required"])
            .Build();
        Equal(4, projected.State.GetProperty("members").GetArrayLength());
        Throws<InvalidOperationException>(() => new MvvmProjectionSnapshotBuilder(vocabulary)
            .AddProperty(1, Json("null"))
            .Build());
        Throws<ArgumentException>(() => new MvvmProjectionSnapshotBuilder(vocabulary)
            .AddProperty(2, Json("null")));
        var validatedPatches = new MvvmProjectionPatchBuilder(vocabulary)
            .Property(1, Json("\"Grace\""))
            .Collection(3, MvvmCollectionOperation.Insert, 0, [Json("2")])
            .Command(2, canExecute: false, isExecuting: true)
            .Validation(3, ["invalid"]);
        Equal(4, validatedPatches.Build().Count);
        Throws<ArgumentException>(() => validatedPatches.Property(3, Json("null")));

        int propertyCalls = 0;
        int commandCalls = 0;
        await using IMvvmBindingAdapter adapter = new MvvmBindingAdapterBuilder(
            _ => ValueTask.FromResult(projected),
            vocabulary)
            .BindProperty(1, (request, _) =>
            {
                Interlocked.Increment(ref propertyCalls);
                return ValueTask.FromResult(MvvmBindingResult.Success(request.Payload));
            })
            .BindCommand(2, (_, _) =>
            {
                Interlocked.Increment(ref commandCalls);
                return ValueTask.FromResult(MvvmBindingResult.Success());
            })
            .Build();
        var provider = adapter as IMvvmBindingVocabularyProvider;
        True(provider is not null);
        True(ReferenceEquals(vocabulary, provider!.Vocabulary));
        Equal(3, provider!.Vocabulary.Members.Count);
        True((await adapter.DispatchAsync(new MvvmMutationRequest(Id(), MvvmMutationKind.SetProperty, 0, 1, Json("7")), default)).Succeeded);
        True((await adapter.DispatchAsync(new MvvmMutationRequest(Id(), MvvmMutationKind.ExecuteCommand, 0, 2, Json("null")), default)).Succeeded);
        MvvmBindingResult unknown = await adapter.DispatchAsync(
            new MvvmMutationRequest(Id(), MvvmMutationKind.SetProperty, 0, 3, Json("null")), default);
        False(unknown.Succeeded);
        Equal(MvvmFaultCodes.MemberUnknown, unknown.Fault!.Code);
        Equal(1, propertyCalls);
        Equal(1, commandCalls);
    }

    private static async Task BindingAdapterLifecycleIsDeterministic()
    {
        int disposals = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = new MvvmBindingAdapterBuilder(
            _ => ValueTask.FromResult(new MvvmSnapshot(Json("{}"))))
            .BindProperty(1, (_, _) => ValueTask.FromResult(MvvmBindingResult.Success()))
            .OnDispose(async () =>
            {
                Interlocked.Increment(ref disposals);
                await release.Task;
            });
        IMvvmBindingAdapter adapter = builder.Build();
        Throws<InvalidOperationException>(() => builder.Build());
        Throws<InvalidOperationException>(() => builder.BindCommand(2, (_, _) => ValueTask.FromResult(MvvmBindingResult.Success())));

        Task first = adapter.DisposeAsync().AsTask();
        Task second = adapter.DisposeAsync().AsTask();
        False(first.IsCompleted);
        False(second.IsCompleted);
        Equal(1, Volatile.Read(ref disposals));
        release.SetResult();
        await Task.WhenAll(first, second);
        await ThrowsAsync<ObjectDisposedException>(async () => await adapter.SnapshotAsync(default));
        await ThrowsAsync<ObjectDisposedException>(async () => await adapter.DispatchAsync(Mutation(0), default));
    }

    private static async Task ProviderSnapshotViolationPoisonsWithoutRevision()
    {
        var vocabulary = new MvvmBindingVocabulary(
            [new MvvmBindingMember(1, MvvmBindingMemberKind.Property)]);
        var adapter = new VocabularyAdapter(vocabulary)
        {
            Snapshot = _ => ValueTask.FromResult(new MvvmSnapshot(Json(
                "{\"members\":[{\"type\":\"command\",\"member\":1,\"canExecute\":true,\"isExecuting\":false}]}"))),
        };
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("provider-snapshot"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter)));
        await using IMvvmSessionFactory factory = registry.Build();
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("provider-snapshot"));

        MvvmResponse invalid = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        False(invalid.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, invalid.Fault!.Code);
        Equal(0L, invalid.Revision);
        Equal(0L, session.Revision);
        MvvmResponse closed = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.SessionClosed, closed.Fault!.Code);
    }

    private static async Task ProviderPatchViolationPoisonsWithoutRevision()
    {
        var vocabulary = new MvvmBindingVocabulary(
            [new MvvmBindingMember(1, MvvmBindingMemberKind.Property)]);
        var adapter = new VocabularyAdapter(vocabulary)
        {
            Snapshot = _ => ValueTask.FromResult(new MvvmSnapshot(Json(
                "{\"members\":[{\"type\":\"property\",\"member\":1,\"value\":0}]}"))),
            Dispatch = (_, _) => ValueTask.FromResult(MvvmBindingResult.Success(
                patches: [new MvvmCommandPatch(1, canExecute: true, isExecuting: false)])),
        };
        var registry = new MvvmSessionRegistry();
        registry.Map(
            new MvvmContract("provider-patch"),
            _ => ValueTask.FromResult(new MvvmSessionActivation(adapter)));
        await using IMvvmSessionFactory factory = registry.Build();
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("provider-patch"));

        MvvmResponse invalid = await session.DispatchAsync(Mutation(0));
        False(invalid.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, invalid.Fault!.Code);
        Equal(0L, invalid.Revision);
        Equal(0L, session.Revision);
        Equal(0, invalid.Patches.Count);
        MvvmResponse closed = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.SessionClosed, closed.Fault!.Code);
    }

    private static async Task ReconnectSnapshotWaitsForCommittedMutation()
    {
        var mutationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMutation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int state = 0;
        var adapter = new DelegateAdapter
        {
            Snapshot = _ => ValueTask.FromResult(new MvvmSnapshot(Json($"{{\"count\":{Volatile.Read(ref state)}}}"))),
            Dispatch = async (_, cancellationToken) =>
            {
                mutationStarted.SetResult();
                await releaseMutation.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref state);
                return MvvmBindingResult.Success(patches: [new MvvmPropertyPatch(1, Json("1"))]);
            },
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        Task<MvvmResponse> mutation = session.DispatchAsync(Mutation(0)).AsTask();
        await mutationStarted.Task;
        Task<MvvmResponse> reconnect = session.DispatchAsync(new MvvmSnapshotRequest(Id())).AsTask();
        False(reconnect.IsCompleted);
        releaseMutation.SetResult();

        MvvmResponse mutationResponse = await mutation;
        MvvmResponse snapshot = await reconnect;
        True(mutationResponse.Succeeded);
        Equal(1L, mutationResponse.Revision);
        True(snapshot.Succeeded);
        Equal(1L, snapshot.Revision);
        Equal(1, snapshot.Payload!.Value.GetProperty("count").GetInt32());
    }

    private static async Task ConcurrentAcknowledgementsConvergeMonotonically()
    {
        var adapter = new DelegateAdapter();
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        for (long revision = 0; revision < 8; revision++)
        {
            True((await session.DispatchAsync(Mutation(revision))).Succeeded);
        }

        long[] revisions = [3, 0, 7, 4, 8, 2, 6, 1, 5];
        MvvmResponse[] responses = await Task.WhenAll(revisions.Select(revision =>
            session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), revision)).AsTask()));
        Equal(9, responses.Count(static response => response.Succeeded));
        Equal<long?>(8L, session.AcknowledgedRevision);

        MvvmResponse future = await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 9));
        False(future.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, future.Fault!.Code);
        Equal<long?>(8L, session.AcknowledgedRevision);
    }

    private static async Task DuplicateInFlightRequestIdsAreRejected()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId requestId = Id();
        Task<MvvmResponse> first = session.DispatchAsync(Mutation(0, requestId)).AsTask();
        await started.Task;

        MvvmResponse duplicate = await session.DispatchAsync(Mutation(0, requestId));
        False(duplicate.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, duplicate.Fault!.Code);
        Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        True((await first).Succeeded);
    }

    private static async Task CompletedMutationRequestIdsCannotReplay()
    {
        int calls = 0;
        var adapter = new DelegateAdapter
        {
            Dispatch = (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromResult(MvvmBindingResult.Success());
            },
        };
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId requestId = Id();
        MvvmResponse first = await session.DispatchAsync(Mutation(0, requestId));
        MvvmResponse replay = await session.DispatchAsync(Mutation(1, requestId));

        True(first.Succeeded);
        False(replay.Succeeded);
        Equal(MvvmFaultCodes.RequestInvalid, replay.Fault!.Code);
        Equal(1, Volatile.Read(ref calls));
        Equal(1L, session.Revision);
    }

    private static async Task CancelAfterCompletionReportsPublishedRevision()
    {
        var adapter = new DelegateAdapter();
        await using IMvvmSessionFactory factory = Factory(adapter);
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId targetId = Id();
        MvvmResponse mutation = await session.DispatchAsync(Mutation(0, targetId));

        MvvmResponse cancellation = await session.DispatchAsync(new MvvmCancelRequest(Id(), targetId));
        True(mutation.Succeeded);
        Equal(1L, mutation.Revision);
        True(cancellation.Succeeded);
        Equal<bool?>(false, cancellation.CancellationAccepted);
        Equal(mutation.Revision, cancellation.Revision);
        Equal(session.Revision, cancellation.Revision);
    }

    private static async Task CompletedRequestLedgerIsBoundedAndFailsClosed()
    {
        const int ledgerCapacity = 65_536;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        await using IMvvmSessionFactory factory = Factory(
            adapter,
            MvvmLimits.Default with { MaxPendingRequests = 1 });
        await using IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        Task<MvvmResponse> active = session.DispatchAsync(Mutation(0)).AsTask();
        await started.Task;

        MvvmResponse? lastBackpressure = null;
        for (int index = 1; index < ledgerCapacity; index++)
        {
            lastBackpressure = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        }

        Equal(MvvmFaultCodes.LimitExceeded, lastBackpressure!.Fault!.Code);
        MvvmResponse exhausted = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        False(exhausted.Succeeded);
        Equal(MvvmFaultCodes.SessionClosed, exhausted.Fault!.Code);
        Equal(1, Volatile.Read(ref calls));

        release.SetResult();
        MvvmResponse terminal = await active;
        True(terminal.Succeeded || terminal.Fault?.Code == MvvmFaultCodes.SessionClosed);
        MvvmResponse remainsClosed = await session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), terminal.Revision));
        Equal(MvvmFaultCodes.SessionClosed, remainsClosed.Fault!.Code);
        Equal(1, Volatile.Read(ref calls));
    }

    private static async Task CancelFloodAgainstStuckAdapterCompletesPromptly()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, _) =>
            {
                started.SetResult();
                await release.Task;
                return MvvmBindingResult.Success();
            },
        };
        await using IMvvmSessionFactory factory = Factory(
            adapter,
            MvvmLimits.Default with { MaxShutdownDuration = TimeSpan.FromMilliseconds(30) });
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        MvvmRequestId targetId = Id();
        Task<MvvmResponse> target = session.DispatchAsync(Mutation(0, targetId)).AsTask();
        await started.Task;

        Task<MvvmResponse>[] cancels = Enumerable.Range(0, 512)
            .Select(_ => session.DispatchAsync(new MvvmCancelRequest(Id(), targetId)).AsTask())
            .ToArray();
        MvvmResponse[] cancellationResponses = await Task.WhenAll(cancels)
            .WaitAsync(TimeSpan.FromSeconds(2));
        MvvmResponse cancelled = await target.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(512, cancellationResponses.Length);
        Equal(1, cancellationResponses.Count(static response => response.CancellationAccepted == true));
        True(cancellationResponses.Count(static response => response.Succeeded) <= MvvmLimits.Default.MaxPendingRequests);
        True(cancellationResponses.All(static response =>
            (response.Succeeded && response.CancellationAccepted is not null) ||
            response.Fault?.Code is MvvmFaultCodes.LimitExceeded or MvvmFaultCodes.SessionClosed));
        Equal(512, cancellationResponses.Count(static response =>
            response.Succeeded ||
            response.Fault?.Code is MvvmFaultCodes.LimitExceeded or MvvmFaultCodes.SessionClosed));
        False(cancelled.Succeeded);
        Equal(MvvmFaultCodes.RequestCancelled, cancelled.Fault!.Code);
        Equal(0L, session.Revision);

        MvvmResponse poisoned = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        Equal(MvvmFaultCodes.SessionClosed, poisoned.Fault!.Code);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
    }

    private static async Task CloseStopsAdmissionAndDrainsQueuedWork()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dispatchCalls = 0;
        var adapter = new DelegateAdapter
        {
            Dispatch = async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref dispatchCalls);
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MvvmBindingResult.Success();
            },
        };
        IMvvmSessionFactory factory = Factory(adapter, MvvmLimits.Default with { MaxPendingRequests = 3 });
        IMvvmSession session = await factory.OpenAsync(new MvvmContract("counter"));
        Task<MvvmResponse> active = session.DispatchAsync(Mutation(0)).AsTask();
        await started.Task;
        Task<MvvmResponse> queuedSnapshot = session.DispatchAsync(new MvvmSnapshotRequest(Id())).AsTask();
        Task<MvvmResponse> queuedAck = session.DispatchAsync(new MvvmAcknowledgeRequest(Id(), 0)).AsTask();

        Task closing = session.DisposeAsync().AsTask();
        MvvmResponse late = await session.DispatchAsync(new MvvmSnapshotRequest(Id()));
        False(late.Succeeded);
        Equal(MvvmFaultCodes.SessionClosed, late.Fault!.Code);

        MvvmResponse[] drained = await Task.WhenAll(active, queuedSnapshot, queuedAck);
        Equal(3, drained.Count(static response => response.Fault?.Code == MvvmFaultCodes.SessionClosed));
        await closing;
        Equal(1, Volatile.Read(ref dispatchCalls));
        True(adapter.Disposed);
        await factory.DisposeAsync();
    }

    private static IMvvmSessionFactory Factory(IMvvmBindingAdapter adapter, MvvmLimits? limits = null)
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
        Interlocked.Increment(ref _assertions);
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static void False(bool value) => True(!value);

    private static void Equal<T>(T expected, T actual)
    {
        Interlocked.Increment(ref _assertions);
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void Throws<TException>(Action action)
        where TException : Exception
    {
        Interlocked.Increment(ref _assertions);
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
        Interlocked.Increment(ref _assertions);
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

        internal Func<CancellationToken, ValueTask<MvvmSnapshot>> Snapshot { get; set; }

        internal Action? DisposeAction { get; init; }

        internal Func<ValueTask>? Dispose { get; init; }

        internal bool Disposed { get; private set; }

        internal int DispatchCount;

        internal int Concurrent;

        internal int MaxConcurrent;

        internal DelegateAdapter()
        {
            Snapshot = _ => ValueTask.FromResult(new MvvmSnapshot(Json("{\"count\":0}")));
            Dispatch = (_, _) =>
            {
                Interlocked.Increment(ref DispatchCount);
                return ValueTask.FromResult(MvvmBindingResult.Success(patches:
                [
                    new MvvmPropertyPatch(1, Json("42")),
                ]));
            };
        }

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Snapshot(cancellationToken);

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

    private sealed class VocabularyAdapter : IMvvmBindingAdapter, IMvvmBindingVocabularyProvider
    {
        internal VocabularyAdapter(MvvmBindingVocabulary vocabulary)
        {
            Vocabulary = vocabulary;
        }

        public MvvmBindingVocabulary Vocabulary { get; }

        internal Func<CancellationToken, ValueTask<MvvmSnapshot>> Snapshot { get; init; } =
            _ => ValueTask.FromResult(new MvvmSnapshot(Json("{\"members\":[]}")));

        internal Func<MvvmMutationRequest, CancellationToken, ValueTask<MvvmBindingResult>> Dispatch { get; init; } =
            (_, _) => ValueTask.FromResult(MvvmBindingResult.Success());

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) => Snapshot(cancellationToken);

        public ValueTask<MvvmBindingResult> DispatchAsync(
            MvvmMutationRequest request,
            CancellationToken cancellationToken) => Dispatch(request, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ChangeSourceAdapter : IMvvmBindingAdapter, IMvvmBindingChangeSource
    {
        public event EventHandler? StateChanged;

        internal int Value { get; set; }

        internal void RaiseChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public ValueTask<MvvmSnapshot> SnapshotAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new MvvmProjectionSnapshotBuilder()
                .AddProperty(1, Json(Value.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Build());

        public ValueTask<MvvmBindingResult> DispatchAsync(
            MvvmMutationRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MvvmBindingResult.Success());

        public ValueTask<IReadOnlyList<MvvmPatch>> ProjectChangesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MvvmPatch>>(
            [
                new MvvmPropertyPatch(
                    1,
                    Json(Value.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            ]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TrackedResource(string name, List<string> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            order.Add(name);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DelegateResource(Func<ValueTask>? dispose = null) : IAsyncDisposable
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCalls;

        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);

        internal Task Completion => _completion.Task;

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            try
            {
                if (dispose is not null)
                {
                    await dispose();
                }
            }
            finally
            {
                _completion.TrySetResult();
            }
        }
    }

    private sealed class SensitiveFailure(string message) : Exception(message);
}
