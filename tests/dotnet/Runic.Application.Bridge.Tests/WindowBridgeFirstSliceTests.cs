using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Bridge;

namespace Runic.Application.Bridge.Tests;

internal static class WindowBridgeFirstSliceTests
{
    internal static async Task RunAsync()
    {
        await NestedReferencesAndPresentationLeases().ConfigureAwait(false);
        await AcceptedWorkSurvivesDetachedParentWithoutProjection().ConfigureAwait(false);
        await AdmissionDoesNotRunSynchronousModelWorkInline().ConfigureAwait(false);
        await ProjectionGuardPreventsLateNestedRoute().ConfigureAwait(false);
        await AcceptedWorkCannotProjectIntoReplacementGeneration().ConfigureAwait(false);
        await OperationIdentityAndTombstonesStayScopedToTheirSource().ConfigureAwait(false);
        await ReentrantAttachAndDetachDoNotHoldTheSessionGate().ConfigureAwait(false);
        await FailedAsyncReattachIsObservedByClose().ConfigureAwait(false);
        await EndpointDrainRetiresIngressBeforeReleasingViewOrScope().ConfigureAwait(false);
        await FailedDetachStillDrainsOwnedScope().ConfigureAwait(false);
        await FailedAttachmentBatchDetachStillDrainsAndFailsClose().ConfigureAwait(false);
        await AttachmentBatchCommitsMultiEndpointMutationAtomically().ConfigureAwait(false);
        DisconnectDrainsRemainingPresentationLeasesAfterFailure();
        await ParentForgetDuringChildAttachLeavesNoOrphanRoute().ConfigureAwait(false);
        await TwoLogicalWindowsIsolateSharedModels().ConfigureAwait(false);
        await ThrowingCancellationCallbackStillDrainsScope().ConfigureAwait(false);
        await BoundedCloseSealsAndSignalsBeforeDeferredDrain().ConfigureAwait(false);
        await CloseAdmissionDoesNotBlockOnCancellationCallback().ConfigureAwait(false);
        await RetiredChildCleanupDoesNotRaceReparenting().ConfigureAwait(false);
        await RootOwnershipSurvivesSlotsAndDeferredRetirement().ConfigureAwait(false);
        await DynamicChildrenRemainRegisteredUntilTheirOwnerForgetsThem().ConfigureAwait(false);
        await PendingExposeFailureDoesNotClaimRootOwnership().ConfigureAwait(false);
        await FailedNewExposeDoesNotRetainRootOwnership().ConfigureAwait(false);
        await FailedNewExposeRemovesEntryBeforeWaitingExposeCanAttach().ConfigureAwait(false);
        await WaitingExposeDoesNotAttachBeforeSuspendedLeaseDrains().ConfigureAwait(false);
        await PresentationTombstonesRemainConnectionOwned().ConfigureAwait(false);
        await DocumentTombstonesRemainDocumentOwned().ConfigureAwait(false);
        await DocumentEpochRetiresReloadedPresentationAndPreservesSameReferenceIndependence().ConfigureAwait(false);
        await LateRetiredPresentationHandleCannotReleaseReplacement().ConfigureAwait(false);
        await InvalidationAdmissionRequiresCurrentPresentationAndGeneration().ConfigureAwait(false);
    }

    private static async Task InvalidationAdmissionRequiresCurrentPresentationAndGeneration()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport);
        var model = new Child();
        WindowBridgeReference reference = session.Expose("editor", model, Attach);
        True(!session.TryAdmitInvalidation(model, "editor", out _),
            "An unpresented reference admitted an invalidation.");
        WindowBridgeConnection connection = WindowBridgeConnection.Create("client", "invalidation");
        WindowBridgeDocumentEpoch first = WindowBridgeDocumentEpoch.Create("0000000000000001AAAAAAAAAAAAAAAA");
        True(session.BeginDocument(connection, first).Accepted, "The invalidation test document was not admitted.");
        using WindowBridgePresentationLease presentation = session.Mount(reference, connection, first, "editor");
        True(session.TryAdmitInvalidation(model, "editor", out WindowBridgeInvalidation admitted)
            && admitted.Reference == reference && admitted.Revision == 1 && session.CanDispatch(admitted),
            "A current presentation did not admit a dispatchable opaque invalidation.");
        session.Suspend(model);
        True(!session.CanDispatch(admitted), "A retired attachment generation remained dispatchable.");
        Equal(reference, session.Expose("editor", model, Attach), "The invalidation test did not reattach the same reference.");
        using WindowBridgePresentationLease replacement = session.Mount(reference, connection, first, "editor");
        True(session.TryAdmitInvalidation(model, "editor", out WindowBridgeInvalidation reattached)
            && reattached.AttachmentGeneration != admitted.AttachmentGeneration && reattached.Revision == 2,
            "Reattachment did not issue a distinct opaque generation and monotonic revision.");
    }

    private static async Task NestedReferencesAndPresentationLeases()
    {
        var transport = new RecordingTransport();
        var scope = new Scope();
        await using var session = new WindowBridgeSession(transport, scope);
        var shell = new Shell();
        var document = new Document();
        WindowBridgeReference shellReference = session.Expose("shell", shell, Attach);
        int invalidOwnerAttachCount = 0;
        Throws(() => session.Present(new WindowBridgeReference("shell", "outside-this-window"), "main", "document", new Document(), (bridge, model, route) =>
        {
            invalidOwnerAttachCount++;
            return bridge.Bind(route, _ => "{}");
        }), "An invalid parent accepted nested content.");
        Equal(0, invalidOwnerAttachCount, "An invalid parent attached an orphan child route.");
        WindowBridgeReference documentReference = session.Present(shellReference, "main", "document", document, Attach);
        Equal(documentReference, session.Expose("document", document, Attach), "A nested model did not retain its reference identity.");
        True(transport.Has("content" + shellReference.Id) && transport.Has("content" + documentReference.Id),
            "Exposed reference routes were not attached.");

        var firstActivation = new ViewPeer(document);
        var secondActivation = new ViewPeer(document);
        WindowBridgeConnection first = WindowBridgeConnection.Create("client", "one");
        WindowBridgeConnection second = WindowBridgeConnection.Create("client", "two");
        WindowBridgePresentationLease left = session.Mount(documentReference, first, "left", firstActivation.Activate);
        WindowBridgePresentationLease leftAgain = session.Mount(documentReference, first, "left", firstActivation.Activate);
        WindowBridgePresentationLease right = session.Mount(documentReference, second, "right", secondActivation.Activate);
        True(ReferenceEquals(left, leftAgain), "The same presentation did not receive an idempotent lease.");
        Equal(1, firstActivation.Starts, "An idempotent mount activated twice.");
        Equal(1, secondActivation.Starts, "The independent presentation did not activate.");
        True(!ReferenceEquals(firstActivation, secondActivation) && ReferenceEquals(document, firstActivation.DataContext)
            && ReferenceEquals(document, secondActivation.DataContext), "Presentations did not receive distinct View peers for their shared DataContext.");
        int presentationWork = 0;
        WindowBridgeOperationAcceptance presentationAcceptance = session.StartOperationFromPresentation(documentReference, first, "presentation-save", _ =>
        {
            presentationWork++;
            return Task.FromResult(WindowBridgeOperationResult.Succeeded());
        });
        True(presentationAcceptance.Accepted, "An active presentation could not admit its own operation.");
        Equal(WindowBridgeOperationKind.Succeeded, (await session.WaitForTerminalAsync(documentReference, "presentation-save").ConfigureAwait(false)).Kind,
            "An active presentation operation did not retain its terminal result.");

        var connectionRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = session.StartOperationFromPresentation(documentReference, first, "connection-owned", async _ =>
        {
            await connectionRelease.Task.ConfigureAwait(false);
            return WindowBridgeOperationResult.Succeeded();
        });
        Equal(WindowBridgeOperationKind.Running, session.LookupOperationFromConnection(documentReference, first, "connection-owned").Kind,
            "The initiating connection could not read its accepted operation.");
        Equal(WindowBridgeOperationKind.Rejected, session.LookupOperationFromConnection(documentReference, second, "connection-owned").Kind,
            "A second mounted connection read another connection's operation.");
        Equal(WindowBridgeOperationKind.Rejected, (await session.WaitForTerminalFromConnectionAsync(documentReference, second, "connection-owned").ConfigureAwait(false)).Kind,
            "A second mounted connection waited on another connection's operation.");
        int duplicateWork = 0;
        WindowBridgeOperationAcceptance duplicateAdmission = session.StartOperationFromPresentation(documentReference, second, "connection-owned", _ =>
        {
            duplicateWork++;
            return Task.FromResult(WindowBridgeOperationResult.Succeeded());
        });
        True(!duplicateAdmission.Accepted && duplicateAdmission.Status.Kind == WindowBridgeOperationKind.Rejected
            && duplicateAdmission.Status.State is null && duplicateWork == 0,
            "A second mounted connection learned or executed another connection's duplicate operation.");
        connectionRelease.TrySetResult();
        Equal(WindowBridgeOperationKind.Succeeded, (await session.WaitForTerminalFromConnectionAsync(documentReference, first, "connection-owned").ConfigureAwait(false)).Kind,
            "The initiating connection did not receive its terminal operation result.");

        session.Unmount(documentReference, second, "left");
        Equal(0, firstActivation.Stops, "A different authenticated connection released another presentation's lease.");
        left.Dispose();
        Equal(1, firstActivation.Stops, "Releasing the first lease did not release its activation.");
        Equal(0, secondActivation.Stops, "Releasing one presentation disposed the other presentation.");
        Equal(1, firstActivation.Disposals, "Releasing the first lease did not dispose its View peer.");
        Equal(0, secondActivation.Disposals, "Releasing one presentation disposed the other View peer.");
        WindowBridgeOperationAcceptance stalePresentation = session.StartOperationFromPresentation(documentReference, first, "stale-presentation", _ =>
        {
            presentationWork++;
            return Task.FromResult(WindowBridgeOperationResult.Succeeded());
        });
        True(!stalePresentation.Accepted && stalePresentation.Status.Kind == WindowBridgeOperationKind.Rejected && presentationWork == 1,
            "A released presentation admitted or executed a source operation.");
        session.Disconnect(second);
        Equal(1, secondActivation.Stops, "Disconnect did not release its own presentation lease.");
        Equal(0, shell.Disposals, "Presentation release disposed the application-owned ViewModel.");

        var editor = new Child();
        var preview = new Child();
        WindowBridgeReference editorReference = session.Present(documentReference, "body", "editor", editor, Attach);
        _ = session.Present(documentReference, "body", "preview", preview, Attach);
        True(!transport.Has("content" + editorReference.Id), "Replacing a parent content slot left its old child endpoint attached.");
        Throws(() => session.Mount(editorReference, first, "stale"), "A replaced child endpoint could still be mounted.");
        Throws(() => session.Resolve("document", "not-from-this-window"), "A fabricated client-shaped identifier resolved in this window.");
    }

    private static async Task LateRetiredPresentationHandleCannotReleaseReplacement()
    {
        await using var session = new WindowBridgeSession(new RecordingTransport());
        var model = new Document();
        WindowBridgeReference reference = session.Expose("document", model, Attach);
        WindowBridgeConnection connection = WindowBridgeConnection.Create("client", "reattached");
        var oldView = new ViewPeer(model);
        WindowBridgePresentationLease oldHandle = session.Mount(reference, connection, "editor", oldView.Activate);

        session.Suspend(model);
        Equal(1, oldView.Stops, "Suspension did not retire the old View activation.");
        Equal(reference, session.Expose("document", model, Attach), "Reattachment changed the model reference.");
        var newView = new ViewPeer(model);
        using WindowBridgePresentationLease newHandle = session.Mount(reference, connection, "editor", newView.Activate);
        oldHandle.Dispose();

        True(session.HasPresentation(reference, connection), "Late old-handle cleanup removed the replacement presentation.");
        Equal(0, newView.Stops, "Late old-handle cleanup disposed the replacement View activation.");
        newHandle.Dispose();
        Equal(1, newView.Stops, "The replacement View did not release normally.");
    }

    private static async Task AcceptedWorkSurvivesDetachedParentWithoutProjection()
    {
        var transport = new RecordingTransport();
        var scope = new Scope();
        await using var session = new WindowBridgeSession(transport, scope);
        var parent = new Parent();
        var child = new Child();
        WindowBridgeReference parentReference = session.Expose("parent", parent, Attach);
        _ = session.Present(parentReference, "main", "child", child, Attach);
        var entered = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int projected = 0;
        WindowBridgeOperationAcceptance acceptance = session.StartOperation(parentReference, "save-1", async _ =>
        {
            entered.Set();
            await release.Task.ConfigureAwait(false);
            parent.Committed = true;
            return WindowBridgeOperationResult.Succeeded();
        }, _ =>
        {
            projected++;
            session.Expose("child", child, Attach);
            return "{\"committed\":true}";
        });
        True(acceptance.Accepted && acceptance.Status.Kind == WindowBridgeOperationKind.Running,
            "The operation was not acknowledged before the asynchronous wait.");
        True(entered.Wait(TimeSpan.FromSeconds(2)), "The accepted operation did not enter model work.");

        session.Suspend(parent);
        session.Suspend(child);
        True(!transport.Has("content" + parentReference.Id), "Suspending the parent left its endpoint attached.");
        release.TrySetResult();
        WindowBridgeOperationStatus terminal = await session.WaitForTerminalAsync(parentReference, "save-1").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Succeeded, terminal.Kind, "Detached work lost its truthful success outcome.");
        Equal<string?>(null, terminal.State, "A detached parent projected state after its route was released.");
        Equal(0, projected, "The detached terminal result invoked a nested content writer.");
        True(parent.Committed, "Detaching the presentation cancelled accepted model work.");

        await session.DisposeAsync().ConfigureAwait(false);
        Equal(1, scope.Disposals, "The window scope was not disposed after owned work and attachments drained.");
    }

    private static async Task AdmissionDoesNotRunSynchronousModelWorkInline()
    {
        await using var session = new WindowBridgeSession(new RecordingTransport());
        WindowBridgeReference reference = session.Expose("parent", new Parent(), Attach);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WindowBridgeOperationAcceptance> admission = Task.Run(() => session.StartOperation(reference, "slow-prefix", _ =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return Task.FromResult(WindowBridgeOperationResult.Succeeded());
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            WindowBridgeOperationAcceptance acceptance = await admission.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            True(acceptance.Accepted && acceptance.Status.Kind == WindowBridgeOperationKind.Running,
                "Synchronous model work held the operation admission callback before it could acknowledge acceptance.");
        }
        finally { release.TrySetResult(); }
        Equal(WindowBridgeOperationKind.Succeeded,
            (await session.WaitForTerminalAsync(reference, "slow-prefix").ConfigureAwait(false)).Kind,
            "Synchronous work lost its terminal result after admission returned.");
    }

    private static async Task OperationIdentityAndTombstonesStayScopedToTheirSource()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport, maximumOperations: 4, maximumRetainedTerminals: 0, maximumExpired: 2);
        var first = new Parent();
        var second = new Parent();
        WindowBridgeReference firstReference = session.Expose("parent", first, Attach);
        WindowBridgeReference secondReference = session.Expose("parent", second, Attach);
        WindowBridgeOperationAcceptance left = session.StartOperation(firstReference, "save", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        WindowBridgeOperationAcceptance right = session.StartOperation(secondReference, "save", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        True(left.Accepted && right.Accepted, "Request identifiers were incorrectly shared between distinct source references.");
        _ = await session.WaitForTerminalAsync(firstReference, "save").ConfigureAwait(false);
        _ = await session.WaitForTerminalAsync(secondReference, "save").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Expired, session.LookupOperation(firstReference, "save").Kind,
            "Evicted terminal work could be mistaken for a fresh request.");
        WindowBridgeOperationAcceptance replay = session.StartOperation(firstReference, "save", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        True(!replay.Accepted && replay.Status.Kind == WindowBridgeOperationKind.Expired,
            "An expired source/request identity re-executed model work.");
        WindowBridgeOperationAcceptance oversized = session.StartOperation(firstReference, new string('x', 129), _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        True(!oversized.Accepted && oversized.Status.Kind == WindowBridgeOperationKind.Rejected,
            "An oversized operation identifier entered the admission table.");
        await using var outcomeSession = new WindowBridgeSession(new RecordingTransport());
        WindowBridgeReference outcomeReference = outcomeSession.Expose("parent", new Parent(), Attach);
        _ = outcomeSession.StartOperation(outcomeReference, "cancelled", _ => Task.FromException<WindowBridgeOperationResult>(new OperationCanceledException()));
        Equal(WindowBridgeOperationKind.Cancelled, (await outcomeSession.WaitForTerminalAsync(outcomeReference, "cancelled").ConfigureAwait(false)).Kind,
            "A command-side cancellation did not produce a cancelled terminal outcome.");
        _ = outcomeSession.StartOperation(outcomeReference, "projection-fault", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()), _ => throw new InvalidOperationException("test projector"));
        WindowBridgeOperationStatus projectionFault = await outcomeSession.WaitForTerminalAsync(outcomeReference, "projection-fault").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Succeeded, projectionFault.Kind, "A projection failure changed a committed command outcome.");
        Equal<string?>(null, projectionFault.State, "A projection failure retained state.");
    }

    private static async Task ProjectionGuardPreventsLateNestedRoute()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport);
        var parent = new Parent();
        var child = new Child();
        WindowBridgeReference parentReference = session.Expose("parent", parent, Attach);
        _ = session.StartOperation(parentReference, "self-detach", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()), _ =>
        {
            session.Suspend(parent);
            Throws(() => session.Expose("child", child, Attach), "A detached projection recreated nested content.");
            return "{\"stale\":true}";
        });
        WindowBridgeOperationStatus terminal = await session.WaitForTerminalAsync(parentReference, "self-detach").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Succeeded, terminal.Kind, "A detached projection changed the accepted operation outcome.");
        Equal<string?>(null, terminal.State, "A detached projection retained stale state.");
        True(!transport.Has("content" + parentReference.Id), "A self-detached parent endpoint remained attached.");
    }

    private static async Task AcceptedWorkCannotProjectIntoReplacementGeneration()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport);
        var parent = new Parent();
        WindowBridgeReference reference = session.Expose("parent", parent, Attach);
        var entered = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int projected = 0;
        _ = session.StartOperation(reference, "replacement", async _ =>
        {
            entered.Set();
            await release.Task.ConfigureAwait(false);
            return WindowBridgeOperationResult.Succeeded();
        }, _ => { projected++; return "{\"stale\":true}"; });
        True(entered.Wait(TimeSpan.FromSeconds(2)), "The replacement-generation operation did not begin.");
        session.Suspend(parent);
        Equal(reference, session.Expose("parent", parent, Attach), "A replacement endpoint did not retain its route identity.");
        release.TrySetResult();
        WindowBridgeOperationStatus terminal = await session.WaitForTerminalAsync(reference, "replacement").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Succeeded, terminal.Kind, "Replacement generation changed a committed operation outcome.");
        Equal<string?>(null, terminal.State, "An operation admitted on an old generation projected state into its replacement.");
        Equal(0, projected, "An old-generation operation invoked its state projector.");
    }

    private static async Task ReentrantAttachAndDetachDoNotHoldTheSessionGate()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport);
        var pending = new Child();
        WindowBridgeReference pendingReference = session.Expose("child", pending, (bridge, _, route) =>
        {
            session.Suspend(pending);
            return bridge.Bind(route, _ => "{}");
        });
        Throws(() => session.Mount(pendingReference, WindowBridgeConnection.Create("client", "pending"), "view"),
            "A route suspended from its attach callback could still be mounted.");

        var model = new Child();
        bool reexposed = false;
        WindowBridgeReference reference = session.Expose("child", model, (bridge, ignoredModel, route) =>
        {
            WindowBridgeEndpointLease binding = bridge.Bind(route, _ => "{}");
            return WindowBridgeAttachment.Create(new Release(() =>
            {
                if (!reexposed)
                {
                    reexposed = true;
                    _ = session.Expose("child", model, (nextBridge, ignoredModel, nextRoute) => nextBridge.Bind(nextRoute, _ => "{}"));
                }
                binding.Dispose();
            }), (WindowBridgeEndpointLease)binding);
        });
        session.Suspend(model);
        True(reexposed && transport.Has("content" + reference.Id), "A nested disposal callback could not reattach its route.");
        session.Forget(model);
        Throws(() => session.Resolve("child", reference.Id), "Forget retained a strong route-table entry for dynamic content.");

        var dynamicParent = new Parent();
        var dynamicChild = new Child();
        WindowBridgeReference dynamicParentReference = session.Expose("dynamic-parent", dynamicParent, Attach);
        WindowBridgeReference dynamicChildReference = session.Present(dynamicParentReference, "main", "dynamic-child", dynamicChild, Attach);
        session.Forget(dynamicParent);
        Throws(() => session.Resolve("dynamic-parent", dynamicParentReference.Id), "Forget retained the dynamic parent route.");
        Throws(() => session.Resolve("dynamic-child", dynamicChildReference.Id), "Forget retained a child route owned only by the forgotten parent.");
    }

    private static async Task FailedDetachStillDrainsOwnedScope()
    {
        var scope = new Scope();
        var session = new WindowBridgeSession(new RecordingTransport(), scope);
        _ = session.Expose("child", new Child(), (_, _, _) => WindowBridgeAttachment.Create(new ThrowingRelease()));
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("A failing attachment cleanup was hidden.");
        }
        catch (AggregateException) { }
        Equal(1, scope.Disposals, "A failing attachment cleanup skipped owned scope disposal.");
    }

    private static async Task FailedAttachmentBatchDetachStillDrainsAndFailsClose()
    {
        await AssertFailedAttachmentBatchDetachStillDrains("entry", throwOnBegin: 2, throwOnEnd: 0).ConfigureAwait(false);
        await AssertFailedAttachmentBatchDetachStillDrains("exit", throwOnBegin: 0, throwOnEnd: 2).ConfigureAwait(false);
    }

    private static async Task AssertFailedAttachmentBatchDetachStillDrains(string phase, int throwOnBegin, int throwOnEnd)
    {
        var transport = new FaultingAttachmentBatchTransport(throwOnBegin, throwOnEnd);
        var scope = new Scope();
        var session = new WindowBridgeSession(transport, scope);
        var resource = new Resource();
        var model = new Child();
        WindowBridgeReference reference = session.Expose("child", model, (routes, _, route) =>
            WindowBridgeAttachment.Create(resource, routes.Bind(route, _ => "{}")));

        try
        {
            session.Suspend(model);
            throw new InvalidOperationException("An attachment batch " + phase + " failure was hidden during detachment.");
        }
        catch (AggregateException exception)
        {
            IReadOnlyCollection<Exception> failures = exception.Flatten().InnerExceptions;
            True(failures.Count(error => error.Message == "expected attachment batch " + phase + " failure") == 1,
                "The attachment batch " + phase + " failure was not surfaced by detachment.");
        }

        Equal(1, resource.Disposals, "An attachment batch " + phase + " failure skipped attachment retirement.");
        True(!transport.Has("content" + reference.Id), "An attachment batch " + phase + " failure retained an endpoint.");

        try
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            throw new InvalidOperationException("An attachment batch " + phase + " failure was hidden by close.");
        }
        catch (AggregateException exception)
        {
            IReadOnlyCollection<Exception> failures = exception.Flatten().InnerExceptions;
            True(failures.Count(error => error.Message == "expected attachment batch " + phase + " failure") == 1,
                "Close did not retain the attachment batch " + phase + " failure.");
        }
        Equal(1, scope.Disposals, "An attachment batch " + phase + " failure stranded close before scope disposal.");
    }

    private static async Task AttachmentBatchCommitsMultiEndpointMutationAtomically()
    {
        var transport = new AtomicAttachmentBatchTransport();
        await using var session = new WindowBridgeSession(transport);
        var model = new Child();
        WindowBridgeReference reference = session.Expose("child", model, (routes, _, route) =>
            WindowBridgeAttachment.Create(
                routes.Bind(route + ".one", _ => "{}"),
                routes.Bind(route + ".two", _ => "{}")));

        Equal(1, transport.Commits, "Attachment creation did not commit one atomic endpoint update.");
        True(transport.Has("content" + reference.Id + ".one") && transport.Has("content" + reference.Id + ".two"),
            "Attachment creation exposed only part of its endpoint set.");

        session.Suspend(model);

        Equal(2, transport.Commits, "Attachment retirement did not commit one atomic endpoint update.");
        True(!transport.Has("content" + reference.Id + ".one") && !transport.Has("content" + reference.Id + ".two"),
            "Attachment retirement exposed a partial endpoint set.");
    }

    private static async Task FailedAsyncReattachIsObservedByClose()
    {
        var transport = new RecordingTransport();
        var scope = new Scope();
        var model = new Child();
        var session = new WindowBridgeSession(transport, scope);
        int attachments = 0;
        WindowBridgeReference? reference = null;
        reference = session.Expose("child", model, (routes, _, route) =>
        {
            if (++attachments == 2) throw new InvalidOperationException("expected reattach failure");
            WindowBridgeEndpointLease endpoint = routes.Bind(route, _ => "{}");
            return WindowBridgeAttachment.Create(new Release(() =>
            {
                session.Expose("child", model, (nextRoutes, _, nextRoute) => nextRoutes.Bind(nextRoute, _ => "{}"));
            }), endpoint);
        });

        session.Suspend(model);
        True(attachments == 2, "The retiring attachment did not schedule exactly one reattach attempt.");
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Close hid the asynchronous reattach failure.");
        }
        catch (AggregateException) { }
        Equal(1, scope.Disposals, "A failed asynchronous reattach leaked the owned scope.");
        True(!transport.Has("content" + reference.Id), "A failed asynchronous reattach retained an active endpoint.");
    }

    private static async Task EndpointDrainRetiresIngressBeforeReleasingViewOrScope()
    {
        var transport = new DrainingTransport();
        var scope = new Scope();
        var resource = new Resource();
        var model = new Parent();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WindowBridgeSession(transport, scope);
        WindowBridgeReference reference = session.Expose("editor", model, (routes, _, route) =>
        {
            WindowBridgeEndpointLease endpoint = routes.BindAsync(route, async (_, _) =>
            {
                callbackEntered.TrySetResult();
                await releaseCallback.Task.ConfigureAwait(false);
                True(resource.Disposals == 0, "An admitted endpoint callback observed its View resource disposed.");
                return "{\"ok\":true}";
            });
            return WindowBridgeAttachment.Create(resource, endpoint);
        });
        var activation = new Release(() => { });
        _ = session.Mount(reference, WindowBridgeConnection.Create("client", "connection"), "editor", () => activation);
        Task<string> callback = transport.InvokeAsync("content" + reference.Id);
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        WindowBridgeOperationAcceptance accepted = session.StartOperation(reference, "save", async _ =>
        {
            saveEntered.TrySetResult();
            await releaseSave.Task.ConfigureAwait(false);
            model.Committed = true;
            return WindowBridgeOperationResult.Succeeded();
        }, _ => "{\"committed\":true}");
        True(accepted.Accepted, "The Save was not accepted before endpoint detach.");
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);

        session.Suspend(model);
        True(!transport.Has("content" + reference.Id), "Endpoint retirement left ingress active.");
        True(resource.Disposals == 0, "Detach released the View resource before its endpoint drained.");
        releaseSave.TrySetResult();
        WindowBridgeOperationStatus terminal = await session.WaitForTerminalAsync(reference, "save").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Succeeded, terminal.Kind, "Detachment rewrote accepted Save success.");
        Equal<string?>(null, terminal.State, "Detached Save published state.");

        WindowBridgeCloseAdmission close = session.BeginClose();
        True(scope.Disposals == 0 && !close.Completion.IsCompleted,
            "Close disposed the owned scope while an admitted endpoint callback was still held.");
        releaseCallback.TrySetResult();
        Equal("{\"ok\":true}", await callback.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false),
            "A callback admitted before retirement lost its truthful reply.");
        await close.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(1, resource.Disposals, "View resource did not release after endpoint drain.");
        Equal(1, scope.Disposals, "Owned scope did not wait for endpoint drain.");
    }

    private static void DisconnectDrainsRemainingPresentationLeasesAfterFailure()
    {
        var transport = new RecordingTransport();
        var session = new WindowBridgeSession(transport);
        var document = new Document();
        WindowBridgeReference reference = session.Expose("document", document, Attach);
        WindowBridgeConnection connection = WindowBridgeConnection.Create("client", "disconnect");
        _ = session.Mount(reference, connection, "throws", () => new ThrowingRelease());
        var healthy = new ViewPeer(document);
        _ = session.Mount(reference, connection, "healthy", healthy.Activate);
        try
        {
            session.Disconnect(connection);
            throw new InvalidOperationException("A failing presentation release was hidden.");
        }
        catch (AggregateException) { }
        Equal(1, healthy.Disposals, "A failing presentation release skipped the connection's remaining lease.");
        session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task ParentForgetDuringChildAttachLeavesNoOrphanRoute()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport);
        var parent = new Parent();
        WindowBridgeReference parentReference = session.Expose("parent", parent, Attach);
        var child = new Child();
        var attachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? childId = null;
        Task<Exception?> presenting = Task.Run(() =>
        {
            try
            {
                _ = session.Present(parentReference, "main", "child", child, (bridge, _, route) =>
                {
                    childId = route["content".Length..];
                    attachEntered.TrySetResult();
                    releaseAttach.Task.GetAwaiter().GetResult();
                    return bridge.Bind(route, _ => "{}");
                });
                return null;
            }
            catch (Exception exception) { return exception; }
        });
        await attachEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Throws(() => session.Expose("child", child, Attach),
            "A concurrent Expose acquired a child route still reserved for its parent ownership decision.");
        session.Forget(parent);
        releaseAttach.TrySetResult();
        True(await presenting.ConfigureAwait(false) is InvalidOperationException,
            "Present did not reject a parent forgotten while its child was attaching.");
        True(childId is not null, "The race witness did not create a child reference.");
        Throws(() => session.Resolve("child", childId!), "Present left an orphan child route after its parent was forgotten.");
    }

    private static async Task TwoLogicalWindowsIsolateSharedModels()
    {
        await using var left = new WindowBridgeSession(new RecordingTransport());
        await using var right = new WindowBridgeSession(new RecordingTransport());
        var shared = new Parent();
        WindowBridgeReference leftReference = left.Expose("parent", shared, Attach);
        WindowBridgeReference rightReference = right.Expose("parent", shared, Attach);
        True(leftReference.Id != rightReference.Id, "Two logical windows reused a route identity for the same model.");
        _ = left.StartOperation(leftReference, "save", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        _ = right.StartOperation(rightReference, "save", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        Equal(WindowBridgeOperationKind.Succeeded, (await left.WaitForTerminalAsync(leftReference, "save").ConfigureAwait(false)).Kind,
            "The left logical window lost its own operation outcome.");
        Equal(WindowBridgeOperationKind.Succeeded, (await right.WaitForTerminalAsync(rightReference, "save").ConfigureAwait(false)).Kind,
            "The right logical window lost its own operation outcome.");
        Throws(() => right.Resolve("parent", leftReference.Id), "A route reference crossed logical-window ownership.");
        Throws(() => right.Mount(leftReference, WindowBridgeConnection.Create("client", "wrong-window"), "view"),
            "A presentation mounted a reference from another logical window.");
        Equal(WindowBridgeOperationKind.Unknown, right.LookupOperation(leftReference, "save").Kind,
            "Operation status crossed logical-window ownership.");
    }

    private static async Task ThrowingCancellationCallbackStillDrainsScope()
    {
        var scope = new Scope();
        var session = new WindowBridgeSession(new RecordingTransport(), scope);
        WindowBridgeReference reference = session.Expose("parent", new Parent(), Attach);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = session.StartOperation(reference, "registered-callback", token =>
        {
            token.Register(() => throw new InvalidOperationException("expected cancellation callback failure"));
            registered.TrySetResult();
            return Task.FromResult(WindowBridgeOperationResult.Succeeded());
        });
        await registered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("A throwing cancellation callback was hidden.");
        }
        catch (AggregateException) { }
        Equal(1, scope.Disposals, "A throwing cancellation callback skipped scope disposal.");
    }

    private static async Task BoundedCloseSealsAndSignalsBeforeDeferredDrain()
    {
        var scope = new Scope();
        var transport = new RecordingTransport();
        var session = new WindowBridgeSession(transport, scope);
        WindowBridgeReference reference = session.Expose("parent", new Parent(), Attach);
        var entered = new ManualResetEventSlim();
        var cancellationObserved = new ManualResetEventSlim();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = session.StartOperation(reference, "close", async token =>
        {
            using CancellationTokenRegistration registration = token.Register(cancellationObserved.Set);
            entered.Set();
            await release.Task.ConfigureAwait(false);
            return WindowBridgeOperationResult.Succeeded();
        });
        True(entered.Wait(TimeSpan.FromSeconds(2)), "The accepted operation did not begin before close admission.");
        WindowBridgeCloseAdmission close = session.BeginClose();
        Equal(1, close.RemainingOperations, "Close admission did not report the accepted in-flight operation.");
        True(cancellationObserved.Wait(TimeSpan.FromSeconds(2)), "Close admission did not request cancellation before returning.");
        Throws(() => session.StartOperation(reference, "after-close", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded())),
            "Close admission did not seal operation ingress.");
        await Task.Delay(50).ConfigureAwait(false);
        True(!close.Completion.IsCompleted && scope.Disposals == 0, "Close admission disposed the scope before cancellation-ignoring work drained.");
        release.TrySetResult();
        await close.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(1, scope.Disposals, "Close completion did not dispose the scope after work drained.");
    }

    private static async Task CloseAdmissionDoesNotBlockOnCancellationCallback()
    {
        var scope = new Scope();
        var session = new WindowBridgeSession(new RecordingTransport(), scope);
        WindowBridgeReference reference = session.Expose("parent", new Parent(), Attach);
        var entered = new ManualResetEventSlim();
        var callbackEntered = new ManualResetEventSlim();
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = session.StartOperation(reference, "blocking-callback", async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                callbackEntered.Set();
                releaseCallback.Task.GetAwaiter().GetResult();
            });
            entered.Set();
            await releaseWork.Task.ConfigureAwait(false);
            return WindowBridgeOperationResult.Succeeded();
        });
        True(entered.Wait(TimeSpan.FromSeconds(2)), "The blocking callback operation did not begin.");
        Stopwatch stopwatch = Stopwatch.StartNew();
        WindowBridgeCloseAdmission close = session.BeginClose();
        stopwatch.Stop();
        True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), "Close admission waited for a blocking cancellation callback.");
        True(callbackEntered.Wait(TimeSpan.FromSeconds(2)), "Close admission did not signal cancellation asynchronously.");
        True(!close.Completion.IsCompleted && scope.Disposals == 0, "Cleanup ran before the blocking cancellation callback drained.");
        releaseCallback.TrySetResult();
        releaseWork.TrySetResult();
        await close.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(1, scope.Disposals, "Close completion did not drain after the blocking callback released.");
    }

    private static async Task RetiredChildCleanupDoesNotRaceReparenting()
    {
        await ReparentDuringRetiredCleanup(clearParent: true).ConfigureAwait(false);
        await ReparentDuringRetiredCleanup(clearParent: false).ConfigureAwait(false);
    }

    private static async Task ReparentDuringRetiredCleanup(bool clearParent)
    {
        var reachedRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool blockRetirement = true;
        await using var session = new WindowBridgeSession(new RecordingTransport(), beforeRetiredCleanup: () =>
        {
            if (!blockRetirement) return;
            reachedRetirement.TrySetResult();
            continueRetirement.Task.GetAwaiter().GetResult();
        });
        var firstOwner = new Parent();
        var secondOwner = new Parent();
        var child = new Child();
        WindowBridgeReference first = session.Expose("parent", firstOwner, Attach);
        WindowBridgeReference second = session.Expose("parent", secondOwner, Attach);
        WindowBridgeReference childReference = session.Present(first, "main", "child", child, Attach);
        Task retire = Task.Run(() =>
        {
            if (clearParent) session.Clear(first, "main");
            else session.Forget(firstOwner);
        });
        await reachedRetirement.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _ = session.Present(second, "main", "child", child, Attach);
        continueRetirement.TrySetResult();
        await retire.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        WindowBridgeConnection connection = WindowBridgeConnection.Create("client", clearParent ? "clear" : "forget");
        using WindowBridgePresentationLease lease = session.Mount(childReference, connection, "child");
        True(session.HasPresentation(childReference, connection), "A reparented child was detached by deferred retirement cleanup.");
    }

    private static async Task RootOwnershipSurvivesSlotsAndDeferredRetirement()
    {
        var reachedRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRetirement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool blockRetirement = false;
        await using var session = new WindowBridgeSession(new RecordingTransport(), beforeRetiredCleanup: () =>
        {
            if (!blockRetirement) return;
            reachedRetirement.TrySetResult();
            continueRetirement.Task.GetAwaiter().GetResult();
        });
        var parent = new Parent();
        var rootChild = new Child();
        WindowBridgeReference parentReference = session.Expose("parent", parent, Attach);
        WindowBridgeReference rootReference = session.Expose("child", rootChild, Attach);
        _ = session.Present(parentReference, "main", "child", rootChild, Attach);
        session.Clear(parentReference, "main");
        using (WindowBridgePresentationLease rootLease = session.Mount(rootReference, WindowBridgeConnection.Create("client", "root-clear"), "root"))
            True(session.HasPresentation(rootReference, WindowBridgeConnection.Create("client", "root-clear")), "Clearing a parent retired independent root ownership.");
        _ = session.Present(parentReference, "main", "child", rootChild, Attach);
        session.Forget(parent);
        using (WindowBridgePresentationLease rootLease = session.Mount(rootReference, WindowBridgeConnection.Create("client", "root-forget"), "root"))
            True(session.HasPresentation(rootReference, WindowBridgeConnection.Create("client", "root-forget")), "Forgetting a parent retired independent root ownership.");
        session.ReleaseRoot(rootReference);
        Throws(() => session.Mount(rootReference, WindowBridgeConnection.Create("client", "released-root"), "root"),
            "Releasing the last root ownership did not retire an unparented child.");

        var nextParent = new Parent();
        var nestedChild = new Child();
        WindowBridgeReference nextParentReference = session.Expose("parent", nextParent, Attach);
        WindowBridgeReference nestedReference = session.Present(nextParentReference, "main", "nested", nestedChild, Attach);
        blockRetirement = true;
        Task clear = Task.Run(() => session.Clear(nextParentReference, "main"));
        await reachedRetirement.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Equal(nestedReference, session.Expose("nested", nestedChild, Attach), "Re-expose did not preserve the nested route identity.");
        continueRetirement.TrySetResult();
        await clear.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        using WindowBridgePresentationLease reexposed = session.Mount(nestedReference, WindowBridgeConnection.Create("client", "reexposed"), "nested");
    }

    private static async Task DynamicChildrenRemainRegisteredUntilTheirOwnerForgetsThem()
    {
        const int childCount = 192;
        await using var session = new WindowBridgeSession(new RecordingTransport());
        WindowBridgeReference owner = session.Expose("owner", new Parent(), Attach);
        var retired = new List<(WindowBridgeReference Reference, WeakReference Model)>(childCount);

        for (int index = 0; index < childCount; index++)
            retired.Add(CreateAndClearTransientChild(session, owner));

        CollectGarbage();
        Equal(childCount, CountLiveModels(retired),
            "Cleared dynamic children unexpectedly lost their reusable model identity.");
        foreach ((WindowBridgeReference reference, _) in retired)
            Equal(reference, session.Resolve("transient", reference.Id),
                "A cleared dynamic child lost its reusable route identity.");

        ForgetLiveModels(session, retired);
        CollectGarbage();
        int remainingModels = CountLiveModels(retired);
        Equal(0, remainingModels,
            $"Forgetting retired dynamic children did not release their model identities for collection ({remainingModels} still alive).");
        foreach ((WindowBridgeReference reference, _) in retired)
            Throws(() => session.Resolve("transient", reference.Id),
                "Forgetting a retired dynamic child retained its route identity.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WindowBridgeReference Reference, WeakReference Model) CreateAndClearTransientChild(
        WindowBridgeSession session, WindowBridgeReference owner)
    {
        var child = new Child();
        WindowBridgeReference reference = session.Present(owner, "main", "transient", child, Attach);
        var weakReference = new WeakReference(child);
        session.Clear(owner, "main");
        return (reference, weakReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForgetLiveModels(WindowBridgeSession session,
        IReadOnlyList<(WindowBridgeReference Reference, WeakReference Model)> retired)
    {
        foreach ((_, WeakReference modelReference) in retired)
        {
            if (modelReference.Target is not Child model)
                throw new InvalidOperationException("A cleared child was collected before the session released its registered identity.");
            session.Forget(model);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountLiveModels(IReadOnlyList<(WindowBridgeReference Reference, WeakReference Model)> retired) =>
        retired.Count(entry => entry.Model.IsAlive);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task PendingExposeFailureDoesNotClaimRootOwnership()
    {
        await using var session = new WindowBridgeSession(new RecordingTransport());
        var parent = new Parent();
        var child = new Child();
        WindowBridgeReference parentReference = session.Expose("parent", parent, Attach);
        var attachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAttach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<WindowBridgeReference> presenting = Task.Run(() => session.Present(parentReference, "main", "child", child, (transport, _, route) =>
        {
            attachEntered.TrySetResult();
            releaseAttach.Task.GetAwaiter().GetResult();
            return transport.Bind(route, _ => "{}");
        }));
        await attachEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Throws(() => session.Expose("child", child, Attach), "A pending presentation accepted a concurrent root expose.");
        releaseAttach.TrySetResult();
        WindowBridgeReference childReference = await presenting.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        session.Clear(parentReference, "main");
        Throws(() => session.Mount(childReference, WindowBridgeConnection.Create("client", "pending-root"), "child"),
            "A failed pending root expose retained child ownership after Clear.");
    }

    private static async Task FailedNewExposeDoesNotRetainRootOwnership()
    {
        await AssertFailedRootExposeCanBePresentedAndRetired((_, _, _) => throw new InvalidOperationException("expected attach failure")).ConfigureAwait(false);
        await AssertFailedRootExposeCanBePresentedAndRetired((_, _, _) => null!).ConfigureAwait(false);
    }

    private static async Task AssertFailedRootExposeCanBePresentedAndRetired(Func<IWindowBridgeTransport, Child, string, WindowBridgeAttachment> failingAttach)
    {
        await using var session = new WindowBridgeSession(new RecordingTransport());
        var parent = new Parent();
        var child = new Child();
        WindowBridgeReference parentReference = session.Expose("parent", parent, Attach);
        Throws(() => session.Expose("child", child, failingAttach), "A failed first Expose was reported as successful.");
        WindowBridgeReference childReference = session.Present(parentReference, "main", "child", child, Attach);
        session.Clear(parentReference, "main");
        Throws(() => session.Mount(childReference, WindowBridgeConnection.Create("client", "failed-new"), "child"),
            "A failed new Expose retained root ownership after the successful nested route was cleared.");
    }

    private static async Task FailedNewExposeRemovesEntryBeforeWaitingExposeCanAttach()
    {
        var firstAttachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WindowBridgeSession(new RecordingTransport(), beforeAttachmentWait: () => waiterObserved.TrySetResult());
        var child = new Child();
        Task<Exception?> failed = Task.Run(() =>
        {
            try
            {
                _ = session.Expose("child", child, (_, _, _) =>
                {
                    firstAttachEntered.TrySetResult();
                    releaseFailure.Task.GetAwaiter().GetResult();
                    throw new InvalidOperationException("expected failure");
                });
                return null;
            }
            catch (Exception exception) { return exception; }
        });
        await firstAttachEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Task<Exception?> waiting = Task.Run(() =>
        {
            try { _ = session.Expose("child", child, Attach); return null; }
            catch (Exception exception) { return exception; }
        });
        await waiterObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        releaseFailure.TrySetResult();
        True(await failed.ConfigureAwait(false) is InvalidOperationException, "The first attach failure was hidden.");
        True(await waiting.ConfigureAwait(false) is InvalidOperationException,
            "A waiter attached the entry that a failed first Expose had retired.");
        WindowBridgeReference retry = session.Expose("child", child, Attach);
        using WindowBridgePresentationLease lease = session.Mount(retry, WindowBridgeConnection.Create("client", "retry"), "child");
    }

    private static async Task WaitingExposeDoesNotAttachBeforeSuspendedLeaseDrains()
    {
        var firstAttachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAttach = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstDrain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttachEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new WindowBridgeSession(new RecordingTransport(), beforeAttachmentWait: () => waiterObserved.TrySetResult());
        var child = new Child();
        int attachments = 0;
        Task<WindowBridgeReference> first = Task.Run(() => session.Expose("child", child, (_, _, _) =>
        {
            int number = Interlocked.Increment(ref attachments);
            if (number == 1)
            {
                firstAttachEntered.TrySetResult();
                releaseFirstAttach.Task.GetAwaiter().GetResult();
            }
            else secondAttachEntered.TrySetResult();
            return WindowBridgeAttachment.Create(new ControlledDrainLease(number == 1 ? releaseFirstDrain.Task : Task.CompletedTask));
        }));
        try
        {
            await firstAttachEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Task<WindowBridgeReference> waiting = Task.Run(() => session.Expose("child", child,
                (_, _, _) => throw new InvalidOperationException("The existing entry must keep its original factory.")));
            await waiterObserved.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            session.Suspend(child);
            releaseFirstAttach.TrySetResult();
            WindowBridgeReference firstReference = await first.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            WindowBridgeReference waitedReference = await waiting.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Equal(firstReference, waitedReference, "The waiting Expose changed route identity after Suspend.");
            Equal(1, Volatile.Read(ref attachments), "The waiting Expose attached before the suspended lease drained.");
            releaseFirstDrain.TrySetResult();
            await secondAttachEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Equal(2, Volatile.Read(ref attachments), "The route did not reattach after its old lease drained.");
        }
        finally
        {
            releaseFirstAttach.TrySetResult();
            releaseFirstDrain.TrySetResult();
        }
    }

    private static async Task PresentationTombstonesRemainConnectionOwned()
    {
        await using var session = new WindowBridgeSession(new RecordingTransport(), maximumRetainedTerminals: 0, maximumExpired: 2);
        var model = new Parent();
        WindowBridgeReference reference = session.Expose("parent", model, Attach);
        WindowBridgeConnection first = WindowBridgeConnection.Create("client", "tombstone-a");
        WindowBridgeConnection second = WindowBridgeConnection.Create("client", "tombstone-b");
        using WindowBridgePresentationLease firstLease = session.Mount(reference, first, "a");
        using WindowBridgePresentationLease secondLease = session.Mount(reference, second, "b");
        _ = session.StartOperationFromPresentation(reference, first, "expired", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        _ = await session.WaitForTerminalAsync(reference, "expired").ConfigureAwait(false);
        Equal(WindowBridgeOperationKind.Expired, session.LookupOperationFromConnection(reference, first, "expired").Kind,
            "The initiating connection did not retain its own expired receipt identity.");
        Equal(WindowBridgeOperationKind.Rejected, session.LookupOperationFromConnection(reference, second, "expired").Kind,
            "A second connection observed another connection's expired tombstone.");
        WindowBridgeOperationAcceptance secondReplay = session.StartOperationFromPresentation(reference, second, "expired", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        True(!secondReplay.Accepted && secondReplay.Status.Kind == WindowBridgeOperationKind.Rejected,
            "A second connection learned an expired tombstone through duplicate admission.");
    }

    private static async Task DocumentTombstonesRemainDocumentOwned()
    {
        await using var session = new WindowBridgeSession(new RecordingTransport(), maximumRetainedTerminals: 0, maximumExpired: 2);
        WindowBridgeReference reference = session.Expose("parent", new Parent(), Attach);
        WindowBridgeConnection connection = WindowBridgeConnection.Create("client", "tombstone-document");
        WindowBridgeDocumentEpoch first = WindowBridgeDocumentEpoch.Create("0000000000000001AAAAAAAAAAAAAAAA");
        WindowBridgeDocumentEpoch replacement = WindowBridgeDocumentEpoch.Create("0000000000000002BBBBBBBBBBBBBBBB");
        True(session.BeginDocument(connection, first).Accepted, "The tombstone test document was not admitted.");
        using WindowBridgePresentationLease lease = session.Mount(reference, connection, first, "editor");
        WindowBridgeOperationAcceptance accepted = session.StartOperationFromPresentation(reference, connection,
            first, "editor", "expired-document-save", _ => Task.FromResult(WindowBridgeOperationResult.Succeeded()));
        True(accepted.Accepted, "The test operation was not admitted from its document presentation.");
        _ = await session.WaitForTerminalAsync(reference, "expired-document-save").ConfigureAwait(false);
        True(session.BeginDocument(connection, replacement).Accepted, "The tombstone replacement document was not admitted.");
        Equal(WindowBridgeOperationKind.Expired,
            (await session.WaitForTerminalFromPresentationAsync(reference, connection, first, "expired-document-save").ConfigureAwait(false)).Kind,
            "The initiating document lost its expired operation identity.");
        Equal(WindowBridgeOperationKind.Rejected,
            (await session.WaitForTerminalFromPresentationAsync(reference, connection, replacement, "expired-document-save").ConfigureAwait(false)).Kind,
            "A replacement document observed another document's expired operation identity.");
    }

    private static async Task DocumentEpochRetiresReloadedPresentationAndPreservesSameReferenceIndependence()
    {
        var transport = new RecordingTransport();
        await using var session = new WindowBridgeSession(transport);
        WindowBridgeReference editor = session.Expose("editor", new Child(), Attach);
        WindowBridgeConnection connection = WindowBridgeConnection.Create("client", "reused-native-connection");
        WindowBridgeDocumentEpoch first = WindowBridgeDocumentEpoch.Create("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        WindowBridgeDocumentEpoch replacement = WindowBridgeDocumentEpoch.Create("BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB");
        var queued = new QueuedPublication();
        var firstLeft = new ViewPeer(editor);
        var firstRight = new ViewPeer(editor);

        True(session.BeginDocument(connection, first).Accepted, "The first document epoch was not admitted.");
        using WindowBridgePresentationLease oldLeft = session.Mount(editor, connection, first, "left", () => firstLeft.ActivateWith(queued));
        using WindowBridgePresentationLease oldRight = session.Mount(editor, connection, first, "right", firstRight.Activate);
        queued.Queue();
        var releaseAcceptedWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowBridgeOperationAcceptance accepted = session.StartOperationFromPresentation(editor, connection, first, "document-a-save", async _ =>
        {
            await releaseAcceptedWork.Task.ConfigureAwait(false);
            return WindowBridgeOperationResult.Succeeded();
        });
        True(accepted.Accepted, "An active document presentation could not admit work.");
        WindowBridgeOperationAcceptance secondAccepted = session.StartOperationFromPresentation(editor, connection, first, "document-a-save-2", async _ =>
        {
            await releaseAcceptedWork.Task.ConfigureAwait(false);
            return WindowBridgeOperationResult.Succeeded();
        });
        True(secondAccepted.Accepted, "A second operation in the initiating document was not admitted.");

        True(session.BeginDocument(connection, replacement).Accepted, "The replacement document epoch was not admitted.");
        Equal(1, firstLeft.Stops, "Replacing a document did not release its left presentation.");
        Equal(1, firstRight.Stops, "Replacing a document did not release its right presentation.");
        queued.Deliver();
        Equal(0, queued.Deliveries, "A queued old-document publication reached a retired presentation.");
        True(!session.HasPresentation(editor, connection, first), "The old document still owned a presentation after replacement.");
        True(!session.Unmount(editor, connection, first, "left"), "A stale document cleanup released a presentation.");
        Throws(() => session.Mount(editor, connection, first, "left"), "A stale document remounted after replacement.");
        Equal(WindowBridgeOperationKind.Rejected,
            (await session.WaitForTerminalFromPresentationAsync(editor, connection, replacement, "document-a-save").ConfigureAwait(false)).Kind,
            "A replacement document waited on an operation owned by the retired document.");
        releaseAcceptedWork.TrySetResult();
        Equal(WindowBridgeOperationKind.Succeeded,
            (await session.WaitForTerminalFromPresentationAsync(editor, connection, first, "document-a-save").ConfigureAwait(false)).Kind,
            "Replacing a document cancelled accepted work or denied its initiating document a terminal result.");
        Equal(WindowBridgeOperationKind.Succeeded,
            (await session.WaitForTerminalFromPresentationAsync(editor, connection, first, "document-a-save-2").ConfigureAwait(false)).Kind,
            "A second accepted operation lost its initiating-document terminal result.");
        Equal(WindowBridgeOperationKind.Rejected,
            session.LookupOperationFromPresentation(editor, connection, replacement, "document-a-save").Kind,
            "A replacement document observed an operation owned by the retired document.");

        var currentLeft = new ViewPeer(editor);
        var currentRight = new ViewPeer(editor);
        using WindowBridgePresentationLease newLeft = session.Mount(editor, connection, replacement, "left", currentLeft.Activate);
        using WindowBridgePresentationLease newRight = session.Mount(editor, connection, replacement, "right", currentRight.Activate);
        oldLeft.Dispose();
        True(session.HasPresentation(editor, connection, replacement), "Late old-document cleanup removed the replacement presentation.");
        newLeft.Dispose();
        Equal(1, currentLeft.Stops, "Releasing the left replacement did not release its own activation.");
        Equal(0, currentRight.Stops, "Releasing one same-reference presentation released its independent peer.");
        True(session.HasPresentation(editor, connection, replacement), "The right same-reference presentation was not retained.");
        WindowBridgeDocumentAdmission staleRetry = session.BeginDocument(connection, first);
        True(!staleRetry.Accepted && staleRetry.Error == "The document epoch is not newer than the current document.",
            "A retired document epoch reclaimed a preserved native connection.");
        session.Disconnect(connection);
        True(!session.BeginDocument(connection, replacement).Accepted,
            "A disconnected native connection started a replacement document.");
        Throws(() => session.Mount(editor, connection, replacement, "after-disconnect"),
            "A delayed callback mounted after its native connection disconnected.");
        WindowBridgeDocumentEpoch reconnected = WindowBridgeDocumentEpoch.Create("CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC");
        True(session.BeginDocument(connection, reconnected).Accepted,
            "A strictly newer document did not reopen a reused raw native callback pair.");
        using WindowBridgePresentationLease reconnectedLease = session.Mount(editor, connection, reconnected, "after-reconnect");
        WindowBridgeDocumentAdmission oldAfterReconnect = session.BeginDocument(connection, replacement);
        True(!oldAfterReconnect.Accepted && oldAfterReconnect.Error == "The document epoch is not newer than the current document.",
            "A prior document epoch reclaimed a raw callback pair after a newer reconnect.");

        await using var repeated = new WindowBridgeSession(new RecordingTransport());
        WindowBridgeConnection repeatedConnection = WindowBridgeConnection.Create("client", "many-reloads");
        WindowBridgeDocumentEpoch? latest = null;
        for (int ordinal = 1; ordinal <= 140; ordinal++)
        {
            WindowBridgeDocumentEpoch next = WindowBridgeDocumentEpoch.Create($"{ordinal:X16}" + "AAAAAAAAAAAAAAAA");
            True(repeated.BeginDocument(repeatedConnection, next).Accepted,
                "A long development reload sequence exhausted document admission.");
            if (latest is not null)
                True(!repeated.BeginDocument(repeatedConnection, latest).Accepted,
                    "An old ticket regained admission after repeated reloads.");
            latest = next;
        }

        // The older page's first begin callback can arrive only after the
        // replacement has already been admitted on reused native IDs.
        await using var reordered = new WindowBridgeSession(new RecordingTransport());
        WindowBridgeReference reorderedEditor = reordered.Expose("editor", new Child(), Attach);
        WindowBridgeConnection reorderedConnection = WindowBridgeConnection.Create("client", "reordered-document-begin");
        True(reordered.BeginDocument(reorderedConnection, replacement).Accepted,
            "The replacement document could not begin before an older delayed callback.");
        using WindowBridgePresentationLease surviving = reordered.Mount(reorderedEditor, reorderedConnection, replacement, "editor");
        WindowBridgeDocumentAdmission delayedOld = reordered.BeginDocument(reorderedConnection, first);
        True(!delayedOld.Accepted && reordered.HasPresentation(reorderedEditor, reorderedConnection, replacement),
            "An unseen older document begin retired a newer mounted document.");
    }

    private static WindowBridgeAttachment Attach<T>(IWindowBridgeTransport transport, T _, string route) where T : class =>
        transport.Bind(route, _ => "{\"route\":\"" + route + "\"}");

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class Shell : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class Document { }
    private sealed class Child { }
    private sealed class Parent { internal bool Committed { get; set; } }

    private sealed class Scope : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }

    private sealed class ViewPeer(object dataContext) : IDisposable
    {
        internal object DataContext { get; } = dataContext;
        internal int Starts;
        internal int Stops;
        internal int Disposals;
        internal Release Activate() { Starts++; return new Release(() => { Stops++; Dispose(); }); }
        internal Release ActivateWith(QueuedPublication publication)
        {
            Starts++;
            publication.Activate();
            return new Release(() => { publication.Deactivate(); Stops++; Dispose(); });
        }
        public void Dispose() { Disposals++; }
    }

    private sealed class QueuedPublication
    {
        private bool _active;
        private bool _queued;
        internal int Deliveries;
        internal void Activate() => _active = true;
        internal void Deactivate() => _active = false;
        internal void Queue() => _queued = true;
        internal void Deliver()
        {
            if (_queued && _active) Deliveries++;
            _queued = false;
        }
    }

    private sealed class Release(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private sealed class ThrowingRelease : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("expected test cleanup failure");
    }

    private sealed class Resource : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }

    private sealed class ControlledDrainLease(Task drain) : WindowBridgeEndpointLease
    {
        public override Task Drain => drain;
        public override void Dispose() { }
    }

    private sealed class DrainingTransport : IWindowBridgeTransport
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, Endpoint> _routes = new(StringComparer.Ordinal);

        public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> handler) =>
            Add(route, (_, _) => ValueTask.FromResult(handler(new Arguments())));

        public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler) =>
            Add(route, handler);

        internal bool Has(string route) { lock (_gate) return _routes.ContainsKey(route); }

        internal async Task<string> InvokeAsync(string route)
        {
            Endpoint endpoint;
            lock (_gate)
            {
                endpoint = _routes[route];
                endpoint.InFlight++;
            }
            try { return await endpoint.Handler(new Arguments(), CancellationToken.None).ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    endpoint.InFlight--;
                    if (endpoint.Retired && endpoint.InFlight == 0) endpoint.Drain.TrySetResult();
                }
            }
        }

        private HeldLease Add(string route,
            Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler)
        {
            var endpoint = new Endpoint(route, handler);
            lock (_gate) _routes.Add(route, endpoint);
            return new HeldLease(this, endpoint);
        }

        private void Retire(Endpoint endpoint)
        {
            lock (_gate)
            {
                if (endpoint.Retired) return;
                endpoint.Retired = true;
                _routes.Remove(endpoint.Route);
                if (endpoint.InFlight == 0) endpoint.Drain.TrySetResult();
            }
        }

        private sealed class Endpoint(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> handler)
        {
            internal string Route { get; } = route;
            internal Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> Handler { get; } = handler;
            internal TaskCompletionSource Drain { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal int InFlight;
            internal bool Retired;
        }

        private sealed class HeldLease(DrainingTransport owner, Endpoint endpoint) : WindowBridgeEndpointLease
        {
            public override Task Drain => endpoint.Drain.Task;
            public override void Dispose() => owner.Retire(endpoint);
        }

        private sealed class Arguments : WindowBridgeArguments
        {
            public WindowBridgeConnection Connection { get; } = WindowBridgeConnection.Create("client", "connection");
            public string GetString() => "{}";
            public long GetInt64() => 0;
            public bool GetBoolean() => false;
        }
    }

    private sealed class FaultingAttachmentBatchTransport(int throwOnBegin, int throwOnEnd) : IWindowBridgeTransport, IWindowBridgeAttachmentBatcher
    {
        private readonly Dictionary<string, IDisposable> _bindings = new(StringComparer.Ordinal);
        private int _begins;
        private int _ends;

        public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> _) => Add(route);

        public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> _) => Add(route);

        public IDisposable BeginAttachmentUpdate()
        {
            if (++_begins == throwOnBegin) throw new InvalidOperationException("expected attachment batch entry failure");
            return new Release(() =>
            {
                if (++_ends == throwOnEnd) throw new InvalidOperationException("expected attachment batch exit failure");
            });
        }

        internal bool Has(string route) => _bindings.ContainsKey(route);

        private WindowBridgeEndpointLease Add(string route)
        {
            var binding = new Release(() => _bindings.Remove(route));
            _bindings.Add(route, binding);
            return WindowBridgeEndpointLease.Direct(route, binding);
        }
    }

    private sealed class AtomicAttachmentBatchTransport : IWindowBridgeTransport, IWindowBridgeAttachmentBatcher
    {
        private readonly Dictionary<string, bool> _routes = new(StringComparer.Ordinal);
        private readonly List<Action> _pending = [];
        private int _depth;

        internal int Commits { get; private set; }

        public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> _) => Add(route);

        public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> _) => Add(route);

        public IDisposable BeginAttachmentUpdate()
        {
            _depth++;
            return new Release(() =>
            {
                if (--_depth != 0) return;
                foreach (Action change in _pending) change();
                _pending.Clear();
                Commits++;
            });
        }

        internal bool Has(string route) => _routes.ContainsKey(route);

        private WindowBridgeEndpointLease Add(string route)
        {
            if (_depth == 0) throw new InvalidOperationException("Endpoint binding escaped its attachment transaction.");
            _pending.Add(() => _routes.Add(route, true));
            return WindowBridgeEndpointLease.Direct(route, new Release(() =>
            {
                if (_depth == 0) throw new InvalidOperationException("Endpoint retirement escaped its attachment transaction.");
                _pending.Add(() => _routes.Remove(route));
            }));
        }
    }

    private sealed class RecordingTransport : IWindowBridgeTransport
    {
        private readonly Dictionary<string, IDisposable> _bindings = new(StringComparer.Ordinal);

        public WindowBridgeEndpointLease Bind(string route, Func<WindowBridgeArguments, string> _)
        {
            var binding = new Release(() => _bindings.Remove(route));
            _bindings.Add(route, binding);
            return WindowBridgeEndpointLease.Direct(route, binding);
        }

        public WindowBridgeEndpointLease BindAsync(string route, Func<WindowBridgeArguments, CancellationToken, ValueTask<string>> _)
        {
            var binding = new Release(() => _bindings.Remove(route));
            _bindings.Add(route, binding);
            return WindowBridgeEndpointLease.Direct(route, binding);
        }

        internal bool Has(string route) => _bindings.ContainsKey(route);
    }
}
