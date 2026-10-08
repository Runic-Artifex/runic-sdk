using System.IO;
using System.Windows;
using System.Windows.Controls;
using Runic.Application.Views.Wpf;
using Runic.Desktop;
using Runic.Navigation;

namespace HybridNotes.Wpf;

// Navigation owns this page's model. This view owns just the current web session.
public partial class EditorView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly IRunicModelContext _context;
    private readonly SemaphoreSlim _changes = new(1);
    private DesktopHost? _desktop;
    private WpfBridgeView<EditorViewModel>? _web;
    private RunicWebView? _control;
    private CancellationTokenSource? _opening;
    private bool _useWeb;
    private bool _loaded;
    internal Task PresentationChange { get; private set; } = Task.CompletedTask;
    internal WpfBridgeView<EditorViewModel>? WebBinding => _web;
    internal Exception? LastPresentationError { get; private set; }

    public EditorView(IServiceProvider services, IRunicModelContext context)
    {
        _services = services;
        _context = context;
        InitializeComponent();
        Loaded += async (_, _) => { _loaded = true; await (PresentationChange = PresentAsync()); };
        Unloaded += async (_, _) =>
        {
            _loaded = false;
            _opening?.Cancel();
            await (PresentationChange = PresentAsync());
        };
    }

    private async void SwitchPresentation(object sender, RoutedEventArgs e)
    {
        _useWeb = !_useWeb;
        await (PresentationChange = PresentAsync());
    }

    private async void ReloadWebEditor(object sender, RoutedEventArgs e)
    {
        if (_useWeb) await (PresentationChange = PresentAsync());
    }

    private async Task PresentAsync()
    {
        await _changes.WaitAsync();
        try
        {
            // Tear down before removing HwndHost; never dispose the borrowed model,
            // provider, model context or navigation entry on a presentation change.
            await ReleasePresentationAsync();
            if (!_loaded || DataContext is not EditorViewModel model) return;
            LastPresentationError = null;
            PresentationError.Text = "";
            Switch.Content = _useWeb ? "Use native editor" : "Use web editor";
            if (!_useWeb)
            {
                EditorHost.Children.Add(new NativeEditorView { DataContext = model });
                return;
            }
            using var opening = new CancellationTokenSource();
            _opening = opening;
            var control = _control = new RunicWebView();
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ChildLoaded(object sender, RoutedEventArgs e) => loaded.TrySetResult();
            control.Loaded += ChildLoaded;
            EditorHost.Children.Add(control);
            try
            {
                // Insertion doesn't synchronously create the child HWND or run Loaded.
                // Opening on DispatcherPriority.Normal can otherwise run ahead of layout.
                if (!control.IsLoaded) await loaded.Task.WaitAsync(opening.Token);
                opening.Token.ThrowIfCancellationRequested();
                _desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = control.CreateWindowHostFactory() }, opening.Token);
                _web = await _services.CreateWpfViewAsync(_desktop,
                    new DesktopSurfaceOptions
                    {
                        Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html"),
                    }, model, modelContext: _context, cancellationToken: opening.Token);
                await _web.OpenAsync(cancellationToken: opening.Token);
            }
            finally
            {
                control.Loaded -= ChildLoaded;
                _opening = null;
            }
        }
        catch (OperationCanceledException) when (!_loaded)
        {
            await ReleasePresentationAsync();
        }
        catch (Exception exception)
        {
            // Host failures are presentation failures; the same draft remains available natively.
            LastPresentationError = exception;
            await ReleasePresentationAsync();
            if (_loaded && DataContext is EditorViewModel model)
            {
                _useWeb = false;
                Switch.Content = "Use web editor";
                EditorHost.Children.Add(new NativeEditorView { DataContext = model });
                PresentationError.Text = $"Web editor unavailable: {exception.Message} You can keep editing natively or try opening the web editor again.";
            }
        }
        finally { _changes.Release(); }
    }

    private async Task ReleasePresentationAsync()
    {
        if (_web is not null) { await _web.DisposeAsync(); _web = null; }
        if (_desktop is not null) { await _desktop.DisposeAsync(); _desktop = null; }
        EditorHost.Children.Clear();
        _control?.Dispose();
        _control = null;
    }
}
