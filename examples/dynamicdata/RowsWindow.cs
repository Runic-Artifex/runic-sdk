using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Desktop;

namespace DynamicDataExample;

public sealed partial class RowsWindow(DesktopBridgeWindow<RowsViewModel> host)
    : RunicWindow<RowsViewModel>(host.ViewModel), IAsyncDisposable
{
    public DesktopWindow Presentation => host.Presentation;
    public ValueTask DisposeAsync() => host.DisposeAsync();
}
