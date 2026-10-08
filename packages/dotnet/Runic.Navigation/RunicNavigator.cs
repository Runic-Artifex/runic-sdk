using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Runic.Navigation;

/// <summary>Configures a <see cref="RunicNavigator"/>.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class RunicNavigatorOptions
{
    /// <summary>The window's model context. Commits run in its turns; owned content is bound to it.</summary>
    public required IRunicModelContext ModelContext { get; init; }

    /// <summary>
    /// The service provider that constructs created content: <see cref="NavigationTarget.Create{T}()"/> resolves
    /// constructor parameters from it, and <see cref="NavigationTarget.Create{T}(Func{IServiceProvider, T})"/>
    /// factories receive it. With <see cref="CreateEntryScopes"/>, each created entry uses its own scope of it instead.
    /// </summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>
    /// Creates one <see cref="IServiceScope"/> from <see cref="Services"/> for each entry built by a
    /// <see cref="NavigationTarget"/> <c>Create</c> target, including an initial target. The entry's content is
    /// constructed from the scope, <see cref="INavigationEntry.Services"/> returns it, and the scope is disposed
    /// after the content when the entry retires. <c>Borrow</c> and <c>Own</c> entries never get one. Requires
    /// <see cref="Services"/>.
    /// </summary>
    /// <remarks>
    /// Turn it on only when <see cref="Services"/> is the root provider, for example for an application-wide
    /// navigator registered as a singleton: then constructor dependencies that are scoped, or disposable
    /// transients, live and die with their entry. Leave it off for a navigator resolved from a scope, such as the
    /// one <see cref="RunicNavigationServiceCollectionExtensions.AddRunicNavigation"/> registers. Creating a scope from
    /// a scoped provider makes a sibling scope of the root, so the entry's scoped dependencies would silently differ
    /// from the navigator's. The navigator can't tell a root provider from a scoped one.
    /// </remarks>
    public bool CreateEntryScopes { get; init; }

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
/// <para>
/// Each region admits one transition at a time: a later request supersedes earlier ones that have not
/// started committing. Guards, initialize and resume run outside model turns; a commit re-checks the
/// region and applies the new state in one turn. Disposing the navigator cancels in-flight transitions
/// and retires every entry; owned content is disposed outside model turns.
/// </para>
/// <para>
/// When the model context implements <see cref="IRunicModelHookScheduler"/>, factories, guards, initialize and resume
/// hooks and the disposal of owned content each run as a separate operation on the model's thread, never inline in the
/// caller's frame or in a commit turn. Otherwise they run outside turns, on the caller's thread until its first await or
/// on the thread pool. The navigator's own work between hooks always continues on the thread pool.
/// </para>
/// </remarks>
[Experimental(DiagnosticId)]
public sealed class RunicNavigator : IAsyncDisposable, IDisposable
{
    /// <summary>The diagnostic ID of the experimental navigation API.</summary>
    public const string DiagnosticId = "RUNICNAV001";

    /// <summary>The category of the navigator's log entries (events 1060-1079).</summary>
    public const string LogCategory = "Runic.Navigation";

    internal static readonly TimeSpan OverrunWarningDelay = TimeSpan.FromSeconds(5);

    // Every instance any navigator has ever owned, live or retired.
    private static readonly ConditionalWeakTable<object, object> EverOwned = new();
    private static readonly object EverOwnedGate = new();
    private static readonly object OwnedMarker = new();

    // The initial-target creations in progress on this thread (CreateRegion's cycle guard).
    [ThreadStatic] private static List<(Type Owner, Type Target)>? s_initialCreations;

    private readonly TimeProvider _time;
    private readonly TimeSpan _closeTimeout;
    private readonly bool _createEntryScopes;
    private long _nextTransitionId;
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
    // Set when the model context reported itself closed: through IRunicModelContextLifetime.Closed, or with an
    // ObjectDisposedException before a turn or hook started.
    private bool _contextClosed;
    // The registration on IRunicModelContextLifetime.Closed, released when disposal finishes.
    private readonly CancellationTokenRegistration _contextClosedRegistration;
    private TaskCompletionSource? _disposal;
    // Completed when disposal starts, so scheduled owned disposal that is still queued becomes bounded.
    private readonly TaskCompletionSource _disposalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The attached presentations: the sinks that forget owned content when it retires.
    private readonly List<PresentationAttachment> _presentations = [];

    /// <summary>Creates a navigator for one window.</summary>
    public RunicNavigator(RunicNavigatorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ModelContext = options.ModelContext ?? throw new ArgumentException("A model context is required.", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CloseTimeout, TimeSpan.Zero, nameof(options));
        Services = options.Services;
        if (options.CreateEntryScopes && options.Services is null)
            throw new ArgumentException("CreateEntryScopes requires Services, the provider the entry scopes are created from.", nameof(options));
        _createEntryScopes = options.CreateEntryScopes;
        _time = options.TimeProvider ?? TimeProvider.System;
        _closeTimeout = options.CloseTimeout;
        Logger = options.LoggerFactory?.CreateLogger(LogCategory) ?? TraceFallbackLogger.Instance;
        // Last: a context that is already closed runs the callback here, and closing needs the fields above.
        if (ModelContext is IRunicModelContextLifetime lifetime)
            _contextClosedRegistration = lifetime.Closed.UnsafeRegister(static state => ((RunicNavigator)state!).OnModelContextClosing(), this);
    }

    /// <summary>Gets the model context whose turns commit this navigator's state. Owned content is bound to it.</summary>
    public IRunicModelContext ModelContext { get; }

    /// <summary>Gets the service provider passed to target factories, or <see langword="null"/> when none was configured.</summary>
    public IServiceProvider? Services { get; }

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
    /// Gets whether <see cref="DisposeAsync"/> has started or the model context closed (see <see cref="IRunicModelContextLifetime"/>). A closed navigator rejects requests as
    /// <see cref="NavigationRejection.Closed"/>, throws <see cref="ObjectDisposedException"/> from <see cref="CreateRegion{TContent}"/>
    /// and ignores <see cref="AttachPresentation"/>.
    /// </summary>
    public bool IsClosed
    {
        get { lock (Gate) return _closing; }
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
        if (target is { Instance: null }) ThrowIfInitialCreationCycle(owner.GetType(), target.ContentType);
        lock (Gate) ObjectDisposedException.ThrowIf(_closing, this);

        object? content = target?.Instance;
        IServiceScope? scope = null;
        if (target is not null && content is null)
        {
            scope = CreateEntryScope();
            var creations = s_initialCreations ??= [];
            creations.Add((owner.GetType(), target.ContentType));
            try { content = target.Create(scope?.ServiceProvider ?? Services ?? EmptyServiceProvider.Instance); }
            catch
            {
                DisposeUnclaimed(null, null, scope);
                throw;
            }
            finally { creations.RemoveAt(creations.Count - 1); }
            try { RejectInitializable(content, nameof(initial)); }
            catch
            {
                DisposeUnclaimed(content, null, scope);
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
            DisposeUnclaimed(created && (claimed || !owned) ? content : null, lease, scope);
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
                    if (scope is not null) entry.PublishScope(scope);
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
            DisposeUnclaimed(created ? content : null, lease, scope);
            throw;
        }
        if (retireAtOnce is not null) _ = RetireAsync(retireAtOnce, null);
        return new NavigationRegion<TContent>(region);
    }

    // Best-effort disposal of an instance the factory created and no region took, then of its
    // entry scope, then release of its lease (the order of retirement). User disposal code never
    // runs under the gate or inside a model turn: inside a turn, for asynchronous-only disposal
    // and for scopes, it runs on the thread pool; with a hook scheduler each disposal runs as its
    // own scheduled operation (RunCleanupAsync, A9). That work is tracked like a retirement, so
    // WhenIdleAsync and DisposeAsync wait for it.
    private void DisposeUnclaimed(object? content, IRunicModelContextLease? lease, IServiceScope? scope = null)
    {
        if (content is null && scope is null)
        {
            ReleaseUnclaimedLease(lease);
            return;
        }
        if (ModelContext is not IRunicModelHookScheduler && !ModelContext.IsExecuting && scope is null && content is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { }
            ReleaseUnclaimedLease(lease);
            return;
        }
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate) _retirements.Add(done.Task);
        _ = DisposeUnclaimedAsync(content, lease, scope, done);
    }

    private async Task DisposeUnclaimedAsync(object? content, IRunicModelContextLease? lease, IServiceScope? scope, TaskCompletionSource done)
    {
        try
        {
            await NavigationAwait.Hop();
            if (content is not null)
            {
                try { await this.AfterUserCode(RunCleanupAsync(() => DisposeContent(content))); }
                catch { }
            }
            if (scope is not null)
            {
                try { await this.AfterUserCode(RunCleanupAsync(() => DisposeScopeAsync(scope))); }
                catch { }
            }
            ReleaseUnclaimedLease(lease);
        }
        finally
        {
            lock (Gate) _retirements.Remove(done.Task);
            done.TrySetResult();
        }
    }

    // Creates the scope of an entry built by a Create target, or null when entry scopes are off.
    private IServiceScope? CreateEntryScope() => _createEntryScopes ? Services!.GetRequiredService<IServiceScopeFactory>().CreateScope() : null;

    private static ValueTask DisposeScopeAsync(IServiceScope? scope)
    {
        if (scope is IAsyncDisposable asyncScope) return asyncScope.DisposeAsync();
        scope?.Dispose();
        return ValueTask.CompletedTask;
    }

    // Creating an initial target of a type for an owner type while the same pair is already being
    // created on this thread is a dependency cycle: the target's constructor resolved the service
    // that creates the region. Container resolution would recurse until the stack overflows
    // (W240-001 §7). The key ignores the navigator, so a cycle through scoped navigators is caught
    // too, and includes the target type, so one holder type reused at two levels is not a cycle.
    private static void ThrowIfInitialCreationCycle(Type ownerType, Type target)
    {
        if (s_initialCreations is not { Count: > 0 } creations) return;
        foreach (var (creatingOwner, creatingTarget) in creations)
        {
            if (creatingOwner != ownerType || creatingTarget != target) continue;
            throw new InvalidOperationException(
                $"Creating {target.Name}, the initial target of a region owned by {ownerType.Name}, created another region owned by a {ownerType.Name} with the same initial target. "
                + $"This is a dependency cycle: {target.Name}'s constructor depends on the service that creates the region. "
                + $"Create the region without an initial target and reset it after construction, for example with ResetAsync<{target.Name}>().");
        }
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
    /// not skip the others. A presentation attached after the navigator started closing (<see cref="IsClosed"/>)
    /// is ignored, and the returned <see cref="IDisposable"/> does nothing.
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

    // The number of attached presentations; for tests.
    internal int PresentationCount
    {
        get { lock (Gate) return _presentations.Count; }
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
            await this.AfterUserCode(Task.WhenAll(pending).WaitAsync(cancellationToken));
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
    /// <para>
    /// With a model context that implements <see cref="IRunicModelHookScheduler"/>, owned content is disposed as scheduled
    /// operations on the model's thread. Those that haven't started by the clearing turns' deadline run on the thread pool
    /// instead, so disposal still completes when the model's thread is blocked. Never block the model's thread on this task
    /// (for example with <c>GetAwaiter().GetResult()</c> in a WPF <c>OnExit</c>): content would then be disposed off that
    /// thread after the timeout. Call <see cref="Dispose"/> there, which doesn't wait.
    /// </para>
    /// </remarks>
    public ValueTask DisposeAsync() => new(StartDisposal());

    /// <summary>
    /// Starts <see cref="DisposeAsync"/> and returns without waiting for it, so a synchronous container disposal on the
    /// model's thread can't deadlock against a commit that needs that thread. Prefer <see cref="DisposeAsync"/>: after
    /// this method returns, owned content may still be disposing in the background.
    /// </summary>
    /// <remarks>It is idempotent and shares one disposal with <see cref="DisposeAsync"/>. Failures are logged (1073).</remarks>
    public void Dispose() => _ = StartDisposal();

    private Task StartDisposal()
    {
        var disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var existing = Interlocked.CompareExchange(ref _disposal, disposal, null);
        if (existing is not null) return existing.Task;
        // New requests are refused at once, and the wait for running transitions is bounded from
        // here; the rest of the disposal runs on the thread pool.
        lock (Gate) _closing = true;
        _disposalStarted.TrySetResult();
        _ = DisposeCoreAsync(disposal, new CancellationTokenSource(_closeTimeout, _time));
        return disposal.Task;
    }

    private async Task DisposeCoreAsync(TaskCompletionSource disposal, CancellationTokenSource transitionWait)
    {
        try
        {
            // Never inline in the caller: it may be on the model's thread.
            await NavigationAwait.Hop();
            BeginClosing();
            Task[] running;
            lock (Gate) running = [.. _running.Select(transition => transition.Finished.Task), .. _retirements];
            if (running.Length > 0)
            {
                try { await this.AfterUserCode(Task.WhenAll(running).WaitAsync(transitionWait.Token)); }
                catch (OperationCanceledException) when (transitionWait.IsCancellationRequested) { lock (Gate) _transitionWaitTimedOut = true; }
            }

            NavigationRegionCore[] regions;
            lock (Gate) regions = [.. _regions];
            foreach (var region in regions)
                foreach (var entry in await this.AfterUserCode(CloseRegionAsync(region)))
                    await this.AfterUserCode(RetireAsync(entry, null));
            NavigationEntryCore[] remaining;
            lock (Gate) remaining = [.. _tracked];
            foreach (var entry in remaining.OrderBy(entry => entry.Id.Value))
                await this.AfterUserCode(RetireAsync(entry, null));
            lock (Gate) _presentations.Clear();
        }
        catch (Exception error)
        {
            NavigationLog.NavigatorDisposeFailed(Logger, error, NavigationTelemetry.ErrorType(error));
        }
        finally
        {
            transitionWait.Dispose();
            _contextClosedRegistration.Unregister();
            disposal.TrySetResult();
        }
    }

    // Refuses new requests, dismisses open PushForResult requests (so guards awaiting a result
    // unblock), then cancels in-flight transitions as closed. Idempotent.
    private void BeginClosing()
    {
        List<NavigationTransition> cancel = [];
        NavigationResultRequestCore[] requests;
        lock (Gate)
        {
            _closing = true;
            foreach (var region in _regions)
                foreach (var transition in region.InFlight)
                    if (transition.TrySetCancelReason(NavigationCancelReason.Closed)) cancel.Add(transition);
            requests = [.. _resultRequests];
        }
        foreach (var request in requests) FinishResult(request, NavigationResultDismissal.Closed);
        Cancel(cancel);
    }

    // The model context threw ObjectDisposedException before a turn or hook started: no commit
    // can run any more, so the navigator starts closing (amendment A6). DisposeAsync then clears
    // regions outside turns at once instead of waiting for the close timeout.
    private void OnModelContextClosed()
    {
        lock (Gate)
        {
            if (_contextClosed) return;
            _contextClosed = true;
        }
        BeginClosing();
    }

    // IRunicModelContextLifetime.Closed fired, on the thread that closed the context (often the UI thread while it
    // shuts down). Requests are refused from here on, synchronously, so none is admitted to wait behind a hook that
    // ignores its cancellation; dismissing result requests and cancelling transitions runs user callbacks, so that
    // part runs on the pool.
    private void OnModelContextClosing()
    {
        lock (Gate)
        {
            if (_contextClosed) return;
            _contextClosed = true;
            _closing = true;
        }
        ThreadPool.UnsafeQueueUserWorkItem(static self => self.BeginClosing(), this, preferLocal: false);
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
        NavigationTransition? joined = null;
        List<NavigationTransition> superseded = [];
        var transitioningChanged = false;
        lock (Gate)
        {
            var related = RelatedRunningLocked(region);
            if (_closing || region.Closed) rejected = NavigationOutcome.Reject(NavigationRejection.Closed);
            else if (cancellationToken.IsCancellationRequested)
                // Rejected before admission, so a dead request never supersedes its predecessors.
                rejected = NavigationOutcome.Reject(NavigationRejection.Cancelled);
            else if (NavigationHookMarker.Active is { } hook && !hook.Released
                && (ReferenceEquals(hook.Region, region) || related.Contains(hook)))
                // A hook never waits on a transition that waits on it. This also rejects requests to
                // ancestor and descendant regions of the hook's region (deviation 15): conservative.
                rejected = NavigationOutcome.Reject(NavigationRejection.Reentrant);
            else if (expected is { } expectedId && region.CurrentEntry?.Id != expectedId)
                rejected = NavigationOutcome.Reject(NavigationRejection.NotCurrent);
            else if (operation == NavigationOperation.Back && region.Stack.Length < (@return is null ? 2 : 1))
                rejected = NavigationOutcome.Reject(NavigationRejection.NoHistory);
            else if (operation == NavigationOperation.Back && @return is null && !cancellationToken.CanBeCanceled
                && JoinableBackLocked(region) is { } pendingBack)
                // A second Back to the same destination (a double click, or Back while its guard asks)
                // joins the pending Back instead of superseding it, so the guard asks once.
                joined = pendingBack;
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

                var settlement = new NavigationSettlementSource(new NavigationTransitionId(++_nextTransitionId));
                List<Task> waitFor = [];
                foreach (var earlier in region.InFlight)
                {
                    if (earlier.TrySupersede(settlement)) superseded.Add(earlier);
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
                        if (earlier.TrySupersede(settlement)) superseded.Add(earlier);
                        waitFor.Add(earlier.Terminated.Task);
                    }
                }
                // Related transitions of ancestor and descendant regions are not waited
                // on here: their guard sets are only known when they start guarding.
                // Each guard hook checks for overlap under the gate (EnterGuardHookAsync).
                transition = new NavigationTransition(region, operation, target, pending, backTo, expected,
                    [.. waitFor], settlement, cancellationToken) { Return = @return, AdmittedPlan = plan };
                if (pending is not null) pending.Transition = transition;
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
        if (joined is not null)
        {
            if (Logger.IsEnabled(LogLevel.Debug)) NavigationLog.NavigationBackJoined(Logger, null, region.ContentTypeName, region.Id);
            return joined.Result.Task;
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

    // The region's latest admitted transition when it is a plain Back that is still live and nothing its plan
    // covers has changed since its admission: neither the region nor the child regions the departing entry
    // owns. A Back admitted now would then make the same plan. A return from a result entry (CompleteAsync,
    // DismissAsync) never is joined: its outcome carries a result. A Back with a cancellable token never joins,
    // and never is joined, so cancelling one caller's Back never cancels another's. A superseded, cancelled or
    // closed Back is never joined. (A released transition has left InFlight already.) Call under the gate.
    private static NavigationTransition? JoinableBackLocked(NavigationRegionCore region)
    {
        if (region.InFlight.Count == 0) return null;
        var latest = region.InFlight[^1];
        return latest is { Operation: NavigationOperation.Back, Return: null, CancelReason: NavigationCancelReason.None,
                AdmittedPlan: { Rejection: null } plan }
            && !latest.CallerToken.CanBeCanceled && plan.BasisMatches()
            ? latest
            : null;
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
                (outcome, commit) = await this.AfterUserCode(RunPhasesAsync(transition));
            }
            catch (Exception error)
            {
                // Phases return outcomes; this only guards against a defect.
                outcome = NavigationOutcome.Fail(error, transition.Phase);
            }

            // One terminal path: release admission (unless the commit turn
            // already did), then let waiting requests proceed.
            // Settle first, so a request admitted once admission is released never sees a
            // settlement still open for a transition that already ended (a vetoed standing yes).
            Settle(transition, outcome);
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
                            "None", "Cancel", NavigationTelemetry.ErrorType(error));
                    }
                }
                List<NavigationEntryId> retired = [];
                foreach (var entry in commit.Retire)
                {
                    try { await this.AfterUserCode(RetireAsync(entry, retired)); }
                    catch (Exception error)
                    {
                        NavigationLog.NavigationEntryCleanupFailed(Logger, error, entry.Region.ContentTypeName, entry.Region.Id,
                            entry.ContentTypeName, "Retire", NavigationTelemetry.ErrorType(error));
                    }
                }
                outcome = NavigationOutcome.Commit(commit.Current, retired);
            }
            else if (transition.Pending is { } pending)
            {
                if (pending.ResultRequest is { } request) FinishResult(request, NavigationResultDismissal.NotCommitted);
                await this.AfterUserCode(RetireAsync(pending, null));
            }
            LogOutcome(transition.Region, transition.Operation, outcome);
        }
        finally
        {
            transition.Dispose();
            lock (Gate) _running.Remove(transition);
            transition.Finished.TrySetResult();
            // The settlement never stays open, even after a defect (a no-op once Settle ran).
            transition.Settlement.Settle(new(NavigationDepartureOutcome.Ended, null));
            // Asynchronous continuations, set after the commit turn returned.
            transition.Result.TrySetResult(outcome);
        }
    }

    // Completes the transition's settlement once its outcome is known, before admission is released
    // and before retirement. A supersession names its superseder only when a later request superseded
    // this one; a basis change leaves it unknown (W240-001 §6.1).
    private void Settle(NavigationTransition transition, NavigationOutcome outcome)
    {
        NavigationSettlementSource? by;
        lock (Gate) by = transition.CancelReason == NavigationCancelReason.Superseded ? transition.SupersededBy : null;
        transition.Settlement.Settle(outcome.Kind switch
        {
            NavigationOutcomeKind.Committed => new(NavigationDepartureOutcome.Committed, null),
            NavigationOutcomeKind.Superseded => new(NavigationDepartureOutcome.Superseded, by?.Id) { Superseder = by?.Task },
            _ => new(NavigationDepartureOutcome.Ended, null),
        });
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
            try { await this.AfterUserCode(Task.WhenAll(transition.WaitFor).WaitAsync(token)); }
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
            switch (await this.AfterUserCode(EnterGuardHookAsync(transition, entry, plan, token)))
            {
                case GuardSlot.Cancelled: return (CancelledOutcome(transition), null);
                case GuardSlot.Stale: return (NavigationOutcome.Superseded, null);
            }
            // One departure per guard invocation; OnCommitted closes when the guard's task completes.
            var departure = new NavigationDeparture(entry, kind, transition);
            try
            {
                allowed = await this.AfterUserCode(StartHook(transition, () => guard.CanDepartAsync(departure, token), token));
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
                    OperationName(transition.Operation), entry.ContentTypeName, NavigationTelemetry.ErrorType(error));
                return (NavigationOutcome.Fail(error, NavigationPhase.Guarding), null);
            }
            finally
            {
                departure.Close();
                ExitGuardHook(transition);
            }
            if (!allowed) return (NavigationOutcome.Reject(NavigationRejection.Guard, entry.Id), null);
        }

        // Preparing.
        transition.Phase = NavigationPhase.Preparing;
        if (token.IsCancellationRequested) return (CancelledOutcome(transition), null);
        if (IsClosing(region)) return (NavigationOutcome.Reject(NavigationRejection.Closed), null);
        if (!BasisMatches(plan)) return (NavigationOutcome.Superseded, null);
        var failure = await this.AfterUserCode(PrepareAsync(transition, plan));
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
        // Set when the turn starts, so a closed context (ObjectDisposedException before the turn ran)
        // is told apart from a turn that threw it.
        var started = new StrongBox<bool>();
        try
        {
            result = await this.AfterUserCode(ModelContext.InvokeAsync(() =>
            {
                Volatile.Write(ref started.Value, true);
                return CommitTurn(transition, plan);
            }));
        }
        catch (Exception error)
        {
            if (error is ObjectDisposedException && !Volatile.Read(ref started.Value)) OnModelContextClosed();
            bool closing;
            lock (Gate) closing = _closing;
            if (closing) return (NavigationOutcome.Reject(NavigationRejection.Closed), null);
            NavigationLog.NavigationCommitFailed(Logger, error, region.ContentTypeName, region.Id,
                OperationName(transition.Operation), NavigationTelemetry.ErrorType(error));
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
                    // The entry scope is created just before the factory, and belongs to this path until
                    // it is published together with the content.
                    var scope = CreateEntryScope();
                    object content;
                    try
                    {
                        var services = scope?.ServiceProvider ?? Services ?? EmptyServiceProvider.Instance;
                        content = await this.AfterUserCode(StartHook(transition, () => ValueTask.FromResult(target.Create(services)), token));
                        Claim(content);
                    }
                    catch
                    {
                        await this.AfterUserCode(DisposeEntryScopeAsync(entry, scope));
                        throw;
                    }
                    bool retiring;
                    lock (Gate)
                    {
                        // A navigator that timed out waiting may already have
                        // retired this pending entry without content.
                        retiring = entry.Retiring is not null;
                        if (!retiring)
                        {
                            entry.Content = content;
                            if (scope is not null) entry.PublishScope(scope);
                            _ownedEntries[content] = entry;
                        }
                    }
                    if (retiring)
                    {
                        try { await this.AfterUserCode(RunCleanupAsync(() => DisposeContent(content))); }
                        catch (Exception error) { LogCleanup(entry, "Dispose", error); }
                        await this.AfterUserCode(DisposeEntryScopeAsync(entry, scope));
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
                        await this.AfterUserCode(bound.DisposeAsync());
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
                        await this.AfterUserCode(StartHook(transition, () => target.InitializeWithInputAsync(content_, entry.Context, token), token));
                    else if (content_ is INavigationInitialize initialize)
                        await this.AfterUserCode(StartHook(transition, () => initialize.InitializeAsync(entry.Context, token), token));
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
                    OperationName(transition.Operation), entry.ContentTypeName, NavigationTelemetry.ErrorType(error));
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
                await this.AfterUserCode(StartHook(transition, () => resume.ResumeAsync(request, token), token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return CancelledOutcome(transition);
            }
            catch (Exception error)
            {
                if (IsClosing(region)) return NavigationOutcome.Reject(NavigationRejection.Closed);
                NavigationLog.NavigationPreparationFailed(Logger, error, region.ContentTypeName, region.Id,
                    OperationName(transition.Operation), resumed.ContentTypeName, NavigationTelemetry.ErrorType(error));
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
        (NavigationEntryCore Entry, Action Action)[] committed = [];
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
                committed = [.. transition.CommitActions];
            }
            // Admission is released in the commit turn, so IsTransitioning
            // changes together with the committed state.
            ReleaseLocked(transition);
        }

        // OnCommitted actions run in this turn, outside the gate, after the new state is applied and
        // before the notifications. One that throws is logged; the commit stands (D-13).
        foreach (var (entry, action) in committed)
        {
            try { action(); }
            catch (Exception error)
            {
                NavigationLog.NavigationDepartureActionFailed(Logger, error, region.ContentTypeName, region.Id,
                    OperationName(transition.Operation), entry.ContentTypeName, NavigationTelemetry.ErrorType(error));
            }
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
            try { await this.AfterUserCode(blocker.WaitAsync(token)); }
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
            using (NavigationHookMarker.Enter(null))
                _ = Start(entry.Region, NavigationOperation.Back, null, null, entry.Id, CancellationToken.None,
                    new NavigationReturn(null), runDetached: true);
        });
    }

    // NavigationEntryContext.DismissAsync (W240-001 §6.2). The entry's state decides, under the gate:
    // a current entry dismisses its open request (the caller-cancellation path) and goes back, which
    // may leave the region empty; a pending entry dismisses its request and cancels its own push
    // without awaiting it (the caller may be that push's initialize hook); a retained entry only
    // dismisses its request; a retiring or retired entry is left alone.
    internal Task<NavigationOutcome> Dismiss(NavigationEntryCore entry, CancellationToken cancellationToken)
    {
        NavigationEntryPhase phase;
        NavigationResultRequestCore? request;
        NavigationTransition? pushing = null;
        lock (Gate)
        {
            phase = entry.Phase;
            request = entry.ResultRequest;
            if (phase == NavigationEntryPhase.Pending && entry.Transition is { } transition
                && transition.TrySetCancelReason(NavigationCancelReason.Dismissed))
                pushing = transition;
        }
        switch (phase)
        {
            case NavigationEntryPhase.Active:
                Task<NavigationOutcome>? back = null;
                var dismissed = request is not null && FinishResult(request, NavigationResultDismissal.Dismissed, onlyActive: true,
                    beforeEnd: () => back = Start(entry.Region, NavigationOperation.Back, null, null, entry.Id, cancellationToken,
                        new NavigationReturn(null)));
                return dismissed
                    ? back!
                    : Start(entry.Region, NavigationOperation.Back, null, null, entry.Id, cancellationToken, new NavigationReturn(null));
            case NavigationEntryPhase.Pending:
                if (request is not null) FinishResult(request, NavigationResultDismissal.Dismissed);
                if (pushing is not null)
                {
                    Cancel([pushing]);
                    return Task.FromResult(NavigationOutcome.Reject(NavigationRejection.Cancelled));
                }
                // The push already ended another way (superseded, closed): report that, without
                // awaiting a push that may be awaiting this caller's hook.
                if (entry.Transition is not { } ended) return Task.FromResult(NavigationOutcome.Reject(NavigationRejection.Cancelled));
                return ended.Result.Task.IsCompleted ? ended.Result.Task : Task.FromResult(CancelledOutcome(ended));
            case NavigationEntryPhase.Retained:
                if (request is not null) FinishResult(request, NavigationResultDismissal.Dismissed, onlyActive: true);
                return Task.FromResult(NavigationOutcome.Reject(NavigationRejection.NotCurrent));
            default:
                return Task.FromResult(NavigationOutcome.Reject(NavigationRejection.NotCurrent));
        }
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
            // Owned disposal never runs inside a model turn: retirement continues on the thread
            // pool, and user cleanup runs there or as its own scheduled operation.
            await NavigationAwait.Hop();

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
                        try { await this.AfterUserCode(initializing.WaitAsync(limit, _time)); }
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
                    foreach (var descendant in await this.AfterUserCode(CloseRegionAsync(child)))
                    {
                        try { await this.AfterUserCode(RetireAsync(descendant, retired)); }
                        catch (Exception error) { LogCleanup(entry, "Children", error); }
                    }
                }

                // 3. Detach every presentation of the content.
                ForgetPresentations(entry);

                // 4. Dispose the content outside turns: on the thread pool, or as its own
                // scheduled operation on the model's thread while the context accepts them (A9).
                try { await this.AfterUserCode(RunCleanupAsync(() => DisposeContent(content))); }
                catch (Exception error) { LogCleanup(entry, "Dispose", error); }
            }

            // 4b. Dispose the entry's service scope, after its content (W240-001 §5.1). A pending
            // entry whose factory failed has none: preparation disposed it.
            IServiceScope? scope;
            lock (Gate)
            {
                scope = entry.Scope;
                entry.Scope = null;
            }
            await this.AfterUserCode(DisposeEntryScopeAsync(entry, scope));

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
                try { await this.AfterUserCode(lease.DisposeAsync()); }
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

    // Disposes an entry scope outside turns, like content (RunCleanupAsync, A9); a failure is logged
    // like a content disposal failure.
    private async ValueTask DisposeEntryScopeAsync(NavigationEntryCore entry, IServiceScope? scope)
    {
        if (scope is null) return;
        try { await this.AfterUserCode(RunCleanupAsync(() => DisposeScopeAsync(scope))); }
        catch (Exception error) { LogCleanup(entry, "DisposeScope", error); }
    }

    private void Step(NavigationEntryCore entry, string step, Action<NavigationEntryCore> action)
    {
        try { action(entry); }
        catch (Exception error) { LogCleanup(entry, step, error); }
    }

    private void LogCleanup(NavigationEntryCore entry, string step, Exception error) =>
        NavigationLog.NavigationEntryCleanupFailed(Logger, error, entry.Region.ContentTypeName, entry.Region.Id,
            entry.ContentTypeName, step, NavigationTelemetry.ErrorType(error));

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
            Observe(turn);
            try { await this.AfterUserCode(turn.WaitAsync(wait.Value, _time)); }
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
                OnModelContextClosed();
                fallback = true;
            }
            catch (Exception error)
            {
                NavigationLog.NavigationEntryCleanupFailed(Logger, error, region.ContentTypeName, region.Id,
                    "None", "Close", NavigationTelemetry.ErrorType(error));
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
        // Based on disposal, not _closing: a closed model context starts closing long before disposal,
        // and must not start the disposal's shared deadline early.
        if (Volatile.Read(ref _disposal) is null) return _closeTimeout;
        lock (Gate)
        {
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
            // A closed context runs no turn: clear outside a turn at once.
            if (_contextClosed) return null;
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
                "None", "Cancel", NavigationTelemetry.ErrorType(error));
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

    // Starts one hook of a transition: a factory, guard, initialize or resume. Without a hook
    // scheduler it runs here, outside turns, as before. With one it runs as its own scheduled
    // operation on the model's thread (W240-001 §4.4); the operation sets the hook marker itself,
    // so reentrancy checks see it on that thread. An ObjectDisposedException before the operation
    // started means that the context closed: the navigator starts closing, so the caller's
    // failure path settles the transition as Rejected(Closed) without logging a failure.
    private ValueTask<T> StartHook<T>(NavigationTransition transition, Func<ValueTask<T>> hook, CancellationToken token)
    {
        if (ModelContext is IRunicModelHookScheduler scheduler)
            return new(ScheduleHookAsync(scheduler, new ScheduledHook<T>(transition, hook), token));
        using var marker = NavigationHookMarker.Enter(transition);
        try { return hook(); }
        catch (Exception error) { return ValueTask.FromException<T>(error); }
    }

    private ValueTask<bool> StartHook(NavigationTransition transition, Func<ValueTask> hook, CancellationToken token) =>
        StartHook(transition, () => Completion(hook()), token);

    private async Task<T> ScheduleHookAsync<T>(IRunicModelHookScheduler scheduler, ScheduledHook<T> hook, CancellationToken token)
    {
        try { return await this.AfterUserCode(Schedule(scheduler, hook.Run, token)); }
        // The claim decides whether the hook ran, not the scheduler's word: an ObjectDisposedException
        // or OperationCanceledException that the hook threw itself is that hook's failure, and an
        // operation the scheduler reported as not run can no longer start late.
        catch (ObjectDisposedException) when (hook.TryAbandon())
        {
            OnModelContextClosed();
            throw;
        }
        // Abandoning on OperationCanceledException only matters for a scheduler that breaks its
        // contract by cancelling an operation it already accepted: a conforming scheduler either runs
        // the operation or reports it as not run before it starts.
        catch (OperationCanceledException) when (hook.TryAbandon()) { throw; }
    }

    // Runs user cleanup (owned Dispose/DisposeAsync) outside model turns. With a hook scheduler it is
    // its own scheduled operation on the model's thread; once the context is closed it falls back to
    // the thread pool, so cleanup still runs after shutdown (amendment A9). Without a scheduler it runs
    // here; callers are already on the thread pool. Failures propagate to the caller, which logs them.
    //
    // While the navigator disposes, an operation that hasn't started by the clearing turns' deadline
    // (CleanupWait) is abandoned and the cleanup runs on the pool instead: a host that blocks the model's
    // thread on DisposeAsync would otherwise never let it start. The operation's claim makes sure the
    // cleanup runs exactly once, whichever way. An operation that started is awaited without bound.
    private async Task RunCleanupAsync(Func<ValueTask> cleanup)
    {
        if (ModelContext is IRunicModelHookScheduler scheduler)
        {
            var operation = new ScheduledHook<bool>(null, () => Completion(cleanup()));
            var scheduled = Schedule(scheduler, operation.Run, CancellationToken.None);
            try
            {
                // Outside disposal, wait however long the model's thread is busy.
                await this.AfterUserCode(Task.WhenAny(scheduled, _disposalStarted.Task));
                if (!scheduled.IsCompleted && Volatile.Read(ref _disposal) is { Task.IsCompleted: false })
                    await this.AfterUserCode(scheduled.WaitAsync(CleanupWait(), _time));
                await this.AfterUserCode(scheduled);
                return;
            }
            catch (Exception error) when (error is TimeoutException or ObjectDisposedException or OperationCanceledException
                && operation.TryAbandon())
            {
                // It never ran, and now never will.
                Observe(scheduled);
            }
            catch (TimeoutException)
            {
                // It started in time; the cleanup itself is not bounded. This rethrows a TimeoutException
                // that the cleanup threw.
                await this.AfterUserCode(scheduled);
                return;
            }
            await NavigationAwait.Hop();
        }
        await this.AfterUserCode(cleanup());
    }

    // How long disposal waits for a scheduled cleanup to finish once it is past the start of disposal.
    // It shares the clearing turns' deadline, which a blocked model thread also exhausts, so the
    // worst case of a disposal stays about twice the close timeout. A cleanup that waits here during
    // the transition wait, before the clearing turns begin, starts that shared deadline early; that
    // only shortens the clearing turns' wait and never extends the worst case.
    private TimeSpan CleanupWait()
    {
        lock (Gate)
        {
            if (_closeTurnsTimedOut) return TimeSpan.Zero;
            _closeTurnsStart ??= _time.GetTimestamp();
            var remaining = _closeTimeout - _time.GetElapsedTime(_closeTurnsStart.Value);
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
    }

    // Observes a task that is no longer awaited, so a late fault isn't reported as unobserved.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static ValueTask DisposeContent(object content)
    {
        try
        {
            if (content is IAsyncDisposable asyncDisposable) return asyncDisposable.DisposeAsync();
            (content as IDisposable)?.Dispose();
            return ValueTask.CompletedTask;
        }
        catch (Exception error) { return ValueTask.FromException(error); }
    }

    private static Task<T> Schedule<T>(IRunicModelHookScheduler scheduler, Func<Task<T>> operation, CancellationToken token)
    {
        try
        {
            return scheduler.RunHookAsync(operation, token)
                ?? Task.FromException<T>(new InvalidOperationException("IRunicModelHookScheduler.RunHookAsync returned null."));
        }
        catch (Exception error) { return Task.FromException<T>(error); }
    }

    // Adapts a hook without a result, preserving its exception, without an await. A cancelled hook
    // becomes Faulted(TaskCanceledException) rather than Canceled, on purpose: callers only look at the
    // exception (an OperationCanceledException either way), and every outcome stays an exception.
    private static ValueTask<bool> Completion(ValueTask task)
    {
        if (task.IsCompletedSuccessfully) return new(true);
        return new(task.AsTask().ContinueWith(static completed =>
        {
            completed.GetAwaiter().GetResult();
            return true;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
    }

    // One scheduled operation. Its claim is taken once: by Run (started) or by the navigator (abandoned,
    // when the scheduler reported that it didn't run, or disposal stopped waiting for it to start), so the
    // operation runs at most once and the navigator knows whether it ran. Run sets the hook marker of its
    // transition around the synchronous part of the hook and turns a synchronous throw into a faulted task.
    private sealed class ScheduledHook<T>(NavigationTransition? transition, Func<ValueTask<T>> hook)
    {
        private const int NotStarted = 0, Started = 1, Abandoned = 2;
        private int _state;

        // True when the operation hasn't started; it then never will.
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, NotStarted) != Started;

        public Task<T> Run()
        {
            // Abandoned: the navigator no longer waits for this result.
            if (Interlocked.CompareExchange(ref _state, Started, NotStarted) != NotStarted) return Task.FromResult(default(T)!);
            using var marker = NavigationHookMarker.Enter(transition);
            try { return hook().AsTask(); }
            catch (Exception error) { return Task.FromException<T>(error); }
        }
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
}

internal enum NavigationCancelReason
{
    None,
    Superseded,
    Closed,
    // The pending entry dismissed itself (NavigationEntryContext.DismissAsync); ends as Rejected(Cancelled).
    Dismissed,
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
        Task[] waitFor, NavigationSettlementSource settlement, CancellationToken callerToken)
    {
        Region = region;
        Operation = operation;
        Target = target;
        Pending = pending;
        BackTo = backTo;
        Expected = expected;
        CallerToken = callerToken;
        WaitFor = waitFor;
        Settlement = settlement;
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

    // The provisional plan at admission. Its basis (the versions of the region and the child regions it
    // would close) tells whether a later Back would make the same plan, and so may join this one.
    public NavigationPlan? AdmittedPlan { get; init; }

    // Set in the commit turn when a returned value found its request already dismissed.
    public bool ResultDropped { get; set; }

    // The transition's identity and settlement (NavigationDeparture.Settled).
    public NavigationSettlementSource Settlement { get; }

    // The later transition that superseded this one, if a specific one did. Set under the gate.
    public NavigationSettlementSource? SupersededBy { get; private set; }

    // NavigationDeparture.OnCommitted actions of this transition's guards, in registration order.
    // Guarded by the navigator's gate; run only by the commit turn.
    public List<(NavigationEntryCore Entry, Action Action)> CommitActions { get; } = [];

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
    public bool TrySupersede(NavigationSettlementSource by)
    {
        if (CommitStarted || !TrySetCancelReason(NavigationCancelReason.Superseded)) return false;
        SupersededBy = by;
        return true;
    }

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
