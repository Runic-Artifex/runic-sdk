using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReactiveUI.Primitives;
using ReactiveUI.Primitives.Concurrency;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// #132: every way a ReactiveUI ViewModel reaches a window either binds it to the
// window's model context or fails at once, naming the ViewModel type. None of
// these ViewModels calls RunicModelContextRegistry.Bind.
internal static class ModelContextPresentationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static RunicModelContextRegistry Registry => RunicModelContextRegistry.Shared;

    public static async Task RunAsync()
    {
        await WindowRootAndCreatedChildAreBoundWithoutManualBind();
        await RegionEntriesAreBoundByTheNavigatorAndWindow();
        await HandBuiltSessionBindsTheBridgedModel();
        await ForeignContextFailsFastNamingTheViewModel();
    }

    // The host path of Desktop and CS-WebUI windows: the ViewModels come from the
    // window's service scope, and the window is given that scope's context.
    private static async Task WindowRootAndCreatedChildAreBoundWithoutManualBind()
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped<ContextShellViewModel>();
        services.AddScoped<ContextPageViewModel>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<IRunicModelContext>();
        var shell = scope.ServiceProvider.GetRequiredService<ContextShellViewModel>();
        var welcome = shell.Main!;
        using (var host = new RunicWindowTestHost<ContextShellViewModel>(shell,
            (transport, content, vm) => new ContextShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new ContextPageViewLocator() }))
        {
            Require(BoundTo(shell, context), "The window did not bind its root ViewModel to the window's context.");
            var first = host.Root.Snapshot().Reference(vm => vm.Main)!;
            Require(BoundTo(welcome, context), "The window did not bind the child ViewModel it presents.");
            await RunAsync(host, $"content{first.Id}Increment");
            Require(welcome.Count == 1, "The presented child's command did not run.");

            // A child the shell creates in its own command is bound when it is presented.
            await RunAsync(host, "contextShellOpen");
            var created = shell.Main!;
            Require(!ReferenceEquals(created, welcome), "The shell command did not create a child.");
            var second = host.Root.Snapshot().Reference(vm => vm.Main)!;
            Require(second.Id != first.Id && BoundTo(created, context),
                "The window did not bind a child ViewModel created by its ViewModel.");
            await RunAsync(host, $"content{second.Id}Increment");
            Require(created.Count == 1, "The created child's command did not run on the window's context.");
            Require(!Registry.TryGet(welcome, out _), "The window kept the replaced child bound.");
            created.Dispose();
        }
        Require(!Registry.TryGet(shell, out _), "Closing the window kept its root ViewModel bound.");
    }

    private static async Task RegionEntriesAreBoundByTheNavigatorAndWindow()
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var home = new NavHomeViewModel();
        using var shell = new ReactiveNavShellViewModel(navigator, context, home);
        using var host = new RunicWindowTestHost<ReactiveNavShellViewModel>(shell,
            (transport, content, vm) => new ReactiveNavShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        _ = host.Root.Snapshot().Reference(vm => vm.Main);
        Require(BoundTo(home, context), "The window did not bind a borrowed region entry it presents.");

        await ReactiveCommandExecution.Execute(shell.OpenCommand, RxVoid.Default, CancellationToken.None).WaitAsync(Timeout);
        Require(shell.Main.Current is NavEditorViewModel editor && BoundTo(editor, context),
            "The navigator did not bind the region entry it owns.");
        _ = host.Root.Snapshot().Reference(vm => vm.Main);
        Require(BoundTo(shell.Main.Current!, context), "Presenting the owned region entry changed its context.");
    }

    // The session of issue #132: built by hand, without a root ViewModel. Its bridge
    // binds the model for the bridge's lifetime instead of falling back to a lock.
    private static async Task HandBuiltSessionBindsTheBridgedModel()
    {
        using var transport = new InMemoryViewTransport();
        using var session = new WindowContentSession(transport, new ContextPageViewLocator());
        var context = session.ModelContext!;
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        using var page = new ContextPageViewModel(scheduler);
        using var shell = new ContextShellViewModel(page, scheduler);
        var bridge = new ContextShellBridge(transport, shell, content: session);
        try
        {
            Require(BoundTo(shell, context), "A bridge attached to a hand-built session left its model unbound.");
            await transport.CallAsync("contextShellOpen").AsTask().WaitAsync(Timeout);
            Require(BoundTo(shell.Main!, context), "A hand-built session did not bind the child it presents.");
        }
        finally { bridge.Dispose(); }
        Require(!Registry.TryGet(shell, out _), "Disposing the bridge kept the model it bound.");
        shell.Main?.Dispose();
    }

    private static async Task ForeignContextFailsFastNamingTheViewModel()
    {
        await using var context = new RunicModelContext();
        await using var other = new RunicModelContext();
        var scheduler = new RunicReactiveSchedulerProvider().For(context);
        using var page = new ContextPageViewModel(scheduler);
        using var shell = new ContextShellViewModel(page, scheduler);

        // Window root.
        using (Registry.Bind(other, shell))
        {
            var error = Throws(() => new RunicWindowTestHost<ContextShellViewModel>(shell,
                (transport, content, vm) => new ContextShellBridge(transport, vm, content: content),
                new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new ContextPageViewLocator() }).Dispose());
            RequireNamed<ContextShellViewModel>(error, "window root");
        }

        using var host = new RunicWindowTestHost<ContextShellViewModel>(shell,
            (transport, content, vm) => new ContextShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new ContextPageViewLocator() });

        // A child ViewModel presented by the window.
        using var foreign = new ContextPageViewModel(scheduler);
        using (Registry.Bind(other, foreign))
        {
            var error = Throws(() => host.Content.Expose("page", foreign,
                (transport, model, route) => new ContextPageBridge(transport, model, route, content: host.Content)));
            RequireNamed<ContextPageViewModel>(error, "presented child");

            // A bridge attached directly to the window.
            error = Throws(() => new ContextPageBridge(host.Transport, foreign, "direct", content: host.Content).Dispose());
            RequireNamed<ContextPageViewModel>(error, "directly bridged ViewModel");
        }

        // A region entry the navigator owns.
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var region = navigator.CreateRegion<INavPageViewModel>(new object());
        var entry = new NavEditorViewModel("foreign");
        using (Registry.Bind(other, entry))
        {
            var result = await region.PushAsync(NavigationTarget.Own<INavPageViewModel>(entry)).AsTask().WaitAsync(Timeout);
            var failure = result as NavigationResult<INavPageViewModel>.Failed
                ?? throw new InvalidOperationException($"Owning a ViewModel bound to another context did not fail: {result}.");
            RequireNamed<NavEditorViewModel>(failure.Error, "owned region entry");
        }
    }

    private static bool BoundTo(object model, IRunicModelContext context) =>
        Registry.TryGet(model, out var bound) && ReferenceEquals(bound, context);

    private static async Task RunAsync(RunicWindowTestHost<ContextShellViewModel> host, string route)
    {
        using var reply = JsonDocument.Parse(await host.Transport.CallAsync(route).AsTask().WaitAsync(Timeout));
        Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"{route} failed: {reply.RootElement}");
    }

    private static Exception Throws(Action action)
    {
        try { action(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("The action did not fail.");
    }

    private static void RequireNamed<T>(Exception error, string path) =>
        Require(error is InvalidOperationException && error.Message.Contains(typeof(T).FullName!, StringComparison.Ordinal),
            $"A {path} bound to another context did not fail fast naming {typeof(T).Name}: {error}");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
