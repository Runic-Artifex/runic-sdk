using Prism.Commands;
using Prism.Dialogs;
using Prism.Mvvm;

namespace Comparison.PrismApp.S2;

public sealed class ConfirmDialogViewModel : BindableBase, IDialogAware
{
    private string _message = "";

    public ConfirmDialogViewModel() =>
        CloseCommand = new DelegateCommand<string>(button =>
            RequestClose.Invoke(button == "OK" ? ButtonResult.OK : ButtonResult.Cancel));

    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public DelegateCommand<string> CloseCommand { get; }
    public DialogCloseListener RequestClose { get; }

    public bool CanCloseDialog() => true;
    public void OnDialogClosed() { }
    public void OnDialogOpened(IDialogParameters parameters) => Message = parameters.GetValue<string>("message");
}
