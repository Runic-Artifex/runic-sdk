using Runic.Application.Views.CsWebUi;

namespace FirstWindow;

public sealed partial class CounterWindow(CsWebUiBridgeWindow<CounterViewModel> host)
    : CsWebUiWindow<CounterViewModel>(host);
