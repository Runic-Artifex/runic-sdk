using CsWebUi;
using Runic.Application.Views.CsWebUi;
using Runic.Application.Views.ReactiveUI;

namespace NotesReactiveViews;

public sealed partial class NotesWindow : ReactiveRunicWindow<ShellViewModel>, IDisposable, IAsyncDisposable
{
    private readonly CsWebUiBridgeWindow<ShellViewModel> _host;
    internal NotesWindow(CsWebUiBridgeWindow<ShellViewModel> host) : base(host.ViewModel) => _host = host;

    public WebUiWindow NativeWindow => _host.NativeWindow;
    public void SetRootFolder(string path) => _host.SetRootFolder(path);
    public void SetPort(ushort port) => _host.SetPort(port);
    public void SetSize(uint width, uint height) => _host.SetSize(width, height);
    public void Show(string entry) => _host.Show(entry);
    public string StartServer(string entry) => _host.StartServer(entry);
    public ValueTask<CsWebUiBridgeCloseResult> CloseAsync(TimeSpan timeout) => _host.CloseAsync(timeout);
    public void Dispose() => _host.Dispose();
    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
