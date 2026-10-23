using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>Creates navigation entries for unit tests of content that takes a <see cref="NavigationEntryContext"/>.</summary>
public static class RunicTestNavigationEntry
{
    /// <summary>
    /// Creates a navigator with its own <see cref="RunicModelContext"/> and one region, and pushes the content that
    /// <paramref name="create"/> constructs with its entry's <see cref="NavigationEntryContext"/>, as the navigator constructs a
    /// page: the content is owned by its entry, and its navigation hooks run as in the application.
    /// </summary>
    /// <remarks>
    /// The entry is pushed with <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/> for an
    /// <see cref="object"/> result, so <see cref="NavigationEntryContext.CompleteAsync{TResult}"/> accepts a value of any type
    /// and <see cref="RunicTestNavigationEntry{TContent}.Completion"/> reports it.
    /// </remarks>
    /// <typeparam name="TContent">The content's type.</typeparam>
    /// <param name="create">Constructs the content with its entry's context.</param>
    /// <param name="services">
    /// The navigator's services (<see cref="RunicNavigatorOptions.Services"/>), which <see cref="NavigationEntryContext.Services"/>
    /// returns, or <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>The entry, current in its region.</returns>
    /// <exception cref="InvalidOperationException">The push did not commit, for example because <paramref name="create"/> threw.</exception>
    public static async ValueTask<RunicTestNavigationEntry<TContent>> CreateAsync<TContent>(Func<NavigationEntryContext, TContent> create,
        IServiceProvider? services = null, CancellationToken cancellationToken = default) where TContent : class
    {
        ArgumentNullException.ThrowIfNull(create);
        var model = new RunicModelContext();
        var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = model, Services = services });
        try
        {
            var owner = new object();
            var region = navigator.CreateRegion<object>(owner);
            NavigationEntryContext? context = null;
            var request = region.PushForResult<object?>(NavigationTarget.Create<TContent>((_, entry) =>
            {
                context = entry;
                return create(entry);
            }), cancellationToken: cancellationToken);
            var result = await request.Transition.ConfigureAwait(false);
            if (result is not NavigationResult<object>.Committed { Current: { } current } || context is null)
                throw new InvalidOperationException($"The test navigation entry was not pushed: {result}.");
            return new RunicTestNavigationEntry<TContent>(model, navigator, owner, region, current, context, request.Completion);
        }
        catch
        {
            await navigator.DisposeAsync().ConfigureAwait(false);
            await model.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

/// <summary>
/// One navigation entry of a test navigator, created by <see cref="RunicTestNavigationEntry.CreateAsync{TContent}"/>, with the
/// transitions that change its state. Dispose it to close the navigator, which retires the entry.
/// </summary>
/// <typeparam name="TContent">The content's type.</typeparam>
public sealed class RunicTestNavigationEntry<TContent> : IAsyncDisposable where TContent : class
{
    private readonly RunicModelContext _model;
    // The region's owner: the region lives as long as this object.
    private readonly object _owner;

    internal RunicTestNavigationEntry(RunicModelContext model, RunicNavigator navigator, object owner, NavigationRegion<object> region,
        NavigationEntry<object> entry, NavigationEntryContext context, Task<NavigationCompletion<object?>> completion)
    {
        _model = model;
        _owner = owner;
        Navigator = navigator;
        Region = region;
        Entry = entry;
        Context = context;
        Completion = completion;
    }

    /// <summary>Gets the content, constructed with <see cref="Context"/>.</summary>
    public TContent Content => (TContent)Entry.Content;

    /// <summary>Gets the context the content was constructed with.</summary>
    public NavigationEntryContext Context { get; }

    /// <summary>Gets the entry.</summary>
    public NavigationEntry<object> Entry { get; }

    /// <summary>Gets the navigator, whose model context is a <see cref="RunicModelContext"/> owned by this object.</summary>
    public RunicNavigator Navigator { get; }

    /// <summary>Gets the region that holds the entry. It has no other entries unless <see cref="RetainAsync"/> pushed one.</summary>
    public NavigationRegion<object> Region { get; }

    /// <summary>
    /// Gets the completion of the request that pushed the entry: <see cref="NavigationCompletion{TResult}.Completed"/> with the
    /// value of <see cref="NavigationEntryContext.CompleteAsync{TResult}"/>, or <see cref="NavigationCompletion{TResult}.Dismissed"/>
    /// when the entry leaves otherwise.
    /// </summary>
    public Task<NavigationCompletion<object?>> Completion { get; }

    /// <summary>Pushes another entry over this one, which retains it.</summary>
    /// <param name="cancellationToken">Cancels the push.</param>
    /// <returns>The push's result.</returns>
    public ValueTask<NavigationResult<object>> RetainAsync(CancellationToken cancellationToken = default) =>
        Region.PushAsync(NavigationTarget.Own<object>(new RetainingPage()), cancellationToken: cancellationToken);

    /// <summary>Goes back to this entry, which makes a retained entry current again.</summary>
    /// <param name="cancellationToken">Cancels the transition.</param>
    /// <returns>The transition's result, which is rejected when the entry is not retained.</returns>
    public ValueTask<NavigationResult<object>> ActivateAsync(CancellationToken cancellationToken = default) =>
        Region.BackToAsync(Entry.Id, cancellationToken: cancellationToken);

    /// <summary>Clears the region, which retires the entry after its departure guards agree.</summary>
    /// <param name="cancellationToken">Cancels the transition.</param>
    /// <returns>The clear's result.</returns>
    public ValueTask<NavigationResult<object>> RetireAsync(CancellationToken cancellationToken = default) =>
        Region.ClearAsync(cancellationToken: cancellationToken);

    /// <summary>Closes the navigator, which retires the entry without asking its guards, then its model context.</summary>
    public async ValueTask DisposeAsync()
    {
        await Navigator.DisposeAsync().ConfigureAwait(false);
        await _model.DisposeAsync().ConfigureAwait(false);
        GC.KeepAlive(_owner);
    }

    private sealed class RetainingPage;
}
