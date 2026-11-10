#if (viewModels == "reactiveui")
using ReactiveUI;
using ReactiveUI.Primitives;
#if (translations)
using System.ComponentModel;
using Runic.Application.Views;
using Runic.Translations;
#endif

namespace RunicWindowApp;

public interface IWorkspacePage { }

#if (translations)
public sealed class WorkspaceViewModel : ReactiveObject, IDisposable
#else
public sealed class WorkspaceViewModel : ReactiveObject
#endif
{
    private IWorkspacePage _main;
#if (translations)
    private readonly IRunicWindowLocale _locale;
    private string _language;
#endif

    // The Window runs these commands in its model turns, and AddRunicReactiveModelContext
    // makes ReactiveUI deliver their notifications there, ordered with Bridge replies,
    // so a command needs no scheduler argument. The generated Bridge logs a command's
    // unexpected exception, and a command needs disposing only when its canExecute
    // observes an object that outlives this ViewModel, so these need neither.
#if (translations)
    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter,
        IRunicApplicationLocale application, IRunicWindowLocale locale, TranslationContext translations)
#else
    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter)
#endif
    {
        _main = welcome;
        ShowWelcomeCommand = ReactiveCommand.Create(() => { Main = welcome; });
        ShowCounterCommand = ReactiveCommand.Create(() => { Main = counter; });
#if (translations)
        // The Window follows the application's language; Language shows it to the page's language list.
        _locale = locale;
        _language = locale.Locale ?? AppTextCatalog.DefaultLocale;
        _locale.PropertyChanged += OnLocaleChanged;
        // The page asks for one of the catalog's languages; the application owns it. The C# catalog switches first, then
        // every Window. A language the catalog does not have is ignored.
        ChooseLanguageCommand = ReactiveCommand.CreateFromTask<string>(async (language, token) =>
        {
            if (WorkspaceServices.SupportedLanguage(translations, language) is { } supported)
                await application.SetLocaleAsync(supported, token);
        });
#endif
    }

    public IWorkspacePage Main
    {
        get => _main;
        private set => this.RaiseAndSetIfChanged(ref _main, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> ShowWelcomeCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ShowCounterCommand { get; }
#if (translations)

    // The language every Window shows, such as "en" or "de".
    public string Language
    {
        get => _language;
        private set => this.RaiseAndSetIfChanged(ref _language, value);
    }

    public ReactiveCommand<string, RxVoid> ChooseLanguageCommand { get; }

    // Raised in a turn of this Window's model context when the application's language reaches it.
    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IRunicWindowLocale.Locale)) Language = _locale.Locale ?? AppTextCatalog.DefaultLocale;
    }

    public void Dispose() => _locale.PropertyChanged -= OnLocaleChanged;
#endif
}

public sealed class WelcomeViewModel : ReactiveObject, IWorkspacePage
{
#if (translations)
    // A text reference of the catalog (Frontend/translations): the page shows it in the Window's language.
    public TranslationReference Greeting { get; } = AppText.References.welcome_greeting();
#else
    public string Greeting => "Your first Runic View is ready.";
#endif
}

public sealed class CounterViewModel : ReactiveObject, IWorkspacePage
{
    private int _count;
    private int _step = 1;
    private string _label = "Clicks";

    public CounterViewModel() =>
        IncrementCommand = ReactiveCommand.Create(() => { Count += Step; });

    // As for any MVVM View, the setter's accessibility decides what the web
    // frontend may set: Count is read-only state, Step is a form field, so the
    // generated client has setStep but no setCount.
    public int Count
    {
        get => _count;
        private set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public int Step
    {
        get => _step;
        set => this.RaiseAndSetIfChanged(ref _step, value);
    }

    // A text form field: the page edits it with the same field binding as Step.
    public string Label
    {
        get => _label;
        set => this.RaiseAndSetIfChanged(ref _label, value);
    }

    public ReactiveCommand<RxVoid, RxVoid> IncrementCommand { get; }
}
#else
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
#if (translations)
using System.ComponentModel;
using Runic.Application.Views;
using Runic.Translations;
#endif

namespace RunicWindowApp;

public interface IWorkspacePage { }

#if (translations)
public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
#else
public sealed partial class WorkspaceViewModel : ObservableObject
#endif
{
    private readonly WelcomeViewModel _welcome;
    private readonly CounterViewModel _counter;
    private IWorkspacePage _main;
#if (translations)
    private readonly IRunicApplicationLocale _application;
    private readonly IRunicWindowLocale _locale;
    private readonly TranslationContext _translations;

    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter,
        IRunicApplicationLocale application, IRunicWindowLocale locale, TranslationContext translations)
    {
        _welcome = welcome;
        _counter = counter;
        _main = welcome;
        // The Window follows the application's language; Language shows it to the page's language list.
        _application = application;
        _locale = locale;
        _translations = translations;
        Language = locale.Locale ?? AppTextCatalog.DefaultLocale;
        _locale.PropertyChanged += OnLocaleChanged;
    }
#else

    public WorkspaceViewModel(WelcomeViewModel welcome, CounterViewModel counter)
    {
        _welcome = welcome;
        _counter = counter;
        _main = welcome;
    }
#endif

    public IWorkspacePage Main
    {
        get => _main;
        private set => SetProperty(ref _main, value);
    }

    [RelayCommand]
    private void ShowWelcome() => Main = _welcome;

    [RelayCommand]
    private void ShowCounter() => Main = _counter;
#if (translations)

    // The language every Window shows, such as "en" or "de".
    [ObservableProperty]
    public partial string Language { get; private set; }

    // The page asks for one of the catalog's languages; the application owns it. The C# catalog switches first, then
    // every Window. A language the catalog does not have is ignored.
    [RelayCommand]
    private async Task ChooseLanguageAsync(string language, CancellationToken cancellationToken)
    {
        if (WorkspaceServices.SupportedLanguage(_translations, language) is { } supported)
            await _application.SetLocaleAsync(supported, cancellationToken);
    }

    // Raised in a turn of this Window's model context when the application's language reaches it.
    private void OnLocaleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IRunicWindowLocale.Locale)) Language = _locale.Locale ?? AppTextCatalog.DefaultLocale;
    }

    public void Dispose() => _locale.PropertyChanged -= OnLocaleChanged;
#endif
}

public sealed partial class WelcomeViewModel : ObservableObject, IWorkspacePage
{
#if (translations)
    // A text reference of the catalog (Frontend/translations): the page shows it in the Window's language.
    public TranslationReference Greeting { get; } = AppText.References.welcome_greeting();
#else
    public string Greeting => "Your first Runic View is ready.";
#endif
}

public sealed partial class CounterViewModel : ObservableObject, IWorkspacePage
{
    public CounterViewModel()
    {
        Step = 1;
        Label = "Clicks";
    }

    // As for any MVVM View, the setter's accessibility decides what the web
    // frontend may set: Count is read-only state, Step is a form field, so the
    // generated client has setStep but no setCount.
    [ObservableProperty]
    public partial int Count { get; private set; }

    [ObservableProperty]
    public partial int Step { get; set; }

    // A text form field: the page edits it with the same field binding as Step.
    [ObservableProperty]
    public partial string Label { get; set; }

    [RelayCommand]
    private void Increment() => Count += Step;
}
#endif
