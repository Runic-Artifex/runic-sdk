using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

/// <summary>
/// One navigation region: a stack of typed entries whose top is <see cref="Current"/>. State changes
/// commit in one model turn and raise <see cref="PropertyChanged"/> inside that turn.
/// </summary>
/// <typeparam name="TContent">The content type presented by the region.</typeparam>
/// <remarks>
/// Operations throw only for argument errors and ownership violations; every navigation outcome is
/// returned as a <see cref="NavigationResult{TContent}"/>. Called from inside a model turn, an
/// operation is admitted without blocking and continues on the thread pool; do not block on it there.
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class NavigationRegion<TContent> : INotifyPropertyChanged where TContent : class
{
    private static readonly PropertyChangedEventArgs CurrentChanged = new(nameof(Current));
    private static readonly PropertyChangedEventArgs CurrentEntryChanged = new(nameof(CurrentEntry));
    private static readonly PropertyChangedEventArgs HistoryChanged = new(nameof(History));
    private static readonly PropertyChangedEventArgs CanGoBackChanged = new(nameof(CanGoBack));
    private static readonly PropertyChangedEventArgs IsTransitioningChanged = new(nameof(IsTransitioning));

    internal NavigationRegion(NavigationRegionCore core)
    {
        Core = core;
        core.Raise = Raise;
    }

    internal NavigationRegionCore Core { get; }

    internal RunicNavigator Navigator => Core.Navigator;

    /// <summary>Gets the current content, or <see langword="null"/> when the region is empty.</summary>
    public TContent? Current => (TContent?)Core.CurrentEntry?.Content;

    /// <summary>Gets the current entry, or <see langword="null"/> when the region is empty.</summary>
    public NavigationEntry<TContent>? CurrentEntry => Core.CurrentEntry?.View<TContent>();

    /// <summary>Gets the retained entries below the current entry, from bottom to top.</summary>
    public IReadOnlyList<NavigationEntry<TContent>> History => Core.History<TContent>();

    /// <summary>Gets whether a retained entry exists to go back to.</summary>
    public bool CanGoBack => Core.CanGoBack;

    /// <summary>Gets whether a transition of this region is in flight.</summary>
    public bool IsTransitioning => Core.IsTransitioning;

    /// <summary>
    /// Raised inside a model turn after a commit, for <see cref="Current"/>, <see cref="CurrentEntry"/>,
    /// <see cref="History"/>, <see cref="CanGoBack"/> and <see cref="IsTransitioning"/>. Each handler is
    /// called separately; one that throws is logged and does not skip the others.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Pushes a new entry. The current entry is retained.</summary>
    public ValueTask<NavigationResult<TContent>> PushAsync(INavigationTarget<TContent> target,
        NavigationRequestOptions? options = null, CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Push, Target(target), null, options, cancellationToken);

    /// <summary>Retires the current entry and resumes the top retained entry.</summary>
    public ValueTask<NavigationResult<TContent>> BackAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Back, null, null, options, cancellationToken);

    /// <summary>Retires every entry above <paramref name="entry"/> and resumes it.</summary>
    public ValueTask<NavigationResult<TContent>> BackToAsync(NavigationEntryId entry,
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

    /// <summary>Retires every retained entry. The current entry stays.</summary>
    public ValueTask<NavigationResult<TContent>> ClearHistoryAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.ClearHistory, null, null, options, cancellationToken);

    /// <summary>Retires every entry. The region becomes empty.</summary>
    public ValueTask<NavigationResult<TContent>> ClearAsync(NavigationRequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Run(NavigationOperation.Clear, null, null, options, cancellationToken);

    /// <inheritdoc />
    public override string ToString() => $"NavigationRegion<{typeof(TContent).Name}> #{Core.Id}";

    private ValueTask<NavigationResult<TContent>> Run(NavigationOperation operation, NavigationTargetCore? target,
        NavigationEntryId? backTo, NavigationRequestOptions? options, CancellationToken cancellationToken) =>
        NavigationResults.MapAsync<TContent>(Core.Start(operation, target, backTo, options, cancellationToken));

    private static NavigationTargetCore Target(INavigationTarget<TContent> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target as NavigationTargetCore
            ?? throw new ArgumentException("Navigation targets must be created with NavigationTarget.", nameof(target));
    }

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
                    args.PropertyName ?? string.Empty, BridgeTelemetry.ErrorType(error));
            }
        }
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
}

// Immutable history cache entry: the stack a view was built from, and that view.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed record HistoryCache(NavigationEntryCore[] Source, object View);

// The non-generic region state. Every field is guarded by the navigator's gate;
// the stack is an immutable array replaced on change.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationRegionCore(RunicNavigator navigator, int id, string contentTypeName, object owner,
    NavigationChildRetention whileParentRetained)
{
    // One immutable (source, view) pair, so a reader can never pair a stack with
    // another reader's view. Published and read only through Volatile.
    private HistoryCache? _history;
    private bool _raisedTransitioning;

    public RunicNavigator Navigator { get; } = navigator;
    public int Id { get; } = id;
    public string ContentTypeName { get; } = contentTypeName;
    public object Owner { get; } = owner;
    public NavigationChildRetention WhileParentRetained { get; } = whileParentRetained;

    // Bottom to top; the top entry is current.
    private volatile NavigationEntryCore[] _stack = [];

    public NavigationEntryCore[] Stack { get => _stack; set => _stack = value; }
    public long Version { get; set; }
    public bool Closed { get; set; }
    // Admitted transitions that have not released admission, in admission order.
    public List<NavigationTransition> InFlight { get; } = [];

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

    public Task<NavigationOutcome> Start(NavigationOperation operation, NavigationTargetCore? target,
        NavigationEntryId? backTo, NavigationRequestOptions? options, CancellationToken cancellationToken) =>
        Navigator.Start(this, operation, target, backTo, options?.ExpectedCurrent, cancellationToken);

    // Runs inside a model turn. IsTransitioning is raised only when its value
    // differs from the last value raised, so admission and release on
    // different paths never report a stale or duplicate change.
    public void RaiseChanges(NavigationRegionChanges changes)
    {
        var transitioning = IsTransitioning;
        if (transitioning != _raisedTransitioning)
        {
            _raisedTransitioning = transitioning;
            changes |= NavigationRegionChanges.Transitioning;
        }
        else changes &= ~NavigationRegionChanges.Transitioning;
        if (changes != NavigationRegionChanges.None) Raise?.Invoke(changes);
    }
}
