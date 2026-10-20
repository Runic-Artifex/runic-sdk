using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using Runic.Application.Views.Desktop;
using Runic.Desktop;
using Runic.Navigation;
using Runic.Navigation.Wpf;

namespace Runic.Application.Views.Wpf;

/// <summary>Presents an existing model through its generated Runic web View, inside the WPF visual tree.</summary>
/// <remarks>
/// <para>
/// The host opens the web presentation when it is loaded with a <see cref="ViewModel"/> and <see cref="Services"/>, and
/// closes it when it is unloaded or the ViewModel is cleared. A change of model, services or content closes the current
/// presentation and opens a fresh one. Changes run one at a time; a newer change cancels an open in progress. Closing
/// releases the Bridge session first, draining its accepted operations, then the Desktop host, then the native child.
/// </para>
/// <para>
/// When the presentation can't open (no WebView2 Runtime, no registered Bridge, missing frontend), the host shows
/// <see cref="Fallback"/> with the same model as its <see cref="FrameworkElement.DataContext"/>, sets
/// <see cref="State"/> to <see cref="RunicViewHostState.Failed"/> and keeps the failure in <see cref="Error"/>.
/// </para>
/// <para>
/// The host borrows the model, services and model context: it never creates or disposes a ViewModel, a DI scope or a
/// navigation entry. It manages its own <see cref="Decorator.Child"/>: setting <c>Child</c> is not supported; set the
/// fallback as its content instead. Derive from the host only to style it.
/// </para>
/// <para>
/// Screen readers see the web View as a pane that contains the page's document. Set
/// <see cref="AutomationProperties.NameProperty">AutomationProperties.Name</see> (and <c>HelpText</c>) on the host to
/// name that pane; the host passes them to its <see cref="RunicWebView"/>.
/// </para>
/// </remarks>
[ContentProperty(nameof(Fallback))]
public class RunicViewHost : Decorator
{
    /// <summary>Identifies the <see cref="ViewModel"/> dependency property.</summary>
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(nameof(ViewModel), typeof(object),
        typeof(RunicViewHost), new PropertyMetadata(null, Reopen));

    /// <summary>Identifies the Services attached dependency property, which descendants inherit.</summary>
    /// <remarks>
    /// It is <see cref="ViewHost.ServicesProperty"/> of <c>Runic.Navigation.Wpf</c>: one provider set on a window
    /// with <see cref="SetServices"/> or <see cref="ViewHost.SetServices"/> reaches every <see cref="RunicViewHost"/>
    /// and navigation <see cref="ViewHost"/> in it.
    /// </remarks>
    public static readonly DependencyProperty ServicesProperty = ViewHost.ServicesProperty.AddOwner(typeof(RunicViewHost),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, Reopen));

    /// <summary>Identifies the <see cref="ModelContext"/> dependency property.</summary>
    public static readonly DependencyProperty ModelContextProperty = DependencyProperty.Register(nameof(ModelContext),
        typeof(IRunicModelContext), typeof(RunicViewHost), new PropertyMetadata(null, Reopen));

    // The runtime configuration option that the Runic build sets for RunicApplicationFrontendWebRoot; the same as
    // RunicWindowOptions in Runic.Application.Views.
    private static string DefaultWebRoot =>
        AppContext.GetData("Runic.Application.FrontendWebRoot") is string { Length: > 0 } webRoot ? webRoot : "www";

    /// <summary>Identifies the <see cref="ContentRoot"/> dependency property.</summary>
    public static readonly DependencyProperty ContentRootProperty = DependencyProperty.Register(nameof(ContentRoot), typeof(string),
        typeof(RunicViewHost), new PropertyMetadata(null, Reopen));

    /// <summary>Identifies the <see cref="StartPage"/> dependency property.</summary>
    public static readonly DependencyProperty StartPageProperty = DependencyProperty.Register(nameof(StartPage), typeof(string),
        typeof(RunicViewHost), new PropertyMetadata(null, Reopen));

    /// <summary>Identifies the <see cref="SurfaceOptions"/> dependency property.</summary>
    public static readonly DependencyProperty SurfaceOptionsProperty = DependencyProperty.Register(nameof(SurfaceOptions),
        typeof(DesktopSurfaceOptions), typeof(RunicViewHost), new PropertyMetadata(null, Reopen));

    /// <summary>Identifies the <see cref="Fallback"/> dependency property.</summary>
    public static readonly DependencyProperty FallbackProperty = DependencyProperty.Register(nameof(Fallback), typeof(object),
        typeof(RunicViewHost), new PropertyMetadata(null, static (d, _) => ((RunicViewHost)d).OnFallbackChanged()));

    private static readonly DependencyPropertyKey StatePropertyKey = DependencyProperty.RegisterReadOnly(nameof(State),
        typeof(RunicViewHostState), typeof(RunicViewHost), new PropertyMetadata(RunicViewHostState.Closed,
            static (d, _) => ((RunicViewHost)d).StateChanged?.Invoke(d, EventArgs.Empty)));

    /// <summary>Identifies the <see cref="State"/> dependency property.</summary>
    public static readonly DependencyProperty StateProperty = StatePropertyKey.DependencyProperty;

    private static readonly DependencyPropertyKey ErrorPropertyKey = DependencyProperty.RegisterReadOnly(nameof(Error),
        typeof(Exception), typeof(RunicViewHost), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="Error"/> dependency property.</summary>
    public static readonly DependencyProperty ErrorProperty = ErrorPropertyKey.DependencyProperty;

    private readonly PresentationLifecycle _lifecycle = new();
    private IWpfBridgeView? _view;
    private ContentPresenter? _fallback;

    /// <summary>Creates a host.</summary>
    public RunicViewHost()
    {
        _lifecycle.Changed += OnLifecycleChanged;
        // WPF may raise Loaded again without Unloaded; an open or opening presentation stays.
        Loaded += (_, _) =>
        {
            if (_lifecycle.State is not (RunicViewHostState.Open or RunicViewHostState.Opening)) Request();
        };
        Unloaded += (_, _) => Request();
    }

    /// <summary>Raised on the UI thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Gets or sets the existing ViewModel to present, usually <c>{Binding}</c>.</summary>
    /// <remarks>A generated Bridge must be registered for its class or a base class, through <c>AddRunicViews()</c>.</remarks>
    public object? ViewModel
    {
        get => GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    /// <summary>Gets or sets the application's service provider, with the generated Bridges registered.</summary>
    /// <remarks>
    /// Inherited: set it once on the window with <see cref="SetServices"/>, or on the host from a View that received
    /// its provider. The host never disposes it; keep it alive until the host has closed.
    /// </remarks>
    public IServiceProvider? Services
    {
        get => (IServiceProvider?)GetValue(ServicesProperty);
        set => SetValue(ServicesProperty, value);
    }

    /// <summary>Gets or sets the model context the Bridge uses, or <see langword="null"/> for the one registered in <see cref="Services"/>.</summary>
    /// <remarks>Supply the dispatcher context the navigator uses when navigation and the web View present the same model.</remarks>
    public IRunicModelContext? ModelContext
    {
        get => (IRunicModelContext?)GetValue(ModelContextProperty);
        set => SetValue(ModelContextProperty, value);
    }

    /// <summary>
    /// Gets or sets the built frontend's directory, relative to the application's base directory.
    /// <see langword="null"/>, the default, is the build's web root: <c>www</c>, or the directory that the build
    /// property <c>RunicApplicationFrontendWebRoot</c> names.
    /// </summary>
    public string? ContentRoot
    {
        get => (string?)GetValue(ContentRootProperty);
        set => SetValue(ContentRootProperty, value);
    }

    /// <summary>
    /// Gets or sets the page to open, relative to <see cref="ContentRoot"/>, or <see langword="null"/> for
    /// <c>index.html</c>; the same as <c>RunicWindowOptions.StartPage</c> of a Runic Window.
    /// </summary>
    public string? StartPage
    {
        get => (string?)GetValue(StartPageProperty);
        set => SetValue(StartPageProperty, value);
    }

    /// <summary>Gets or sets the complete surface options; when set, <see cref="ContentRoot"/> and <see cref="StartPage"/> are ignored.</summary>
    public DesktopSurfaceOptions? SurfaceOptions
    {
        get => (DesktopSurfaceOptions?)GetValue(SurfaceOptionsProperty);
        set => SetValue(SurfaceOptionsProperty, value);
    }

    /// <summary>Gets or sets the content shown when the web presentation fails to open, such as the native View for the model.</summary>
    public object? Fallback
    {
        get => GetValue(FallbackProperty);
        set => SetValue(FallbackProperty, value);
    }

    /// <summary>Gets the presentation state.</summary>
    public RunicViewHostState State => (RunicViewHostState)GetValue(StateProperty);

    /// <summary>Gets why the last open failed, while <see cref="State"/> is <see cref="RunicViewHostState.Failed"/>.</summary>
    public Exception? Error => (Exception?)GetValue(ErrorProperty);

    /// <summary>Gets the Desktop surface of the open presentation, or <see langword="null"/>.</summary>
    public DesktopSurface? Surface => _view?.Surface;

    /// <summary>Gets the provider set on <paramref name="element"/> for the hosts it contains.</summary>
    public static IServiceProvider? GetServices(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IServiceProvider?)element.GetValue(ServicesProperty);
    }

    /// <summary>Sets the provider for every host <paramref name="element"/> contains, such as a window.</summary>
    public static void SetServices(DependencyObject element, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ServicesProperty, services);
    }

    /// <summary>Closes the presentation and opens a fresh session for the same model, and returns when it has settled.</summary>
    /// <param name="cancellationToken">Stops the wait; the reload goes on, as <see cref="WaitForPresentationAsync"/> describes.</param>
    /// <remarks>Also retries after a failure. The model, its state and its navigation entry stay.</remarks>
    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Request();
        return WaitForPresentationAsync(cancellationToken);
    }

    /// <summary>Returns when no presentation change is pending: the presentation is open, failed, or closed and released.</summary>
    /// <param name="cancellationToken">Stops the wait without cancelling the presentation change.</param>
    public async Task WaitForPresentationAsync(CancellationToken cancellationToken = default)
    {
        Task settled;
        do
        {
            settled = _lifecycle.Settled;
            await settled.WaitAsync(cancellationToken);
        }
        while (!ReferenceEquals(settled, _lifecycle.Settled));
    }

    private static void Reopen(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        // Services inherits down the tree, so WPF also calls this for descendants that are not hosts.
        if (target is RunicViewHost { IsLoaded: true } host) host.Request();
    }

    private void Request() => _ = _lifecycle.RequestAsync(() => IsLoaded && ViewModel is not null, OpenAsync, ShowFallback);

    private async Task OpenAsync(PresentationLifecycle lifecycle, CancellationToken cancellationToken)
    {
        var model = ViewModel!;
        // Only an open reads Services, which an application may dispose before the host unloads. This open, its
        // fallback and the releases until the next open log through the factory lent from it; once that is
        // disposed, they go to Trace.
        var loggerFactory = LentLoggerFactory.From(Services);
        lifecycle.Logger = WpfViewsLog.For(loggerFactory);
        var services = Services ?? throw new InvalidOperationException(
            "RunicViewHost has no Services. Set RunicViewHost.Services, on the host or its window, to the provider with AddRunicViews().");
        var surfaceOptions = SurfaceOptions ?? new DesktopSurfaceOptions
        {
            Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, ContentRoot ?? DefaultWebRoot), StartPage),
        };
        var control = new RunicWebView();
        // The pane that UI Automation clients such as Narrator announce takes the host's accessible name.
        control.SetBinding(AutomationProperties.NameProperty, new Binding { Source = this, Path = new PropertyPath(AutomationProperties.NameProperty) });
        control.SetBinding(AutomationProperties.HelpTextProperty, new Binding { Source = this, Path = new PropertyPath(AutomationProperties.HelpTextProperty) });
        lifecycle.Own(() =>
        {
            // Last: the session and host have released the presentation before the HwndHost leaves the tree.
            if (ReferenceEquals(Child, control)) Child = null;
            control.Dispose();
        });
        var factory = control.CreateWindowHostFactory();
        if (!factory.IsSupported)
            throw new PlatformNotSupportedException("The Microsoft Edge WebView2 Runtime is not available on this machine.");
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult();
        control.Loaded += OnLoaded;
        try
        {
            Child = control;
            // Insertion creates the child HWND and raises Loaded later, after layout.
            if (!control.IsLoaded) await loaded.Task.WaitAsync(cancellationToken);
        }
        finally { control.Loaded -= OnLoaded; }
        var desktop = await DesktopHost.StartAsync(new DesktopHostOptions
        {
            WindowHostFactory = factory,
            LoggerFactory = loggerFactory,
        }, cancellationToken);
        lifecycle.Own(desktop.DisposeAsync);
        var view = await services.CreateWpfViewForModelAsync(desktop, surfaceOptions, model, ModelContext, cancellationToken);
        lifecycle.Own(() =>
        {
            _view = null;
            return view.DisposeAsync();
        });
        _view = view;
        await view.OpenAsync(cancellationToken);
    }

    private void ShowFallback(PresentationLifecycle lifecycle, Exception error)
    {
        WpfViewsLog.FallbackShown(lifecycle.Logger, error, WpfViewsLog.ErrorType(error));
        if (!IsLoaded) return;
        var presenter = _fallback = new ContentPresenter { Content = Fallback };
        Child = presenter;
        lifecycle.Own(() =>
        {
            // Disconnects a fallback element so a later failure can show it again.
            presenter.Content = null;
            if (ReferenceEquals(Child, presenter)) Child = null;
            if (ReferenceEquals(_fallback, presenter)) _fallback = null;
        });
    }

    private void OnFallbackChanged()
    {
        if (_fallback is not null) _fallback.Content = Fallback;
    }

    private void OnLifecycleChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnLifecycleChanged);
            return;
        }
        SetValue(ErrorPropertyKey, _lifecycle.Error);
        SetValue(StatePropertyKey, _lifecycle.State);
    }
}
