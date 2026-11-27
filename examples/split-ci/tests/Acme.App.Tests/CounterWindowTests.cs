using Acme.App;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;

namespace Acme.App.Tests;

// Drives the Window's real ViewModel and generated Bridge as the page does, without a browser or native window, so
// the tests run on the Linux runners although the application ships for Windows.
public sealed class CounterWindowTests : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;
    private readonly RunicWindowTestHost<CounterViewModel> _window;

    public CounterWindowTests()
    {
        _services = new ServiceCollection()
            .AddAcmeApp()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        _window = RunicWindowTestHost.Create<CounterViewModel>(_scope.ServiceProvider);
    }

    [Test]
    public async Task The_counter_starts_at_zero()
    {
        await Assert.That(_window.Root.Snapshot().Read(vm => vm.Count)).IsEqualTo(0);
    }

    [Test]
    public async Task The_counter_counts_each_increment()
    {
        (await _window.Root.ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();
        (await _window.Root.ExecuteAsync(vm => vm.IncrementCommand)).ThrowIfFailed();

        await Assert.That(_window.Root.Snapshot().Read(vm => vm.Count)).IsEqualTo(2);
    }

    public async ValueTask DisposeAsync()
    {
        _window.Dispose();
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
    }
}
