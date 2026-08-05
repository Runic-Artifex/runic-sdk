using System;
using System.Threading;
using System.Threading.Tasks;
using CsWebUi;
using RunicToolkit.Hosting.WebUi;
using RunicToolkit.MVVM;

namespace RunicToolkit.Hosting.CsWebUi.Mvvm;

/// <summary>Describes one generated-contract MVVM frontend application.</summary>
public sealed record MvvmFrontendApplicationOptions<TModel>
{
    /// <summary>Creates complete native MVVM application options.</summary>
    public MvvmFrontendApplicationOptions(
        IFrontendAssetProvider assets,
        CsWebUiAdapterOptions adapter,
        BrowserHostOptions browserHost,
        BrowserWindowOptions browserWindow,
        MvvmContract contract,
        Func<CancellationToken, ValueTask<TModel>> activateModel,
        Func<TModel, IMvvmBindingAdapter> createAdapter,
        MvvmLimits? limits = null,
        TimeSpan? sessionCloseTimeout = null,
        TimeSpan? windowCloseTimeout = null)
    {
        Assets = assets ?? throw new ArgumentNullException(nameof(assets));
        Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        BrowserHost = browserHost ?? throw new ArgumentNullException(nameof(browserHost));
        BrowserWindow = browserWindow ?? throw new ArgumentNullException(nameof(browserWindow));
        if (string.IsNullOrEmpty(contract.Value))
        {
            throw new ArgumentException("An MVVM contract is required.", nameof(contract));
        }

        Contract = contract;
        ActivateModel = activateModel ?? throw new ArgumentNullException(nameof(activateModel));
        CreateAdapter = createAdapter ?? throw new ArgumentNullException(nameof(createAdapter));
        Limits = limits ?? MvvmLimits.Default;
        Limits.Validate();
        SessionCloseTimeout = sessionCloseTimeout ?? TimeSpan.FromSeconds(5);
        WindowCloseTimeout = windowCloseTimeout ?? TimeSpan.FromSeconds(5);
        _ = new WebUiModeOptions(
            BrowserHost,
            BrowserWindow,
            SessionCloseTimeout,
            WindowCloseTimeout);
    }

    /// <summary>Gets the immutable application assets.</summary>
    public IFrontendAssetProvider Assets { get; }

    /// <summary>Gets the native adapter policy.</summary>
    public CsWebUiAdapterOptions Adapter { get; }

    /// <summary>Gets the native browser-host identity.</summary>
    public BrowserHostOptions BrowserHost { get; }

    /// <summary>Gets the native window configuration.</summary>
    public BrowserWindowOptions BrowserWindow { get; }

    /// <summary>Gets the generated MVVM contract.</summary>
    public MvvmContract Contract { get; }

    /// <summary>Gets the reflection-free ViewModel activator.</summary>
    public Func<CancellationToken, ValueTask<TModel>> ActivateModel { get; }

    /// <summary>Gets the generated closed-adapter factory.</summary>
    public Func<TModel, IMvvmBindingAdapter> CreateAdapter { get; }

    /// <summary>Gets bounded protocol/session policy.</summary>
    public MvvmLimits Limits { get; }

    /// <summary>Gets the root-session close bound.</summary>
    public TimeSpan SessionCloseTimeout { get; }

    /// <summary>Gets the native-window close bound.</summary>
    public TimeSpan WindowCloseTimeout { get; }
}

/// <summary>
/// Owns the generated-contract session factory registered with one native
/// framework frontend.
/// </summary>
public sealed class MvvmFrontendApplication : IAsyncDisposable
{
    private MvvmFrontendRoot? _root;

    internal MvvmFrontendApplication(MvvmFrontendRoot root)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        MvvmFrontendRoot? root = Interlocked.Exchange(ref _root, null);
        if (root is not null)
        {
            await root.DisposeAsync().ConfigureAwait(false);
        }
    }
}

internal sealed class MvvmFrontendRoot : IRootSessionFactory, IAsyncDisposable
{
    private readonly MvvmContract _contract;
    private readonly IMvvmSessionFactory _sessions;
    private WebUiWindow? _window;

    internal MvvmFrontendRoot(
        MvvmContract contract,
        IMvvmSessionFactory sessions)
    {
        _contract = contract;
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    internal void AttachWindow(WebUiWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Interlocked.CompareExchange(ref _window, window, null) is not null)
        {
            throw new InvalidOperationException(
                "An MVVM frontend application supports one native root window.");
        }
    }

    public async ValueTask<IRootSession> OpenAsync(
        CancellationToken cancellationToken)
    {
        WebUiWindow window = _window ??
            throw new InvalidOperationException(
                "CsWebUi must create the native window before opening the MVVM root session.");
        IMvvmSession session = await _sessions
            .OpenAsync(_contract, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return new RootSession(CsWebUiMvvmBridge.Attach(window, session));
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => _sessions.DisposeAsync();

    private sealed class RootSession : IRootSession
    {
        private CsWebUiMvvmBridge? _bridge;

        internal RootSession(CsWebUiMvvmBridge bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        }

        public ValueTask ActivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DeactivateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            CsWebUiMvvmBridge? bridge = Interlocked.Exchange(ref _bridge, null);
            if (bridge is not null)
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
