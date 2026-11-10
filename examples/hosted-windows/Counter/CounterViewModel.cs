using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Platform;
using Runic.Translations;

namespace CounterTool;

// The counter's root ViewModel. Nothing in it knows whether a shell hosts the counter: in hosted mode the message box
// and the launcher it takes are the host's (dialogs/1, window-services/1), and the menu bar's items run its commands
// whether the user chooses them in the counter's own window or in the shell's window that shows it.
public sealed class CounterViewModel : ReactiveObject, IRunicMenuCommandSource, IDisposable
{
    public static readonly Uri Documentation = new("https://docs.runic-artifex.eu/");

    private int _count;
    private TranslationReference? _status;

    public CounterViewModel(IDesktopMessageBoxes messageBoxes, IDesktopFileLauncher launcher, TranslationContext translations)
    {
        IncrementCommand = ReactiveCommand.Create(() => { Count++; Status = null; });
        // Asks first, in the application's language. Hosted, the shell shows the message box over this Window.
        ResetCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            var text = new CounterText(translations).Messages;
            var confirm = new DesktopMessageBoxButton("reset", text.reset_confirm) { Role = DesktopMessageBoxButtonRole.Destructive };
            var answer = await messageBoxes.ShowAsync(new DesktopMessageBox(text.reset_question(count: Count))
            {
                Kind = DesktopMessageBoxKind.Warning,
                Buttons = [confirm, new DesktopMessageBoxButton("cancel", text.reset_cancel) { Role = DesktopMessageBoxButtonRole.Cancel }],
            }, token);
            if (answer is PlatformResult<DesktopMessageBoxButton?>.Success { Value.Id: "reset" }) Count = 0;
        });
        // Hosted, the shell decides whether the link opens (its IRunicHostedWindowPolicy); a refusal is PermissionDenied.
        OpenDocumentationCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            var result = await launcher.LaunchUriAsync(Documentation, token);
            Status = result is PlatformResult<PlatformUnit>.Success ? null : CounterText.References.link_refused();
        });
    }

    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    // A text reference of the catalog, which the page shows in the application's language; null for none.
    public TranslationReference? Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ResetCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenDocumentationCommand { get; }

    // The menu bar's command ids (CounterServices.MenuBar).
    public ICommand? GetMenuCommand(string commandId) => commandId switch
    {
        CounterServices.IncrementId => IncrementCommand,
        CounterServices.ResetId => ResetCommand,
        CounterServices.DocumentationId => OpenDocumentationCommand,
        _ => null,
    };

    public void Dispose()
    {
        IncrementCommand.Dispose();
        ResetCommand.Dispose();
        OpenDocumentationCommand.Dispose();
    }
}
