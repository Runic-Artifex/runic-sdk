using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using MultiWindow;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using Runic.Platform;

namespace MultiWindow.Tests;

// Drives two project Windows, each in its own scope, with the application's services. The window service, the
// settings file and the desktop preferences are replaced with test doubles registered after AddProject().
public sealed class ProjectWindowTests : IAsyncDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"runic-multi-window-{Guid.NewGuid():N}", "settings.json");
    private readonly RecordingWindowService _windows = new();
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _firstScope;
    private readonly AsyncServiceScope _secondScope;
    private readonly RunicWindowTestHost<ProjectViewModel> _first;
    private readonly RunicWindowTestHost<ProjectViewModel> _second;

    public ProjectWindowTests()
    {
        _services = new ServiceCollection()
            .AddProject()
            .AddSingleton<IRunicWindowService>(_windows)
            .AddSingleton(new SettingsStore(_settingsPath))
            .AddSingleton<IDesktopSettings>(new FixedDesktopSettings(DesktopColorScheme.Dark))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _firstScope = _services.CreateAsyncScope();
        _secondScope = _services.CreateAsyncScope();
        _first = RunicWindowTestHost.Create<ProjectViewModel>(_firstScope.ServiceProvider);
        _second = RunicWindowTestHost.Create<ProjectViewModel>(_secondScope.ServiceProvider);
    }

    [Test]
    public async Task Renaming_in_one_window_updates_the_other()
    {
        (await _first.Root.ExecuteAsync(vm => vm.RenameCommand, "\"Atlas\"")).ThrowIfFailed();

        // The rename posted a turn to the second Window's model context; a turn after it sees the new title.
        await _secondScope.ServiceProvider.GetRequiredService<IRunicModelContext>().InvokeAsync(static () => { });
        await Assert.That(_second.Root.Snapshot().Read(vm => vm.Title)).IsEqualTo("Atlas");
        await Assert.That(_first.Root.Snapshot().Read(vm => vm.Title)).IsEqualTo("Atlas");
    }

    [Test]
    public async Task New_window_opens_another_project_window()
    {
        (await _first.Root.ExecuteAsync(vm => vm.NewWindowCommand)).ThrowIfFailed();

        await Assert.That(_windows.Opened).IsEquivalentTo([typeof(ProjectWindow)]);
    }

    [Test]
    public async Task The_chosen_theme_is_saved_in_the_settings_file()
    {
        (await _first.Root.ExecuteAsync(vm => vm.ChooseThemeCommand, "\"dark\"")).ThrowIfFailed();

        await Assert.That(_first.Root.Snapshot().Read(vm => vm.Theme)).IsEqualTo("dark");
        await Assert.That(new SettingsStore(_settingsPath).Load()).IsEqualTo(new AppSettings(Theme: "dark"));
        await Assert.That(new AppSettings(Theme: "dark").ColorScheme).IsEqualTo(DesktopColorScheme.Dark);
        await Assert.That(new AppSettings().ColorScheme).IsEqualTo(DesktopColorScheme.NoPreference);
    }

    [Test]
    public async Task Saving_the_theme_tells_the_application()
    {
        var store = _services.GetRequiredService<SettingsStore>();
        AppSettings? saved = null;
        store.Saved += (_, settings) => saved = settings;
        (await _first.Root.ExecuteAsync(vm => vm.ChooseThemeCommand, "\"light\"")).ThrowIfFailed();

        await Assert.That(saved?.ColorScheme).IsEqualTo(DesktopColorScheme.Light);
    }

    [Test]
    public async Task The_desktop_color_scheme_reaches_the_view_model()
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_first.Root.Snapshot().Read(vm => vm.DesktopScheme) != "Dark" && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        await Assert.That(_first.Root.Snapshot().Read(vm => vm.DesktopScheme)).IsEqualTo("Dark");
    }

    [Test]
    public async Task The_saved_language_is_the_window_locale_and_a_switch_saves_it()
    {
        await Assert.That(_first.Content.Locale).IsEqualTo("en");
        (await _first.Root.ExecuteAsync(vm => vm.ChooseLanguageCommand, "\"de\"")).ThrowIfFailed();

        // The page follows the Window's locale; the other Window keeps its own until it switches.
        await Assert.That(_first.Content.Locale).IsEqualTo("de");
        await Assert.That(_second.Content.Locale).IsEqualTo("en");
        await Assert.That(_first.Root.Snapshot().Read(vm => vm.Language)).IsEqualTo("de");
        await Assert.That(new SettingsStore(_settingsPath).Load().Language).IsEqualTo("de");
    }

    [Test]
    public async Task Renaming_reports_a_status_the_page_translates()
    {
        await Assert.That(_first.Root.Snapshot().Read(vm => vm.Status).Key).IsEqualTo("ready");
        (await _first.Root.ExecuteAsync(vm => vm.RenameCommand, "\"Atlas\"")).ThrowIfFailed();

        var status = _first.Root.Snapshot().Read(vm => vm.Status);
        await Assert.That(status.Key).IsEqualTo("renamed");
        await Assert.That(status.Arguments["title"].Value).IsEqualTo("Atlas");
        await Assert.That(status.FallbackText).IsEqualTo("Renamed to Atlas.");
    }

    [Test]
    public async Task A_long_title_has_a_validation_message_the_page_translates()
    {
        (await _first.Root.SetAsync(vm => vm.Draft, new string('x', ProjectViewModel.MaximumTitleLength + 1))).ThrowIfFailed();

        var error = _first.Root.Snapshot()["validation"].GetProperty("errors")[0];
        await Assert.That(error.GetProperty("message").GetString()).IsEqualTo("The title must be at most 40 characters.");
        await Assert.That(error.GetProperty("text").GetProperty("key").GetString()).IsEqualTo("title_too_long");
        await Assert.That(error.GetProperty("text").GetProperty("arguments").GetProperty("max").GetProperty("value").GetString()).IsEqualTo("40");
    }

    public async ValueTask DisposeAsync()
    {
        _first.Dispose();
        _second.Dispose();
        await _firstScope.DisposeAsync();
        await _secondScope.DisposeAsync();
        await _services.DisposeAsync();
        var directory = Path.GetDirectoryName(_settingsPath)!;
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    // The window service's test double: it records the requested Window and opens nothing.
    private sealed class RecordingWindowService : IRunicWindowService
    {
        public List<Type> Opened { get; } = [];

        public ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
            where TWindow : class, IRunicWindow, new()
        {
            Opened.Add(typeof(TWindow));
            return ValueTask.FromResult(new TWindow());
        }
    }

    // Desktop preferences that report one color scheme and never change.
    internal sealed class FixedDesktopSettings(DesktopColorScheme scheme) : IDesktopSettings
    {
        private PlatformResult<DesktopAppearance> Current => new PlatformResult<DesktopAppearance>.Success(new DesktopAppearance(scheme));

        public ValueTask<PlatformResult<DesktopAppearance>> ReadAsync(CancellationToken cancellationToken = default) => new(Current);

        public async IAsyncEnumerable<PlatformResult<DesktopAppearance>> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Current;
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
