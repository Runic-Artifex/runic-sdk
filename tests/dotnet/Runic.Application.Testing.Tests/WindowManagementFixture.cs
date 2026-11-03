using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// B6 and E2: a shell that opens documents in their own Windows, or focuses one already open, and a window-modal
// "choose account" Window that returns a value.
public sealed record ManagedDocument(string Path);

public sealed record AccountQuery(string Prompt);

// The close guards asked, in order, by the Windows' names.
public sealed class GuardLog
{
    private readonly List<string> _asked = [];
    public void Add(string name) { lock (_asked) _asked.Add(name); }
    public IReadOnlyList<string> Asked { get { lock (_asked) return [.. _asked]; } }
}

public sealed class ManagedShellViewModel(IRunicWindowService windows, IRunicWindowContext window) : INotifyPropertyChanged
{
    public string Title { get; } = "shell";

    // A command of the shell's page, refused while a modal Window blocks it.
    public IRelayCommand PingCommand { get; } = new RelayCommand(static () => { });

    public async Task<ManagedDocumentWindow> OpenOwnedDocumentAsync(string path) =>
        await windows.OpenWindowAsync<ManagedDocumentWindow, ManagedDocument>(new ManagedDocument(path),
            new RunicWindowOptions { Owner = window.Window });

    public async Task<RunicWindowResult<string>> ChooseEagerlyAsync() =>
        await windows.OpenForResultAsync<EagerPickerWindow, string>(new RunicWindowOptions { Owner = window.Window, Modality = RunicWindowModality.Window },
            CancellationToken.None);

    public async Task<ManagedDocumentWindow> OpenDocumentAsync(string path) =>
        await windows.FindOrOpenAsync<ManagedDocumentWindow, ManagedDocument>(path, new ManagedDocument(path));

    public async Task<RunicWindowResult<string>> ChooseAccountAsync(CancellationToken cancellationToken = default) =>
        await windows.OpenForResultAsync<AccountPickerWindow, AccountQuery, string>(new AccountQuery("Pay from"),
            new RunicWindowOptions { Owner = window.Window, Modality = RunicWindowModality.Window }, cancellationToken);

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class ManagedShellWindow : RunicWindow<ManagedShellViewModel>
{
    public ManagedShellWindow() => GuardClose = true;

    protected override ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        Services.GetService<GuardLog>()?.Add("shell");
        return ValueTask.FromResult(true);
    }
}

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

    protected override async ValueTask<bool> CanCloseAsync(CancellationToken cancellationToken)
    {
        Services.GetService<GuardLog>()?.Add(ViewModel.Path);
        return ViewModel.Saved && await base.CanCloseAsync(cancellationToken);
    }
}

public sealed class AccountPickerViewModel(IRunicWindowParameter<AccountQuery> query, IRunicWindowResult<string> result)
    : INotifyPropertyChanged
{
    public string Prompt { get; } = query.Value.Prompt;

    public ValueTask<bool> ChooseAsync(string account) => result.CompleteAsync(account);

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class AccountPickerWindow : RunicWindow<AccountPickerViewModel>;

// Completes its result in its constructor, before its Window has opened.
public sealed class EagerPickerViewModel : INotifyPropertyChanged
{
    public EagerPickerViewModel(IRunicWindowResult<string> result) => _ = result.CompleteAsync("eager").AsTask();

    public string Title { get; } = "eager";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class EagerPickerWindow : RunicWindow<EagerPickerViewModel>;

// Its constructor waits for Gate, so a test can cancel the open while another call waits for it.
public sealed class SlowDocumentViewModel : INotifyPropertyChanged
{
    public static ManualResetEventSlim? Gate { get; set; }
    public static TaskCompletionSource? Entered { get; set; }

    public SlowDocumentViewModel()
    {
        Entered?.TrySetResult();
        Gate?.Wait(TimeSpan.FromSeconds(10));
    }

    public string Title { get; } = "slow";

    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
}

public sealed partial class SlowDocumentWindow : RunicWindow<SlowDocumentViewModel>;
