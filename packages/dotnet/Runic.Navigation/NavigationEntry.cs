using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation;

/// <summary>
/// The content-type-independent view of a <see cref="NavigationEntry{TContent}"/>, for hosts that present
/// a region without knowing its content type. Only <see cref="NavigationEntry{TContent}"/> implements it.
/// </summary>
public interface INavigationEntry
{
    /// <summary>Gets the entry's id, unique within its navigator.</summary>
    NavigationEntryId Id { get; }

    /// <summary>Gets the entry's content.</summary>
    object Content { get; }

    /// <summary>Gets whether the navigator owns the content.</summary>
    NavigationOwnership Ownership { get; }

    /// <summary>Gets the entry's current state.</summary>
    NavigationEntryState State { get; }

    /// <summary>
    /// Gets the entry's service provider: its own service scope when the navigator created one
    /// (<see cref="RunicNavigatorOptions.CreateEntryScopes"/>), otherwise the navigator's
    /// <see cref="RunicNavigator.Services"/>, otherwise an empty provider. After an entry with its own scope retires,
    /// resolving from it throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    IServiceProvider Services { get; }
}

/// <summary>One entry of a navigation region: its stable id, content, ownership and state.</summary>
/// <typeparam name="TContent">The region's content type.</typeparam>
public sealed class NavigationEntry<TContent> : INavigationEntry where TContent : class
{
    internal NavigationEntry(NavigationEntryCore core) => Core = core;

    internal NavigationEntryCore Core { get; }

    /// <summary>Gets the entry's id, unique within its navigator.</summary>
    public NavigationEntryId Id => Core.Id;

    /// <summary>Gets the entry's content.</summary>
    public TContent Content => (TContent)Core.Content!;

    object INavigationEntry.Content => Core.Content!;

    /// <summary>Gets whether the navigator owns the content.</summary>
    public NavigationOwnership Ownership => Core.Ownership;

    /// <summary>Gets the entry's current state.</summary>
    public NavigationEntryState State => Core.State;

    /// <inheritdoc cref="INavigationEntry.Services" />
    public IServiceProvider Services => Core.Services;

    /// <inheritdoc />
    public override string ToString() => $"{Id.Value}:{Core.ContentTypeName} ({State})";
}

/// <summary>
/// Gives an entry's content its identity, state, retirement signal, model context and late-completion operations.
/// </summary>
/// <remarks>
/// Content that the navigator creates takes its context as a constructor parameter: with
/// <see cref="NavigationTarget.Create{T}()"/> and the region methods that wrap it, or from the factory of
/// <see cref="NavigationTarget.Create{T}(Func{IServiceProvider, NavigationEntryContext, T})"/>; the typed-input forms and
/// initial targets included. Only the content receives it: a service resolved from the container can't, and
/// resolving it there fails. Content created with
/// <see cref="NavigationTarget.Own{T}"/> or <see cref="NavigationTarget.Borrow{T}"/> receives it in
/// <see cref="INavigationInitialize.InitializeAsync"/>.
/// </remarks>
public sealed class NavigationEntryContext
{
    private readonly NavigationEntryCore _entry;

    internal NavigationEntryContext(NavigationEntryCore entry) => _entry = entry;

    /// <summary>Gets the entry's id.</summary>
    public NavigationEntryId Id => _entry.Id;

    /// <summary>
    /// Gets the entry's current state. It is <see cref="NavigationEntryState.Pending"/> while the content is constructed
    /// and initialized, and changes in the model turn that commits a change (see <see cref="StateChanged"/>).
    /// </summary>
    public NavigationEntryState State => _entry.State;

    /// <summary>
    /// Raised once for each change of <see cref="State"/>, inside a model turn: the commit turn of a navigation; the turn
    /// that clears the entry's region when it closes; and, for a pending entry whose transition did not commit, a turn
    /// posted when it retires. When an initial target becomes current, <see cref="RunicNavigator.CreateRegion{TContent}"/>
    /// raises it before it returns, on the caller's thread. A change that happens after the model context closed is not raised.
    /// </summary>
    /// <remarks>
    /// In the commit turn it is raised after the new navigation state is applied and before the regions raise
    /// <c>PropertyChanged</c>, together with <see cref="INavigationAware"/> (see there for the order). Each handler is
    /// called separately; one that throws is logged (event 1075) and does not skip the others or undo the change.
    /// </remarks>
    public event EventHandler<NavigationEntryStateChangedEventArgs>? StateChanged;

    // The handlers, or null when there are none; read in a model turn.
    internal EventHandler<NavigationEntryStateChangedEventArgs>? StateChangedHandlers => StateChanged;

    /// <summary>Gets a token that is cancelled when the entry starts retiring.</summary>
    public CancellationToken Retirement => _entry.Retirement.Token;

    /// <summary>Gets the model context that commits navigation state.</summary>
    public IRunicModelContext ModelContext => _entry.Region.Navigator.ModelContext;

    /// <summary>
    /// Gets the entry's service provider: its own service scope when the navigator created one
    /// (<see cref="RunicNavigatorOptions.CreateEntryScopes"/>), otherwise the navigator's
    /// <see cref="RunicNavigator.Services"/>, otherwise an empty provider. It is the provider
    /// <see cref="INavigationEntry.Services"/> returns. After an entry with its own scope retires, resolving from it
    /// throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public IServiceProvider Services => _entry.Services;

    /// <summary>
    /// Leaves this entry without a result. What happens depends on the entry's state:
    /// <list type="bullet">
    /// <item><description>
    /// Current, pushed with <see cref="NavigationRegion{TContent}.PushForResult{TResult}"/>: the request ends
    /// <see cref="NavigationCompletion{TResult}.Dismissed"/> at once, then the entry goes back like
    /// <see cref="CompleteAsync{TResult}"/>, which may leave the region empty. This is the path of a cancelled
    /// caller token: if that Back is rejected, the request stays dismissed and the entry stays.
    /// </description></item>
    /// <item><description>
    /// Current, without an open result request: goes back from the entry, which may leave the region empty.
    /// </description></item>
    /// <item><description>
    /// Pending, because the push that creates it has not committed (for example, called from its own
    /// <see cref="INavigationInitialize.InitializeAsync"/>): dismisses its request, cancels that push, which ends
    /// <see cref="NavigationRejection.Cancelled"/>, and returns that rejection at once without awaiting the push.
    /// The hook's token is cancelled.
    /// </description></item>
    /// <item><description>Retained: dismisses its request and leaves the entry in place. Returns <see cref="NavigationRejection.NotCurrent"/>.</description></item>
    /// <item><description>Retiring or retired: does nothing. Returns <see cref="NavigationRejection.NotCurrent"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="cancellationToken">Cancels the Back before it commits. The dismissal of the request is not undone.</param>
    public ValueTask<NavigationResult<object>> DismissAsync(CancellationToken cancellationToken = default) =>
        NavigationResults.MapAsync<object>(_entry.Region.Navigator.Dismiss(_entry, cancellationToken));

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

    // The transition that pushes this pending entry; set at admission.
    public NavigationTransition? Transition { get; set; }

    // The entry's service scope (CreateEntryScopes), set under the gate together with the
    // created content and taken by retirement step 4.
    public IServiceScope? Scope { get; set; }

    // Read without the gate: the provider that constructs a Create target's content is published once,
    // before the construction, and stays valid (throwing ObjectDisposedException once a scope behind it
    // is disposed) after retirement.
    private IServiceProvider? _services;

    public IServiceProvider Services => Volatile.Read(ref _services) ?? Region.Navigator.Services ?? EmptyServiceProvider.Instance;

    public void PublishServices(IServiceProvider services) => Volatile.Write(ref _services, services);

    public NavigationEntryState State => Phase switch
    {
        NavigationEntryPhase.Pending => NavigationEntryState.Pending,
        NavigationEntryPhase.Active => NavigationEntryState.Active,
        NavigationEntryPhase.Retained => NavigationEntryState.Retained,
        _ => NavigationEntryState.Retired,
    };

    // The cached view keeps the region's typed entry stable. An object view (from
    // NavigationEntryContext or INavigationRegion) does not replace a typed one. Any thread may
    // read an entry first (#288), so the view is published once: concurrent first readers all
    // return the one that was stored.
    public NavigationEntry<T> View<T>() where T : class
    {
        var current = Volatile.Read(ref _typedView);
        if (current is NavigationEntry<T> cached) return cached;
        var view = new NavigationEntry<T>(this);
        while (current is null || (typeof(T) != typeof(object) && current is not NavigationEntry<T>))
        {
            var seen = Interlocked.CompareExchange(ref _typedView, view, current);
            if (ReferenceEquals(seen, current)) return view;
            if (seen is NavigationEntry<T> published) return published;
            current = seen;
        }
        return view;
    }
}

// The provider of a navigator without Services.
internal sealed class EmptyServiceProvider : IServiceProvider
{
    public static readonly EmptyServiceProvider Instance = new();
    public object? GetService(Type serviceType) => null;
}

// One state change of an entry, raised as StateChanged and, around Active, as the INavigationAware calls.
internal readonly record struct NavigationEntryChange(NavigationEntryCore Entry, NavigationEntryState Previous, NavigationEntryState State);

// The provider ActivatorUtilities constructs a Create<T>() target's content from (W250-032 §3): the entry's
// provider, which also answers the entry's NavigationEntryContext until the construction returns. Only the
// content's own constructor gets the context; services are resolved from the entry's provider, so they can't
// receive it. It forwards the optional provider interfaces ActivatorUtilities uses (required, keyed and
// IsService checks). The context is not passed as an ActivatorUtilities argument because ActivatorUtilities
// then rejects the constructors that don't take it.
internal sealed class NavigationConstructionServices(IServiceProvider services, NavigationEntryContext context)
    : IServiceProvider, ISupportRequiredService, IKeyedServiceProvider
{
    private volatile bool _constructing = true;

    public static T Activate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        IServiceProvider services, NavigationEntryContext context)
    {
        var construction = new NavigationConstructionServices(services, context);
        try { return ActivatorUtilities.CreateInstance<T>(construction); }
        finally { construction._constructing = false; }
    }

    private bool Answers(Type serviceType) => _constructing && serviceType == typeof(NavigationEntryContext);

    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (Answers(serviceType)) return context;
        if (serviceType == typeof(IServiceProviderIsService) || serviceType == typeof(IServiceProviderIsKeyedService))
            return services.GetService(serviceType) is IServiceProviderIsService inner ? new ServiceCheck(this, inner) : null;
        return services.GetService(serviceType);
    }

    public object GetRequiredService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (Answers(serviceType)) return context;
        return services is ISupportRequiredService required
            ? required.GetRequiredService(serviceType)
            : services.GetService(serviceType) ?? throw new InvalidOperationException($"No service for type '{serviceType}' has been registered.");
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey) => Keyed().GetKeyedService(serviceType, serviceKey);

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => Keyed().GetRequiredKeyedService(serviceType, serviceKey);

    private IKeyedServiceProvider Keyed() => services as IKeyedServiceProvider
        ?? throw new InvalidOperationException("This service provider doesn't support keyed services.");

    // The entry provider's IsService check, extended with the context while the construction runs, so
    // ActivatorUtilities considers a constructor that takes it.
    private sealed class ServiceCheck(NavigationConstructionServices owner, IServiceProviderIsService inner) : IServiceProviderIsKeyedService
    {
        public bool IsService(Type serviceType) => owner.Answers(serviceType) || inner.IsService(serviceType);

        public bool IsKeyedService(Type serviceType, object? serviceKey) =>
            inner is IServiceProviderIsKeyedService keyed && keyed.IsKeyedService(serviceType, serviceKey);
    }
}
