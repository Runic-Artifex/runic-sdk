using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

/// <summary>Configures what happens to a child region while the entry that owns it is retained.</summary>
/// <param name="WhileParentRetained">The policy applied when a push retains the owning entry.</param>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed record NavigationRegionOptions(NavigationChildRetention WhileParentRetained = NavigationChildRetention.Keep);

/// <summary>What a child region does while the entry that owns it is retained.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
public sealed record NavigationRequestOptions(NavigationEntryId? ExpectedCurrent = null);

/// <summary>Identifies one navigation entry. Ids increase monotonically per navigator.</summary>
/// <param name="Value">The id's sequence number.</param>
[Experimental(RunicNavigator.DiagnosticId)]
public readonly record struct NavigationEntryId(long Value);

/// <summary>The lifecycle state of a navigation entry.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
public enum NavigationOwnership
{
    /// <summary>The navigator forgets and disposes the content when the entry retires.</summary>
    Owned,

    /// <summary>The caller or a container owns the content; retiring only drops the reference.</summary>
    Borrowed,
}

/// <summary>The operation that caused a departure, resume or result.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
public enum NavigationDepartureKind
{
    /// <summary>The entry stays in history (a push).</summary>
    Retain,

    /// <summary>The entry retires and never becomes active again.</summary>
    Retire,
}

/// <summary>Describes a departure that a guard can veto.</summary>
/// <param name="Entry">The departing entry.</param>
/// <param name="Kind">Whether the entry is retained or retired.</param>
/// <param name="Operation">The requested operation.</param>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed record NavigationDeparture(NavigationEntryId Entry, NavigationDepartureKind Kind, NavigationOperation Operation);

/// <summary>Describes a retained entry becoming current again.</summary>
/// <param name="Entry">The resuming entry.</param>
/// <param name="Operation">The operation that resumes it.</param>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed record NavigationResume(NavigationEntryId Entry, NavigationOperation Operation);

/// <summary>Why a navigation request was rejected.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
}

/// <summary>The phase in which a transition failed.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
public abstract record NavigationResult<TContent> where TContent : class
{
    private NavigationResult()
    {
    }

    /// <summary>The transition committed.</summary>
    /// <param name="Current">The region's current entry after the commit, or <see langword="null"/> when it is empty.</param>
    /// <param name="Retired">The entries whose retirement this transition performed.</param>
    public sealed record Committed(NavigationEntry<TContent>? Current, IReadOnlyList<NavigationEntryId> Retired) : NavigationResult<TContent>;

    /// <summary>The transition was rejected; history is unchanged.</summary>
    /// <param name="Reason">Why it was rejected.</param>
    /// <param name="By">The entry whose guard vetoed it, for <see cref="NavigationRejection.Guard"/>.</param>
    public sealed record Rejected(NavigationRejection Reason, NavigationEntryId? By) : NavigationResult<TContent>;

    /// <summary>The transition failed; history is unchanged.</summary>
    /// <param name="Error">The exception.</param>
    /// <param name="Phase">The phase that failed.</param>
    public sealed record Failed(Exception Error, NavigationPhase Phase) : NavigationResult<TContent>;

    /// <summary>A later request of the region, or of its parent, replaced this one before it committed.</summary>
    public sealed record Superseded : NavigationResult<TContent>;
}

/// <summary>How a <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> request ended.</summary>
/// <typeparam name="TResult">The result type.</typeparam>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A covariant marker for targets created by NavigationTarget.")]
public interface INavigationTarget<out TContent> where TContent : class
{
}

/// <summary>Initializes a new entry once, before it first becomes current.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
[EditorBrowsable(EditorBrowsableState.Never)]
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A marker that keeps the initial-target check free of reflection for NativeAOT.")]
public interface INavigationInputInitialize
{
}

/// <summary>Initializes a new entry created by <see cref="NavigationTarget.Create{T, TInput}"/> with typed input.</summary>
/// <typeparam name="TInput">The input type.</typeparam>
[Experimental(RunicNavigator.DiagnosticId)]
public interface INavigationInitialize<in TInput> : INavigationInputInitialize
{
    /// <summary>Initializes the entry's content. Runs outside model turns and at most once per entry.</summary>
    /// <param name="entry">The entry being created.</param>
    /// <param name="input">The target's input.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    ValueTask InitializeAsync(NavigationEntryContext entry, TInput input, CancellationToken cancellationToken);
}

/// <summary>Runs when a retained entry becomes current again.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public interface INavigationResume
{
    /// <summary>Prepares the content to become current. Runs outside model turns and again on a retry, so it must be repeatable.</summary>
    /// <param name="resume">The resuming entry and operation.</param>
    /// <param name="cancellationToken">Cancelled when the transition is superseded, cancelled or closed.</param>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "The parameter names the resumption it describes.")]
    ValueTask ResumeAsync(NavigationResume resume, CancellationToken cancellationToken);
}

/// <summary>Can veto a transition that retains or retires an entry.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
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
[Experimental(RunicNavigator.DiagnosticId)]
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
    /// Creates owned content when the transition prepares. The factory receives the window's
    /// service provider to resolve constructor dependencies, which stay container-owned; it
    /// must construct the content itself with <see langword="new"/>.
    /// </summary>
    public static INavigationTarget<T> Create<T>(Func<IServiceProvider, T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new FactoryNavigationTarget<T>(factory);
    }

    /// <summary>
    /// Creates owned content like <see cref="Create{T}(Func{IServiceProvider, T})"/>, then calls only
    /// <see cref="INavigationInitialize{TInput}.InitializeAsync"/> with <paramref name="input"/>.
    /// </summary>
    public static INavigationTarget<T> Create<T, TInput>(Func<IServiceProvider, T> factory, TInput input)
        where T : class, INavigationInitialize<TInput>
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new InputNavigationTarget<T, TInput>(factory, input);
    }
}

// The only INavigationTarget<T> implementations. The region works with this
// non-generic core; the public interface stays a covariant marker.
[Experimental(RunicNavigator.DiagnosticId)]
internal abstract class NavigationTargetCore
{
    public abstract NavigationOwnership Ownership { get; }

    // The supplied instance, or null when the target creates its content.
    public abstract object? Instance { get; }

    public virtual bool HasInput => false;

    public virtual object Create(IServiceProvider services) =>
        Instance ?? throw new InvalidOperationException("The navigation target has no content.");

    public virtual ValueTask InitializeWithInputAsync(object content, NavigationEntryContext entry, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class InstanceNavigationTarget<T>(T content, NavigationOwnership ownership)
    : NavigationTargetCore, INavigationTarget<T> where T : class
{
    public override NavigationOwnership Ownership => ownership;
    public override object? Instance => content;
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class FactoryNavigationTarget<T>(Func<IServiceProvider, T> factory)
    : NavigationTargetCore, INavigationTarget<T> where T : class
{
    public override NavigationOwnership Ownership => NavigationOwnership.Owned;
    public override object? Instance => null;
    public override object Create(IServiceProvider services) =>
        factory(services) ?? throw new InvalidOperationException($"The navigation factory for {typeof(T).Name} returned null.");
}

[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class InputNavigationTarget<T, TInput>(Func<IServiceProvider, T> factory, TInput input)
    : NavigationTargetCore, INavigationTarget<T> where T : class, INavigationInitialize<TInput>
{
    public override NavigationOwnership Ownership => NavigationOwnership.Owned;
    public override object? Instance => null;
    public override bool HasInput => true;
    public override object Create(IServiceProvider services) =>
        factory(services) ?? throw new InvalidOperationException($"The navigation factory for {typeof(T).Name} returned null.");
    public override ValueTask InitializeWithInputAsync(object content, NavigationEntryContext entry, CancellationToken cancellationToken) =>
        ((T)content).InitializeAsync(entry, input, cancellationToken);
}
