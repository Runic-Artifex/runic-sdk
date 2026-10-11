using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;

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

    [Test]
    public async Task The_window_opens_on_the_welcome_page()
    {
        var welcome = _window.Root.View<WelcomeViewModel>(vm => vm.Main).Snapshot();

        await Assert.That(welcome.Read(vm => vm.Greeting)).IsEqualTo("Your first Runic View is ready.");
    }

    [Test]
    public async Task The_counter_counts_each_increment()
    {
        (await _window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).EnsureOk();
        var counter = _window.Root.View<CounterViewModel>(vm => vm.Main);

        (await counter.ExecuteAsync(vm => vm.IncrementCommand)).EnsureOk();
        (await counter.ExecuteAsync(vm => vm.IncrementCommand)).EnsureOk();

        await Assert.That(counter.Snapshot().Read(vm => vm.Count)).IsEqualTo(2);
    }

    [Test]
    public async Task The_counter_keeps_its_count_when_the_welcome_page_returns()
    {
        (await _window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).EnsureOk();
        (await _window.Root.View<CounterViewModel>(vm => vm.Main).ExecuteAsync(vm => vm.IncrementCommand)).EnsureOk();

        (await _window.Root.ExecuteAsync(vm => vm.ShowWelcomeCommand)).EnsureOk();
        _ = _window.Root.View<WelcomeViewModel>(vm => vm.Main);
        (await _window.Root.ExecuteAsync(vm => vm.ShowCounterCommand)).EnsureOk();

        await Assert.That(_window.Root.View<CounterViewModel>(vm => vm.Main).Snapshot().Read(vm => vm.Count)).IsEqualTo(1);
    }

    public async ValueTask DisposeAsync()
    {
        _window.Dispose();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}
