using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;

namespace MultiWindow;

// The root ViewModel of each Window. The singletons it takes are shared by every Window; IRunicModelContext is this
// Window's own.
public sealed partial class ProjectViewModel : ObservableObject, IDisposable
{
    private readonly ProjectService _project;
    private readonly SettingsStore _settings;
    private readonly IRunicWindowService _windows;
    private readonly IRunicModelContext _context;
    private readonly CancellationTokenSource _stop = new();

    public ProjectViewModel(ProjectService project, SettingsStore settings, IDesktopSettings desktop,
        IRunicWindowService windows, IRunicModelContext context)
    {
        _project = project;
        _settings = settings;
        _windows = windows;
        _context = context;
        Title = project.Title;
        Theme = settings.Load().Theme;
        _project.Renamed += OnRenamed;
        _ = Task.Run(() => WatchAppearanceAsync(desktop, _stop.Token));
    }

    [ObservableProperty]
    public partial string Title { get; private set; }

    // The app setting: "system", "light" or "dark".
    [ObservableProperty]
    public partial string Theme { get; private set; }

    // The desktop's preference, which "system" follows: NoPreference, Light or Dark.
    [ObservableProperty]
    public partial string DesktopScheme { get; private set; } = nameof(DesktopColorScheme.NoPreference);

    [RelayCommand]
    private void Rename(string title) => _project.Rename(title);

    // Opens another Window on the same project, in its own scope. The service releases it when it closes.
    [RelayCommand]
    private async Task NewWindowAsync() => await _windows.OpenWindowAsync<ProjectWindow>();

    [RelayCommand]
    private void ChooseTheme(string theme)
    {
        _settings.Save(_settings.Load() with { Theme = theme });
        Theme = theme;
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
        // Ends the watch; the token source is not disposed, because the watch may still be observing it.
        _stop.Cancel();
    }
}
