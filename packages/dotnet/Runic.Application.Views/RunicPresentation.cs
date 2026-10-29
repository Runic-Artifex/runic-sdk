using Runic.Navigation;

namespace Runic.Application.Views;

/// <summary>A .NET presentation object associated with a web component.</summary>
public interface IRunicView
{
    /// <summary>The ViewModel presented by this view.</summary>
    object? DataContext { get; set; }
}

/// <summary>
/// A typed logical view. It owns no DOM and does not inherit Avalonia's control
/// tree; the frontend chooses and renders the corresponding component.
/// </summary>
public abstract class RunicView<TViewModel> : IRunicView where TViewModel : class
{
    private TViewModel? _dataContext;

    /// <summary>Creates a view whose ViewModel is assigned later.</summary>
    protected RunicView() { }

    /// <summary>Creates a view for <paramref name="dataContext"/>.</summary>
    protected RunicView(TViewModel dataContext) => _dataContext = dataContext;

    /// <summary>The typed ViewModel presented by this view.</summary>
    public virtual TViewModel? DataContext { get => _dataContext; set => _dataContext = value; }

    object? IRunicView.DataContext
    {
        get => DataContext;
        set => DataContext = value is null ? null : value as TViewModel
            ?? throw new ArgumentException($"Expected {typeof(TViewModel).FullName}.", nameof(value));
    }
}

/// <summary>
/// An application Window that any host can open. <see cref="RunicWindow{TViewModel}"/> implements it.
/// </summary>
/// <remarks>Only <see cref="RunicWindow{TViewModel}"/> can implement this interface.</remarks>
public interface IRunicWindow : IRunicView, IAsyncDisposable
{
    /// <summary>The host adapter of the open window, such as <c>DesktopBridgeWindow&lt;TViewModel&gt;</c>.</summary>
    /// <exception cref="InvalidOperationException">The window has not opened.</exception>
    IBridgeWindow Host { get; }

    /// <inheritdoc cref="IBridgeWindow.WaitForCloseAsync"/>
    ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="IBridgeWindow.CloseAsync"/>
    ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="RunicWindow{TViewModel}.RequestCloseAsync"/>
    ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default);

    /// <inheritdoc cref="RunicWindow{TViewModel}.Services"/>
    IServiceProvider Services { get; }

    internal ValueTask OpenAsync(IRunicWindowHost host, RunicWindowOptions options, object? parameter, CancellationToken cancellationToken);

    internal RunicWindowValidationResult Validate(IRunicWindowHost host, RunicWindowOptions options);
}

/// <summary>
/// The native window and root typed context for one Bridge session. Declare one partial subclass per
/// Window; the same declaration opens on every host and with either MVVM library:
/// <code>
/// public sealed partial class MainWindow : RunicWindow&lt;MainViewModel&gt;;
/// </code>
/// Open it with <c>await host.OpenWindowAsync&lt;MainWindow&gt;()</c>, wait with
/// <see cref="WaitForCloseAsync"/>, and release it with <c>await using</c> or <see cref="DisposeAsync"/>.
/// </summary>
/// <remarks>
/// <see cref="RunicWindowHostExtensions.OpenWindowAsync{TWindow}"/> creates the Window, resolves its
/// ViewModel in a new DI scope, assigns it to <see cref="RunicView{TViewModel}.DataContext"/>, and opens the
/// window on the host. The generated Bridge and TypeScript client are named after the Window class.
/// </remarks>
public abstract class RunicWindow<TViewModel> : RunicView<TViewModel>, IRunicWindow where TViewModel : class
{
    private IBridgeWindow? _host;
    private IServiceProvider? _services;
    private int _opened;
    // The window's session and root Bridge, recorded when the host attaches it.
    private WindowContentSession? _content;
    private IDisposable? _bridge;
    // RequestCloseAsync: the host's own close request, if it has one, else one shared decision at a time, cancelled
    // when the application closes the window without asking.
    private Func<CancellationToken, ValueTask<bool>>? _hostCloseRequest;
    private readonly object _closeGate = new();
    private Task<bool>? _closeRequest;
    private readonly CancellationTokenSource _closeWithoutAsking = new();
    private static readonly NavigationRequestOptions CloseClearOptions = new() { Admission = NavigationAdmission.Supersede };

    /// <summary>Creates a window whose ViewModel is assigned when it opens.</summary>
    protected RunicWindow() { }

    /// <summary>The window's scoped root ViewModel.</summary>
    /// <exception cref="InvalidOperationException">The window has not opened.</exception>
    public TViewModel ViewModel => DataContext ?? throw NotOpen();

    /// <summary>
    /// The services of the window's dependency injection scope, which resolved <see cref="ViewModel"/>. Scoped services
    /// resolved here are the ones the window's ViewModels received.
    /// </summary>
    /// <remarks>
    /// The window's host disposes the scope when the window is released; resolving from it after that throws
    /// <see cref="ObjectDisposedException"/>. A test reads the scoped services of a window it opened:
    /// <code>
    /// await using var window = await host.OpenWindowAsync&lt;MainWindow&gt;();
    /// var documents = window.Services.GetRequiredService&lt;DocumentStore&gt;();
    /// </code>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The window has not started opening.</exception>
    public IServiceProvider Services => Volatile.Read(ref _services) ?? throw NotOpen();

    /// <summary>
    /// Gets whether a close the user starts, with the title-bar close button, Alt+F4 or the window manager, first asks
    /// <see cref="CanCloseAsync"/>, so the window can stay open. The default is <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set it in the Window's constructor; the host reads it when the window opens:
    /// <code>
    /// public sealed partial class EditorWindow : RunicWindow&lt;EditorViewModel&gt;
    /// {
    ///     public EditorWindow() =&gt; GuardClose = true;
    /// }
    /// </code>
    /// The default <see cref="CanCloseAsync"/> asks the departure guards of the navigation regions the Window
    /// presents, as WPF's <c>NavigationHost.GuardWindowClose</c> does.
    /// </para>
    /// <para>
    /// Runic Desktop's embedded window asks it; the window closes when it returns <see langword="true"/>. A
    /// presentation that cannot keep its window open, CS-WebUI or a browser, opens unguarded and logs
    /// <c>WindowCloseGuardUnavailable</c> (event 1053). <see cref="RequestCloseAsync"/> asks it on every host.
    /// <see cref="CloseAsync"/>, <see cref="DisposeAsync"/> and a host shutdown (Ctrl+C, SIGTERM) close without
    /// asking: they are the application's own close.
    /// </para>
    /// </remarks>
    public bool GuardClose { get; init; }

    /// <summary>
    /// Asks to close the window as the user's close button does, like <c>Window.Close()</c> in WPF and Avalonia, and
    /// returns whether it closed. With <see cref="GuardClose"/> it first asks <see cref="CanCloseAsync"/>, and returns
    /// <see langword="false"/> with the window open when that does; without it the window closes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request while another request or a user close is being decided waits for the same decision. When the
    /// window may close, its visible window closes at once, and accepted operations finish before
    /// <see cref="DisposeAsync"/> releases its scope, as after a user close. On Runic Desktop's embedded window the
    /// request is the native close request, so a <c>DesktopWindowOptions.ConfirmCloseAsync</c> of the host is asked
    /// too. On CS-WebUI and in a browser, where the user's close cannot be guarded, this request still is.
    /// </para>
    /// <para>
    /// Use <see cref="CloseAsync"/> to close without asking, for example after the application has already decided.
    /// A closed window returns <see langword="true"/>.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Stops this caller's wait; the decision continues for other requests.</param>
    /// <returns><see langword="true"/> when the window closed; <see langword="false"/> when it stays open.</returns>
    /// <exception cref="InvalidOperationException">The window has not opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public ValueTask<bool> RequestCloseAsync(CancellationToken cancellationToken = default)
    {
        var host = Host;
        if (Volatile.Read(ref _hostCloseRequest) is { } hostRequest) return hostRequest(cancellationToken);
        Task<bool> pending;
        lock (_closeGate)
        {
            if (_closeRequest is null || _closeRequest.IsCompleted)
                _closeRequest = Task.Run(() => RequestCloseCoreAsync(host));
            pending = _closeRequest;
        }
        return new(pending.WaitAsync(cancellationToken));
    }

    /// <summary>
    /// Decides whether a close the user started may proceed, when <see cref="GuardClose"/> is <see langword="true"/>.
    /// Return <see langword="false"/> to keep the window open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default clears each navigation region the Window's Bridge presents (each <c>NavigationRegion&lt;T&gt;</c>
    /// property of its ViewModel), in declaration order, skipping empty ones. A clear asks the departure guard of every
    /// entry and of the child regions they own, as any other navigation would. The window closes when every clear
    /// commits; a guard that rejects or fails, a cancelled or superseded clear keeps it open with its entries, and the
    /// regions after it are not asked. A guard can confirm through a dialog region of the same window, or through a
    /// ReactiveUI interaction that a web View of this window answers: the close is the window's own departure, so the
    /// one presentation in the window that has mounted a handler for the interaction is asked. Override it to add
    /// checks of your own, and call the base method to keep the navigation guards.
    /// </para>
    /// <para>
    /// It starts in a turn of the window's model context, as a command body does, and one decision runs at a time:
    /// another close request while it runs waits for the same decision. It may take as long as a prompt needs.
    /// <paramref name="cancellationToken"/> is cancelled when the window closes without asking, for example with
    /// <see cref="CloseAsync"/>. An exception keeps the window open and is logged as <c>WindowCloseGuardFailed</c>
    /// (event 1052).
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancelled when the window closes without asking or the decision is abandoned.</param>
    /// <returns><see langword="true"/> to let the window close.</returns>
    protected virtual async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        foreach (var region in (_bridge as IBridgeRegionSlots)?.NavigationRegions() ?? [])
        {
            if (region.CurrentEntry is null && !region.CanGoBack) continue;
            // Stays in the model turn, like a command body. Supersede: a close is never refused because the region is
            // busy, also when it rejects requests while busy.
            var result = await region.ClearAsync(CloseClearOptions, cancellationToken).ConfigureAwait(true);
            // A closed navigator has nothing left to guard.
            if (result is NavigationResult<object>.Committed or NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed })
                continue;
            return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public IBridgeWindow Host => Volatile.Read(ref _host) ?? throw NotOpen();

    /// <inheritdoc/>
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        Host.WaitForCloseAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<RunicWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var host = Host;
        CloseWithoutAsking();
        return host.CloseAsync(timeout, cancellationToken);
    }

    /// <summary>Closes the window immediately and waits until its resources are released. Does nothing before it opens.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _host) is { } host)
        {
            CloseWithoutAsking();
            await host.DisposeAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    // The application's own close abandons a pending RequestCloseAsync decision.
    private void CloseWithoutAsking()
    {
        try { _closeWithoutAsking.Cancel(); }
        catch (AggregateException error)
        {
            ViewsLog.WindowCloseGuardFailed(Volatile.Read(ref _content)?.Logger ?? TraceFallbackLogger.Instance, error,
                GetType().Name, error.GetType().FullName ?? error.GetType().Name);
        }
    }

    // RequestCloseAsync on a host without its own close request: ask, then close the visible window as a user close
    // does. The decision also ends when the window closes some other way.
    private async Task<bool> RequestCloseCoreAsync(IBridgeWindow host)
    {
        if (GuardClose)
        {
            var decision = CancellationTokenSource.CreateLinkedTokenSource(_closeWithoutAsking.Token);
            _ = CancelWhenClosedAsync(host, decision);
            try
            {
                // Only a close without asking cancels the decision: the window is closed or closing then.
                if (!await AskCanCloseAsync(decision.Token).ConfigureAwait(false)) return decision.IsCancellationRequested;
            }
            finally
            {
                // Ends the close watch; the source is left to the collector because the watch may still cancel it.
                await decision.CancelAsync().ConfigureAwait(false);
            }
        }
        if (_closeWithoutAsking.IsCancellationRequested) return true;
        _ = await host.CloseAsync(TimeSpan.Zero).ConfigureAwait(false);
        return true;
    }

    private static async Task CancelWhenClosedAsync(IBridgeWindow host, CancellationTokenSource decision)
    {
        try
        {
            await host.WaitForCloseAsync(decision.Token).ConfigureAwait(false);
            await decision.CancelAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { await decision.CancelAsync().ConfigureAwait(false); }
    }

    async ValueTask IRunicWindow.OpenAsync(IRunicWindowHost host, RunicWindowOptions options, object? parameter,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _opened, 1) != 0)
            throw new InvalidOperationException($"{GetType().Name} has already been opened; open a new instance for another window.");
        var opened = await RunicWindowOpening.OpenAsync(host, this, options, parameter, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _host, opened);
    }

    RunicWindowValidationResult IRunicWindow.Validate(IRunicWindowHost host, RunicWindowOptions options) =>
        RunicWindowOpening.Validate<TViewModel>(host, options);

    // The window's scope, set before its ViewModel is resolved.
    internal void UseServices(IServiceProvider services) => Volatile.Write(ref _services, services);

    internal void UseHostCloseRequest(Func<CancellationToken, ValueTask<bool>> requestClose) =>
        Volatile.Write(ref _hostCloseRequest, requestClose);

    internal void AttachedBridge(WindowContentSession content, IDisposable bridge)
    {
        Volatile.Write(ref _content, content);
        Volatile.Write(ref _bridge, bridge);
    }

    // The host's close guard: CanCloseAsync in a model turn. It never throws, so a host keeps the window open on any
    // failure without logging it again.
    internal async ValueTask<bool> AskCanCloseAsync(CancellationToken cancellationToken)
    {
        var content = Volatile.Read(ref _content);
        // The window starts this departure: a guard's interaction may ask the page's mounted web View handler.
        using var scope = content is null ? null : RunicInteractionInvocation.EnterWindowClose(content, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content?.ModelContext is { IsExecuting: false } context)
            {
                var decision = await context.InvokeAsync(() => CanCloseAsync(cancellationToken).AsTask(), cancellationToken)
                    .ConfigureAwait(false);
                return await decision.ConfigureAwait(false);
            }
            return await CanCloseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error)
        {
            ViewsLog.WindowCloseGuardFailed(content?.Logger ?? TraceFallbackLogger.Instance, error, GetType().Name,
                error.GetType().FullName ?? error.GetType().Name);
            return false;
        }
    }

    private InvalidOperationException NotOpen() =>
        new($"{GetType().Name} has not opened. Open it with OpenWindowAsync<{GetType().Name}>().");
}

/// <summary>Selects a named .NET and web View for a content property.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public sealed class RunicViewContractAttribute(string contract) : Attribute
{
    /// <summary>The contract name shared by the .NET and web View.</summary>
    public string Contract { get; } = contract;
}

/// <summary>Leaves a public ViewModel member on .NET without exposing it to the web Bridge.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class RunicIgnoreAttribute : Attribute;

/// <summary>
/// Keeps DTO properties that System.Text.Json ignores in the Bridge contract of the ViewModels declared in the
/// annotated assembly. Without it, a property marked with <c>System.Text.Json.Serialization.JsonIgnoreAttribute</c>
/// whose condition is <c>Always</c> (including an unspecified condition), or with <c>JsonExtensionDataAttribute</c>,
/// is not part of the contract, throughout nested DTO graphs and including DTOs from assemblies that do not
/// reference Runic. Conditional JSON ignores and <c>Never</c> stay included either way. ViewModel properties use
/// <see cref="RunicIgnoreAttribute"/>.
/// </summary>
/// <remarks>Adding or removing the attribute changes the generated contract, and the frontend's types with it.</remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RunicIncludeJsonIgnoredAttribute : Attribute;

/// <summary>Resolves a fresh .NET view for a presented ViewModel.</summary>
public interface IRunicViewLocator
{
    /// <summary>Resolves the default view for <typeparamref name="TViewModel"/>.</summary>
    TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class;

    /// <summary>Resolves the view registered for <paramref name="contract"/>, or the default view when it is <see langword="null"/>.</summary>
    TView Locate<TView, TViewModel>(string? contract)
        where TView : class, IRunicView
        where TViewModel : class => contract is null
            ? Locate<TView, TViewModel>()
            : throw new InvalidOperationException($"The view locator does not support contract '{contract}'.");
}

/// <summary>
/// Resolves each presented View from the window's service provider. Generated
/// <c>AddRunicViews()</c> registrations make this the default locator and
/// register every non-Window View as transient. A View contract is already a
/// distinct View type, so the contract needs no separate lookup.
/// </summary>
public sealed class ServiceProviderViewLocator(IServiceProvider services) : IRunicViewLocator
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <inheritdoc/>
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class =>
        _services.GetService(typeof(TView)) as TView
            ?? throw new InvalidOperationException(
                $"{typeof(TView).FullName} is not registered. Call AddRunicViews() or register the View as transient.");

    /// <inheritdoc/>
    public TView Locate<TView, TViewModel>(string? contract)
        where TView : class, IRunicView
        where TViewModel : class => Locate<TView, TViewModel>();
}

/// <summary>Optional hooks for the period during which a view is presented.</summary>
public interface IRunicViewLifetime
{
    /// <summary>Called when the view starts being presented.</summary>
    void OnAttached();
    /// <summary>Called when the view stops being presented.</summary>
    void OnDetached();
}

/// <summary>Optional visual mount acknowledgement from a web component.</summary>
public interface IRunicWebMountLifetime
{
    /// <summary>Called when the web component reports that it mounted.</summary>
    void OnWebMounted();
    /// <summary>Called when the web component unmounts or its presentation ends.</summary>
    void OnWebUnmounted();
}

/// <summary>
/// Optional ownership hooks for a logical View instance created for each web
/// presentation. The presentation identifier is private transport bookkeeping;
/// application code should not manufacture or retain it.
/// </summary>
internal interface IRunicWebPresentationLifetime
{
    /// <summary>Called when the web presentation <paramref name="presentationId"/> mounted.</summary>
    void OnWebMounted(string presentationId);
    /// <summary>Called when the web presentation <paramref name="presentationId"/> ended.</summary>
    void OnWebUnmounted(string presentationId);
}
