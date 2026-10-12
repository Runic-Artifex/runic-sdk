using ReactiveUI;
using ReactiveUI.Primitives;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;

namespace MultiWindow;

// The root ViewModel of each Window. The singletons it takes are shared by every Window; IRunicModelContext is this
// Window's own. The commands take no scheduler: AddRunicReactiveModelContext delivers their notifications on the
// model context of the Window that runs them.
public sealed class ProjectViewModel : ReactiveObject, IDisposable
{
    private readonly ProjectService _project;
    private readonly IRunicModelContext _context;
    private readonly CancellationTokenSource _stop = new();
    private string _title;
    private string _theme;
    private string _desktopScheme = nameof(DesktopColorScheme.NoPreference);

    public ProjectViewModel(ProjectService project, SettingsStore settings, IDesktopSettings desktop,
        IRunicWindowService windows, IRunicModelContext context)
    {
        _project = project;
        _context = context;
        _title = project.Title;
        _theme = settings.Load().Theme;
        RenameCommand = ReactiveCommand.Create<string>(project.Rename);
        // Opens another Window on the same project, in its own scope. The service releases it when it closes.
        NewWindowCommand = ReactiveCommand.CreateFromTask(async token =>
        {
            await windows.OpenWindowAsync<ProjectWindow>(cancellationToken: token);
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

    public ReactiveCommand<string, RxVoid> RenameCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> NewWindowCommand { get; }
    public ReactiveCommand<string, RxVoid> ChooseThemeCommand { get; }

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
