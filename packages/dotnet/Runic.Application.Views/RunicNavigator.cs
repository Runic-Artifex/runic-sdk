using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

/// <summary>Configures a <see cref="RunicNavigator"/>.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class RunicNavigatorOptions
{
    /// <summary>The window's model context. Commits run in its turns; owned content is bound to it.</summary>
    public required IRunicModelContext ModelContext { get; init; }

    /// <summary>The window's service provider, passed to <see cref="NavigationTarget.Create{T}(Func{IServiceProvider, T})"/> factories.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>Creates the navigator's logger. When omitted, failures are written to <see cref="System.Diagnostics.Trace"/>.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>The clock of the close timeout and overrun warnings. Defaults to <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// How long <see cref="RunicNavigator.DisposeAsync"/> waits for cancelled transitions, and then for the
    /// regions to be cleared in model turns (one deadline shared by all regions). A retiring entry whose initialize hook
    /// ignored cancellation is not awaited again after the first wait timed out, so disposal takes about twice this
    /// value in the worst case, independent of the number of regions, plus the disposal of the content itself.
    /// Defaults to 10 seconds.
    /// </summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Owns the navigation regions and entries of one window. Register it per window scope with
/// <see cref="RunicNavigationServiceCollectionExtensions.AddRunicNavigation"/>.
/// </summary>
/// <remarks>
/// Each region admits one transition at a time: a later request supersedes earlier ones that have not
/// started committing. Guards, initialize and resume run outside model turns; a commit re-checks the
/// region and applies the new state in one turn. Disposing the navigator cancels in-flight transitions
/// and retires every entry; owned content is disposed outside model turns.
/// </remarks>
[Experimental(DiagnosticId)]
public sealed class RunicNavigator : IAsyncDisposable
{
    /// <summary>The diagnostic ID of the experimental navigation API.</summary>
    public const string DiagnosticId = "RUNICNAV001";

    internal static readonly TimeSpan OverrunWarningDelay = TimeSpan.FromSeconds(5);

    // Every instance any navigator has ever owned, live or retired.
    private static readonly ConditionalWeakTable<object, object> EverOwned = new();
    private static readonly object EverOwnedGate = new();
    private static readonly object OwnedMarker = new();

    // Set only around a hook invocation, to the transition that runs it.
    private static readonly AsyncLocal<NavigationTransition?> HookTransition = new();

    private readonly TimeProvider _time;
    private readonly TimeSpan _closeTimeout;
    // The shared deadline of the clearing turns of one disposal (guarded by Gate).
    private long? _closeTurnsStart;
    private bool _closeTurnsTimedOut;
    // Set when the disposal's wait for cancelled transitions timed out: their initialize hooks already had the close timeout.
    private bool _transitionWaitTimedOut;
    private long? _initializeStart;
    private readonly List<NavigationRegionCore> _regions = [];
    private readonly Dictionary<object, List<NavigationRegionCore>> _regionsByOwner = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, NavigationEntryCore> _ownedEntries = new(ReferenceEqualityComparer.Instance);
    // Pending, committed, and removed entries that have not finished retiring.
    private readonly HashSet<NavigationEntryCore> _tracked = [];
    private readonly HashSet<NavigationTransition> _running = [];
    private readonly HashSet<Task> _retirements = [];
    // PushForResult requests that have not completed or been dismissed.
    private readonly HashSet<NavigationResultRequestCore> _resultRequests = [];
    private long _nextEntryId;
    private int _nextRegionId;
    private bool _closing;
    private TaskCompletionSource? _disposal;
    // The attached presentations: the sinks that forget owned content when it retires.
    private readonly List<PresentationAttachment> _presentations = [];

    /// <summary>Creates a navigator for one window.</summary>
    public RunicNavigator(RunicNavigatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ModelContext = options.ModelContext ?? throw new ArgumentException("A model context is required.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CloseTimeout, TimeSpan.Zero, nameof(options));
        Services = options.Services;
        _time = options.TimeProvider ?? TimeProvider.System;
        _closeTimeout = options.CloseTimeout;
        Logger = options.LoggerFactory?.CreateLogger(RunicViewsTelemetry.LogCategory) ?? TraceFallbackLogger.Instance;
    }

    /// <summary>Gets the model context whose turns commit this navigator's state. Owned content is bound to it.</summary>
    public IRunicModelContext ModelContext { get; }

    internal IServiceProvider? Services { get; }

    internal ILogger Logger { get; }

    internal object Gate { get; } = new();

    /// <summary>
    /// Gets the entries that have not finished retiring: pending, committed, and removed entries whose
    /// cleanup is still running. It is zero after <see cref="DisposeAsync"/>.
    /// </summary>
    public int UnretiredEntryCount
    {
        get { lock (Gate) return _tracked.Count; }
    }

    /// <summary>
    /// Creates a region owned by <paramref name="owner"/>. A region owned by the content of an owned
    /// entry is that entry's child and closes when it retires; other regions close when the navigator
    /// is disposed.
    /// </summary>
    /// <param name="owner">The object that owns the region, such as the ViewModel exposing it.</param>
    /// <param name="initial">
    /// Content committed synchronously as the first entry. It must implement neither
    /// <see cref="INavigationInitialize"/> nor <see cref="INavigationInitialize{TInput}"/>.
    /// </param>
    /// <param name="options">The region's child policy.</param>
    public NavigationRegion<TContent> CreateRegion<TContent>(object owner, INavigationTarget<TContent>? initial = null,
        NavigationRegionOptions? options = null) where TContent : class
    {
        ArgumentNullException.ThrowIfNull(owner);
        NavigationTargetCore? target = null;
        if (initial is not null)
        {
            target = initial as NavigationTargetCore
                ?? throw new ArgumentException("Navigation targets must be created with NavigationTarget.", nameof(initial));
            if (target.HasInput)
                throw new ArgumentException("A target created with input initializes asynchronously and can't be an initial target.", nameof(initial));
            if (target.Instance is { } instance) RejectInitializable(instance, nameof(initial));
        }
        lock (Gate) ObjectDisposedException.ThrowIf(_closing, this);

        object? content = target?.Instance;
        if (target is not null && content is null)
        {
            content = target.Create(Services ?? EmptyServiceProvider.Instance);
            try { RejectInitializable(content, nameof(initial)); }
            catch
            {
                DisposeUnclaimed(content, null);
                throw;
            }
        }

        var owned = target?.Ownership == NavigationOwnership.Owned;
        // The instance the factory created is ours to dispose when no region takes it.
        var created = target is not null && target.Instance is null;
        var claimed = false;
        IRunicModelContextLease? lease = null;
        try
        {
            if (owned)
            {
                // A claimed instance stays in the owned table even if binding
                // fails, like any retired instance.
                Claim(content!);
                claimed = true;
                lease = RunicModelContextRegistry.Shared.Bind(ModelContext, content!);
            }
        }
        catch
        {
            DisposeUnclaimed(created && (claimed || !owned) ? content : null, lease);
            throw;
        }
        NavigationEntryCore? retireAtOnce = null;
        NavigationRegionCore region;
        try
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                region = new NavigationRegionCore(this, ++_nextRegionId, typeof(TContent).Name, owner,
                    options?.WhileParentRetained ?? NavigationChildRetention.Keep);
                // A region whose owner already started retiring is never reached
                // by that retirement, so it starts closed.
                region.Closed = IsRetiringOwnerLocked(owner);
                if (!region.Closed) _regions.Add(region);
                if (!_regionsByOwner.TryGetValue(owner, out var ownerRegions)) _regionsByOwner.Add(owner, ownerRegions = []);
                ownerRegions.Add(region);
                if (content is not null)
                {
                    var entry = new NavigationEntryCore(region, NextEntryId(), target!.Ownership, content)
                    {
                        Phase = NavigationEntryPhase.Active,
                        Lease = lease,
                    };
                    _tracked.Add(entry);
                    if (owned) _ownedEntries[content] = entry;
                    if (region.Closed)
                    {
                        entry.Phase = NavigationEntryPhase.Removed;
                        retireAtOnce = entry;
                    }
                    else region.Stack = [entry];
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The navigator started closing; user disposal runs outside the gate.
            DisposeUnclaimed(created ? content : null, lease);
            throw;
        }
        if (retireAtOnce is not null) _ = RetireAsync(retireAtOnce, null);
        return new NavigationRegion<TContent>(region);
    }

    // Best-effort disposal of an instance the factory created and no region took, then
    // release of its lease (content first, like retirement). User disposal code never runs
    // under the gate or inside a model turn: inside a turn, and for asynchronous-only
    // disposal, it runs on the thread pool. That work is tracked like a retirement, so
    // WhenIdleAsync and DisposeAsync wait for it.
    private void DisposeUnclaimed(object? content, IRunicModelContextLease? lease)
    {
        if (content is null)
        {
            ReleaseUnclaimedLease(lease);
            return;
        }
        if (!ModelContext.IsExecuting && content is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { }
            ReleaseUnclaimedLease(lease);
            return;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate) _retirements.Add(done.Task);
        _ = Task.Run(async () =>
        {
            try
            {
                if (content is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else (content as IDisposable)?.Dispose();
            }
            catch { }
            ReleaseUnclaimedLease(lease);
            lock (Gate) _retirements.Remove(done.Task);
            done.TrySetResult();
        });
    }

    private static void ReleaseUnclaimedLease(IRunicModelContextLease? lease)
    {
        try { lease?.Dispose(); }
        catch { }
    }

    /// <summary>
    /// Attaches a presentation that shows this navigator's content. Presentation integrations, such as the
    /// Runic Views runtime, call it; applications do not.
    /// </summary>
    /// <remarks>
    /// When owned content retires, after its child regions are closed and before the content is disposed,
    /// the navigator calls <see cref="INavigationPresentation.Forget"/> on each attached presentation,
    /// outside model turns. A presentation that throws is logged (event 1064, step <c>Forget</c>) and does
    /// not skip the others. A presentation attached after the navigator started closing is ignored.
    /// </remarks>
    /// <param name="presentation">The presentation to attach.</param>
    /// <returns>An <see cref="IDisposable"/> that detaches the presentation.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public IDisposable AttachPresentation(INavigationPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        lock (Gate)
        {
            if (_closing) return PresentationAttachment.None;
            var attachment = new PresentationAttachment(this, presentation);
            _presentations.Add(attachment);
            return attachment;
        }
    }

    // One attachment of a presentation; detaching removes only this attachment.
    private sealed class PresentationAttachment(RunicNavigator? navigator, INavigationPresentation? presentation) : IDisposable
    {
        public static readonly PresentationAttachment None = new(null, null);

        public INavigationPresentation Presentation { get; } = presentation!;

        public void Dispose()
        {
            if (navigator is null) return;
            lock (navigator.Gate) navigator._presentations.Remove(this);
        }
    }

    /// <summary>Completes when no transition is in flight and no retirement is running.</summary>
    public async ValueTask WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task[] pending;
            lock (Gate)
                pending = [.. _running.Select(transition => transition.Finished.Task), .. _retirements];
            if (pending.Length == 0) return;
            await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Refuses new requests and ends every open <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/>
    /// request as <see cref="NavigationCompletion{TResult}.Dismissed"/> (or <see cref="NavigationCompletion{TResult}.Completed"/>
    /// when its return already committed), so guards awaiting a result unblock. Then cancels in-flight transitions and
    /// waits for them up to the close timeout, and closes and retires every entry region by region in creation order.
    /// Does not run guards.
    /// </summary>
    /// <remarks>
    /// Each region is cleared in a model turn so the change is serialized with its commit. The wait for those
    /// turns is bounded by <see cref="RunicNavigatorOptions.CloseTimeout"/> for the whole disposal: when a turn stays blocked,
    /// that region is cleared outside a turn after the timeout and a warning (1068) is logged, the notifications it owes
    /// are posted, and the remaining regions are cleared without waiting. Disposal always completes; content disposal itself is not bounded.
    /// Retiring an entry whose initialize hook is running waits for the hook, within the same close timeout, before it disposes the
    /// content; once the wait for cancelled transitions timed out the hook is not awaited again and a warning (1069) is logged.
    /// An initialize hook never starts on content that retirement already claimed. The worst case of a disposal is about twice
    /// <see cref="RunicNavigatorOptions.CloseTimeout"/> (transition wait plus clearing turns), plus content disposal.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _disposal, disposal, null);
        if (existing is not null) return new(existing.Task);
        _ = DisposeCoreAsync(disposal);
        return new(disposal.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource disposal)
    {
        try
        {
            if (ModelContext.IsExecuting) await Task.Yield();
            List<NavigationTransition> cancel = [];
            Task[] running;
            NavigationResultRequestCore[] requests;
            lock (Gate)
            {
                _closing = true;
                foreach (var region in _regions)
                    foreach (var transition in region.InFlight)
                        if (transition.TrySetCancelReason(NavigationCancelReason.Closed)) cancel.Add(transition);
                requests = [.. _resultRequests];
            }
            // Dismiss open PushForResult requests first, so guards awaiting a result unblock.
            foreach (var request in requests) FinishResult(request, NavigationResultDismissal.Closed);
            Cancel(cancel);
            lock (Gate) running = [.. _running.Select(transition => transition.Finished.Task), .. _retirements];
            if (running.Length > 0)
            {
                try { await Task.WhenAll(running).WaitAsync(_closeTimeout, _time).ConfigureAwait(false); }
                catch (TimeoutException) { lock (Gate) _transitionWaitTimedOut = true; }
            }

            NavigationRegionCore[] regions;
            lock (Gate) regions = [.. _regions];
            foreach (var region in regions)
                foreach (var entry in await CloseRegionAsync(region).ConfigureAwait(false))
                    await RetireAsync(entry, null).ConfigureAwait(false);
            NavigationEntryCore[] remaining;
            lock (Gate) remaining = [.. _tracked];
            foreach (var entry in remaining.OrderBy(entry => entry.Id.Value))
                await RetireAsync(entry, null).ConfigureAwait(false);
            lock (Gate) _presentations.Clear();
        }
        finally
        {
            disposal.TrySetResult();
        }
    }

    // ---- Admission -------------------------------------------------------

    // Test seam: runs between the cancellation check and the initialize claim.
    internal Action? BeforeInitializeClaim { get; set; }

    internal NavigationResultRequest<TContent, TResult> PushForResult<TContent, TResult>(NavigationRegionCore region,
        NavigationTargetCore target, NavigationRequestOptions? options, CancellationToken cancellationToken) where TContent : class
    {
        var request = new NavigationResultRequestCore<TResult>(region, cancellationToken);
        var outcome = Start(region, NavigationOperation.Push, target, null, options?.ExpectedCurrent, cancellationToken,
            @return: null, request);
        return new(NavigationResults.MapTask<TContent>(outcome), request.Completion);
    }

    internal Task<NavigationOutcome> Start(NavigationRegionCore region, NavigationOperation operation,
        NavigationTargetCore? target, NavigationEntryId? backTo, NavigationEntryId? expected,
        CancellationToken cancellationToken, NavigationReturn? @return = null, NavigationResultRequestCore? request = null,
        bool runDetached = false)
    {
        NavigationOutcome? rejected = null;
        NavigationTransition? transition = null;
        List<NavigationTransition> superseded = [];
        var transitioningChanged = false;
        lock (Gate)
        {
            var related = RelatedRunningLocked(region);
            if (_closing || region.Closed) rejected = NavigationOutcome.Reject(NavigationRejection.Closed);
            else if (cancellationToken.IsCancellationRequested)
                // Rejected before admission, so a dead request never supersedes its predecessors.
                rejected = NavigationOutcome.Reject(NavigationRejection.Cancelled);
            else if (HookTransition.Value is { } hook && !hook.Released
                && (ReferenceEquals(hook.Region, region) || related.Contains(hook)))
                // A hook never waits on a transition that waits on it. This also rejects requests to
                // ancestor and descendant regions of the hook's region (deviation 15): conservative.
                rejected = NavigationOutcome.Reject(NavigationRejection.Reentrant);
            else if (expected is { } expectedId && region.CurrentEntry?.Id != expectedId)
                rejected = NavigationOutcome.Reject(NavigationRejection.NotCurrent);
            else if (operation == NavigationOperation.Back && region.Stack.Length < (@return is null ? 2 : 1))
                rejected = NavigationOutcome.Reject(NavigationRejection.NoHistory);
            else
            {
                NavigationEntryCore? pending = null;
                if (target is not null)
                {
                    if (target is { Ownership: NavigationOwnership.Owned, Instance: { } instance }) Claim(instance);
                    pending = new NavigationEntryCore(region, NextEntryId(), target.Ownership, target.Instance)
                    {
                        Phase = NavigationEntryPhase.Pending,
                        ResultRequest = request,
                    };
                    _tracked.Add(pending);
                    if (pending.Owned && pending.Content is { } claimed) _ownedEntries[claimed] = pending;
                    if (request is not null)
                    {
                        request.Entry = pending;
                        _resultRequests.Add(request);
                    }
                }

                List<Task> waitFor = [];
                foreach (var earlier in region.InFlight)
                {
                    if (earlier.TrySupersede()) superseded.Add(earlier);
                    waitFor.Add(earlier.Terminated.Task);
                }
                // The provisional plan: the child regions this request would
                // reset or close. Their transitions are superseded the same way.
                var plan = ComputePlanLocked(region, operation, pending: null, backTo, expected, @return is not null);
                foreach (var child in plan.Regions)
                {
                    if (ReferenceEquals(child, region)) continue;
                    foreach (var earlier in child.InFlight)
                    {
                        if (earlier.TrySupersede()) superseded.Add(earlier);
                        waitFor.Add(earlier.Terminated.Task);
                    }
                }
                // Related transitions of ancestor and descendant regions are not waited
                // on here: their guard sets are only known when they start guarding.
                // Each guard hook checks for overlap under the gate (EnterGuardHookAsync).
                transition = new NavigationTransition(region, operation, target, pending, backTo, expected,
                    [.. waitFor], cancellationToken) { Return = @return };
                region.InFlight.Add(transition);
                transitioningChanged = region.InFlight.Count == 1;
                _running.Add(transition);
            }
        }

        if (rejected is not null)
        {
            LogOutcome(region, operation, rejected);
            if (request is not null) FinishResult(request, NavigationResultDismissal.NotCommitted);
            return Task.FromResult(rejected);
        }

        foreach (var earlier in superseded) StartOverrunTimer(earlier);
        Cancel(superseded);
        if (transitioningChanged) Notify(region, NavigationRegionChanges.Transitioning);
        // No hook ever runs in the caller's turn.
        if (runDetached)
            // Admitted here; the phases run on the thread pool without this thread's execution context.
            ThreadPool.UnsafeQueueUserWorkItem(static transition => _ = transition.Region.Navigator.RunAsync(transition), transition!, preferLocal: false);
        else if (ModelContext.IsExecuting) _ = Task.Run(() => RunAsync(transition!), CancellationToken.None);
        else _ = RunAsync(transition!);
        return transition!.Result.Task;
    }

    // ---- Transition ------------------------------------------------------

    private async Task RunAsync(NavigationTransition transition)
    {
        NavigationOutcome outcome = NavigationOutcome.Superseded;
        NavigationCommit? commit = null;
        try
        {
            try
            {
                (outcome, commit) = await RunPhasesAsync(transition).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // Phases return outcomes; this only guards against a defect.
                outcome = NavigationOutcome.Fail(error, transition.Phase);
            }

            // One terminal path: release admission (unless the commit turn
            // already did), then let waiting requests proceed.
            Release(transition);
            transition.Terminated.TrySetResult();
            if (commit is not null)
            {
                Cancel(commit.Cancel);
                if (transition.ResultDropped)
                    NavigationLog.NavigationResultDropped(Logger, null, transition.Region.ContentTypeName, transition.Region.Id);
                // Before retirement, so caller cancellation takes effect at once. Guarded,
                // so a cancellation callback that throws can't skip retirement.
                if (transition.Pending?.ResultRequest is { } request)
                {
                    try { WatchCaller(request); }
                    catch (Exception error)
                    {
                        NavigationLog.NavigationEntryCleanupFailed(Logger, error, transition.Region.ContentTypeName, transition.Region.Id,
                            "None", "Cancel", BridgeTelemetry.ErrorType(error));
                    }
                }
                List<NavigationEntryId> retired = [];
                foreach (var entry in commit.Retire)
                {
                    try { await RetireAsync(entry, retired).ConfigureAwait(false); }
                    catch (Exception error)
                    {
                        NavigationLog.NavigationEntryCleanupFailed(Logger, error, entry.Region.ContentTypeName, entry.Region.Id,
                            entry.ContentTypeName, "Retire", BridgeTelemetry.ErrorType(error));
                    }
                }
                outcome = NavigationOutcome.Commit(commit.Current, retired);
            }
            else if (transition.Pending is { } pending)
            {
                if (pending.ResultRequest is { } request) FinishResult(request, NavigationResultDismissal.NotCommitted);
                await RetireAsync(pending, null).ConfigureAwait(false);
            }
            LogOutcome(transition.Region, transition.Operation, outcome);
        }
        finally
        {
            transition.Dispose();
            lock (Gate) _running.Remove(transition);
            transition.Finished.TrySetResult();
            // Asynchronous continuations, set after the commit turn returned.
            transition.Result.TrySetResult(outcome);
        }
    }

    private async Task<(NavigationOutcome Outcome, NavigationCommit? Commit)> RunPhasesAsync(NavigationTransition transition)
    {
        var region = transition.Region;
        var token = transition.Token;

        // Waiting: for the predecessors in this region and the in-flight
        // transitions of child regions in the provisional plan.
        transition.Phase = NavigationPhase.Guarding;
        if (transition.WaitFor.Length > 0)
        {
            try { await Task.WhenAll(transition.WaitFor).WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (CancelledOutcome(transition), null);
            }
        }

        // Guarding.
        NavigationPlan plan;
        lock (Gate)
        {
            if (transition.CancelReason != NavigationCancelReason.None || _closing || region.Closed
                || transition.CallerToken.IsCancellationRequested)
                return (CancelledOutcome(transition), null);
            plan = ComputePlanLocked(region, transition.Operation, transition.Pending, transition.BackTo, transition.Expected,
                transition.Return is not null);
        }
        if (plan.Rejection is { } rejection) return (NavigationOutcome.Reject(rejection), null);
        if (plan.Unchanged) return (NavigationOutcome.Commit(region.CurrentEntry, []), null);

        foreach (var (entry, kind) in plan.Guards)
        {
            if (entry.Content is not INavigationDepartureGuard guard) continue;
            if (token.IsCancellationRequested) return (CancelledOutcome(transition), null);
            // A plan region changed since the basis: the commit would be
            // superseded, and the entry may already have retired.
            if (!BasisMatches(plan)) return (NavigationOutcome.Superseded, null);
            bool allowed;
            // Guard hooks of one region never overlap across transitions (see EnterGuardHookAsync).
            switch (await EnterGuardHookAsync(transition, entry, plan, token).ConfigureAwait(false))
            {
                case GuardSlot.Cancelled: return (CancelledOutcome(transition), null);
                case GuardSlot.Stale: return (NavigationOutcome.Superseded, null);
            }
            try
            {
                var departure = new NavigationDeparture(entry.Id, kind, transition.Operation);
                allowed = await InvokeHook(transition, () => guard.CanDepartAsync(departure, token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return (CancelledOutcome(transition), null);
            }
            catch (Exception error)
            {
                // A guard that fails because the navigator or region is closing is a close, not a failure.
                if (IsClosing(region)) return (NavigationOutcome.Reject(NavigationRejection.Closed), null);
                NavigationLog.NavigationGuardFailed(Logger, error, region.ContentTypeName, region.Id,
                    OperationName(transition.Operation), entry.ContentTypeName, BridgeTelemetry.ErrorType(error));
                return (NavigationOutcome.Fail(error, NavigationPhase.Guarding), null);
            }
            finally { ExitGuardHook(transition); }
            if (!allowed) return (NavigationOutcome.Reject(NavigationRejection.Guard, entry.Id), null);
        }

        // Preparing.
        transition.Phase = NavigationPhase.Preparing;
        if (token.IsCancellationRequested) return (CancelledOutcome(transition), null);
        if (IsClosing(region)) return (NavigationOutcome.Reject(NavigationRejection.Closed), null);
        if (!BasisMatches(plan)) return (NavigationOutcome.Superseded, null);
        var failure = await PrepareAsync(transition, plan).ConfigureAwait(false);
        if (failure is not null) return (failure, null);
        if (token.IsCancellationRequested) return (CancelledOutcome(transition), null);

        // Committing.
        lock (Gate)
        {
            if (transition.CancelReason != NavigationCancelReason.None || _closing)
                return (CancelledOutcome(transition), null);
            transition.CommitStarted = true;
            transition.Phase = NavigationPhase.Committing;
        }
        NavigationCommitResult result;
        try
        {
            result = await ModelContext.InvokeAsync(() => CommitTurn(transition, plan)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            bool closing;
            lock (Gate) closing = _closing;
            if (closing) return (NavigationOutcome.Reject(NavigationRejection.Closed), null);
            NavigationLog.NavigationCommitFailed(Logger, error, region.ContentTypeName, region.Id,
                OperationName(transition.Operation), BridgeTelemetry.ErrorType(error));
            return (NavigationOutcome.Fail(error, NavigationPhase.Committing), null);
        }
        return (result.Outcome, result.Commit);
    }

    private async Task<NavigationOutcome?> PrepareAsync(NavigationTransition transition, NavigationPlan plan)
    {
        var region = transition.Region;
        var token = transition.Token;
        if (transition.Pending is { } entry)
        {
            var target = transition.Target!;
            try
            {
                if (entry.Content is null)
                {
                    var services = Services ?? EmptyServiceProvider.Instance;
                    var content = InvokeHookSync(transition, () => target.Create(services));
                    Claim(content);
                    bool retiring;
                    lock (Gate)
                    {
                        // A navigator that timed out waiting may already have
                        // retired this pending entry without content.
                        retiring = entry.Retiring is not null;
                        if (!retiring)
                        {
                            entry.Content = content;
                            _ownedEntries[content] = entry;
                        }
                    }
                    if (retiring)
                    {
                        if (content is IAsyncDisposable late) await late.DisposeAsync().ConfigureAwait(false);
                        else (content as IDisposable)?.Dispose();
                        return NavigationOutcome.Reject(NavigationRejection.Closed);
                    }
                }
                if (entry.Owned)
                {
                    var bound = RunicModelContextRegistry.Shared.Bind(ModelContext, entry.Content!);
                    bool retiringNow;
                    lock (Gate)
                    {
                        // Retirement may have already read the lease slot. Assign
                        // only while the entry is not retiring; otherwise this
                        // path owns the new lease and must release it.
                        retiringNow = entry.Retiring is not null;
                        if (!retiringNow) entry.Lease = bound;
                    }
                    if (retiringNow)
                    {
                        await bound.DisposeAsync().ConfigureAwait(false);
                        return NavigationOutcome.Reject(NavigationRejection.Closed);
                    }
                }
                if (token.IsCancellationRequested) return CancelledOutcome(transition);
                var content_ = entry.Content!;
                BeforeInitializeClaim?.Invoke();
                TaskCompletionSource? initializing = null;
                if (target.HasInput || entry.Content is INavigationInitialize)
                {
                    // Claim "initializing" under the gate retirement uses: either
                    // retirement already won and initialize never starts, or
                    // retirement waits for this hook before disposing the content.
                    lock (Gate)
                    {
                        if (entry.Retiring is null)
                        {
                            initializing = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            entry.Initializing = initializing.Task;
                        }
                    }
                    if (initializing is null) return NavigationOutcome.Reject(NavigationRejection.Closed);
                }
                try
                {
                    if (target.HasInput)
                        await InvokeHook(transition, () => target.InitializeWithInputAsync(content_, entry.Context, token)).ConfigureAwait(false);
                    else if (content_ is INavigationInitialize initialize)
                        await InvokeHook(transition, () => initialize.InitializeAsync(entry.Context, token)).ConfigureAwait(false);
                }
                finally { initializing?.TrySetResult(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return CancelledOutcome(transition);
            }
            catch (Exception error)
            {
                // A factory that touches a navigator that is closing (for example
                // CreateRegion) fails because of the close, not on its own.
                if (IsClosing(region)) return NavigationOutcome.Reject(NavigationRejection.Closed);
                NavigationLog.NavigationPreparationFailed(Logger, error, region.ContentTypeName, region.Id,
                    OperationName(transition.Operation), entry.ContentTypeName, BridgeTelemetry.ErrorType(error));
                return NavigationOutcome.Fail(error, NavigationPhase.Preparing);
            }
        }
        else if (plan.Resume is { Content: INavigationResume resume } resumed)
        {
            try
            {
                // Skip a resume that is cancelled or whose entry retirement already claimed. A resume
                // that started before retirement may still overrun disposal once the close timeout
                // passed; that is the accepted overrun.
                if (token.IsCancellationRequested) return CancelledOutcome(transition);
                bool retiringResume;
                lock (Gate) retiringResume = resumed.Retiring is not null;
                if (retiringResume) return NavigationOutcome.Reject(NavigationRejection.Closed);
                var request = new NavigationResume(resumed.Id, transition.Operation);
                await InvokeHook(transition, () => resume.ResumeAsync(request, token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return CancelledOutcome(transition);
            }
            catch (Exception error)
            {
                if (IsClosing(region)) return NavigationOutcome.Reject(NavigationRejection.Closed);
                NavigationLog.NavigationPreparationFailed(Logger, error, region.ContentTypeName, region.Id,
                    OperationName(transition.Operation), resumed.ContentTypeName, BridgeTelemetry.ErrorType(error));
                return NavigationOutcome.Fail(error, NavigationPhase.Preparing);
            }
        }
        return null;
    }

    // Runs inside one model turn: re-check, apply the plan, then notify.
    private NavigationCommitResult CommitTurn(NavigationTransition transition, NavigationPlan plan)
    {
        var region = transition.Region;
        NavigationOutcome? stopped = null;
        NavigationCommit? commit = null;
        List<(NavigationRegionCore Region, NavigationRegionChanges Changes)> changed = [];
        lock (Gate)
        {
            if (transition.CancelReason != NavigationCancelReason.None || _closing || region.Closed)
                stopped = CancelledOutcome(transition);
            else if (transition.CallerToken.IsCancellationRequested)
                stopped = NavigationOutcome.Reject(NavigationRejection.Cancelled);
            else if (!plan.BasisMatches())
                stopped = NavigationOutcome.Superseded;
            else if (transition.Expected is { } expected && region.CurrentEntry?.Id != expected)
                stopped = NavigationOutcome.Reject(NavigationRejection.NotCurrent);
            else if (plan.Resume is { } resume && Array.IndexOf(region.Stack, resume) < 0)
                stopped = NavigationOutcome.Reject(NavigationRejection.EntryNotFound);
            else
            {
                commit = new NavigationCommit();
                foreach (var (child, stack, removed) in plan.ChildResets)
                {
                    var before = Snapshot(child);
                    foreach (var entry in removed) entry.Phase = NavigationEntryPhase.Removed;
                    ApplyStack(child, stack);
                    commit.Retire.AddRange(removed);
                    changed.Add((child, Changes(before, child)));
                }
                var regionBefore = Snapshot(region);
                foreach (var entry in plan.Removed) entry.Phase = NavigationEntryPhase.Removed;
                ApplyStack(region, plan.NewStack);
                commit.Retire.AddRange(plan.Removed);
                foreach (var closing in plan.Closing)
                    MarkClosedLocked(closing, commit.Cancel);
                foreach (var affected in plan.Regions) affected.Version++;
                // A committed PushForResult can now complete; a committed return takes the
                // value it carries, unless the request was already dismissed (the value is dropped).
                if (transition.Pending?.ResultRequest is { State: NavigationResultState.Pending } pushed)
                    pushed.State = NavigationResultState.Active;
                if (transition.Return?.Result is { } staged
                    && !(staged.Request.Entry is { } returning && plan.Removed.Contains(returning) && staged.TryAccept()))
                    transition.ResultDropped = true;
                commit.Current = region.CurrentEntry;
                changed.Add((region, Changes(regionBefore, region)));
            }
            // Admission is released in the commit turn, so IsTransitioning
            // changes together with the committed state.
            ReleaseLocked(transition);
        }

        foreach (var (affected, changes) in changed) affected.RaiseChanges(changes);
        if (!changed.Any(item => ReferenceEquals(item.Region, region))) region.RaiseChanges(NavigationRegionChanges.None);
        return new NavigationCommitResult(stopped ?? NavigationOutcome.Commit(commit!.Current, []), commit);
    }

    private bool IsClosing(NavigationRegionCore region)
    {
        lock (Gate) return _closing || region.Closed;
    }

    private bool BasisMatches(NavigationPlan plan)
    {
        lock (Gate) return plan.BasisMatches();
    }

    private static (NavigationEntryCore? Current, NavigationEntryCore[] Stack, bool CanGoBack) Snapshot(NavigationRegionCore region) =>
        (region.CurrentEntry, region.Stack, region.CanGoBack);

    private static NavigationRegionChanges Changes((NavigationEntryCore? Current, NavigationEntryCore[] Stack, bool CanGoBack) before,
        NavigationRegionCore region)
    {
        var changes = NavigationRegionChanges.None;
        if (!ReferenceEquals(before.Current, region.CurrentEntry)) changes |= NavigationRegionChanges.Current;
        var history = region.Stack.AsSpan(0, Math.Max(0, region.Stack.Length - 1));
        var previous = before.Stack.AsSpan(0, Math.Max(0, before.Stack.Length - 1));
        if (!history.SequenceEqual(previous)) changes |= NavigationRegionChanges.History;
        if (before.CanGoBack != region.CanGoBack) changes |= NavigationRegionChanges.CanGoBack;
        return changes;
    }

    private static void ApplyStack(NavigationRegionCore region, NavigationEntryCore[] stack)
    {
        for (var index = 0; index < stack.Length; index++)
            stack[index].Phase = index == stack.Length - 1 ? NavigationEntryPhase.Active : NavigationEntryPhase.Retained;
        region.Stack = stack;
    }

    // ---- Plans -----------------------------------------------------------

    // Running transitions whose region is an ancestor or a descendant of this one.
    private List<NavigationTransition> RelatedRunningLocked(NavigationRegionCore region)
    {
        List<NavigationTransition> related = [];
        if (_running.Count == 0) return related;
        var ancestors = AncestorsLocked(region);
        foreach (var transition in _running)
        {
            if (ReferenceEquals(transition.Region, region)) continue;
            if (ancestors.Contains(transition.Region) || AncestorsLocked(transition.Region).Contains(region)) related.Add(transition);
        }
        return related;
    }

    // Starts one departure guard hook. Guard hooks of one region never overlap, across
    // transitions: a hook waits for a running hook whose entry belongs to the same
    // region. The check is per hook, not per plan, because a later guard of a plan
    // may not have started yet. Hooks in different regions (a parent's guard and its
    // child's guard) may overlap. The wait can be long, so the plan's basis and the entry's
    // state are checked again under the gate when the slot is taken: a guard never runs
    // on an entry that retired, or on a plan that was superseded, while this hook waited.
    // Under that rule a parent transition can re-run a guard the child already ran for
    // the same entry (deviation 14).
    private enum GuardSlot { Entered, Cancelled, Stale }

    private async Task<GuardSlot> EnterGuardHookAsync(NavigationTransition transition, NavigationEntryCore entry,
        NavigationPlan plan, CancellationToken token)
    {
        while (true)
        {
            Task? blocker = null;
            lock (Gate)
            {
                if (_closing || transition.Region.Closed
                    || transition.CancelReason != NavigationCancelReason.None)
                    return GuardSlot.Cancelled;
                if (entry.Region.Closed || !plan.BasisMatches() || entry.Retiring is not null) return GuardSlot.Stale;
                foreach (var other in _running)
                {
                    if (ReferenceEquals(other, transition) || other.GuardHook is not { } hook) continue;
                    if (other.GuardingEntry is not { } guarding || !ReferenceEquals(guarding.Region, entry.Region)) continue;
                    blocker = hook.Task;
                    break;
                }
                if (blocker is null)
                {
                    transition.GuardingEntry = entry;
                    transition.GuardHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    return GuardSlot.Entered;
                }
            }
            try { await blocker.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return GuardSlot.Cancelled; }
        }
    }

    private void ExitGuardHook(NavigationTransition transition)
    {
        TaskCompletionSource? hook;
        lock (Gate)
        {
            hook = transition.GuardHook;
            transition.GuardHook = null;
            transition.GuardingEntry = null;
        }
        hook?.TrySetResult();
    }

    // The regions that hold the owner of this region, transitively. A borrowed
    // owner can sit in several regions, so every holder counts.
    private HashSet<NavigationRegionCore> AncestorsLocked(NavigationRegionCore region)
    {
        HashSet<NavigationRegionCore> found = new(ReferenceEqualityComparer.Instance);
        Stack<NavigationRegionCore> pending = new();
        pending.Push(region);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var entry in _tracked)
            {
                if (ReferenceEquals(entry.Content, current.Owner) && found.Add(entry.Region)) pending.Push(entry.Region);
            }
        }
        return found;
    }

    private NavigationPlan ComputePlanLocked(NavigationRegionCore region, NavigationOperation operation,
        NavigationEntryCore? pending, NavigationEntryId? backTo, NavigationEntryId? expected, bool popToEmpty = false)
    {
        var plan = new NavigationPlan();
        plan.Regions.Add(region);
        var stack = region.Stack;
        var current = stack.Length == 0 ? null : stack[^1];
        if (expected is { } expectedId && current?.Id != expectedId)
            return plan.Reject(NavigationRejection.NotCurrent);

        switch (operation)
        {
            case NavigationOperation.Push:
                // A result entry whose caller cancelled is on its way out: a push retires it rather
                // than retaining it under the new entry (OnCallerCancelled).
                if (current?.ResultRequest is { State: NavigationResultState.Dismissed })
                {
                    plan.Removed.Add(current);
                    plan.NewStack = pending is null ? stack : [.. stack.AsSpan(0, stack.Length - 1), pending];
                    break;
                }
                plan.NewStack = pending is null ? stack : [.. stack, pending];
                if (current is not null)
                {
                    if (current is { Owned: true, Content: { } content } && _regionsByOwner.TryGetValue(content, out var children))
                    {
                        foreach (var child in children)
                        {
                            if (child.Closed || child.WhileParentRetained == NavigationChildRetention.Keep) continue;
                            var keep = child.WhileParentRetained == NavigationChildRetention.ResetToRoot ? Math.Min(1, child.Stack.Length) : 0;
                            if (child.Stack.Length == keep) continue;
                            var removed = TopDown(child.Stack, keep);
                            plan.Regions.Add(child);
                            plan.ChildResets.Add((child, child.Stack[..keep], removed));
                            foreach (var entry in removed) AddRetiring(plan, entry);
                        }
                    }
                    plan.Guards.Add((current, NavigationDepartureKind.Retain));
                }
                break;
            case NavigationOperation.Back:
                // A return from a result entry may leave the region empty.
                if (stack.Length < (popToEmpty ? 1 : 2)) return plan.Reject(NavigationRejection.NoHistory);
                plan.Removed.Add(current!);
                plan.Resume = stack.Length > 1 ? stack[^2] : null;
                plan.NewStack = stack[..^1];
                break;
            case NavigationOperation.BackTo:
                var index = Array.FindIndex(stack, entry => entry.Id == backTo);
                if (index < 0) return plan.Reject(NavigationRejection.EntryNotFound);
                if (index == stack.Length - 1)
                {
                    plan.Unchanged = true;
                    break;
                }
                plan.Removed.AddRange(TopDown(stack, index + 1));
                plan.Resume = stack[index];
                plan.NewStack = stack[..(index + 1)];
                break;
            case NavigationOperation.Replace:
                if (current is not null) plan.Removed.Add(current);
                plan.NewStack = pending is null ? stack : [.. stack.AsSpan(0, Math.Max(0, stack.Length - 1)), pending];
                break;
            case NavigationOperation.Reset:
                plan.Removed.AddRange(TopDown(stack, 0));
                plan.NewStack = pending is null ? [] : [pending];
                break;
            case NavigationOperation.ClearHistory:
                plan.Removed.AddRange(TopDown(stack, 0).Skip(current is null ? 0 : 1));
                plan.NewStack = current is null ? [] : [current];
                break;
            case NavigationOperation.Clear:
                plan.Removed.AddRange(TopDown(stack, 0));
                plan.NewStack = [];
                break;
        }
        foreach (var entry in plan.Removed) AddRetiring(plan, entry);
        plan.Basis = [.. plan.Regions.Select(affected => affected.Version)];
        return plan;
    }

    // Entries from the top of the stack down to (and including) index `from`.
    private static List<NavigationEntryCore> TopDown(NavigationEntryCore[] stack, int from)
    {
        List<NavigationEntryCore> entries = [];
        for (var index = stack.Length - 1; index >= from; index--) entries.Add(stack[index]);
        return entries;
    }

    // Adds a retiring entry's owned descendants (deepest first), then the entry's guard.
    private void AddRetiring(NavigationPlan plan, NavigationEntryCore entry)
    {
        if (entry is { Owned: true, Content: { } content } && _regionsByOwner.TryGetValue(content, out var children))
        {
            foreach (var child in children)
            {
                if (!plan.Regions.Contains(child)) plan.Regions.Add(child);
                if (!plan.Closing.Contains(child)) plan.Closing.Add(child);
                foreach (var descendant in TopDown(child.Stack, 0)) AddRetiring(plan, descendant);
            }
        }
        plan.Guards.Add((entry, NavigationDepartureKind.Retire));
    }

    // ---- Results ---------------------------------------------------------

    private static readonly Action<object?> CallerCancelled = static state =>
    {
        var request = (NavigationResultRequestCore)state!;
        request.Region.Navigator.OnCallerCancelled(request);
    };

    // Ends a result request once. A request whose return already committed with a value
    // (Completing) completes on every path; any other open request is dismissed. With
    // onlyActive, only a committed request that has not completed is dismissed. The
    // sources are set outside the gate, never inside a commit turn; beforeEnd runs after the
    // state became terminal and before the source is set.
    private bool FinishResult(NavigationResultRequestCore request, NavigationResultDismissal reason, bool onlyActive = false,
        Action? beforeEnd = null)
    {
        bool completed;
        CancellationTokenRegistration registration;
        lock (Gate)
        {
            if (request.State is NavigationResultState.Completed or NavigationResultState.Dismissed) return false;
            if (onlyActive && request.State != NavigationResultState.Active) return false;
            completed = request.State == NavigationResultState.Completing;
            request.State = completed ? NavigationResultState.Completed : NavigationResultState.Dismissed;
            registration = request.Registration;
            request.Registration = default;
            _resultRequests.Remove(request);
        }
        // Unregister does not wait for a running callback, so this is safe from the callback itself.
        registration.Unregister();
        beforeEnd?.Invoke();
        if (completed) request.Complete();
        else
        {
            request.Dismiss();
            NavigationLog.NavigationResultDismissed(Logger, null, request.Region.ContentTypeName, request.Region.Id, reason);
        }
        return true;
    }

    // After the push committed, cancelling the caller's token dismisses the request.
    private void WatchCaller(NavigationResultRequestCore request)
    {
        if (!request.CallerToken.CanBeCanceled) return;
        // No execution context is captured: the callback must not inherit a hook marker.
        // An already cancelled token runs the callback here, which dismisses the request.
        var registration = request.CallerToken.UnsafeRegister(CallerCancelled, request);
        bool keep;
        lock (Gate)
        {
            keep = request.State == NavigationResultState.Active;
            if (keep) request.Registration = registration;
        }
        if (!keep) registration.Unregister();
    }

    // Dismisses at once and goes back from the entry if it is still current. The Back is
    // admitted before the completion is set, so a request the caller makes after observing
    // the dismissal supersedes it; a Push over the dismissed entry then retires it
    // (ComputePlanLocked). The Back runs on the thread pool and is not awaited; its rejection
    // or supersession is logged (1065/1066). The callback may run on a thread inside a hook,
    // so the hook marker is cleared for the admission.
    private void OnCallerCancelled(NavigationResultRequestCore request)
    {
        NavigationEntryCore? entry;
        lock (Gate) entry = request.Entry;
        if (entry is null) return;
        FinishResult(request, NavigationResultDismissal.Cancelled, onlyActive: true, beforeEnd: () =>
        {
            var hook = HookTransition.Value;
            HookTransition.Value = null;
            try
            {
                _ = Start(entry.Region, NavigationOperation.Back, null, null, entry.Id, CancellationToken.None,
                    new NavigationReturn(null), runDetached: true);
            }
            finally { HookTransition.Value = hook; }
        });
    }

    // ---- Retirement ------------------------------------------------------

    // The move to Retiring happens once per entry, whichever path gets here first.
    internal Task RetireAsync(NavigationEntryCore entry, List<NavigationEntryId>? retired)
    {
        TaskCompletionSource done;
        lock (Gate)
        {
            if (entry.Retiring is { } running) return running;
            done = new(TaskCreationOptions.RunContinuationsAsynchronously);
            entry.Retiring = done.Task;
            entry.Phase = NavigationEntryPhase.Retiring;
            _retirements.Add(done.Task);
        }
        retired?.Add(entry.Id);
        _ = RetireCoreAsync(entry, done, retired);
        return done.Task;
    }

    private async Task RetireCoreAsync(NavigationEntryCore entry, TaskCompletionSource done, List<NavigationEntryId>? retired)
    {
        try
        {
            // Owned disposal never runs inside a model turn.
            if (ModelContext.IsExecuting) await Task.Yield();

            // 1. Cancel the entry's retirement token, then end its result request: completed
            // when a committed return carried a value, dismissed otherwise.
            Step(entry, "Retirement", static entry => entry.Retirement.Cancel());
            if (entry.ResultRequest is { } request) FinishResult(request, NavigationResultDismissal.Retired);

            var content = entry.Content;
            if (entry.Owned && content is not null)
            {
                // An initialize hook that already started owns the content until it
                // returns; wait for it, bounded by CloseTimeout. A hook that never
                // returns is abandoned after the timeout.
                Task? initializing;
                lock (Gate) initializing = entry.Initializing;
                if (initializing is { IsCompleted: false })
                {
                    var wait = InitializeWait();
                    if (wait is { } limit)
                    {
                        try { await initializing.WaitAsync(limit, _time).ConfigureAwait(false); }
                        catch (TimeoutException) { NavigationLog.NavigationInitializeTimedOut(Logger, null, entry.Region.ContentTypeName, entry.Region.Id, entry.ContentTypeName); }
                    }
                    else NavigationLog.NavigationInitializeTimedOut(Logger, null, entry.Region.ContentTypeName, entry.Region.Id, entry.ContentTypeName);
                }

                // 2. Close child regions without awaiting their transitions,
                // then retire their entries depth-first.
                List<NavigationRegionCore>? children;
                lock (Gate) _regionsByOwner.Remove(content, out children);
                foreach (var child in children ?? [])
                {
                    foreach (var descendant in await CloseRegionAsync(child).ConfigureAwait(false))
                    {
                        try { await RetireAsync(descendant, retired).ConfigureAwait(false); }
                        catch (Exception error) { LogCleanup(entry, "Children", error); }
                    }
                }

                // 3. Detach every presentation of the content.
                ForgetPresentations(entry);

                // 4. Dispose the content outside turns.
                try
                {
                    if (content is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    else (content as IDisposable)?.Dispose();
                }
                catch (Exception error) { LogCleanup(entry, "Dispose", error); }
            }

            // 5. Release the model-context lease.
            IRunicModelContextLease? lease;
            lock (Gate)
            {
                // Retiring is already set, so a late bind sees it and releases its own lease.
                lease = entry.Lease;
                entry.Lease = null;
            }
            if (lease is not null)
            {
                try { await lease.DisposeAsync().ConfigureAwait(false); }
                catch (Exception error) { LogCleanup(entry, "Lease", error); }
            }
        }
        finally
        {
            lock (Gate)
            {
                entry.Phase = NavigationEntryPhase.Retired;
                _tracked.Remove(entry);
                if (entry.Content is { } content && _ownedEntries.TryGetValue(content, out var owner) && ReferenceEquals(owner, entry))
                    _ownedEntries.Remove(content);
                _retirements.Remove(done.Task);
            }
            done.TrySetResult();
        }
    }

    // Detaches retiring owned content from every attached presentation, one at a
    // time, so one failure doesn't skip the others. A presentation detached while
    // this runs still gets the snapshot's call.
    private void ForgetPresentations(NavigationEntryCore entry)
    {
        if (entry.Content is not { } content) return;
        PresentationAttachment[] attachments;
        lock (Gate) attachments = [.. _presentations];
        foreach (var attachment in attachments)
        {
            try { attachment.Presentation.Forget(content); }
            catch (Exception error) { LogCleanup(entry, "Forget", error); }
        }
    }

    private void Step(NavigationEntryCore entry, string step, Action<NavigationEntryCore> action)
    {
        try { action(entry); }
        catch (Exception error) { LogCleanup(entry, step, error); }
    }

    private void LogCleanup(NavigationEntryCore entry, string step, Exception error) =>
        NavigationLog.NavigationEntryCleanupFailed(Logger, error, entry.Region.ContentTypeName, entry.Region.Id,
            entry.ContentTypeName, step, BridgeTelemetry.ErrorType(error));

    // Marks a region closed, cancels its in-flight transitions and takes its
    // committed entries (current first, then history from top to bottom). The
    // region's stack is cleared in a model turn, so the change is serialized
    // with a commit turn of the region that is still raising its notifications.
    // A commit applies its stack under the gate and only while the region is
    // open, so marking the region closed first leaves nothing to race with.
    private async Task<List<NavigationEntryCore>> CloseRegionAsync(NavigationRegionCore region)
    {
        List<NavigationTransition> cancel = [];
        List<NavigationEntryCore> entries;
        (NavigationEntryCore? Current, NavigationEntryCore[] Stack, bool CanGoBack) before;
        lock (Gate)
        {
            MarkClosedLocked(region, cancel);
            entries = TopDown(region.Stack, 0);
            before = Snapshot(region);
            _regions.Remove(region);
        }
        Cancel(cancel);

        // Whoever clears first, the clearing turn or the fallback, owes the notifications.
        var cleared = false;
        NavigationRegionChanges? ClearOnce()
        {
            lock (Gate)
            {
                if (cleared) return null;
                cleared = true;
                RemoveClosedEntries(region, entries);
                return Changes(before, region);
            }
        }

        // An entry's state is observable too, so the entries leave Active/Retained in the
        // same turn that clears the stack. While the navigator is closing, the wait for
        // those turns shares one deadline across the whole disposal.
        var fallback = false;
        var wait = TurnWait();
        if (wait is null) fallback = true;
        else
        {
            var turn = ModelContext.InvokeAsync(() =>
            {
                if (ClearOnce() is { } changes) region.RaiseChanges(changes);
            }).AsTask();
            // A turn abandoned after a timeout can still fault later; observe it.
            _ = turn.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try { await turn.WaitAsync(wait.Value, _time).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // A blocked turn: clear outside a turn rather than hang disposal. A turn that
                // runs later finds the region already cleared. The disposal waits for no more turns.
                lock (Gate) _closeTurnsTimedOut |= _closing;
                NavigationLog.NavigationCloseTimedOut(Logger, null, region.ContentTypeName, region.Id);
                fallback = true;
            }
            catch (ObjectDisposedException)
            {
                // The model context is gone, so no turn can observe the region any more.
                fallback = true;
            }
            catch (Exception error)
            {
                NavigationLog.NavigationEntryCleanupFailed(Logger, error, region.ContentTypeName, region.Id,
                    "None", "Close", BridgeTelemetry.ErrorType(error));
                fallback = true;
            }
        }
        if (fallback && ClearOnce() is { } owed && owed != NavigationRegionChanges.None) Notify(region, owed);
        return entries;
    }

    // How long retirement may wait for a running initialize hook, or null to skip the wait. Outside
    // disposal that is the close timeout. During disposal a hook already had the close timeout in the
    // wait for cancelled transitions, so once that timed out no hook is awaited again; otherwise the
    // waits share one deadline. The worst case of a disposal is therefore about twice the close timeout.
    private TimeSpan? InitializeWait()
    {
        lock (Gate)
        {
            if (!_closing) return _closeTimeout;
            if (_transitionWaitTimedOut) return null;
            _initializeStart ??= _time.GetTimestamp();
            var remaining = _closeTimeout - _time.GetElapsedTime(_initializeStart.Value);
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    // How long a close may wait for its clearing turn, or null to skip the wait. A region
    // closed by a retirement waits up to the close timeout. During disposal the clearing
    // turns of all regions share one deadline, and once a turn timed out none is awaited.
    private TimeSpan? TurnWait()
    {
        lock (Gate)
        {
            if (!_closing) return _closeTimeout;
            if (_closeTurnsTimedOut) return null;
            _closeTurnsStart ??= _time.GetTimestamp();
            var remaining = _closeTimeout - _time.GetElapsedTime(_closeTurnsStart.Value);
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    private static void RemoveClosedEntries(NavigationRegionCore region, List<NavigationEntryCore> entries)
    {
        foreach (var entry in entries)
            if (entry.Phase is NavigationEntryPhase.Active or NavigationEntryPhase.Retained)
                entry.Phase = NavigationEntryPhase.Removed;
        region.Stack = [];
    }

    private static void MarkClosedLocked(NavigationRegionCore region, List<NavigationTransition> cancel)
    {
        region.Closed = true;
        region.Version++;
        foreach (var transition in region.InFlight)
            if (transition.TrySetCancelReason(NavigationCancelReason.Closed)) cancel.Add(transition);
    }

    // ---- Helpers ---------------------------------------------------------

    private void Release(NavigationTransition transition)
    {
        bool changed;
        lock (Gate)
        {
            if (transition.Released) return;
            changed = ReleaseLocked(transition);
        }
        if (changed) Notify(transition.Region, NavigationRegionChanges.Transitioning);
    }

    private static bool ReleaseLocked(NavigationTransition transition)
    {
        if (transition.Released) return false;
        transition.Released = true;
        var region = transition.Region;
        region.InFlight.Remove(transition);
        return region.InFlight.Count == 0;
    }

    // Raises changes inside a model turn: inline in a turn, otherwise posted.
    private void Notify(NavigationRegionCore region, NavigationRegionChanges changes)
    {
        if (ModelContext.IsExecuting) region.RaiseChanges(changes);
        else
        {
            try { ModelContext.TryPost(() => region.RaiseChanges(changes)); }
            catch (ObjectDisposedException) { }
        }
    }

    // Cancellation callbacks can run user continuations, so they never run in a turn.
    private void Cancel(List<NavigationTransition> transitions)
    {
        if (transitions.Count == 0) return;
        if (ModelContext.IsExecuting)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Navigator.CancelAll(state.Transitions),
                (Navigator: this, Transitions: transitions), preferLocal: false);
            return;
        }
        CancelAll(transitions);
    }

    // A throwing cancellation callback is the user's: log it and keep cancelling, so
    // disposal and retirement continue.
    private void CancelAll(List<NavigationTransition> transitions)
    {
        foreach (var transition in transitions)
        {
            if (transition.Cancel() is not { } error) continue;
            NavigationLog.NavigationEntryCleanupFailed(Logger, error, transition.Region.ContentTypeName, transition.Region.Id,
                "None", "Cancel", BridgeTelemetry.ErrorType(error));
        }
    }

    private void StartOverrunTimer(NavigationTransition transition)
    {
        transition.StartOverrunTimer(_time, OverrunWarningDelay, () =>
            NavigationLog.NavigationSupersededTransitionOverrun(Logger, null, transition.Region.ContentTypeName,
                transition.Region.Id, OperationName(transition.Operation)));
    }

    private NavigationOutcome CancelledOutcome(NavigationTransition transition)
    {
        bool closing;
        lock (Gate) closing = _closing || transition.Region.Closed;
        return transition.CancelReason switch
        {
            NavigationCancelReason.Closed => NavigationOutcome.Reject(NavigationRejection.Closed),
            NavigationCancelReason.Superseded => NavigationOutcome.Superseded,
            _ when closing => NavigationOutcome.Reject(NavigationRejection.Closed),
            _ => NavigationOutcome.Reject(NavigationRejection.Cancelled),
        };
    }

    private void LogOutcome(NavigationRegionCore region, NavigationOperation operation, NavigationOutcome outcome)
    {
        if (outcome.Kind is not (NavigationOutcomeKind.Rejected or NavigationOutcomeKind.Superseded)) return;
        if (!Logger.IsEnabled(LogLevel.Debug)) return;
        if (outcome.Kind == NavigationOutcomeKind.Rejected)
            NavigationLog.NavigationTransitionRejected(Logger, null, region.ContentTypeName, region.Id, operation, outcome.Reason);
        else
            NavigationLog.NavigationTransitionSuperseded(Logger, null, region.ContentTypeName, region.Id, operation);
    }

    private NavigationEntryId NextEntryId() => new(++_nextEntryId);

    private bool IsRetiringOwnerLocked(object owner)
    {
        if (_ownedEntries.TryGetValue(owner, out var entry))
            return entry.Phase is NavigationEntryPhase.Removed or NavigationEntryPhase.Retiring or NavigationEntryPhase.Retired;
        lock (EverOwnedGate) return EverOwned.TryGetValue(owner, out _);
    }

    private static void Claim(object content)
    {
        lock (EverOwnedGate)
        {
            if (EverOwned.TryGetValue(content, out _))
                throw new InvalidOperationException(
                    $"This {content.GetType().Name} instance was already owned by a navigator. Own or create a new instance, or borrow container-owned content.");
            EverOwned.Add(content, OwnedMarker);
        }
    }

    private static T InvokeHookSync<T>(NavigationTransition transition, Func<T> hook)
    {
        var previous = HookTransition.Value;
        HookTransition.Value = transition;
        try { return hook(); }
        finally { HookTransition.Value = previous; }
    }

    private static ValueTask InvokeHook(NavigationTransition transition, Func<ValueTask> hook)
    {
        var previous = HookTransition.Value;
        HookTransition.Value = transition;
        try { return hook(); }
        catch (Exception error) { return ValueTask.FromException(error); }
        finally { HookTransition.Value = previous; }
    }

    private static ValueTask<T> InvokeHook<T>(NavigationTransition transition, Func<ValueTask<T>> hook)
    {
        var previous = HookTransition.Value;
        HookTransition.Value = transition;
        try { return hook(); }
        catch (Exception error) { return ValueTask.FromException<T>(error); }
        finally { HookTransition.Value = previous; }
    }

    // Interface checks only: every INavigationInitialize<TInput> is an
    // INavigationInputInitialize, which needs no reflection (NativeAOT).
    private static void RejectInitializable(object content, string parameter)
    {
        if (content is INavigationInitialize)
            throw new ArgumentException($"An initial target's content ({content.GetType().Name}) must not implement INavigationInitialize.", parameter);
        if (content is INavigationInputInitialize)
            throw new ArgumentException($"An initial target's content ({content.GetType().Name}) must not implement INavigationInitialize<TInput>.", parameter);
    }

    internal static string OperationName(NavigationOperation operation) => operation switch
    {
        NavigationOperation.Push => "Push",
        NavigationOperation.Back => "Back",
        NavigationOperation.BackTo => "BackTo",
        NavigationOperation.Replace => "Replace",
        NavigationOperation.Reset => "Reset",
        NavigationOperation.ClearHistory => "ClearHistory",
        _ => "Clear",
    };

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}

internal enum NavigationCancelReason
{
    None,
    Superseded,
    Closed,
}

// One admitted request. Mutable fields are guarded by the navigator's gate,
// except the cancellation source, which is cancelled outside it.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationTransition : IDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private ITimer? _overrun;
    private int _cancelReason;

    public NavigationTransition(NavigationRegionCore region, NavigationOperation operation, NavigationTargetCore? target,
        NavigationEntryCore? pending, NavigationEntryId? backTo, NavigationEntryId? expected,
        Task[] waitFor, CancellationToken callerToken)
    {
        Region = region;
        Operation = operation;
        Target = target;
        Pending = pending;
        BackTo = backTo;
        Expected = expected;
        CallerToken = callerToken;
        WaitFor = waitFor;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        Token = _cancellation.Token;
    }

    public NavigationRegionCore Region { get; }
    public NavigationOperation Operation { get; }
    public NavigationTargetCore? Target { get; }
    public NavigationEntryCore? Pending { get; }
    public NavigationEntryId? BackTo { get; }
    public NavigationEntryId? Expected { get; }
    public CancellationToken CallerToken { get; }
    public CancellationToken Token { get; }
    public Task[] WaitFor { get; }
    public NavigationPhase Phase { get; set; }
    public bool CommitStarted { get; set; }
    public bool Released { get; set; }

    // Set for a return from a result entry (CompleteAsync, or a cancelled PushForResult caller).
    public NavigationReturn? Return { get; init; }

    // Set in the commit turn when a returned value found its request already dismissed.
    public bool ResultDropped { get; set; }

    // The entry whose departure guard hook this transition is running, and the
    // signal that the hook has returned. Set and cleared under the navigator's gate.
    public NavigationEntryCore? GuardingEntry { get; set; }
    public TaskCompletionSource? GuardHook { get; set; }

    // Admission released: requests waiting on this one may proceed.
    public TaskCompletionSource Terminated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The whole transition, including post-commit cleanup, has finished.
    public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The caller-visible outcome.
    public TaskCompletionSource<NavigationOutcome> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NavigationCancelReason CancelReason => (NavigationCancelReason)Volatile.Read(ref _cancelReason);

    // Supersession applies only before Committing. Call under the gate.
    public bool TrySupersede() => !CommitStarted && TrySetCancelReason(NavigationCancelReason.Superseded);

    public bool TrySetCancelReason(NavigationCancelReason reason) =>
        !Released && Interlocked.CompareExchange(ref _cancelReason, (int)reason, (int)NavigationCancelReason.None) == 0;

    // Returns the exception a cancellation callback threw, if any.
    public Exception? Cancel()
    {
        try
        {
            _cancellation.Cancel();
            return null;
        }
        catch (ObjectDisposedException) { return null; }
        catch (Exception error) { return error; }
    }

    public void StartOverrunTimer(TimeProvider time, TimeSpan delay, Action warn)
    {
        lock (this)
        {
            if (_overrun is not null || Terminated.Task.IsCompleted) return;
            _overrun = time.CreateTimer(_ =>
            {
                if (!Terminated.Task.IsCompleted) warn();
            }, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (this)
        {
            _overrun?.Dispose();
            _overrun = null;
        }
        _cancellation.Dispose();
    }
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationPlan
{
    public List<NavigationRegionCore> Regions { get; } = [];
    public long[] Basis { get; set; } = [];
    public List<(NavigationEntryCore Entry, NavigationDepartureKind Kind)> Guards { get; } = [];
    public List<NavigationEntryCore> Removed { get; } = [];
    public List<(NavigationRegionCore Region, NavigationEntryCore[] Stack, List<NavigationEntryCore> Removed)> ChildResets { get; } = [];
    public List<NavigationRegionCore> Closing { get; } = [];
    public NavigationEntryCore[] NewStack { get; set; } = [];
    public NavigationEntryCore? Resume { get; set; }
    public NavigationRejection? Rejection { get; private set; }
    public bool Unchanged { get; set; }

    public NavigationPlan Reject(NavigationRejection rejection)
    {
        Rejection = rejection;
        return this;
    }

    // Call under the gate.
    public bool BasisMatches()
    {
        for (var index = 0; index < Regions.Count; index++)
            if (Regions[index].Version != Basis[index]) return false;
        return true;
    }
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationCommit
{
    public NavigationEntryCore? Current { get; set; }
    public List<NavigationEntryCore> Retire { get; } = [];
    public List<NavigationTransition> Cancel { get; } = [];
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed record NavigationCommitResult(NavigationOutcome Outcome, NavigationCommit? Commit);

internal enum NavigationOutcomeKind
{
    Committed,
    Rejected,
    Failed,
    Superseded,
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationOutcome
{
    public static readonly NavigationOutcome Superseded = new() { Kind = NavigationOutcomeKind.Superseded };

    public NavigationOutcomeKind Kind { get; private init; }
    public NavigationEntryCore? Current { get; private init; }
    public IReadOnlyList<NavigationEntryId> Retired { get; private init; } = [];
    public NavigationRejection Reason { get; private init; }
    public NavigationEntryId? By { get; private init; }
    public Exception? Error { get; private init; }
    public NavigationPhase Phase { get; private init; }

    public static NavigationOutcome Commit(NavigationEntryCore? current, IReadOnlyList<NavigationEntryId> retired) =>
        new() { Kind = NavigationOutcomeKind.Committed, Current = current, Retired = retired };

    public static NavigationOutcome Reject(NavigationRejection reason, NavigationEntryId? by = null) =>
        new() { Kind = NavigationOutcomeKind.Rejected, Reason = reason, By = by };

    public static NavigationOutcome Fail(Exception error, NavigationPhase phase) =>
        new() { Kind = NavigationOutcomeKind.Failed, Error = error, Phase = phase };
}

[Experimental(RunicNavigator.DiagnosticId)]
internal static class NavigationResults
{
    public static ValueTask<NavigationResult<T>> MapAsync<T>(Task<NavigationOutcome> outcome) where T : class =>
        outcome.IsCompletedSuccessfully ? new(Map<T>(outcome.Result)) : AwaitAsync<T>(outcome);

    public static Task<NavigationResult<T>> MapTask<T>(Task<NavigationOutcome> outcome) where T : class =>
        outcome.IsCompletedSuccessfully ? Task.FromResult(Map<T>(outcome.Result)) : AwaitTaskAsync<T>(outcome);

    private static async Task<NavigationResult<T>> AwaitTaskAsync<T>(Task<NavigationOutcome> outcome) where T : class =>
        Map<T>(await outcome.ConfigureAwait(false));

    private static async ValueTask<NavigationResult<T>> AwaitAsync<T>(Task<NavigationOutcome> outcome) where T : class =>
        Map<T>(await outcome.ConfigureAwait(false));

    public static NavigationResult<T> Map<T>(NavigationOutcome outcome) where T : class => outcome.Kind switch
    {
        NavigationOutcomeKind.Committed => new NavigationResult<T>.Committed(outcome.Current?.View<T>(), outcome.Retired),
        NavigationOutcomeKind.Rejected => new NavigationResult<T>.Rejected(outcome.Reason, outcome.By),
        NavigationOutcomeKind.Failed => new NavigationResult<T>.Failed(outcome.Error!, outcome.Phase),
        _ => new NavigationResult<T>.Superseded(),
    };
}
