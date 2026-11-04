using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;

namespace MultiWindow;

// What the "choose account" Window starts from.
public sealed record AccountRequest(string Current);

// A window-modal dialog that returns the chosen account: IRunicWindowResult completes it and closes the Window. Closing
// it otherwise, with Cancel or the title bar, returns no result.
public sealed class AccountViewModel : ReactiveObject
{
    public AccountViewModel(IRunicWindowParameter<AccountRequest> request, IRunicWindowResult<string> result, IRunicWindowContext window)
    {
        Current = request.Value.Current;
        ChooseCommand = ReactiveCommand.CreateFromTask<string>(async (account, token) => { await result.CompleteAsync(account, token); });
        CancelCommand = ReactiveCommand.CreateFromTask(async token => { await window.RequestCloseAsync(token); });
    }

    public IReadOnlyList<string> Accounts { get; } = ["Household", "Savings", "Travel"];

    public string Current { get; }

    public ReactiveCommand<string, RxVoid> ChooseCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}

public sealed partial class AccountWindow : RunicWindow<AccountViewModel>;
