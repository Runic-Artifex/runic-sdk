using CsWebUi;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace RunicWindowApp;

public sealed partial class WorkspaceWindow : RunicWindow<WorkspaceViewModel>, IDisposable, IAsyncDisposable
{
    private readonly CsWebUiBridgeWindow<WorkspaceViewModel> _host;

    public WorkspaceWindow(CsWebUiBridgeWindow<WorkspaceViewModel> host) : base(host.ViewModel) =>
        _host = host;

    public void SetRootFolder(string path) => _host.SetRootFolder(path);
    public void Show(string entry) => _host.Show(entry);
    public string StartServer(string entry) => _host.StartServer(entry);
    public ValueTask<CsWebUiBridgeCloseResult> CloseAsync(TimeSpan timeout) => _host.CloseAsync(timeout);
    public void Dispose() => _host.Dispose();
    public ValueTask DisposeAsync() => _host.DisposeAsync();
}

public sealed class MicrosoftViewLocator(IServiceProvider services) : IRunicViewLocator
{
    public TView Locate<TView, TViewModel>()
        where TView : class, IRunicView
        where TViewModel : class => services.GetRequiredService<TView>();
}
