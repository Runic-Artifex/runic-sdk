using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation;

/// <summary>Configures a region: what happens to it while the entry that owns it is retained, and how it admits requests.</summary>
/// <param name="WhileParentRetained">The policy applied when a push retains the owning entry.</param>
public sealed record NavigationRegionOptions(NavigationChildRetention WhileParentRetained = NavigationChildRetention.Keep)
{
    /// <summary>
    /// Gets how the region admits a request while an earlier one runs. The default, <see cref="NavigationAdmission.Supersede"/>,
    /// lets a later request supersede earlier ones that have not committed.
    /// </summary>
    public NavigationAdmission Admission { get; init; }
}

/// <summary>How a region admits a request while an earlier request of the region runs.</summary>
/// <remarks>This set is open: a minor release may add modes, so handle unknown values, for example with a default arm.</remarks>
public enum NavigationAdmission
{
    /// <summary>A later request supersedes the earlier requests of the region that have not started committing.</summary>
    Supersede,

    /// <summary>
    /// While the region is busy (<see cref="NavigationRegion.IsBusy"/>), a request is rejected at admission
    /// with <see cref="NavigationRejection.Busy"/>. A Back that can join a pending Back still joins it.
    /// </summary>
    RejectWhileBusy,
}

/// <summary>What a child region does while the entry that owns it is retained.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum NavigationChildRetention
{
    /// <summary>The child region keeps its entries.</summary>
    Keep,

    /// <summary>Every entry above the child region's root entry retires.</summary>
    ResetToRoot,

    /// <summary>Every entry of the child region retires and it becomes empty.</summary>
    Clear,
}

/// <summary>Options of one navigation request.</summary>
/// <param name="ExpectedCurrent">
/// The entry that must be current when the request is admitted and when it commits;
/// otherwise the request is rejected with <see cref="NavigationRejection.NotCurrent"/>.
/// </param>
public sealed record NavigationRequestOptions(NavigationEntryId? ExpectedCurrent = null)
{
    /// <summary>
    /// Gets the admission policy of this request, overriding the region's <see cref="NavigationRegionOptions.Admission"/>,
    /// or <see langword="null"/> to use the region's. Pass <see cref="NavigationAdmission.Supersede"/> for a request that
    /// must never be rejected as <see cref="NavigationRejection.Busy"/>, such as the clear of a window close.
    /// </summary>
    public NavigationAdmission? Admission { get; init; }

    /// <summary>
    /// Gets the entry on whose behalf the request is made, or <see langword="null"/>. When set, the request is rejected
    /// with <see cref="NavigationRejection.NotCurrent"/>, and <see cref="NavigationResult{TContent}.Rejected.By"/> set to
    /// this entry, unless the entry and every entry that owns its region, up to a region the navigator's content does not
    /// own, are current when the request is admitted and when it commits.
    /// </summary>
    /// <remarks>
    /// Use it for navigation that work of an entry starts late, such as an operation that completes and then opens a page
    /// in a parent or sibling region: when the entry was retained or retired in the meantime, the request does nothing.
    /// <see cref="ExpectedCurrent"/> checks only the current entry of the region the request is made to; an entry that
    /// navigates its own region uses <see cref="NavigationEntryContext.BackAsync"/> or
    /// <see cref="NavigationEntryContext.CompleteAsync{TResult}"/>, which already expect it to be current. An unknown
    /// entry, or one that finished retiring, is not current.
    /// </remarks>
    public NavigationEntryId? Requester { get; init; }
}

/// <summary>Identifies one navigation entry. Ids increase monotonically per navigator.</summary>
/// <param name="Value">The id's sequence number.</param>
public readonly record struct NavigationEntryId(long Value);

/// <summary>The lifecycle state of a navigation entry.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum NavigationEntryState
{
    /// <summary>The entry belongs to a transition that has not committed.</summary>
    Pending,

    /// <summary>The entry is its region's current entry.</summary>
    Active,

    /// <summary>The entry is kept in its region's history.</summary>
    Retained,

    /// <summary>The entry left its region or is being cleaned up. It never becomes active again.</summary>
    Retired,
}

/// <summary>Whether the navigator owns an entry's content.</summary>
/// <remarks>This set is open: a minor release may add ownership kinds, so handle unknown values, for example with a default arm.</remarks>
public enum NavigationOwnership
{
    /// <summary>The navigator forgets and disposes the content when the entry retires.</summary>
    Owned,

    /// <summary>The caller or a container owns the content; retiring only drops the reference.</summary>
    Borrowed,

    /// <summary>
    /// The entry holds the content together with the other entries of its navigator that hold it
    /// (<see cref="NavigationTarget.Share{T}"/>). The navigator forgets and disposes the content when the last of them retires.
    /// </summary>
    Shared,
}

/// <summary>The operation that caused a departure, resume or result.</summary>
/// <remarks>This set is open: a minor release may add operations, so handle unknown values, for example with a default arm.</remarks>
public enum NavigationOperation
{
    /// <summary>A new entry became current; the previous current entry was retained.</summary>
    Push,

    /// <summary>The current entry retired and the top retained entry resumed.</summary>
    Back,

    /// <summary>Every entry above a retained entry retired and that entry resumed.</summary>
    BackTo,

    /// <summary>The current entry retired and a new entry took its place.</summary>
    Replace,

    /// <summary>Every entry retired and a new entry became the root.</summary>
    Reset,

    /// <summary>Every retained entry retired; the current entry stayed.</summary>
    ClearHistory,

    /// <summary>Every entry retired; the region became empty.</summary>
    Clear,
}

/// <summary>Whether a departing entry is retained or retired.</summary>
/// <remarks>This set is closed: it gains values only in a major release.</remarks>
public enum NavigationDepartureKind
{
    /// <summary>
    /// The entry stays in history: a push covers it, or the borrowed content that owns its region departs, as when a tab
    /// switch leaves the tab's pages.
    /// </summary>
    Retain,

    /// <summary>The entry retires and never becomes active again.</summary>
    Retire,
}

/// <summary>
/// Describes a departure that a guard can veto. The navigator creates one for each guard invocation; it
/// belongs to that invocation and to the transition that runs it.
/// </summary>
public sealed class NavigationDeparture
{
    private readonly NavigationTransition _transition;
    private readonly NavigationEntryCore _entry;
    // Set under the navigator's gate when the guard's task has completed.
    private bool _closed;

    internal NavigationDeparture(NavigationEntryCore entry, NavigationDepartureKind kind, NavigationTransition transition,
        bool contentContinues = false)
    {
        _entry = entry;
        _transition = transition;
        Kind = kind;
        ContentContinues = contentContinues;
    }

    /// <summary>Gets the departing entry.</summary>
    public NavigationEntryId Entry => _entry.Id;

    /// <summary>Gets whether the entry is retained or retired.</summary>
    public NavigationDepartureKind Kind { get; }

    /// <summary>
    /// Gets whether the entry retires but its content continues: another entry that this transition doesn't retire holds
    /// the same content (<see cref="NavigationTarget.Share{T}"/>), as when a Replace hands the content over to the next
    /// entry. The content is then neither forgotten nor disposed; a guard that protects the content's state, such as
    /// <see cref="LeaveConfirmation"/>, treats the departure like <see cref="NavigationDepartureKind.Retain"/>.
    /// </summary>
    public bool ContentContinues { get; }

    /// <summary>Gets the requested operation.</summary>
    public NavigationOperation Operation => _transition.Operation;

    /// <summary>
    /// Gets the model context whose turns commit the navigator's state. Guards run outside model turns, so
    /// they read and change shared model state through <see cref="IRunicModelContext.InvokeAsync{T}(Func{T}, CancellationToken)"/>.
    /// </summary>
    public IRunicModelContext ModelContext => _transition.Region.Navigator.ModelContext;

    // The transition that runs this guard, and its settlement (LeaveConfirmation).
    internal NavigationTransitionId Transition => _transition.Settlement.Id;

    internal Task<NavigationDepartureSettlement> Settled => _transition.Settlement.Task;

    /// <summary>
    /// Runs <paramref name="action"/> if, and only if, this transition commits: in its commit turn, after the
    /// new stacks and entry states are applied and before the regions raise <c>PropertyChanged</c>. Actions run
    /// in registration order. An action that throws is logged (event 1072), the commit stands, and the
    /// remaining actions still run. When the transition is superseded, rejected, cancelled or fails, its
    /// actions are dropped; a guard that runs again registers again.
    /// </summary>
    /// <remarks>
    /// Register only while the guard runs, before its task completes. An action runs inside a model turn, so
    /// it must not pump or await navigation. A navigation request it starts is admitted after the commit.
    /// </remarks>
    /// <param name="action">The action to run in the commit turn.</param>
    /// <exception cref="InvalidOperationException">The guard's task has already completed.</exception>
    public void OnCommitted(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_transition.Region.Navigator.Gate)
        {
            if (_closed)
                throw new InvalidOperationException(
                    "OnCommitted can be called only while the departure guard runs, before the task it returned completes.");
            _transition.CommitActions.Add((_entry.ContentTypeName, action));
        }
    }

    // Called by the navigator once the guard's task has completed.
    internal void Close()
    {
        lock (_transition.Region.Navigator.Gate) _closed = true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"NavigationDeparture {{ Entry = {Entry.Value}, Kind = {Kind}, Operation = {Operation}, ContentContinues = {ContentContinues} }}";
}

// Identifies one admitted transition; unique per navigator.
internal readonly record struct NavigationTransitionId(long Value);

// How the transition of a departure settled.
internal enum NavigationDepartureOutcome
{
    Committed,
    Superseded,
    // Rejected, cancelled or failed.
    Ended,
}

// SupersededBy is set when a specific later transition superseded this one, and Superseder is that
// transition's settlement. Both are null when a basis change caused the supersession.
internal readonly record struct NavigationDepartureSettlement(NavigationDepartureOutcome Outcome, NavigationTransitionId? SupersededBy)
{
    public Task<NavigationDepartureSettlement>? Superseder { get; init; }
}

// The settlement of one transition: completed once, outside model turns, with asynchronous
// continuations. It never faults.
internal sealed class NavigationSettlementSource(NavigationTransitionId id)
{
    private readonly TaskCompletionSource<NavigationDepartureSettlement> _source =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NavigationTransitionId Id { get; } = id;
    public Task<NavigationDepartureSettlement> Task => _source.Task;
    public void Settle(NavigationDepartureSettlement settlement) => _source.TrySetResult(settlement);
}

/// <summary>
/// Describes an entry becoming current: a retained entry resuming (<see cref="INavigationResume.ResumeAsync"/>,
/// <see cref="INavigationAware.OnNavigatedTo"/>), or a new entry's first commit (<see cref="INavigationAware.OnNavigatedTo"/>).
/// </summary>
/// <param name="Entry">The entry that becomes current.</param>
/// <param name="Operation">
/// The operation that makes it current: <see cref="NavigationOperation.Back"/> or <see cref="NavigationOperation.BackTo"/>
/// for a resume; <see cref="NavigationOperation.Push"/>, <see cref="NavigationOperation.Replace"/> or
/// <see cref="NavigationOperation.Reset"/> for a new entry. An initial target of
/// <see cref="RunicNavigator.CreateRegion{TContent}"/> arrives with <see cref="NavigationOperation.Reset"/>.
/// </param>
public sealed record NavigationArrival(NavigationEntryId Entry, NavigationOperation Operation);

/// <summary>Describes the current entry stopping being current (<see cref="INavigationAware.OnNavigatedFrom"/>).</summary>
/// <param name="Entry">The entry that stops being current.</param>
/// <param name="Operation">
/// The operation that committed the change. <see cref="NavigationOperation.Clear"/> when the entry's region closed,
/// because the entry that owns the region retired or the navigator was disposed.
/// </param>
/// <param name="Kind">Whether the entry stays in its region's history or retires.</param>
public sealed record NavigationExit(NavigationEntryId Entry, NavigationOperation Operation, NavigationDepartureKind Kind);

/// <summary>The data of <see cref="NavigationEntryContext.StateChanged"/>.</summary>
public sealed class NavigationEntryStateChangedEventArgs : EventArgs
{
    /// <summary>Creates the data of one state change.</summary>
    /// <param name="previousState">The state before the change.</param>
    /// <param name="state">The state after the change.</param>
    /// <param name="operation">The operation that caused the change.</param>
    public NavigationEntryStateChangedEventArgs(NavigationEntryState previousState, NavigationEntryState state, NavigationOperation operation)
    {
        PreviousState = previousState;
        State = state;
        Operation = operation;
    }

    /// <summary>Gets the state before the change.</summary>
    public NavigationEntryState PreviousState { get; }

    /// <summary>Gets the state after the change.</summary>
    public NavigationEntryState State { get; }

    /// <summary>
    /// Gets the operation that caused the change: the committed transition's operation; for a pending entry whose
    /// transition did not commit, that transition's operation; <see cref="NavigationOperation.Reset"/> when an initial
    /// target becomes current; <see cref="NavigationOperation.Clear"/> when the entry's region closed.
    /// </summary>
    public NavigationOperation Operation { get; }
}

/// <summary>Why a navigation request was rejected.</summary>
/// <remarks>This set is open: a minor release may add reasons, so handle unknown values, for example with a default arm.</remarks>
public enum NavigationRejection
{
    /// <summary>A departure guard returned <see langword="false"/>.</summary>
    Guard,

    /// <summary>The caller's token was cancelled before the commit.</summary>
    Cancelled,

    /// <summary>The navigator or the region is closed.</summary>
    Closed,

    /// <summary>
    /// The request came from a hook of an in-flight transition of the same region, or of a region
    /// that holds it or is held by it (an ancestor or a descendant region). The rejection is
    /// conservative: such a request could wait on the transition that is running the hook.
    /// </summary>
    Reentrant,

    /// <summary>A back request found no retained entry.</summary>
    NoHistory,

    /// <summary>The expected current entry is not current.</summary>
    NotCurrent,

    /// <summary>The back-to target is not in the region.</summary>
    EntryNotFound,

    /// <summary>
    /// The region admits requests with <see cref="NavigationAdmission.RejectWhileBusy"/> and was busy: an earlier request
    /// had not completed.
    /// </summary>
    Busy,
}

/// <summary>The phase in which a transition failed.</summary>
/// <remarks>This set is open: a minor release may add phases, so handle unknown values, for example with a default arm.</remarks>
public enum NavigationPhase
{
    /// <summary>A departure guard threw.</summary>
    Guarding,

    /// <summary>A factory, the ownership check, binding, initialize or resume threw.</summary>
    Preparing,

    /// <summary>The commit turn could not run.</summary>
    Committing,
}

/// <summary>The outcome of a navigation request. Navigation outcomes are returned, never thrown.</summary>
/// <typeparam name="TContent">The region's content type.</typeparam>
/// <remarks>This set is closed: it gains cases only in a major release, so an exhaustive <c>switch</c> over the nested records is supported.</remarks>
public abstract record NavigationResult<TContent> where TContent : class
{
    private NavigationResult()
    {
    }

    /// <summary>Gets whether the transition committed, so that code after the request may continue.</summary>
    public bool IsCommitted => this is Committed;

    /// <summary>
    /// Rethrows the error of a <see cref="Failed"/> outcome, or the <see cref="Committed.ArrivalError"/> of a committed
    /// one whose arrival hook failed, with its original stack trace, and returns every other outcome unchanged.
    /// </summary>
    /// <remarks>
    /// A rejected or superseded transition is an expected outcome and does not throw. Combine it with
    /// <see cref="IsCommitted"/> to handle a request in one line:
    /// <c>if (!(await region.PushAsync(target)).ThrowIfFailed().IsCommitted) return;</c>
    /// </remarks>
    /// <returns>This outcome.</returns>
    public NavigationResult<TContent> ThrowIfFailed()
    {
        if (this is Failed failed) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failed.Error);
        if (this is Committed { ArrivalError: { } activation }) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(activation);
        return this;
    }

    /// <summary>The transition committed.</summary>
    /// <param name="Current">The region's current entry after the commit, or <see langword="null"/> when it is empty.</param>
    /// <param name="Retired">The entries whose retirement this transition performed.</param>
    /// <remarks>
    /// The request completes after the departed entries retired and after the arrival hooks
    /// (<see cref="INavigationArrived"/>) of the entries the commit made current ran.
    /// </remarks>
    public sealed record Committed(NavigationEntry<TContent>? Current, IReadOnlyList<NavigationEntryId> Retired) : NavigationResult<TContent>
    {
        /// <summary>
        /// Gets how the arrival hooks of the entries this commit made current ended, or
        /// <see cref="NavigationArrivalState.None"/> when none of them implements <see cref="INavigationArrived"/> or
        /// <see cref="INavigationArrived{TInput}"/>. The commit stands whatever it is.
        /// </summary>
        public NavigationArrivalState Arrival { get; init; }

        /// <summary>
        /// Gets the exception of the first arrival hook that failed when <see cref="Arrival"/> is
        /// <see cref="NavigationArrivalState.Failed"/>; otherwise <see langword="null"/>.
        /// </summary>
        public Exception? ArrivalError { get; init; }
    }

    /// <summary>The transition was rejected; history is unchanged.</summary>
    /// <param name="Reason">Why it was rejected.</param>
    /// <param name="By">
    /// The entry whose guard vetoed it, for <see cref="NavigationRejection.Guard"/>; <see langword="null"/> when a region or
    /// navigator guard (<see cref="INavigationRegionGuard"/>) vetoed it.
    /// </param>
    public sealed record Rejected(NavigationRejection Reason, NavigationEntryId? By) : NavigationResult<TContent>;

    /// <summary>The transition failed; history is unchanged.</summary>
    /// <param name="Error">The exception.</param>
    /// <param name="Phase">The phase that failed.</param>
    public sealed record Failed(Exception Error, NavigationPhase Phase) : NavigationResult<TContent>;

    /// <summary>A later request of the region, or of its parent, replaced this one before it committed.</summary>
    public sealed record Superseded : NavigationResult<TContent>;

    /// <summary>Gets a summary of this result that a Runic View can receive.</summary>
    public NavigationOutcome Outcome => this switch
    {
        Committed committed => new(NavigationOutcomeKind.Committed) { Arrival = committed.Arrival },
        Rejected rejected => new(NavigationOutcomeKind.Rejected, rejected.Reason),
        Superseded => new(NavigationOutcomeKind.Superseded),
        _ => new(NavigationOutcomeKind.Failed),
    };
}

/// <summary>How a <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> request ended.</summary>
/// <typeparam name="TResult">The result type.</typeparam>
/// <remarks>This set is closed: it gains cases only in a major release, so an exhaustive <c>switch</c> over the nested records is supported.</remarks>
public abstract record NavigationCompletion<TResult>
{
    private NavigationCompletion()
    {
    }

    /// <summary>
    /// The result entry called <see cref="NavigationEntryContext.CompleteAsync{TResult}"/>, and the
    /// transition that returned from it with <paramref name="Value"/> committed.
    /// </summary>
    /// <param name="Value">The result.</param>
    public sealed record Completed(TResult Value) : NavigationCompletion<TResult>;

    /// <summary>
    /// The request ended without a result: its push did not commit, the entry retired without
    /// completing, the caller's token was cancelled after the commit, or the navigator closed.
    /// </summary>
    public sealed record Dismissed : NavigationCompletion<TResult>;
}

/// <summary>A push that waits for a typed result from the pushed entry.</summary>
/// <typeparam name="TContent">The region's content type.</typeparam>
/// <typeparam name="TResult">The result type.</typeparam>
/// <remarks>
/// <see cref="Completion"/> ends <see cref="NavigationCompletion{TResult}.Completed"/> only through
/// <see cref="NavigationEntryContext.CompleteAsync{TResult}"/> on the result entry, and
/// <see cref="NavigationCompletion{TResult}.Dismissed"/> on every other path. Both tasks complete
/// asynchronously and are never completed inside a commit turn.
/// </remarks>
public sealed class NavigationResultRequest<TContent, TResult> where TContent : class
{
    private readonly Task<NavigationResult<TContent>> _transition;

    internal NavigationResultRequest(Task<NavigationResult<TContent>> transition, Task<NavigationCompletion<TResult>> completion)
    {
        _transition = transition;
        Completion = completion;
    }

    /// <summary>Gets the outcome of the push. Unlike most value tasks, it can be awaited more than once.</summary>
    public ValueTask<NavigationResult<TContent>> Transition => new(_transition);

    /// <summary>Gets the request's completion: the entry's result, or a dismissal.</summary>
    public Task<NavigationCompletion<TResult>> Completion { get; }
}

/// <summary>A navigation target created by <see cref="NavigationTarget"/>.</summary>
/// <typeparam name="TContent">The content type.</typeparam>
/// <remarks>Only targets from <see cref="NavigationTarget"/> are accepted; other implementations are rejected.</remarks>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A covariant marker for targets created by NavigationTarget.")]
public interface INavigationTarget<out TContent> where TContent : class
{
}

/// <summary>Initializes a new entry once, before it first becomes current.</summary>
public interface INavigationInitialize
{
    /// <summary>Initializes the entry's content. Runs outside model turns and at most once per entry.</summary>
    /// <param name="entry">The entry being created.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    ValueTask InitializeAsync(NavigationEntryContext entry, CancellationToken cancellationToken);
}

/// <summary>
/// The non-generic base of every <see cref="INavigationInitialize{TInput}"/>, so the navigator can recognize
/// content that initializes with input through an interface check. Implement
/// <see cref="INavigationInitialize{TInput}"/> instead.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A marker that keeps the initial-target check free of reflection for NativeAOT.")]
public interface INavigationInputInitialize
{
}

/// <summary>
/// Initializes a new entry created with typed input by <see cref="NavigationTarget.Create{T, TInput}(TInput)"/> or
/// <see cref="NavigationTarget.Create{T, TInput}(Func{IServiceProvider, T}, TInput)"/>.
/// </summary>
/// <typeparam name="TInput">The input type.</typeparam>
public interface INavigationInitialize<in TInput> : INavigationInputInitialize, INavigationInput<TInput>
{
    /// <summary>Initializes the entry's content. Runs outside model turns and at most once per entry.</summary>
    /// <param name="entry">The entry being created.</param>
    /// <param name="input">The target's input.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    ValueTask InitializeAsync(NavigationEntryContext entry, TInput input, CancellationToken cancellationToken);
}

/// <summary>
/// The member-less base of the hooks that receive a target's typed input, <see cref="INavigationInitialize{TInput}"/> and
/// <see cref="INavigationArrived{TInput}"/>, so the input targets accept content that implements either. Implement one of
/// those instead.
/// </summary>
/// <typeparam name="TInput">The input type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "The shared constraint of the input targets.")]
public interface INavigationInput<in TInput>
{
}

/// <summary>How the arrival hooks of a committed transition ended (<see cref="NavigationResult{TContent}.Committed.Arrival"/>).</summary>
/// <remarks>This set is open: a minor release may add states, so handle unknown values, for example with a default arm.</remarks>
public enum NavigationArrivalState
{
    /// <summary>No entry the commit made current has an arrival hook.</summary>
    None,

    /// <summary>Every arrival hook returned.</summary>
    Completed,

    /// <summary>An arrival hook threw; the commit stands.</summary>
    Failed,

    /// <summary>
    /// An arrival hook was cancelled, because its entry stopped being current or retired, or the navigator closed,
    /// or it was never started for that reason.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Runs after the entry became current and the change was presented, outside model turns: on every commit that makes
/// the entry its region's current entry, after <see cref="INavigationAware.OnNavigatedTo"/>. The request that made the entry
/// current awaits it before it returns <see cref="NavigationResult{TContent}.Committed"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use it for work that needs the entry presented first, such as loading the page's data while it shows its loading
/// state. It runs after the departed entries retired, and, with a model context that implements
/// <see cref="IRunicModelPresentationYield"/> (the WPF dispatcher context), after the new view was laid out and rendered.
/// It runs again each time a Back or BackTo resumes the entry, so it must be repeatable.
/// </para>
/// <para>
/// A hook that throws is logged (event 1076) and gives <see cref="NavigationArrivalState.Failed"/> with the exception in
/// <see cref="NavigationResult{TContent}.Committed.ArrivalError"/>; the entry stays current. Its token is cancelled when
/// the entry stops being current (a later commit retains or retires it), when it retires, and when the navigator closes;
/// not when the caller's token is cancelled. Retirement doesn't wait for the hook, so check the token after each await.
/// </para>
/// <para>
/// The hook may navigate: a request from it is admitted, also into its own region (to redirect with Replace or give up
/// with <see cref="NavigationEntryContext.BackAsync"/>) and into the regions its content owns, and a region that rejects
/// requests while busy admits them. It must not await <see cref="RunicNavigator.WhenIdleAsync"/> or its region's
/// <see cref="NavigationRegion.WhenIdleAsync"/>, which wait for it.
/// </para>
/// <para>
/// The content is already presented, so without a hook scheduler (for example <see cref="RunicModelContext"/>) the hook
/// changes presented state through <see cref="NavigationEntryContext.ModelContext"/>. With
/// <see cref="IRunicModelHookScheduler"/> it runs as its own operation on the model's thread and may set state directly.
/// An initial target's hook starts after <see cref="RunicNavigator.CreateRegion{TContent}"/> or
/// <see cref="NavigationRegion{TContent}.Start"/> returned; the region is busy until it ends.
/// </para>
/// </remarks>
public interface INavigationArrived
{
    /// <summary>Runs after the entry became current and was presented.</summary>
    /// <param name="entry">The entry that became current.</param>
    /// <param name="arrival">The entry and the operation that made it current, as <see cref="INavigationAware.OnNavigatedTo"/> received it.</param>
    /// <param name="cancellationToken">Cancelled when the entry stops being current, retires, or the navigator closes.</param>
    ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, CancellationToken cancellationToken);
}

/// <summary>
/// The arrival hook of an entry created with typed input (<see cref="NavigationTarget.Create{T, TInput}(TInput)"/> and the
/// region methods that wrap it), called with that input instead of <see cref="INavigationArrived"/>. It follows the rules
/// of <see cref="INavigationArrived"/>.
/// </summary>
/// <typeparam name="TInput">The input type.</typeparam>
public interface INavigationArrived<in TInput> : INavigationInput<TInput>
{
    /// <summary>Runs after the entry became current and was presented.</summary>
    /// <param name="entry">The entry that became current.</param>
    /// <param name="arrival">The entry and the operation that made it current.</param>
    /// <param name="input">The target's input.</param>
    /// <param name="cancellationToken">Cancelled when the entry stops being current, retires, or the navigator closes.</param>
    ValueTask OnArrivedAsync(NavigationEntryContext entry, NavigationArrival arrival, TInput input, CancellationToken cancellationToken);
}

/// <summary>Runs when a retained entry becomes current again.</summary>
public interface INavigationResume
{
    /// <summary>Prepares the content to become current. Runs outside model turns and again on a retry, so it must be repeatable.</summary>
    /// <param name="resume">The resuming entry and operation.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The parameter names the resumption it describes.")]
    ValueTask ResumeAsync(NavigationArrival resume, CancellationToken cancellationToken);
}

/// <summary>
/// Tells content when its entry becomes its region's current entry and when it stops being current.
/// </summary>
/// <remarks>
/// <para>
/// Both methods are synchronous notifications of a committed change. The navigator calls them inside the model turn
/// that commits it: after the new navigation state is applied and the departure guards' <c>OnCommitted</c> actions ran,
/// and before the regions raise <c>PropertyChanged</c>. In that turn it first notifies the entries that stopped being
/// current, then the entry that became current; for each entry it raises <see cref="NavigationEntryContext.StateChanged"/>
/// before calling this interface. Nothing is called for a transition that is rejected, superseded, cancelled or fails.
/// </para>
/// <para>
/// The calls alternate per entry: <see cref="OnNavigatedTo"/>, then <see cref="OnNavigatedFrom"/>, then
/// <see cref="OnNavigatedTo"/> again when a retained entry resumes. A retained entry that retires gets no second
/// <see cref="OnNavigatedFrom"/>; its <see cref="NavigationEntryContext.StateChanged"/> reports the retirement.
/// <see cref="INavigationInitialize"/> and <see cref="INavigationResume"/> remain the asynchronous hooks that prepare the
/// content before the commit.
/// </para>
/// <para>
/// A method that throws is logged (event 1075); the commit stands and the other notifications still run. A method
/// runs inside a model turn, so it must not block or await navigation; a navigation request it starts is admitted
/// after the turn.
/// </para>
/// </remarks>
public interface INavigationAware
{
    /// <summary>
    /// Called when the entry becomes current: when the push, replace or reset that creates it commits, and each time a
    /// Back or BackTo resumes it. For an initial target, <see cref="RunicNavigator.CreateRegion{TContent}"/> calls it
    /// with <see cref="NavigationOperation.Reset"/> before it returns, on the caller's thread.
    /// </summary>
    /// <param name="arrival">The entry and the operation that made it current.</param>
    void OnNavigatedTo(NavigationArrival arrival);

    /// <summary>
    /// Called when the entry stops being current: a push retains it, or a Back, BackTo, Replace, Reset or Clear
    /// retires it. When its region closes because the entry that owns the region retired, or because the navigator is
    /// disposed, it is called in the turn that clears that region, with <see cref="NavigationOperation.Clear"/>.
    /// </summary>
    /// <param name="exit">The entry, the operation, and whether the entry is retained or retires.</param>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The parameter names the exit it describes.")]
    void OnNavigatedFrom(NavigationExit exit);
}

/// <summary>Can veto a transition that retains or retires an entry.</summary>
public interface INavigationDepartureGuard
{
    /// <summary>
    /// Returns whether the entry may depart. Runs outside model turns. A guard may run again for the
    /// same entry: a parent transition that retires an entry's child regions asks the child's guards
    /// even when a transition of the child already asked them, so a guard must be repeatable.
    /// </summary>
    /// <param name="departure">The departing entry, whether it is retained or retired, and the operation.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    ValueTask<bool> CanDepartAsync(NavigationDeparture departure, CancellationToken cancellationToken);
}

/// <summary>Creates navigation targets and states who owns their content.</summary>
public static class NavigationTarget
{
    /// <summary>
    /// Presents content owned by the caller or a container. The navigator never disposes or
    /// forgets it, and leaves the regions it owns alone.
    /// </summary>
    public static INavigationTarget<T> Borrow<T>(T content) where T : class
    {
        ArgumentNullException.ThrowIfNull(content);
        return new InstanceNavigationTarget<T>(content, NavigationOwnership.Borrowed);
    }

    /// <summary>
    /// Hands content constructed with <see langword="new"/> over to the navigator, which forgets and
    /// disposes it when its entry retires. An instance can be owned only once; content resolved
    /// from a container must be borrowed instead.
    /// </summary>
    public static INavigationTarget<T> Own<T>(T content) where T : class
    {
        ArgumentNullException.ThrowIfNull(content);
        return new InstanceNavigationTarget<T>(content, NavigationOwnership.Owned);
    }

    /// <summary>
    /// Holds <paramref name="content"/> together with the other entries of the navigator that hold it, and forgets and
    /// disposes it when the last of them retires. Pass a new instance, or the content of a live entry of this navigator
    /// created with <see cref="Own{T}"/>, a <c>Create</c> target or <see cref="Share{T}"/>; for example, hand the current
    /// page over to the next entry with <c>ReplaceAsync(NavigationTarget.Share(current))</c>, which disposes nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request throws <see cref="InvalidOperationException"/> when the content was owned and fully released, is held by
    /// another navigator, or owns navigation regions; shared content can't own regions, and
    /// <see cref="RunicNavigator.CreateRegion{TContent}"/> with shared content as owner throws. A shared target can't be an
    /// initial target: start the region with <see cref="Own{T}"/> or a <c>Create</c> target, then share that content.
    /// </para>
    /// <para>
    /// Each entry is its own entry: <see cref="INavigationInitialize"/> runs for each, and content that keeps the
    /// <see cref="NavigationEntryContext"/> it was constructed with still has its first entry's context after that entry
    /// retired; take the context in <see cref="INavigationInitialize.InitializeAsync"/> or
    /// <see cref="INavigationArrived.OnArrivedAsync"/> instead. The entry scope of created content
    /// (<see cref="RunicNavigatorOptions.CreateEntryScopes"/>) lives until the content is disposed. Don't also
    /// <see cref="Borrow{T}"/> shared content: the borrowing entry can outlive its disposal.
    /// </para>
    /// </remarks>
    public static INavigationTarget<T> Share<T>(T content) where T : class
    {
        ArgumentNullException.ThrowIfNull(content);
        return new InstanceNavigationTarget<T>(content, NavigationOwnership.Shared);
    }

    /// <summary>
    /// Creates owned content when the transition prepares. The factory receives the entry's
    /// service provider (<see cref="NavigationEntry.Services"/>) to resolve constructor dependencies, which stay
    /// container-owned; it must construct the content itself with <see langword="new"/>. To pass the content its
    /// entry's <see cref="NavigationEntryContext"/>, use <see cref="Create{T}(Func{IServiceProvider, NavigationEntryContext, T})"/>.
    /// </summary>
    public static INavigationTarget<T> Create<T>(Func<IServiceProvider, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new FactoryNavigationTarget<T>((services, _) => factory(services));
    }

    /// <summary>
    /// Creates owned content like <see cref="Create{T}(Func{IServiceProvider, T})"/>; the factory also receives the
    /// new entry's <see cref="NavigationEntryContext"/> to pass to the content's constructor. The context belongs to
    /// the entry being prepared: its <see cref="NavigationEntryContext.State"/> is
    /// <see cref="NavigationEntryState.Pending"/> until the transition commits.
    /// </summary>
    public static INavigationTarget<T> Create<T>(Func<IServiceProvider, NavigationEntryContext, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new FactoryNavigationTarget<T>(factory);
    }

    /// <summary>
    /// Creates owned content like <see cref="Create{T}(Func{IServiceProvider, T})"/> and passes it <paramref name="input"/>:
    /// <see cref="INavigationInitialize{TInput}.InitializeAsync"/> runs instead of <see cref="INavigationInitialize.InitializeAsync"/>,
    /// and <see cref="INavigationArrived{TInput}.OnArrivedAsync"/> instead of <see cref="INavigationArrived.OnArrivedAsync"/>,
    /// for the hooks the content implements.
    /// </summary>
    public static INavigationTarget<T> Create<T, TInput>(Func<IServiceProvider, T> factory, TInput input)
        where T : class, INavigationInput<TInput>
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new InputNavigationTarget<T, TInput>((services, _) => factory(services), input);
    }

    /// <summary>
    /// Creates owned content like <see cref="Create{T}(Func{IServiceProvider, NavigationEntryContext, T})"/> and passes it
    /// <paramref name="input"/>, like <see cref="Create{T, TInput}(Func{IServiceProvider, T}, TInput)"/>.
    /// </summary>
    public static INavigationTarget<T> Create<T, TInput>(Func<IServiceProvider, NavigationEntryContext, T> factory, TInput input)
        where T : class, INavigationInput<TInput>
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new InputNavigationTarget<T, TInput>(factory, input);
    }

    /// <summary>
    /// Constructs owned content of type <typeparamref name="T"/> when the transition prepares, with
    /// <see cref="ActivatorUtilities.CreateInstance{T}(IServiceProvider, object[])"/>. Constructor parameters
    /// are resolved from the entry's service provider (<see cref="NavigationEntry.Services"/>) and stay
    /// container-owned; a <see cref="NavigationEntryContext"/> parameter receives the new entry's context.
    /// <typeparamref name="T"/> itself is never resolved from the container. Mark a
    /// constructor with <see cref="ActivatorUtilitiesConstructorAttribute"/> to choose it.
    /// </summary>
    /// <typeparam name="T">The content type to construct.</typeparam>
    public static INavigationTarget<T> Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>()
        where T : class =>
        new ActivatedNavigationTarget<T>();

    /// <summary>
    /// Constructs owned content like <see cref="Create{T}()"/> and passes it <paramref name="input"/>, like
    /// <see cref="Create{T, TInput}(Func{IServiceProvider, T}, TInput)"/>: through
    /// <see cref="INavigationInitialize{TInput}.InitializeAsync"/> and <see cref="INavigationArrived{TInput}.OnArrivedAsync"/>,
    /// for the hooks the content implements.
    /// </summary>
    /// <typeparam name="T">The content type to construct.</typeparam>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="input">The input passed to the content's typed initialize and arrival hooks.</param>
    public static INavigationTarget<T> Create<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T, TInput>(TInput input)
        where T : class, INavigationInput<TInput> =>
        new ActivatedInputNavigationTarget<T, TInput>(input);
}

// The only INavigationTarget<T> implementations. The region works with this
// non-generic core; the public interface stays a covariant marker.
internal abstract class NavigationTargetCore
{
    public abstract NavigationOwnership Ownership { get; }

    // The supplied instance, or null when the target creates its content.
    public abstract object? Instance { get; }

    public virtual bool HasInput => false;

    // The target's content type, for messages.
    public abstract Type ContentType { get; }

    // Creates the content for the entry whose context is given; services is the entry's provider.
    public virtual object Create(IServiceProvider services, NavigationEntryContext entry) =>
        Instance ?? throw new InvalidOperationException("The navigation target has no content.");

    // Whether the content takes the input in its initialize hook (INavigationInitialize<TInput>).
    public virtual bool InitializesWithInput(object content) => false;

    public virtual ValueTask InitializeWithInputAsync(object content, NavigationEntryContext entry, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    // The content's typed arrival hook (INavigationArrived<TInput>) bound to the input, or null.
    public virtual NavigationArrivalHook? ArrivalHookWithInput(object content) => null;

    // Throws when the content of an input target takes the input in no hook: it implements only the INavigationInput<TInput> marker.
    public virtual void ValidateInput(object content)
    {
    }
}

// An entry's arrival hook, bound to its content (and input).
internal delegate ValueTask NavigationArrivalHook(NavigationEntryContext entry, NavigationArrival arrival, CancellationToken cancellationToken);

// The input targets: the content's typed hooks receive the input.
internal abstract class InputNavigationTargetCore<TInput>(TInput input) : NavigationTargetCore
{
    public override NavigationOwnership Ownership => NavigationOwnership.Owned;
    public override object? Instance => null;
    public override bool HasInput => true;

    public override bool InitializesWithInput(object content) => content is INavigationInitialize<TInput>;

    public override ValueTask InitializeWithInputAsync(object content, NavigationEntryContext entry, CancellationToken cancellationToken) =>
        ((INavigationInitialize<TInput>)content).InitializeAsync(entry, input, cancellationToken);

    public override NavigationArrivalHook? ArrivalHookWithInput(object content) => content is INavigationArrived<TInput> activated
        ? (entry, arrival, cancellationToken) => activated.OnArrivedAsync(entry, arrival, input, cancellationToken)
        : null;

    public override void ValidateInput(object content)
    {
        if (content is not (INavigationInitialize<TInput> or INavigationArrived<TInput>))
            throw new ArgumentException(
                $"{content.GetType().Name} takes the target's input in no hook: implement INavigationInitialize<{typeof(TInput).Name}> or INavigationArrived<{typeof(TInput).Name}>.");
    }
}

internal sealed class InstanceNavigationTarget<T>(T content, NavigationOwnership ownership)
    : NavigationTargetCore, INavigationTarget<T> where T : class
{
    public override NavigationOwnership Ownership => ownership;
    public override object? Instance => content;
    public override Type ContentType => typeof(T);
}

internal sealed class FactoryNavigationTarget<T>(Func<IServiceProvider, NavigationEntryContext, T> factory)
    : NavigationTargetCore, INavigationTarget<T> where T : class
{
    public override NavigationOwnership Ownership => NavigationOwnership.Owned;
    public override object? Instance => null;
    public override Type ContentType => typeof(T);
    public override object Create(IServiceProvider services, NavigationEntryContext entry) =>
        factory(services, entry) ?? throw new InvalidOperationException($"The navigation factory for {typeof(T).Name} returned null.");
}

internal sealed class InputNavigationTarget<T, TInput>(Func<IServiceProvider, NavigationEntryContext, T> factory, TInput input)
    : InputNavigationTargetCore<TInput>(input), INavigationTarget<T> where T : class, INavigationInput<TInput>
{
    public override Type ContentType => typeof(T);
    public override object Create(IServiceProvider services, NavigationEntryContext entry) =>
        factory(services, entry) ?? throw new InvalidOperationException($"The navigation factory for {typeof(T).Name} returned null.");
}

// Constructs T with ActivatorUtilities from the entry's provider (W240-001 §5, D5). The
// annotation keeps T's public constructors under trimming and NativeAOT.
internal sealed class ActivatedNavigationTarget<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>
    : NavigationTargetCore, INavigationTarget<T> where T : class
{
    public override NavigationOwnership Ownership => NavigationOwnership.Owned;
    public override object? Instance => null;
    public override Type ContentType => typeof(T);
    public override object Create(IServiceProvider services, NavigationEntryContext entry) =>
        NavigationConstructionServices.Activate<T>(services, entry);
}

internal sealed class ActivatedInputNavigationTarget<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T, TInput>(TInput input)
    : InputNavigationTargetCore<TInput>(input), INavigationTarget<T> where T : class, INavigationInput<TInput>
{
    public override Type ContentType => typeof(T);
    public override object Create(IServiceProvider services, NavigationEntryContext entry) =>
        NavigationConstructionServices.Activate<T>(services, entry);
}
