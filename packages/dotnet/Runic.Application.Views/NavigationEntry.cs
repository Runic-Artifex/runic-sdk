using System.Diagnostics.CodeAnalysis;

namespace Runic.Application.Views;

/// <summary>One entry of a navigation region: its stable id, content, ownership and state.</summary>
/// <typeparam name="TContent">The region's content type.</typeparam>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class NavigationEntry<TContent> where TContent : class
{
    internal NavigationEntry(NavigationEntryCore core) => Core = core;

    internal NavigationEntryCore Core { get; }

    /// <summary>Gets the entry's id, unique within its navigator.</summary>
    public NavigationEntryId Id => Core.Id;

    /// <summary>Gets the entry's content.</summary>
    public TContent Content => (TContent)Core.Content!;

    /// <summary>Gets whether the navigator owns the content.</summary>
    public NavigationOwnership Ownership => Core.Ownership;

    /// <summary>Gets the entry's current state.</summary>
    public NavigationEntryState State => Core.State;

    /// <inheritdoc />
    public override string ToString() => $"{Id.Value}:{Core.ContentTypeName} ({State})";
}

/// <summary>Gives an entry's hooks its identity, retirement signal, model context and late-completion operations.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class NavigationEntryContext
{
    private readonly NavigationEntryCore _entry;

    internal NavigationEntryContext(NavigationEntryCore entry) => _entry = entry;

    /// <summary>Gets the entry's id.</summary>
    public NavigationEntryId Id => _entry.Id;

    /// <summary>Gets a token that is cancelled when the entry starts retiring.</summary>
    public CancellationToken Retirement => _entry.Retirement.Token;

    /// <summary>Gets the model context that commits navigation state.</summary>
    public IRunicModelContext ModelContext => _entry.Region.Navigator.ModelContext;

    /// <summary>Gets the window's service provider, or <see langword="null"/> when the navigator has none.</summary>
    public IServiceProvider? Services => _entry.Region.Navigator.Services;

    /// <summary>
    /// Goes back from this entry. The request expects this entry to be current, so a retained or
    /// retired entry receives <see cref="NavigationRejection.NotCurrent"/>.
    /// </summary>
    public ValueTask<NavigationResult<object>> BackAsync(CancellationToken cancellationToken = default)
    {
        var region = _entry.Region;
        var pending = region.Start(NavigationOperation.Back, target: null, backTo: null,
            new NavigationRequestOptions(_entry.Id), cancellationToken);
        return NavigationResults.MapAsync<object>(pending);
    }

    /// <summary>
    /// Completes the <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> request that pushed
    /// this entry and goes back from it, like <see cref="BackAsync"/>. When this entry is the region's
    /// only entry, going back leaves the region empty. The request's completion becomes
    /// <see cref="NavigationCompletion{TResult}.Completed"/> with <paramref name="result"/> only if that
    /// back transition commits; otherwise the entry stays current, the request stays open, and the call
    /// can be repeated. When the request was already dismissed, the entry still goes back and the
    /// result is dropped.
    /// </summary>
    /// <typeparam name="TResult">
    /// The request's result type, or any type whose value is a result-type value at run time, such
    /// as <see cref="bool"/> for a <c>bool?</c> request or <see cref="int"/> for an <see cref="object"/> request.
    /// </typeparam>
    /// <param name="result">The result.</param>
    /// <param name="cancellationToken">Cancels the back transition before it commits.</param>
    /// <exception cref="InvalidOperationException">
    /// The entry was not pushed with <c>PushForResult</c>, or <paramref name="result"/> is not a value of its result type.
    /// </exception>
    public ValueTask<NavigationResult<object>> CompleteAsync<TResult>(TResult result, CancellationToken cancellationToken = default)
    {
        var request = _entry.ResultRequest
            ?? throw new InvalidOperationException("This navigation entry was not pushed with PushForResult, so it has no result to complete.");
        // A reference type assignable to the result type takes the contravariant sink without
        // boxing; anything else (value types, wider static types) takes the boxed run-time check.
        var staged = request is INavigationResultSink<TResult> sink ? sink.Stage(result) : request.TryStageBoxed(result)
            ?? throw new InvalidOperationException(
                $"This navigation entry's result is a {request.ResultTypeName}; this {typeof(TResult).Name} value can't complete it.");
        var pending = _entry.Region.Start(NavigationOperation.Back, target: null, backTo: null,
            new NavigationRequestOptions(_entry.Id), cancellationToken, new NavigationReturn(staged));
        return NavigationResults.MapAsync<object>(pending);
    }
}

internal enum NavigationEntryPhase
{
    Pending,
    Active,
    Retained,
    // A commit removed the entry from its region; retirement has not started.
    Removed,
    Retiring,
    Retired,
}

// The non-generic entry shared by a region's typed views.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationEntryCore
{
    private object? _typedView;

    public NavigationEntryCore(NavigationRegionCore region, NavigationEntryId id, NavigationOwnership ownership, object? content)
    {
        Region = region;
        Id = id;
        Ownership = ownership;
        Content = content;
        Context = new NavigationEntryContext(this);
    }

    public NavigationRegionCore Region { get; }
    public NavigationEntryId Id { get; }
    public NavigationOwnership Ownership { get; }
    public bool Owned => Ownership == NavigationOwnership.Owned;

    // Null only while a factory target has not created its content yet.
    public object? Content { get; set; }

    public string ContentTypeName => Content?.GetType().Name ?? Region.ContentTypeName;

    // Guarded by the navigator's gate.
    public NavigationEntryPhase Phase { get; set; }

    public CancellationTokenSource Retirement { get; } = new();
    public NavigationEntryContext Context { get; }
    public IRunicModelContextLease? Lease { get; set; }

    // Set once by the path that starts retirement (the atomic move to Retiring).
    public Task? Retiring { get; set; }

    // Set under the navigator gate while the initialize hook runs. Retirement of
    // owned content waits for it (bounded by CloseTimeout) before disposing.
    public Task? Initializing { get; set; }

    // The PushForResult request that pushed this entry; set at admission, before any hook runs.
    public NavigationResultRequestCore? ResultRequest { get; set; }

    public NavigationEntryState State => Phase switch
    {
        NavigationEntryPhase.Pending => NavigationEntryState.Pending,
        NavigationEntryPhase.Active => NavigationEntryState.Active,
        NavigationEntryPhase.Retained => NavigationEntryState.Retained,
        _ => NavigationEntryState.Retired,
    };

    public NavigationEntry<T> View<T>() where T : class =>
        _typedView as NavigationEntry<T> ?? (NavigationEntry<T>)(_typedView = new NavigationEntry<T>(this));
}
