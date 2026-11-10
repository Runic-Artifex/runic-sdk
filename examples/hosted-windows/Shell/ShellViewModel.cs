using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Application.Views.Desktop;
using Runic.Navigation;
using Runic.Platform;

namespace HostedShell;

// The shell's Window: it starts the counter as a hosted application, and its language and theme are the ones the
// counter's windows show.
public sealed class ShellViewModel : ReactiveObject, IDisposable
{
    // The shell's languages; the counter's catalog has the same.
    public static readonly IReadOnlyList<string> Languages = ["en", "de"];

    private readonly IRunicModelContext _context;
    private RunicHostedApplication? _counter;
    private string _language;
    private string _theme;
    private string _counterState = "stopped";

    public ShellViewModel(IRunicHostedApplications applications, IRunicApplicationLocale locale, ShellTheme theme, IRunicModelContext context)
    {
        _context = context;
        _language = locale.Locale ?? "en";
        _theme = theme.Name;
        // The application's language. Every guest follows it, with its own catalog.
        ChooseLanguageCommand = ReactiveCommand.CreateFromTask<string>(async (language, token) =>
        {
            if (!Languages.Contains(language)) return;
            await locale.SetLocaleAsync(language, token);
            _context.TryPost(() => Language = language);
        });
        // The application's light or dark theme, which every window of the shell shows, the counter's included
        // (Program.cs: DesktopHost.SetColorSchemeAsync).
        ChooseThemeCommand = ReactiveCommand.Create<string>(name =>
        {
            if (theme.TryChoose(name)) Theme = name;
        });
        // Starts the counter, or brings its window to the front when it runs.
        OpenCounterCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            if (_counter is { } running)
            {
                if (running.Windows.FirstOrDefault() is { } window) await window.Presentation.FocusAsync();
                return;
            }
            CounterState = "starting";
            try
            {
                var counter = await applications.StartAsync(Guests.Counter,
                    new RunicHostedApplicationOptions { HostName = "Hosted windows shell", Policy = new CounterPolicy() }, token);
                _context.TryPost(() => { _counter = counter; CounterState = "running"; });
                _ = WatchAsync(counter);
            }
            catch (RunicHostedApplicationException error)
            {
                _context.TryPost(() => CounterState = $"failed: {error.Code}");
            }
        });
        // Asks the counter to quit; it closes its windows and exits.
        StopCounterCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            if (_counter is { } running) await running.StopAsync(token);
        });
    }

    public string Language { get => _language; private set => this.RaiseAndSetIfChanged(ref _language, value); }

    public string Theme { get => _theme; private set => this.RaiseAndSetIfChanged(ref _theme, value); }

    // "stopped", "starting", "running", "exited with <code>" or "failed: <code>".
    public string CounterState { get => _counterState; private set => this.RaiseAndSetIfChanged(ref _counterState, value); }

    public ReactiveCommand<string, RxVoid> ChooseLanguageCommand { get; }

    public ReactiveCommand<string, RxVoid> ChooseThemeCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenCounterCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StopCounterCommand { get; }

    // The counter can exit by itself, when the user closes its last window, or crash; the shell then closes its windows.
    private async Task WatchAsync(RunicHostedApplication counter)
    {
        var exitCode = await counter.Exited;
        _context.TryPost(() =>
        {
            if (_counter == counter) _counter = null;
            CounterState = $"exited with {exitCode}";
        });
    }

    public void Dispose()
    {
        ChooseLanguageCommand.Dispose();
        ChooseThemeCommand.Dispose();
        OpenCounterCommand.Dispose();
        StopCounterCommand.Dispose();
    }
}

// The shell's theme: "system", "light" or "dark". Program.cs applies each choice to the application's windows.
public sealed class ShellTheme
{
    public string Name { get; private set; } = "system";

    public DesktopColorScheme ColorScheme => Name switch
    {
        "light" => DesktopColorScheme.Light,
        "dark" => DesktopColorScheme.Dark,
        _ => DesktopColorScheme.NoPreference,
    };

    public event EventHandler? Changed;

    public bool TryChoose(string name)
    {
        if (name is not ("system" or "light" or "dark")) return false;
        Name = name;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
