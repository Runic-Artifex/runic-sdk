using System.Collections;
using System.ComponentModel;
using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;
using Runic.Translations;

namespace MultiWindow;

// The root ViewModel of each Window. The singletons it takes are shared by every Window; IRunicModelContext is this
// Window's own. The commands take no scheduler: AddRunicReactiveModelContext delivers their notifications on the
// model context of the Window that runs them.
//
// The status line and the title's validation message are text references of the generated ProjectText catalog
// (Frontend/translations), which the page shows in the application's language. The language is the application's
// (IRunicApplicationLocale): a switch in one Window, from the page or the menu bar, switches every Window.
public sealed class ProjectViewModel : ReactiveObject, INotifyDataErrorInfo, IRunicMenuCommandSource, IDisposable
{
    public const int MaximumTitleLength = 40;

    private readonly ProjectService _project;
    private readonly IRunicModelContext _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly IRunicWindowLocale _locale;
    private string _title;
    private string _theme;
    private string _desktopScheme = nameof(DesktopColorScheme.NoPreference);
    private string _language;
    private string _draft = "";
    private TranslationReference _status = ProjectText.References.ready();
    private string _account = "Household";

    public ProjectViewModel(ProjectService project, SettingsStore settings, IDesktopSettings desktop,
        IRunicWindowService windows, IRunicWindowContext window, IRunicModelContext context,
        IRunicApplicationLocale application, IRunicWindowLocale locale, TranslationContext translations)
    {
        _project = project;
        _context = context;
        _title = project.Title;
        _theme = settings.Load().Theme;
        // The Window follows the application's language; Language shows it, and changes in this Window's model turn.
        _locale = locale;
        _language = locale.Locale ?? ProjectTextCatalog.DefaultLocale;
        _locale.PropertyChanged += OnLocaleChanged;
        RenameCommand = ReactiveCommand.Create<string>(title =>
        {
            project.Rename(title);
            Status = ProjectText.References.renamed(title: title);
            Draft = "";
        });
        // Switches the application's language to one of the catalog's and saves it for the next start, once the switch
        // succeeded. The page and the menu bar ask for it; the application owns it. The C# catalog switches first, then
        // every Window, then the menu bar (Program.cs). A language the catalog does not have is ignored.
        ChooseLanguageCommand = ReactiveCommand.CreateFromTask<string>(async (language, token) =>
        {
            if (ProjectServices.SupportedLanguage(translations, language) is not { } supported) return;
            await application.SetLocaleAsync(supported, token);
            settings.Save(settings.Load() with { Language = supported });
        });
        // Opens another Window on the same project, in its own scope. The service releases it when it closes.
        NewWindowCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            await windows.OpenWindowAsync<ProjectWindow>(cancellationToken: token);
        });
        // Opens the document in a Window of its own, or brings the one already open for that path to the front.
        OpenDocumentCommand = ReactiveCommand.CreateFromTask<string>(async (path, token) =>
        {
            await windows.FindOrOpenAsync<DocumentWindow, DocumentRequest>(path, new DocumentRequest(path),
                new RunicWindowOptions { StartPage = "document.html", Width = 480, Height = 320 }, cancellationToken: token);
        });
        // A dialog over this Window: it blocks this Window until it closes, and returns the account chosen, if any.
        ChooseAccountCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            var chosen = await windows.OpenForResultAsync<AccountWindow, AccountRequest, string>(new AccountRequest(Account),
                new RunicWindowOptions
                {
                    StartPage = "account.html",
                    Width = 360,
                    Height = 280,
                    Owner = window.Window,
                    Modality = RunicWindowModality.Window,
                }, token);
            if (chosen.TryGetValue(out var account)) Account = account;
        });
        ChooseThemeCommand = ReactiveCommand.Create<string>(theme =>
        {
            settings.Save(settings.Load() with { Theme = theme });
            Theme = theme;
        });
        // The commands need no ThrownExceptions subscription, because the generated Bridge logs an unexpected
        // exception, and no disposal, because they observe nothing. Dispose releases the shared project event.
        _project.Renamed += OnRenamed;
        _ = Task.Run(() => WatchAppearanceAsync(desktop, _stop.Token));
    }

    public string Title { get => _title; private set => this.RaiseAndSetIfChanged(ref _title, value); }

    // The app setting: "system", "light" or "dark".
    public string Theme { get => _theme; private set => this.RaiseAndSetIfChanged(ref _theme, value); }

    // The desktop's preference, which "system" follows: NoPreference, Light or Dark.
    public string DesktopScheme { get => _desktopScheme; private set => this.RaiseAndSetIfChanged(ref _desktopScheme, value); }

    // The language every Window shows, one of the catalog's: "ar", "de" or "en".
    public string Language { get => _language; private set => this.RaiseAndSetIfChanged(ref _language, value); }

    // What the last command did, in the Window's language.
    public TranslationReference Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    // The title being typed, validated as the user types.
    public string Draft
    {
        get => _draft;
        set
        {
            this.RaiseAndSetIfChanged(ref _draft, value);
            ErrorsChanged?.Invoke(this, new(nameof(Draft)));
        }
    }

    [RunicIgnore]
    public bool HasErrors => _draft.Length > MaximumTitleLength;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    // The generated factory: the key, the typed argument and the English text as the fallback.
    private static readonly TranslationReference TitleTooLong = ProjectText.References.title_too_long(max: MaximumTitleLength);

    // The message stays the fallback; its text reference shows in the Window's language.
    public IEnumerable GetErrors(string? propertyName) => propertyName == nameof(Draft) && HasErrors
        ? new[]
        {
            new RunicValidationMessage(TitleTooLong.FallbackText!, "title-too-long")
            {
                Text = RunicTextReference.Create(TitleTooLong, TranslationReferenceJsonContext.Default.TranslationReference),
            },
        }
        : Array.Empty<RunicValidationMessage>();

    public ReactiveCommand<string, RxVoid> RenameCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> NewWindowCommand { get; }
    public ReactiveCommand<string, RxVoid> ChooseThemeCommand { get; }
    public ReactiveCommand<string, RxVoid> ChooseLanguageCommand { get; }
    public ReactiveCommand<string, RxVoid> OpenDocumentCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ChooseAccountCommand { get; }

    // The account the "choose account" dialog returned last.
    public string Account { get => _account; private set => this.RaiseAndSetIfChanged(ref _account, value); }

    // The menu bar's Language items (Program.cs) run ChooseLanguageCommand of the focused Window.
    public System.Windows.Input.ICommand? GetMenuCommand(string commandId) => commandId == "language" ? ChooseLanguageCommand : null;

    // Raised in a turn of this Window's model context when the application's language reaches it.
    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IRunicWindowLocale.Locale)) Language = _locale.Locale ?? ProjectTextCatalog.DefaultLocale;
    }

    // Raised on the renaming Window's thread: apply it in this Window's turn.
    private void OnRenamed(object? sender, EventArgs e) => _context.TryPost(() => Title = _project.Title);

    // The values arrive outside the model context, so each one is committed in a turn of it.
    private async Task WatchAppearanceAsync(IDesktopSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in settings.WatchAsync(cancellationToken))
                if (result is PlatformResult<DesktopAppearance>.Success { Value: var appearance })
                    await _context.InvokeAsync(() => DesktopScheme = appearance.ColorScheme.ToString(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _project.Renamed -= OnRenamed;
        _locale.PropertyChanged -= OnLocaleChanged;
        // Ends the watch; the token source is not disposed, because the watch may still be observing it.
        _stop.Cancel();
    }
}
