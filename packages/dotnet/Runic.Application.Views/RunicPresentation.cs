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
    ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    internal ValueTask OpenAsync(IRunicWindowHost host, RunicWindowOptions options, CancellationToken cancellationToken);

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
    private int _opened;
    // The window's session and root Bridge, recorded when the host attaches it.
    private WindowContentSession? _content;
    private IDisposable? _bridge;

    /// <summary>Creates a window whose ViewModel is assigned when it opens.</summary>
    protected RunicWindow() { }

    /// <summary>The window's scoped root ViewModel.</summary>
    /// <exception cref="InvalidOperationException">The window has not opened.</exception>
    public TViewModel ViewModel => DataContext ?? throw NotOpen();

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
    /// <c>WindowCloseGuardUnavailable</c> (event 1052). <see cref="CloseAsync"/>, <see cref="DisposeAsync"/> and a host
    /// shutdown (Ctrl+C, SIGTERM) close without asking: they are the application's own close.
    /// </para>
    /// </remarks>
    public bool GuardClose { get; init; }

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
    /// regions after it are not asked. A guard can confirm through a dialog region of the same window. Override it to
    /// add checks of your own, and call the base method to keep the navigation guards.
    /// </para>
    /// <para>
    /// It starts in a turn of the window's model context, as a command body does, and one decision runs at a time:
    /// another close request while it runs waits for the same decision. It may take as long as a prompt needs.
    /// <paramref name="cancellationToken"/> is cancelled when the window closes without asking, for example with
    /// <see cref="CloseAsync"/>. An exception keeps the window open and is logged as <c>WindowCloseGuardFailed</c>
    /// (event 1051).
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancelled when the window closes without asking or the decision is abandoned.</param>
    /// <returns><see langword="true"/> to let the window close.</returns>
    protected virtual async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
#pragma warning disable RUNICNAV001 // The close guard asks the experimental navigator's guards.
        foreach (var region in (_bridge as IBridgeRegionSlots)?.NavigationRegions() ?? [])
        {
            if (region.CurrentEntry is null && !region.CanGoBack) continue;
            // Stays in the model turn, like a command body.
            var result = await region.ClearAsync(cancellationToken: cancellationToken).ConfigureAwait(true);
            // A closed navigator has nothing left to guard.
            if (result is NavigationResult<object>.Committed or NavigationResult<object>.Rejected { Reason: NavigationRejection.Closed })
                continue;
            return false;
        }
#pragma warning restore RUNICNAV001
        return true;
    }

    /// <inheritdoc/>
    public IBridgeWindow Host => Volatile.Read(ref _host) ?? throw NotOpen();

    /// <inheritdoc/>
    public ValueTask WaitForCloseAsync(CancellationToken cancellationToken = default) =>
        Host.WaitForCloseAsync(cancellationToken);

    /// <inheritdoc/>
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Host.CloseAsync(timeout, cancellationToken);

    /// <summary>Closes the window immediately and waits until its resources are released. Does nothing before it opens.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _host) is { } host) await host.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    async ValueTask IRunicWindow.OpenAsync(IRunicWindowHost host, RunicWindowOptions options, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _opened, 1) != 0)
            throw new InvalidOperationException($"{GetType().Name} has already been opened; open a new instance for another window.");
        var opened = await RunicWindowOpening.OpenAsync(host, this, options, cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _host, opened);
    }

    RunicWindowValidationResult IRunicWindow.Validate(IRunicWindowHost host, RunicWindowOptions options) =>
        RunicWindowOpening.Validate<TViewModel>(host, options);

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
/// Opts ViewModels declared in the annotated assembly into excluding DTO properties marked
/// with <c>System.Text.Json.Serialization.JsonIgnoreAttribute</c> when its condition
/// is <c>Always</c> (including an unspecified condition). This applies throughout
/// nested DTO graphs, including DTOs from assemblies that do not reference Runic.
/// ViewModel properties still use <see cref="RunicIgnoreAttribute"/>; conditional
/// JSON ignores and <c>Never</c> remain included in the Bridge contract.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class RunicBridgeJsonIgnoreAttribute : Attribute;

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
public interface IRunicWebPresentationLifetime
{
    /// <summary>Called when the web presentation <paramref name="presentationId"/> mounted.</summary>
    void OnWebMounted(string presentationId);
    /// <summary>Called when the web presentation <paramref name="presentationId"/> ended.</summary>
    void OnWebUnmounted(string presentationId);
}
