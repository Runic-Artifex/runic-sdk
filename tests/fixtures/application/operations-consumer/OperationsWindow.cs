using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;

namespace OperationsConsumer;

public sealed partial class OperationsWindow(OperationsViewModel model) : ReactiveRunicWindow<OperationsViewModel>(model);
