using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A1: OpenWindowAsync<TWindow, TParameter> hands a value to the new Window's scope, and IRunicWindowHost opens a Window
// with an activation context on every host.
internal sealed class WindowParameterTests
{
    private static ServiceCollection Services(bool windowService = true)
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<WindowParameterViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, WindowParameterViewModel, IDisposable>>(
            _ => (transport, content, vm) => new WindowParameterBridge(transport, vm, content: content));
        if (windowService) services.AddRunicWindowService();
        return services;
    }

    [Test]
    public async Task EachWindowReceivesItsOwnParameterAsync()
    {
        await using var provider = Services().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var host = new TestWindowHost(provider);
        var opened = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
            host.OpenWindowAsync<WindowParameterWindow, WindowParameterRequest>(new($"part {index}")).AsTask()));
        try
        {
            for (var index = 0; index < opened.Length; index++)
            {
                Require(opened[index].ViewModel.Title == $"part {index}", $"Window {index} received '{opened[index].ViewModel.Title}'.");
                Require(opened[index].Services.GetRequiredService<IRunicWindowParameter<WindowParameterRequest>>().Value.Title == $"part {index}",
                    "The Window's scope did not keep its parameter.");
            }
            // IRunicWindowService opens on the host that opened the first Window.
            var service = provider.GetRequiredService<IRunicWindowService>();
            var more = await service.OpenWindowAsync<WindowParameterWindow, WindowParameterRequest>(new("from a command"));
            Require(more.ViewModel.Title == "from a command", "IRunicWindowService did not hand its parameter to the Window.");
            await more.DisposeAsync();
        }
        finally
        {
            foreach (var window in opened) await window.DisposeAsync();
        }
    }

    [Test]
    public async Task AWindowWithoutItsParameterFailsClearlyAsync()
    {
        await using var provider = Services().BuildServiceProvider();
        var host = new TestWindowHost(provider);
        // Opened without a parameter: the ViewModel's constructor reads none.
        var error = await Catch<InvalidOperationException>(() => host.OpenWindowAsync<WindowParameterWindow>().AsTask());
        Require(error?.Message.Contains("without a parameter", StringComparison.Ordinal) == true, $"A missing parameter was not reported: {error}");
        // Opened with another type of parameter.
        error = await Catch<InvalidOperationException>(() => host.OpenWindowAsync<WindowParameterWindow, string>("text").AsTask());
        Require(error?.Message.Contains("String parameter, not a WindowParameterRequest", StringComparison.Ordinal) == true,
            $"A parameter of another type was not reported: {error}");

        await using var unregistered = Services(windowService: false)
            .AddScoped<IRunicWindowParameter<WindowParameterRequest>>(_ => new RunicWindowParameter<WindowParameterRequest>(new("fixed")))
            .BuildServiceProvider();
        var configuration = await Catch<RunicWindowConfigurationException>(() => new TestWindowHost(unregistered)
            .OpenWindowAsync<WindowParameterWindow, WindowParameterRequest>(new("lost")).AsTask());
        Require(configuration?.Diagnostics.Single().Code == RunicWindowConfigurationException.WindowParameterNotRegisteredCode
            && configuration.Message.Contains("AddRunicWindowService", StringComparison.Ordinal),
            $"A parameter without AddRunicWindowService was not reported: {configuration}");
    }

    [Test]
    public async Task HostsWithoutActivationOpenTheWindowAsync()
    {
        await using var provider = Services().AddScoped<IRunicWindowParameter<WindowParameterRequest>>(
            _ => new RunicWindowParameter<WindowParameterRequest>(new("activated"))).BuildServiceProvider();
        IRunicWindowHost host = new TestWindowHost(provider);
        await using var window = await host.OpenWindowAsync<WindowParameterWindow>(null,
            new Runic.Platform.DesktopActivationContext(activationToken: "token"));
        Require(window.ViewModel.Title == "activated", "The default IRunicWindowHost.OpenWindowAsync did not open the Window.");
        // A fake window service that does not override the parameter overload says so.
        IRunicWindowService fake = new FakeWindowService();
        Require(await Catch<NotSupportedException>(() => fake.OpenWindowAsync<WindowParameterWindow, WindowParameterRequest>(new("x")).AsTask()) is not null,
            "A window service without the parameter overload did not report it.");
    }

    private sealed class FakeWindowService : IRunicWindowService
    {
        public ValueTask<TWindow> OpenWindowAsync<TWindow>(RunicWindowOptions? options = null, CancellationToken cancellationToken = default)
            where TWindow : class, IRunicWindow, new() => throw new InvalidOperationException("Not used.");
    }

    private static async Task<TException?> Catch<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); return null; }
        catch (TException error) { return error; }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
