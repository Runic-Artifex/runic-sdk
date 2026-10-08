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
    private bool _useWeb;
    private bool _loaded;
    internal Task PresentationChange { get; private set; } = Task.CompletedTask;
    internal WpfBridgeView<EditorViewModel>? WebBinding => _web;

    public EditorView(IServiceProvider services, IRunicModelContext context)
    {
        _services = services;
        _context = context;
        InitializeComponent();
        Loaded += async (_, _) => { _loaded = true; await (PresentationChange = PresentAsync()); };
        Unloaded += async (_, _) => { _loaded = false; await (PresentationChange = PresentAsync()); };
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
            if (_web is not null) { await _web.DisposeAsync(); _web = null; }
            if (_desktop is not null) { await _desktop.DisposeAsync(); _desktop = null; }
            EditorHost.Children.Clear();
            if (!_loaded || DataContext is not EditorViewModel model) return;
            Switch.Content = _useWeb ? "Use native editor" : "Use web editor";
            if (!_useWeb)
            {
                EditorHost.Children.Add(new NativeEditorView { DataContext = model });
                return;
            }
            var control = new RunicWebView();
            EditorHost.Children.Add(control);
            _desktop = await DesktopHost.StartAsync(new DesktopHostOptions { WindowHostFactory = control.CreateWindowHostFactory() });
            _web = await _services.CreateWpfViewAsync(_desktop,
                new DesktopSurfaceOptions
                {
                    Content = new DesktopContent.Directory(Path.Combine(AppContext.BaseDirectory, "www"), "index.html"),
                }, model, modelContext: _context);
            await _web.OpenAsync();
        }
        catch (Exception exception)
        {
            // Host failures are presentation failures; the same draft remains available natively.
            if (_web is not null) { await _web.DisposeAsync(); _web = null; }
            if (_desktop is not null) { await _desktop.DisposeAsync(); _desktop = null; }
            EditorHost.Children.Clear();
            if (_loaded && DataContext is EditorViewModel model)
            {
                _useWeb = false;
                Switch.Content = "Use web editor";
                EditorHost.Children.Add(new NativeEditorView { DataContext = model });
                MessageBox.Show(Window.GetWindow(this), exception.Message, "Web editor unavailable", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally { _changes.Release(); }
    }
}
