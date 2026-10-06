using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Application.Views.ReactiveUI;
using Runic.Desktop;

namespace FirstWindowDesktop;

public sealed partial class CounterWindow : ReactiveRunicWindow<CounterViewModel>, IBridgeWindow
{
    private readonly DesktopBridgeWindow<CounterViewModel> _host;

    public CounterWindow(DesktopBridgeWindow<CounterViewModel> host) : base(host.ViewModel) => _host = host;

    public DesktopSurface Surface => _host.Surface;
    public DesktopWindow Presentation => _host.Presentation;
    public ValueTask<BridgeWindowCloseResult> CloseAsync(TimeSpan timeout) => _host.CloseAsync(timeout);
    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
