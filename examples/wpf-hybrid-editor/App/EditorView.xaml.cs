using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Runic.Application.Views.Wpf;
using Runic.Navigation;

namespace HybridNotes.Wpf;

// Navigation owns this page's model. RunicViewHost owns just the current web session.
public partial class EditorView : UserControl
{
    private readonly IServiceProvider _services;
    private readonly IRunicModelContext _context;
    private bool _useWeb;

    public EditorView(IServiceProvider services, IRunicModelContext context)
    {
        _services = services;
        _context = context;
        InitializeComponent();
        Present();
    }

    // The web host while the web editor is selected.
    internal RunicViewHost? WebHost { get; private set; }

    private void SwitchPresentation(object sender, RoutedEventArgs e)
    {
        _useWeb = !_useWeb;
        Present();
    }

    private async void ReloadWebEditor(object sender, RoutedEventArgs e)
    {
        if (WebHost is { } host) await host.ReloadAsync();
    }

    private void Present()
    {
        Switch.Content = _useWeb ? "Use native editor" : "Use web editor";
        PresentationError.Text = "";
        if (!_useWeb)
        {
            // Replacing the host unloads it, which closes its web session; the model stays.
            WebHost = null;
            EditorHost.Content = new NativeEditorView();
            return;
        }
        // Both editors inherit this page's DataContext, the navigation entry's EditorViewModel. The native
        // editor is also the fallback when the embedded host can't open, with the same draft.
        var host = WebHost = new RunicViewHost { Services = _services, ModelContext = _context, Fallback = new NativeEditorView() };
        host.SetBinding(RunicViewHost.ModelProperty, new Binding());
        host.StateChanged += (_, _) => PresentationError.Text = host.State == RunicViewHostState.Failed
            ? $"Web editor unavailable: {host.Error?.Message} You can keep editing natively or reload the web editor."
            : "";
        EditorHost.Content = host;
    }
}
