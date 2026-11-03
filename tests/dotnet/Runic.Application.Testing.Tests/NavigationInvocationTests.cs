using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A navigation keeps the identity of the command that started it until the navigation ends: a synchronous command that
// starts a Back without awaiting it ends at once, but the departing page's guard still asks the page that clicked.
internal sealed class NavigationInvocationTests
{
    [Test]
    public async Task AGuardOfANavigationThatASynchronousCommandStartedAsksTheInvokingPageAsync()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<SyncBackShellViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, SyncBackShellViewModel, IDisposable>>(
            _ => (transport, content, vm) => new SyncBackShellBridge(transport, vm, content: content));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var shell = scope.ServiceProvider.GetRequiredService<SyncBackShellViewModel>();
        using var host = RunicWindowTestHost.Create(scope.ServiceProvider, shell);
        var asked = 0;
        using var page = (await host.Root.MountPresentationAsync())
            .Handle(model => model.ConfirmLeave, (string _) => { Interlocked.Increment(ref asked); return true; });
        var guarded = new SyncBackGuardedPage(shell);
        var pushed = await shell.Main.PushAsync(NavigationTarget.Own<object>(guarded));
        Require(pushed is NavigationResult<object>.Committed && shell.Main.Current == guarded, $"The page was not pushed: {pushed}");

        // [RelayCommand] private void Back() => _ = Main.BackAsync(); the command ends before the guard asks.
        (await host.Root.ExecuteAsync(model => model.BackCommand)).ThrowIfFailed();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (shell.Main.Current != shell.Home && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(asked == 1 && shell.Main.Current == shell.Home,
            $"The guard did not ask the page that clicked (asked {asked}); the fallback kept the page.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
