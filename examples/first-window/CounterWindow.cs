using Runic.Application.Views;
using Runic.Application.Views.CsWebUi.DependencyInjection;

namespace FirstWindow;

public sealed partial class CounterWindow : RunicWindow<CounterViewModel>, IDisposable, IAsyncDisposable
{
    private readonly CsWebUiBridgeWindow<CounterViewModel> _host;
    public CounterWindow(CsWebUiBridgeWindow<CounterViewModel> host) : base(host.ViewModel) => _host = host;
    public void SetRootFolder(string path) => _host.SetRootFolder(path);
    public void Show(string entry) => _host.Show(entry);
    public string StartServer(string entry) => _host.StartServer(entry);
    public ValueTask<CsWebUiBridgeCloseResult> CloseAsync(TimeSpan timeout) => _host.CloseAsync(timeout);
    public void Dispose() => _host.Dispose();
    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
