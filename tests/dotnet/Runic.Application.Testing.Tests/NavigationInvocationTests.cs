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

    // A result request whose caller cancels after the commit goes back from the entry on its own (a detached Back). That
    // Back runs for no page: its guard does not ask the page whose command cancelled the token, in a window two pages
    // share, and follows the rules outside a scope, here the ViewQuestion's fallback.
    [Test]
    public async Task ADetachedBackOfACancelledResultRequestAsksNoPageAsync()
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
        var aliceAsked = 0;
        var bobAsked = 0;
        using var alicePage = (await host.Root.MountPresentationAsync(clientKey: "client-alice", connectionKey: "connection-alice"))
            .Handle(model => model.ConfirmLeave, (string _) => { Interlocked.Increment(ref aliceAsked); return true; });
        using var bobPage = (await host.Root.MountPresentationAsync(clientKey: "client-bob", connectionKey: "connection-bob"))
            .Handle(model => model.ConfirmLeave, (string _) => { Interlocked.Increment(ref bobAsked); return true; });
        var bob = host.Content.Dispatcher.ConnectionKeyOf("connection-bob")!;

        var guarded = new SyncBackGuardedPage(shell);
        using var caller = new CancellationTokenSource();
        var request = shell.Main.PushForResult<bool>(NavigationTarget.Own<object>(guarded), cancellationToken: caller.Token);
        Require(await request.Transition is NavigationResult<object>.Committed, "The result request's page was not pushed.");

        // Bob's command cancels the caller's token.
        using (RunicInteractionInvocation.Enter(host.Content, host.RootRoute, "client-bob", bob))
            caller.Cancel();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (guarded.Answers.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(guarded.Answers.SequenceEqual([false]) && bobAsked == 0 && aliceAsked == 0,
            $"The detached Back's guard asked a page (Bob {bobAsked}, Alice {aliceAsked}, answers {string.Join(",", guarded.Answers)}).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
