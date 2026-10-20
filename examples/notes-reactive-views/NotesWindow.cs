using CsWebUi;
using Runic.Application.Views;
using Runic.Application.Views.CsWebUi;

namespace NotesReactiveViews;

public sealed partial class NotesWindow : RunicWindow<ShellViewModel>
{
    // This example runs on CS-WebUI only: its server and development modes use the native window.
    public WebUiWindow NativeWindow => ((CsWebUiBridgeWindow<ShellViewModel>)Host).Window;
}
