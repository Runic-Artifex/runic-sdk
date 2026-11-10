using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
#if (translations)
using Runic.Translations;
#endif

namespace RunicWindowApp.Tests;

// Drives the Window's real ViewModels and generated Bridges as the frontend does,
// without a browser or native window. TUnit creates this class for each test and
// runs tests in parallel; each test gets its own services, scope and Window.
public sealed class WorkspaceWindowTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<WorkspaceViewModel> _window;

    public WorkspaceWindowTests()
    {
        _services = new ServiceCollection()
            .AddWorkspace()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        _window = RunicWindowTestHost.Create<WorkspaceViewModel>(_scope.ServiceProvider);
    }

#if (translations)
    [Test]
    public async Task The_window_opens_on_the_welcome_page_in_the_applications_language()
    {
        // The text a user sees: the ViewModel's reference, resolved with the application's catalog in its language.
        await _services.StartLanguageAsync("en");
        var greeting = _window.Root.View<WelcomeViewModel>(vm => vm.Main).Snapshot().Read(vm => vm.Greeting);
        var translations = _services.GetRequiredService<TranslationContext>();

        await Assert.That(_window.Content.Locale).IsEqualTo("en");
        await Assert.That(translations.Resolve(greeting).Text).IsEqualTo("Your first Runic View is ready.");
    }

    [Test]
    public async Task Choosing_a_language_switches_the_catalog_and_the_window()
    {
        await _services.StartLanguageAsync("en");
        (await _window.Root.ExecuteAsync(vm => vm.ChooseLanguageCommand, "\"de\"")).ThrowIfFailed();
        var greeting = _window.Root.View<WelcomeViewModel>(vm => vm.Main).Snapshot().Read(vm => vm.Greeting);

        await Assert.That(_window.Content.Locale).IsEqualTo("de");
        await Assert.That(_services.GetRequiredService<TranslationContext>().Resolve(greeting).Text)
            .IsEqualTo("Deine erste Runic View ist bereit.");
    }

    [Test]
    public async Task A_regional_or_unknown_language_starts_in_one_of_the_catalogs()
    {
        // RUNIC_LOCALE=de-CH: the catalog's German, so the page's language list shows it.
        await _services.StartLanguageAsync("de-CH");
        await Assert.That(_window.Content.Locale).IsEqualTo("de");
        await Assert.That(_window.ViewModel.Language).IsEqualTo("de");

        // A tag the catalog has no language for, or no tag at all, does not stop the start or a switch.
        await _services.StartLanguageAsync("xx_1");
        await Assert.That(_services.GetRequiredService<TranslationContext>().SupportedLocales).Contains(_window.Content.Locale!);
        (await _window.Root.ExecuteAsync(vm => vm.ChooseLanguageCommand, "\"not a tag\"")).ThrowIfFailed();
        await Assert.That(_services.GetRequiredService<TranslationContext>().SupportedLocales).Contains(_window.Content.Locale!);
    }
#else
    [Test]
    public async Task The_window_opens_on_the_welcome_page()
    {
        var welcome = _window.Root.View<WelcomeViewModel>(vm => vm.Main).Snapshot();

        await Assert.That(welcome.Read(vm => vm.Greeting)).IsEqualTo("Your first Runic View is ready.");
    }
#endif

    [Test]
    public async Task The_counter_counts_each_increment()
    {
        (await _window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).ThrowIfFailed();
        var counter = _window.Root.View<CounterViewModel>(vm => vm.Main);

        (await counter.ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();
        (await counter.ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();

        await Assert.That(counter.Snapshot().Read(vm => vm.Count)).IsEqualTo(2);
    }

    [Test]
    public async Task The_counter_keeps_its_count_when_the_welcome_page_returns()
    {
        (await _window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).ThrowIfFailed();
        (await _window.Root.View<CounterViewModel>(vm => vm.Main).ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();

        (await _window.Root.ExecuteAsync(vm => vm.ShowWelcomeCommand)).ThrowIfFailed();
        _ = _window.Root.View<WelcomeViewModel>(vm => vm.Main);
        (await _window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).ThrowIfFailed();

        await Assert.That(_window.Root.View<CounterViewModel>(vm => vm.Main).Snapshot().Read(vm => vm.Count)).IsEqualTo(1);
    }

    public async ValueTask DisposeAsync()
    {
        _window.Dispose();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}
