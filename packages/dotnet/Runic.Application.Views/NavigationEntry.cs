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
