using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// W260-005: a window opened through OpenWindowAsync shows its root page when the open returns, with no
// discarded ResetAsync, and a failure of the root page fails the open. ReactiveUI and CommunityToolkit shells.
internal sealed class RegionStartTests
{
    [Test]
    public async Task ReactiveShellStartsItsRootPageBeforeTheOpenReturns()
    {
        await using var provider = Services<ReactiveStartShellViewModel>().BuildServiceProvider();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ReactiveStartShellWindow>();
        await RequireRootAndDetailAsync(window.ViewModel.Main, () => window.ViewModel.CurrentTitle, "ReactiveUI");
    }

    [Test]
    public async Task ToolkitShellStartsItsRootPageBeforeTheOpenReturns()
    {
        await using var provider = Services<ToolkitStartShellViewModel>().BuildServiceProvider();
        await using var window = await new TestWindowHost(provider).OpenWindowAsync<ToolkitStartShellWindow>();
        await RequireRootAndDetailAsync(window.ViewModel.Main, () => window.ViewModel.CurrentTitle, "CommunityToolkit");
    }

    [Test]
    public async Task ReactiveShellRootFailureFailsTheOpen() =>
        await RequireOpenFailsAsync<ReactiveStartShellWindow, ReactiveStartShellViewModel>();

    [Test]
    public async Task ToolkitShellRootFailureFailsTheOpen() =>
        await RequireOpenFailsAsync<ToolkitStartShellWindow, ToolkitStartShellViewModel>();

    private static async Task RequireRootAndDetailAsync(NavigationRegion<INavPageViewModel> main, Func<string> currentTitle, string flavour)
    {
        // No model turn has to run first: the region has the root page when the open returns.
        Require(main.Current is StartListViewModel && main.CurrentEntry is { Ownership: NavigationOwnership.Owned },
            $"{flavour}: the root page was not current when OpenWindowAsync returned.");
        await Until(() => currentTitle() == "list", $"{flavour}: the shell did not observe the root page (title '{currentTitle()}').");
        var list = (StartListViewModel)main.Current!;
        Require(await list.OpenDetailAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)) is NavigationResult<INavPageViewModel>.Committed
            && main.Current is NavEditorViewModel && main.CanGoBack,
            $"{flavour}: the root page could not navigate the region it was started in.");
        await Until(() => currentTitle() == "detail", $"{flavour}: the shell did not observe the detail page.");
    }

    private static async Task RequireOpenFailsAsync<TWindow, TViewModel>()
        where TWindow : RunicWindow<TViewModel>, new() where TViewModel : class
    {
        var released = 0;
        var services = Services<TViewModel>();
        services.AddScoped(_ => new ScopeRelease(() => Interlocked.Increment(ref released)));
        services.AddScoped<StartListSource>(provider =>
        {
            provider.GetRequiredService<ScopeRelease>();
            throw new RootPageFailedException();
        });
        await using var provider = services.BuildServiceProvider();
        Exception? error = null;
        try { await using var window = await new TestWindowHost(provider).OpenWindowAsync<TWindow>(); }
        catch (Exception caught) { error = caught; }
        var cause = error;
        while (cause is not null and not RootPageFailedException) cause = cause.InnerException;
        Require(cause is RootPageFailedException, $"{typeof(TViewModel).Name}: a failing root page did not fail OpenWindowAsync: {error}");
        await Until(() => Volatile.Read(ref released) == 1,
            $"{typeof(TViewModel).Name}: the window's scope was not released once after the failed open ({released}).");
    }

    private static ServiceCollection Services<TViewModel>() where TViewModel : class
    {
        var services = new ServiceCollection();
        services.AddRunicNavigation();
        services.AddScoped<StartNavigation>();
        services.AddSingleton<StartListSource>();
        services.AddScoped<TViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, TViewModel, IDisposable>>(_ => static (transport, content, model) => model switch
        {
            ReactiveStartShellViewModel reactive => new ReactiveStartShellBridge(transport, reactive, content: content),
            ToolkitStartShellViewModel toolkit => new ToolkitStartShellBridge(transport, toolkit, content: content),
            _ => throw new NotSupportedException(typeof(TViewModel).Name),
        });
        return services;
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException(message);
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RootPageFailedException() : Exception("The root page failed.");

    private sealed class ScopeRelease(Action released) : IDisposable
    {
        public void Dispose() => released();
    }
}
