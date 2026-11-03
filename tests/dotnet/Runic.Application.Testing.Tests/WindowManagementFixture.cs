using System.ComponentModel;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// B6 and E2: a shell that opens documents in their own Windows, or focuses one already open, and a window-modal
// "choose account" Window that returns a value.
public sealed record ManagedDocument(string Path);

public sealed record AccountQuery(string Prompt);

public sealed class ManagedShellViewModel(IRunicWindowService windows, IRunicWindowContext window) : INotifyPropertyChanged
{
    public string Title { get; } = "shell";

    public async Task<ManagedDocumentWindow> OpenDocumentAsync(string path) =>
        await windows.FindOrOpenAsync<ManagedDocumentWindow, ManagedDocument>(path, new ManagedDocument(path));

    public async Task<RunicWindowResult<string>> ChooseAccountAsync(CancellationToken cancellationToken = default) =>
        await windows.OpenForResultAsync<AccountPickerWindow, AccountQuery, string>(new AccountQuery("Pay from"),
            new RunicWindowOptions { Owner = window.Window, Modality = RunicWindowModality.Window }, cancellationToken);

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ManagedShellWindow : RunicWindow<ManagedShellViewModel>;

public sealed class ManagedDocumentViewModel(IRunicWindowParameter<ManagedDocument> document) : INotifyPropertyChanged
{
    public string Path { get; } = document.Value.Path;

    // Kept open by its close guard until this is set.
    [RunicIgnore]
    public bool Saved { get; set; } = true;

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ManagedDocumentWindow : RunicWindow<ManagedDocumentViewModel>
{
    public ManagedDocumentWindow() => GuardClose = true;

    protected override async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken) =>
        ViewModel.Saved && await base.CanCloseAsync(cancellationToken);
}

public sealed class AccountPickerViewModel(IRunicWindowParameter<AccountQuery> query, IRunicWindowResult<string> result)
    : INotifyPropertyChanged
{
    public string Prompt { get; } = query.Value.Prompt;

    public ValueTask<bool> ChooseAsync(string account) => result.CompleteAsync(account);

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class AccountPickerWindow : RunicWindow<AccountPickerViewModel>;
