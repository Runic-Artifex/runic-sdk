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
// The status line and the title's validation message are text references (Runic.Translations), which the page shows
// in the Window's language. The ViewModel sets the language with IRunicWindowLocale, from the app's settings.
public sealed class ProjectViewModel : ReactiveObject, INotifyDataErrorInfo, IDisposable
{
    public const int MaximumTitleLength = 40;

    private readonly ProjectService _project;
    private readonly IRunicModelContext _context;
    private readonly CancellationTokenSource _stop = new();
    private string _title;
    private string _theme;
    private string _desktopScheme = nameof(DesktopColorScheme.NoPreference);
    private string _language;
    private string _draft = "";
    private TranslationReference _status = ProjectText.Ready();
    private string _account = "Household";

    public ProjectViewModel(ProjectService project, SettingsStore settings, IDesktopSettings desktop,
        IRunicWindowService windows, IRunicWindowContext window, IRunicModelContext context, IRunicWindowLocale locale)
    {
        _project = project;
        _context = context;
        _title = project.Title;
        _theme = settings.Load().Theme;
        // The Window's pages show text references in this language from their first state on.
        _language = settings.Load().Language;
        locale.Locale = _language;
        RenameCommand = ReactiveCommand.Create<string>(title =>
        {
            project.Rename(title);
            Status = ProjectText.Renamed(title);
            Draft = "";
        });
        // Switches this Window's language and saves it, so new Windows start in it. The page asks for it; the
        // ViewModel owns it.
        ChooseLanguageCommand = ReactiveCommand.Create<string>(language =>
        {
            settings.Save(settings.Load() with { Language = language });
            locale.Locale = language;
            Language = language;
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

    // The app setting: "en" or "de".
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

    // The message stays the fallback; its text reference shows in the Window's language.
    public IEnumerable GetErrors(string? propertyName) => propertyName == nameof(Draft) && HasErrors
        ? new[]
        {
            new RunicValidationMessage(ProjectText.TitleTooLong(MaximumTitleLength).FallbackText!, "title-too-long")
            {
                Text = ProjectText.Text(ProjectText.TitleTooLong(MaximumTitleLength)),
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
        // Ends the watch; the token source is not disposed, because the watch may still be observing it.
        _stop.Cancel();
    }
}
