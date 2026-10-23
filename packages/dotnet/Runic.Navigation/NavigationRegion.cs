using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

/// <summary>
/// One navigation region, independent of its content type, for hosts that present a region without knowing it.
/// <see cref="NavigationRegion{TContent}"/> is the typed region, and every region is one.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="CurrentEntry"/> and <see cref="History"/> return the region's typed entries. The entry in a
/// <see cref="NavigationResult{TContent}.Committed"/> returned by <see cref="BackAsync"/>, <see cref="BackToAsync"/>,
/// <see cref="ClearHistoryAsync"/>, <see cref="ClearAsync"/> or <see cref="ReplaceBorrowedAsync"/> is a <see cref="NavigationEntry{TContent}"/> of <see cref="object"/>, a different
/// instance from the typed entry. Compare entries by <see cref="NavigationEntry.Id"/>, not by reference.
/// </para>
/// <para>
/// Only <see cref="RunicNavigator.CreateRegion{TContent}"/> creates regions, so the class can't be derived from outside
/// Runic.Navigation, and members can be added to it in a minor release.
/// </para>
/// </remarks>
public abstract class NavigationRegion : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs CurrentChanged = new(nameof(Current));
    private static readonly PropertyChangedEventArgs CurrentEntryChanged = new(nameof(CurrentEntry));
    private static readonly PropertyChangedEventArgs HistoryChanged = new(nameof(History));
    private static readonly PropertyChangedEventArgs CanGoBackChanged = new(nameof(CanGoBack));
    private static readonly PropertyChangedEventArgs IsTransitioningChanged = new(nameof(IsTransitioning));
    private static readonly PropertyChangedEventArgs IsBusyChanged = new(nameof(IsBusy));

    private protected NavigationRegion(NavigationRegionCore core)
    {
        Core = core;
        core.Raise = Raise;
        core.Public = this;
    }

    internal NavigationRegionCore Core { get; }

    /// <summary>Gets the navigator that owns the region.</summary>
    public RunicNavigator Navigator => Core.Navigator;

    /// <summary>Gets the region's content type.</summary>
    public Type ContentType => ContentTypeCore;

    /// <summary>Gets the current content, or <see langword="null"/> when the region is empty.</summary>
    public object? Current => Core.CurrentEntry?.Content;

    /// <summary>Gets the current entry, or <see langword="null"/> when the region is empty.</summary>
    public NavigationEntry? CurrentEntry => CurrentEntryCore;

    /// <summary>Gets the retained entries below the current entry, from bottom to top.</summary>
    public IReadOnlyList<NavigationEntry> History => HistoryCore;

    /// <summary>Gets whether a retained entry exists to go back to.</summary>
    public bool CanGoBack => Core.CanGoBack;

    /// <summary>
    /// Gets whether a transition of this region is in flight: from a request's admission until its commit decision. It is
    /// <see langword="false"/> again in the commit turn, before the request's task completes.
    /// </summary>
    public bool IsTransitioning => Core.IsTransitioning;

    /// <summary>
    /// Gets whether a request of this region has not completed: from its admission until its task completes, so it spans
    /// the guards, the preparation, the commit and the retirement of the departed entries. A request made after an awaited
    /// request of the region resumed never finds the region busy because of that request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Back whose callers have all cancelled stops counting when the last of them gets
    /// <see cref="NavigationRejection.Cancelled"/>, even while its departure guard is still running because it hasn't
    /// observed the token yet. That Back never commits. A request admitted meanwhile waits for its guard to end before
    /// its own guards run, as it waits for any earlier request of the region.
    /// </para>
    /// <para>
    /// <see cref="PropertyChanged"/> reports a change in a model turn, like <see cref="IsTransitioning"/>; the change of
    /// a request that completes is posted, so the notification can trail its task. The property's value is authoritative.
    /// </para>
    /// </remarks>
    public bool IsBusy => Core.IsBusy;

    /// <summary>Gets how the region admits a request while an earlier one runs (<see cref="NavigationRegionOptions.Admission"/>).</summary>
    public NavigationAdmission Admission => Core.Admission;

    /// <summary>
    /// Completes when <see cref="IsBusy"/> is <see langword="false"/>: when no request of this region is running. Unlike
    /// <see cref="RunicNavigator.WhenIdleAsync"/>, it doesn't wait for other regions or for retirements that no request of
    /// this region awaits, nor for the guard of a Back whose callers have all cancelled.
    /// </summary>
    /// <remarks>A hook of a transition of this region that awaits it deadlocks: the region is busy while the hook runs.</remarks>
    /// <param name="cancellationToken">Stops waiting.</param>
    public ValueTask WhenIdleAsync(CancellationToken cancellationToken = default) => Core.Navigator.WhenRegionIdleAsync(Core, cancellationToken);

    /// <summary>
    /// Registers a guard that is asked before a transition departs from this region: a request to the region, or to a
    /// region whose plan resets or closes it or leaves it, as a tab switch leaves the tab's page regions. It is asked once
    /// per transition, after the departure guards of the entries (<see cref="INavigationDepartureGuard"/>), or before them
    /// with <see cref="NavigationRegionGuardOptions.Order"/>, and only when an entry of the region departs: a push into an
    /// empty region asks it not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The region guards of a transition run region by region, deepest first, each region's guards in registration order,
    /// then the navigator's (<see cref="RunicNavigator.AddDepartureGuard"/>): first those asked
    /// <see cref="NavigationRegionGuardOrder.BeforeEntries"/>, then the entries' guards, then those asked
    /// <see cref="NavigationRegionGuardOrder.AfterEntries"/>. A guard is skipped when the transition only
    /// retains the region's entries, unless <see cref="NavigationRegionGuardOptions.AskOnRetain"/>, and for a
    /// <see cref="NavigationOperation.ClearHistory"/> request, unless <see cref="NavigationRegionGuardOptions.AskOnClearHistory"/>.
    /// </para>
    /// <para>
    /// <see langword="false"/> rejects the transition as <see cref="NavigationRejection.Guard"/> with no
    /// <see cref="NavigationResult{TContent}.Rejected.By"/> entry; a guard that throws fails it in
    /// <see cref="NavigationPhase.Guarding"/> (event 1060, with the guard's type as <c>EntryType</c>). The guards are read
    /// when the transition starts guarding: a guard removed before its turn is not asked, and one added later waits for
    /// the next transition. Navigator disposal asks no guards.
    /// </para>
    /// </remarks>
    /// <param name="guard">The guard.</param>
    /// <param name="options">
    /// When the guard is asked; by default after the entries' guards, and skipped for retained entries and for ClearHistory.
    /// </param>
    /// <returns>An <see cref="IDisposable"/> that removes the guard.</returns>
    public IDisposable AddDepartureGuard(INavigationRegionGuard guard, NavigationRegionGuardOptions? options = null) =>
        Core.Navigator.AddGuard(Core.Guards, guard, options);

    /// <summary>
    /// Raised inside a model turn after a commit, for <see cref="Current"/>, <see cref="CurrentEntry"/>,
    /// <see cref="History"/>, <see cref="CanGoBack"/>, <see cref="IsTransitioning"/> and <see cref="IsBusy"/>. Each handler is
    /// called separately; one that throws is logged and does not skip the others.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Retires the current entry and resumes the top retained entry.</summary>
    /// <remarks>Compare the result's entry with this region's entries by <see cref="NavigationEntry.Id"/>.</remarks>
    public ValueTask<NavigationResult<object>> BackAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        NavigationResults.MapAsync<object>(Core.Start(NavigationOperation.Back, null, null, options, cancellationToken));

    /// <summary>Retires every entry above <paramref name="entry"/> and resumes it.</summary>
    /// <remarks>Compare the result's entry with this region's entries by <see cref="NavigationEntry.Id"/>.</remarks>
    public ValueTask<NavigationResult<object>> BackToAsync(NavigationEntryId entry,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        NavigationResults.MapAsync<object>(Core.Start(NavigationOperation.BackTo, null, entry, options, cancellationToken));

    /// <summary>Retires every retained entry. The current entry stays.</summary>
    /// <remarks>Compare the result's entry with this region's entries by <see cref="NavigationEntry.Id"/>.</remarks>
    public ValueTask<NavigationResult<object>> ClearHistoryAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        NavigationResults.MapAsync<object>(Core.Start(NavigationOperation.ClearHistory, null, null, options, cancellationToken));

    /// <summary>Retires every entry. The region becomes empty.</summary>
    /// <remarks>Compare the result's entry with this region's entries by <see cref="NavigationEntry.Id"/>.</remarks>
    public ValueTask<NavigationResult<object>> ClearAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        NavigationResults.MapAsync<object>(Core.Start(NavigationOperation.Clear, null, null, options, cancellationToken));

    /// <summary>
    /// Replaces the current entry with borrowed <paramref name="content"/>. For presentation adapters that select
    /// existing models without knowing the region's content type, such as Runic.Navigation.Wpf's
    /// <c>NavigationSelector</c>; application code calls <see cref="NavigationRegion{TContent}.ReplaceAsync(INavigationTarget{TContent}, NavigationRequestOptions?, CancellationToken)"/>.
    /// </summary>
    /// <remarks>The engine runs the same guards and lifetime rules as typed Replace. The caller retains ownership.</remarks>
    /// <exception cref="ArgumentException">The content is not assignable to <see cref="ContentType"/>.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ValueTask<NavigationResult<object>> ReplaceBorrowedAsync(object content, NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!ContentType.IsInstanceOfType(content))
            throw new ArgumentException("The selected content is not assignable to the region's content type.", nameof(content));
        return NavigationResults.MapAsync<object>(Core.Start(NavigationOperation.Replace, BorrowTarget(content), null, options,
            cancellationToken));
    }

    private protected abstract Type ContentTypeCore { get; }

    private protected abstract NavigationEntry? CurrentEntryCore { get; }

    private protected abstract IReadOnlyList<NavigationEntry> HistoryCore { get; }

    // A Borrow target of content already checked against ContentType.
    private protected abstract NavigationTargetCore BorrowTarget(object content);

    private void Raise(NavigationRegionChanges changes)
    {
        if ((changes & NavigationRegionChanges.Current) != 0)
        {
            Raise(CurrentChanged);
            Raise(CurrentEntryChanged);
        }
        if ((changes & NavigationRegionChanges.History) != 0) Raise(HistoryChanged);
        if ((changes & NavigationRegionChanges.CanGoBack) != 0) Raise(CanGoBackChanged);
        if ((changes & NavigationRegionChanges.Transitioning) != 0) Raise(IsTransitioningChanged);
        if ((changes & NavigationRegionChanges.Busy) != 0) Raise(IsBusyChanged);
    }

    // D-13: one throwing handler must not skip the others or undo the commit.
    private void Raise(PropertyChangedEventArgs args)
    {
        var handlers = PropertyChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((PropertyChangedEventHandler)handler)(this, args); }
            catch (Exception error)
            {
                NavigationLog.NavigationNotificationFailed(Core.Navigator.Logger, error, Core.ContentTypeName, Core.Id,
                    args.PropertyName ?? string.Empty, NavigationTelemetry.ErrorType(error));
            }
        }
    }
}

/// <summary>
/// One navigation region: a stack of typed entries whose top is <see cref="Current"/>. State changes
/// commit in one model turn and raise <see cref="NavigationRegion.PropertyChanged"/> inside that turn.
/// </summary>
/// <typeparam name="TContent">The content type presented by the region.</typeparam>
/// <remarks>
/// Operations throw only for argument errors and ownership violations; every navigation outcome is
/// returned as a <see cref="NavigationResult{TContent}"/>. Called from inside a model turn, an
/// operation is admitted without blocking and continues on the thread pool; do not block on it there.
/// </remarks>
public sealed class NavigationRegion<TContent> : NavigationRegion where TContent : class
{
    internal NavigationRegion(NavigationRegionCore core) : base(core) { }

    /// <summary>Gets the current content, or <see langword="null"/> when the region is empty.</summary>
    public new TContent? Current => (TContent?)Core.CurrentEntry?.Content;

    /// <summary>Gets the current entry, or <see langword="null"/> when the region is empty.</summary>
    public new NavigationEntry<TContent>? CurrentEntry => Core.CurrentEntry?.View<TContent>();

    /// <summary>Gets the retained entries below the current entry, from bottom to top.</summary>
    public new IReadOnlyList<NavigationEntry<TContent>> History => Core.History<TContent>();

    /// <summary>
    /// Commits the root entry of a region created without an initial target, synchronously, like the
    /// <c>initial</c> target of <see cref="RunicNavigator.CreateRegion{TContent}"/>. Call it once, where the root
    /// page exists, for example in the constructor of a window's ViewModel:
    /// <c>Main.Start(NavigationTarget.Borrow&lt;IPage&gt;(list))</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target follows the rules for initial targets: it has no input, and its content implements neither
    /// <see cref="INavigationInitialize"/> nor <see cref="INavigationInitialize{TInput}"/>. Content that a
    /// <c>Create</c> target constructs can take its <see cref="NavigationEntryContext"/> as a constructor parameter.
    /// </para>
    /// <para>
    /// Before the method returns, the entry's <see cref="NavigationEntryContext.StateChanged"/> and
    /// <see cref="INavigationAware.OnNavigatedTo"/> (with <see cref="NavigationOperation.Reset"/>), then the
    /// region's <see cref="NavigationRegion.PropertyChanged"/>, run on the caller's thread. Call it before the region is shared
    /// with other threads.
    /// </para>
    /// <para>
    /// Unlike a navigation request, a failure throws: an exception from the target's factory or constructor
    /// propagates, and the region stays empty. Opened through <c>IRunicWindowService</c>, the window then fails
    /// to open.
    /// </para>
    /// </remarks>
    /// <param name="target">The root content.</param>
    /// <exception cref="ArgumentException">The target has input, was not created with <see cref="NavigationTarget"/>, or its content initializes asynchronously.</exception>
    /// <exception cref="InvalidOperationException">
    /// The region already has its initial entry (it was created with one, or <see cref="Start"/> was called before),
    /// or it already received a navigation request.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The navigator is closed.</exception>
    public void Start(INavigationTarget<TContent> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Core.Navigator.StartRegion(Core, target, nameof(target));
    }

    /// <summary>Pushes a new entry. The current entry is retained.</summary>
    public ValueTask<NavigationResult<TContent>> PushAsync(INavigationTarget<TContent> target,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Push, Target(target), null, options, cancellationToken);

    /// <summary>
    /// Pushes a new entry like <see cref="PushAsync"/> and waits for a typed result from it. The entry
    /// returns its result with <see cref="NavigationEntryContext.CompleteAsync{TResult}"/>, which goes
    /// back from it; when it was pushed onto an empty region, the region becomes empty again.
    /// </summary>
    /// <remarks>
    /// <see cref="NavigationResultRequest{TContent, TResult}.Completion"/> is dismissed when the push does
    /// not commit, when the entry retires without completing, and when the navigator closes. Cancelling
    /// <paramref name="cancellationToken"/> before the commit gives the usual
    /// <see cref="NavigationRejection.Cancelled"/> outcome. After the commit it dismisses the request at once
    /// and goes back from the entry if it is still current; that back transition is not awaited, and a
    /// rejection or supersession of it is logged (1065, 1066). A departure guard that awaits a result in
    /// another region should pass its own token, so supersession and window close dismiss the request.
    /// </remarks>
    /// <typeparam name="TResult">The result type.</typeparam>
    public NavigationResultRequest<TContent, TResult> PushForResult<TResult>(INavigationTarget<TContent> target,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Core.Navigator.PushForResult<TContent, TResult>(Core, Target(target), options, cancellationToken);

    /// <summary>Retires the current entry and resumes the top retained entry.</summary>
    public new ValueTask<NavigationResult<TContent>> BackAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Back, null, null, options, cancellationToken);

    /// <summary>Retires every entry above <paramref name="entry"/> and resumes it.</summary>
    public new ValueTask<NavigationResult<TContent>> BackToAsync(NavigationEntryId entry,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.BackTo, null, entry, options, cancellationToken);

    /// <summary>Retires the current entry and makes a new entry current. History is unchanged.</summary>
    public ValueTask<NavigationResult<TContent>> ReplaceAsync(INavigationTarget<TContent> target,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Replace, Target(target), null, options, cancellationToken);

    /// <summary>Retires every entry and makes a new entry the root.</summary>
    public ValueTask<NavigationResult<TContent>> ResetAsync(INavigationTarget<TContent> target,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Reset, Target(target), null, options, cancellationToken);

    /// <summary>
    /// Pushes a new <typeparamref name="TViewModel"/> that the navigator constructs and owns, like
    /// <see cref="PushAsync(INavigationTarget{TContent}, NavigationRequestOptions?, CancellationToken)"/> with
    /// <see cref="NavigationTarget.Create{T}()"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    public ValueTask<NavigationResult<TContent>> PushAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent =>
        Run(NavigationOperation.Push, new ActivatedNavigationTarget<TViewModel>(), null, options, cancellationToken);

    /// <summary>
    /// Pushes a new <typeparamref name="TViewModel"/> that the navigator constructs, owns and initializes with
    /// <paramref name="input"/>, like <see cref="NavigationTarget.Create{T, TInput}(TInput)"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="input">The input passed to the content's typed hooks: <see cref="INavigationInitialize{TInput}.InitializeAsync"/> and <see cref="INavigationArrived{TInput}.OnArrivedAsync"/>.</param>
    /// <param name="options">The request's options.</param>
    /// <param name="cancellationToken">Cancels the request before it commits.</param>
    public ValueTask<NavigationResult<TContent>> PushAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TInput>(
        TInput input, NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent, INavigationInput<TInput> =>
        Run(NavigationOperation.Push, new ActivatedInputNavigationTarget<TViewModel, TInput>(input), null, options, cancellationToken);

    /// <summary>
    /// Pushes a new <typeparamref name="TViewModel"/> that the navigator constructs and owns, and waits for a typed
    /// result from it, like <see cref="PushForResult{TResult}"/> with <see cref="NavigationTarget.Create{T}()"/>. For
    /// a target with input, pass <see cref="NavigationTarget.Create{T, TInput}(TInput)"/> to <see cref="PushForResult{TResult}"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    /// <typeparam name="TResult">The result type.</typeparam>
    public NavigationResultRequest<TContent, TResult> PushForResult<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TResult>(
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent =>
        Core.Navigator.PushForResult<TContent, TResult>(Core, new ActivatedNavigationTarget<TViewModel>(), options, cancellationToken);

    /// <summary>
    /// Replaces the current entry with a new <typeparamref name="TViewModel"/> that the navigator constructs and
    /// owns, like <see cref="ReplaceAsync(INavigationTarget{TContent}, NavigationRequestOptions?, CancellationToken)"/>
    /// with <see cref="NavigationTarget.Create{T}()"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    public ValueTask<NavigationResult<TContent>> ReplaceAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent =>
        Run(NavigationOperation.Replace, new ActivatedNavigationTarget<TViewModel>(), null, options, cancellationToken);

    /// <summary>
    /// Replaces the current entry with a new <typeparamref name="TViewModel"/> that the navigator constructs, owns
    /// and initializes with <paramref name="input"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="input">The input passed to the content's typed hooks: <see cref="INavigationInitialize{TInput}.InitializeAsync"/> and <see cref="INavigationArrived{TInput}.OnArrivedAsync"/>.</param>
    /// <param name="options">The request's options.</param>
    /// <param name="cancellationToken">Cancels the request before it commits.</param>
    public ValueTask<NavigationResult<TContent>> ReplaceAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TInput>(
        TInput input, NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent, INavigationInput<TInput> =>
        Run(NavigationOperation.Replace, new ActivatedInputNavigationTarget<TViewModel, TInput>(input), null, options, cancellationToken);

    /// <summary>
    /// Retires every entry and makes a new <typeparamref name="TViewModel"/>, which the navigator constructs and
    /// owns, the root, like <see cref="ResetAsync(INavigationTarget{TContent}, NavigationRequestOptions?, CancellationToken)"/>
    /// with <see cref="NavigationTarget.Create{T}()"/>.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    public ValueTask<NavigationResult<TContent>> ResetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel>(
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent =>
        Run(NavigationOperation.Reset, new ActivatedNavigationTarget<TViewModel>(), null, options, cancellationToken);

    /// <summary>
    /// Retires every entry and makes a new <typeparamref name="TViewModel"/>, which the navigator constructs, owns
    /// and initializes with <paramref name="input"/>, the root.
    /// </summary>
    /// <typeparam name="TViewModel">The content type to construct; its constructor parameters come from the entry's service provider.</typeparam>
    /// <typeparam name="TInput">The input type.</typeparam>
    /// <param name="input">The input passed to the content's typed hooks: <see cref="INavigationInitialize{TInput}.InitializeAsync"/> and <see cref="INavigationArrived{TInput}.OnArrivedAsync"/>.</param>
    /// <param name="options">The request's options.</param>
    /// <param name="cancellationToken">Cancels the request before it commits.</param>
    public ValueTask<NavigationResult<TContent>> ResetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TViewModel, TInput>(
        TInput input, NavigationRequestOptions? options = null, CancellationToken cancellationToken = default)
        where TViewModel : class, TContent, INavigationInput<TInput> =>
        Run(NavigationOperation.Reset, new ActivatedInputNavigationTarget<TViewModel, TInput>(input), null, options, cancellationToken);

    /// <summary>Retires every retained entry. The current entry stays.</summary>
    public new ValueTask<NavigationResult<TContent>> ClearHistoryAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.ClearHistory, null, null, options, cancellationToken);

    /// <summary>Retires every entry. The region becomes empty.</summary>
    public new ValueTask<NavigationResult<TContent>> ClearAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Clear, null, null, options, cancellationToken);

    /// <inheritdoc />
    public override string ToString() => $"NavigationRegion<{typeof(TContent).Name}> #{Core.Id}";

    private ValueTask<NavigationResult<TContent>> Run(NavigationOperation operation, NavigationTargetCore? target,
        NavigationEntryId? backTo, NavigationRequestOptions? options, CancellationToken cancellationToken) =>
        NavigationResults.MapAsync<TContent>(Core.Start(operation, target, backTo, options, cancellationToken));

    private protected override Type ContentTypeCore => typeof(TContent);

    private protected override NavigationEntry? CurrentEntryCore => CurrentEntry;

    private protected override IReadOnlyList<NavigationEntry> HistoryCore => History;

    private protected override NavigationTargetCore BorrowTarget(object content) => Target(NavigationTarget.Borrow((TContent)content));

    private static NavigationTargetCore Target(INavigationTarget<TContent> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target as NavigationTargetCore
            ?? throw new ArgumentException("Navigation targets must be created with NavigationTarget.", nameof(target));
    }
}

[Flags]
internal enum NavigationRegionChanges
{
    None = 0,
    Current = 1,
    History = 2,
    CanGoBack = 4,
    Transitioning = 8,
    Busy = 16,
}

// Immutable history cache entry: the stack a view was built from, and that view.
internal sealed record HistoryCache(NavigationEntryCore[] Source, object View);

// The non-generic region state. Every field is guarded by the navigator's gate;
// the stack is an immutable array replaced on change.
internal sealed class NavigationRegionCore(RunicNavigator navigator, int id, string contentTypeName, object owner,
    NavigationChildRetention whileParentRetained, NavigationAdmission admission = NavigationAdmission.Supersede)
{
    // One immutable (source, view) pair, so a reader can never pair a stack with
    // another reader's view. Published and read only through Volatile.
    private HistoryCache? _history;
    private bool _raisedTransitioning;
    private bool _raisedBusy;

    public RunicNavigator Navigator { get; } = navigator;
    public int Id { get; } = id;
    public string ContentTypeName { get; } = contentTypeName;
    public object Owner { get; } = owner;
    public NavigationChildRetention WhileParentRetained { get; } = whileParentRetained;
    public NavigationAdmission Admission { get; } = admission;

    // Bottom to top; the top entry is current.
    private volatile NavigationEntryCore[] _stack = [];

    public NavigationEntryCore[] Stack { get => _stack; set => _stack = value; }
    public long Version { get; set; }
    public bool Closed { get; set; }
    // An initial entry was taken by CreateRegion or Start; Start is then refused.
    public bool Started { get; set; }
    // A request reached the region (admitted or not); Start is then refused.
    public bool Requested { get; set; }
    // Admitted transitions that have not released admission, in admission order.
    public List<NavigationTransition> InFlight { get; } = [];
    // Admitted transitions whose task has not completed (the region's share of the navigator's running set), except a
    // Back whose callers all cancelled: it leaves this set, not the navigator's, when the last caller is settled (#415).
    public HashSet<NavigationTransition> Running { get; } = [];
    // The region's guards (AddDepartureGuard), in registration order.
    public List<NavigationRegionGuardRegistration> Guards { get; } = [];
    // The public region of this core, set once by its constructor.
    public NavigationRegion? Public { get; set; }

    public Action<NavigationRegionChanges>? Raise { get; set; }

    public NavigationEntryCore? CurrentEntry
    {
        get
        {
            var stack = Stack;
            return stack.Length == 0 ? null : stack[^1];
        }
    }

    public bool CanGoBack => Stack.Length > 1;

    public bool IsTransitioning
    {
        get { lock (Navigator.Gate) return InFlight.Count > 0; }
    }

    public bool IsBusy
    {
        get { lock (Navigator.Gate) return IsBusyLocked; }
    }

    // Call under the navigator's gate.
    public bool IsBusyLocked => Running.Count > 0;

    public IReadOnlyList<NavigationEntry<T>> History<T>() where T : class
    {
        var stack = Stack;
        var cached = Volatile.Read(ref _history);
        if (cached is not null && ReferenceEquals(cached.Source, stack) && cached.View is IReadOnlyList<NavigationEntry<T>> typed)
            return typed;
        var view = new NavigationEntry<T>[Math.Max(0, stack.Length - 1)];
        for (var index = 0; index < view.Length; index++) view[index] = stack[index].View<T>();
        Volatile.Write(ref _history, new HistoryCache(stack, view));
        return view;
    }

    public Task<TransitionOutcome> Start(NavigationOperation operation, NavigationTargetCore? target,
        NavigationEntryId? backTo, NavigationRequestOptions? options, CancellationToken cancellationToken,
        NavigationReturn? @return = null) =>
        Navigator.Start(this, operation, target, backTo, options?.ExpectedCurrent, cancellationToken, @return,
            admission: options?.Admission, requester: options?.Requester, retainView: options?.RetainView);

    // Runs inside a model turn. IsTransitioning and IsBusy are raised only when their
    // value differs from the last value raised, so admission and release on
    // different paths never report a stale or duplicate change.
    public void RaiseChanges(NavigationRegionChanges changes)
    {
        bool transitioning, busy;
        lock (Navigator.Gate)
        {
            transitioning = InFlight.Count > 0;
            busy = IsBusyLocked;
        }
        if (transitioning != _raisedTransitioning)
        {
            _raisedTransitioning = transitioning;
            changes |= NavigationRegionChanges.Transitioning;
        }
        else changes &= ~NavigationRegionChanges.Transitioning;
        if (busy != _raisedBusy)
        {
            _raisedBusy = busy;
            changes |= NavigationRegionChanges.Busy;
        }
        else changes &= ~NavigationRegionChanges.Busy;
        if (changes != NavigationRegionChanges.None) Raise?.Invoke(changes);
    }
}
