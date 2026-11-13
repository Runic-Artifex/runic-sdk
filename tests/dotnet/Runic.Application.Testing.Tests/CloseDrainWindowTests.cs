using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// sdk#900 (W4a) on the window-level test host: a close drain keeps the Window open after its guards agreed, with its page
// connected, until its accepted work has ended; the operations' RunicOperationClose decide what is cancelled.
internal sealed class CloseDrainWindowTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static ServiceProvider Services(DrainingSettings? settings = null)
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddSingleton(settings ?? new DrainingSettings());
        services.AddScoped<DrainingViewModel>();
        services.AddScoped<Func<IBridgeTransport, WindowContentSession, DrainingViewModel, IDisposable>>(
            _ => (transport, content, vm) => new DrainingBridge(transport, vm, content: content));
        services.AddRunicWindowService();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static Task<RunicTestWindow> OpenAsync(ServiceProvider provider, RunicCloseDrain? drain) =>
        RunicWindowTestHost.CreateWindowAsync<DrainingWindow>(provider, hostOptions: new() { CloseDrain = drain });

    [Test]
    public async Task ARefusingGuardKeepsAdmissionOpenAsync()
    {
        await using var provider = Services();
        await using var window = await OpenAsync(provider, RunicCloseDrain.UntilDrained);
        var vm = (DrainingViewModel)window.ViewModel;
        using var release = Releasing(vm);
        vm.AllowClose = false;
        Require(!await Wait(window.RequestUserCloseAsync()) && window.IsOpen && !window.IsDraining,
            "A refused close closed or drained the Window.");
        // The drain starts only after every guard agreed: admission is still open, and no page was told of a close.
        var refresh = window.Root<DrainingViewModel>().Start(vm => vm.RefreshCommand);
        Require(refresh.Admission is RunicOperationAdmissionKind.Accepted, $"A refused close stopped admission: {refresh.AdmissionJson}");
        Require(!window.Host<DrainingViewModel>().Transport.Messages.Any(message => message.Kind == "closing"),
            "A refused close told the page that the Window closes.");
        vm.Refresh.Release.SetResult();
        await Wait(refresh.WaitAsync());
    }

    [Test]
    public async Task AnApprovedCloseKeepsTheWindowOpenUntilItsWorkEndedAsync()
    {
        await using var provider = Services();
        await using var window = await OpenAsync(provider, RunicCloseDrain.UntilDrained);
        var vm = (DrainingViewModel)window.ViewModel;
        using var release = Releasing(vm);
        var host = window.Host<DrainingViewModel>();
        host.Transport.HelloWith("page", ["presented/1"]);
        var root = window.Root<DrainingViewModel>();
        root.Mount();
        var query = root.Start(vm => vm.QueryCommand);
        var refresh = root.Start(vm => vm.RefreshCommand);
        var save = root.Start(vm => vm.SaveCommand);
        await Wait(Task.WhenAll(vm.Query.Started.Task, vm.Refresh.Started.Task, vm.Save.Started.Task));

        // The decision returns once the guard agreed; the Window stays open and drains.
        Require(await Wait(window.RequestUserCloseAsync()) && window.IsOpen && window.IsDraining,
            $"The approved close did not keep the Window open (open {window.IsOpen}, draining {window.IsDraining}).");
        Require(!provider.GetRequiredService<IRunicWindowService>().Windows.Contains(window.Window),
            "A draining Window is still listed as open.");
        await Until(() => host.Transport.Messages.Any(message => message.Kind == "closing" && message.ConnectionKey == "page"),
            "The page was not told that the Window closes.");
        var closing = host.Transport.Messages.Single(message => message.Kind == "closing" && message.ConnectionKey == "page");
        Require(closing.Json == """{"kind":"closing","draining":true}""", $"The closing push is {closing.Json}.");
        // The class's Cancel: asked to cancel when the close was decided.
        Require((await Wait(query.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled, "The read-only query was not cancelled.");
        // Admission stopped.
        var late = root.Start(vm => vm.RefreshCommand, requestId: "late");
        Require(late.Admission is RunicOperationAdmissionKind.Rejected && late.Reason == "owner-closing",
            $"A draining Window admitted an operation: {late.AdmissionJson}");

        // The page stays connected and receives the Window's state while it drains.
        var before = host.Transport.Messages.Count;
        await host.Content.ModelContext!.InvokeAsync(() => vm.Status = "half done");
        await Until(() => host.Transport.Messages.Skip(before).Any(message => message.Kind is "state" or "frame"
                && message.Json.Contains("half done", StringComparison.Ordinal)),
            "The draining Window's page did not receive its state.");

        // A second user close during the drain is ignored: the guard is not asked again.
        Require(await Wait(window.RequestUserCloseAsync()) && window.IsDraining && vm.GuardAsked == 1,
            $"A second close was not ignored (guard asked {vm.GuardAsked} times).");
        // No timeout: the fake clock passing changes nothing, and nothing is cancelled.
        ((FakeTimeProvider)host.Time).Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Require(window.IsOpen && !vm.Refresh.Cancelled && !vm.Save.Cancelled, "UntilDrained closed the Window or cancelled work.");

        vm.Refresh.Release.SetResult();
        await Wait(refresh.WaitAsync());
        Require(window.IsOpen, "The Window closed while its durable operation ran.");
        vm.Save.Release.SetResult();
        Require((await Wait(save.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "The durable operation did not succeed.");
        await Wait(window.Window.WaitForCloseAsync());
        Require(!window.IsOpen && !window.IsDraining, "The Window did not close after its work ended.");
    }

    [Test]
    public async Task SaveAndCloseFromAcceptedWorkDoesNotDeadlockAsync()
    {
        await using var provider = Services();
        await using var window = await OpenAsync(provider, RunicCloseDrain.UntilDrained);
        var vm = (DrainingViewModel)window.ViewModel;
        using var release = Releasing(vm);
        var saveAndClose = window.Root<DrainingViewModel>().Start(vm => vm.SaveAndCloseCommand);
        // RequestCloseAsync returns once the close was approved, not when the Window has gone, which waits for this work.
        Require(await Wait(vm.CloseRequested.Task), "Save and close was refused.");
        Require((await Wait(saveAndClose.WaitAsync())).Kind is RunicOperationStatusKind.Succeeded, "Save and close did not succeed.");
        await Wait(window.Window.WaitForCloseAsync());
        Require(!window.IsOpen, "The Window did not close after Save and close.");
    }

    [Test]
    public async Task ATimedDrainCancelsUnmarkedOperationsOnlyAsync()
    {
        await using var provider = Services();
        var window = await OpenAsync(provider, RunicCloseDrain.For(TimeSpan.FromSeconds(30)));
        var vm = (DrainingViewModel)window.ViewModel;
        using var release = Releasing(vm);
        var host = window.Host<DrainingViewModel>();
        var root = window.Root<DrainingViewModel>();
        var refresh = root.Start(vm => vm.RefreshCommand);
        var save = root.Start(vm => vm.SaveCommand);
        await Wait(Task.WhenAll(vm.Refresh.Started.Task, vm.Save.Started.Task));
        Require(await Wait(window.RequestUserCloseAsync()) && window.IsDraining, "The close did not drain.");
        ((FakeTimeProvider)host.Time).Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        Require(window.IsOpen && !vm.Refresh.Cancelled, "The drain ended before its timeout.");
        ((FakeTimeProvider)host.Time).Advance(TimeSpan.FromSeconds(1));
        // At the timeout the unmarked operation is asked to cancel and the window closes; the durable one keeps running.
        Require((await Wait(refresh.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled, "The unmarked operation was not cancelled at the timeout.");
        await Wait(window.Window.WaitForCloseAsync());
        Require(!vm.Save.Cancelled && !vm.Save.Ended, "The timeout cancelled the durable operation.");
        // Disposal waits for the durable work, which keeps the Window's scope.
        var disposal = window.DisposeAsync().AsTask();
        await Task.Delay(50);
        Require(!disposal.IsCompleted, "Disposal did not wait for the durable work.");
        vm.Save.Release.SetResult();
        await Wait(disposal);
    }

    [Test]
    public async Task TheApplicationsOwnCloseEndsTheDrainAsync()
    {
        await using var provider = Services();
        var window = await OpenAsync(provider, RunicCloseDrain.UntilDrained);
        var vm = (DrainingViewModel)window.ViewModel;
        using var release = Releasing(vm);
        var root = window.Root<DrainingViewModel>();
        var refresh = root.Start(vm => vm.RefreshCommand);
        var save = root.Start(vm => vm.SaveCommand);
        await Wait(Task.WhenAll(vm.Refresh.Started.Task, vm.Save.Started.Task));
        Require(await Wait(window.RequestUserCloseAsync()) && window.IsDraining, "The close did not drain.");
        // CloseAsync without a timeout, as DisposeAsync: the visible window closes now, unmarked work is asked to
        // cancel, and durable work is not.
        var result = await Wait(window.Window.CloseAsync(TimeSpan.Zero));
        Require(!window.IsOpen && result.RemainingDurableWork == 1, $"The close ended {result} with the Window open {window.IsOpen}.");
        Require((await Wait(refresh.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled && !vm.Save.Cancelled,
            "The application's close did not honour the operations' policies.");
        vm.Save.Release.SetResult();
        await Wait(window.DisposeAsync());
    }

    [Test]
    public async Task WithoutADrainTheWindowClosesAsBeforeAsync()
    {
        await using var provider = Services();
        await using var window = await OpenAsync(provider, drain: null);
        var vm = (DrainingViewModel)window.ViewModel;
        using var release = Releasing(vm);
        var refresh = window.Root<DrainingViewModel>().Start(vm => vm.RefreshCommand);
        await Wait(vm.Refresh.Started.Task);
        Require(await Wait(window.RequestUserCloseAsync()) && !window.IsOpen && !window.IsDraining,
            "A Window without a drain stayed open after its close.");
        Require((await Wait(refresh.WaitAsync())).Kind is RunicOperationStatusKind.Cancelled, "The close did not cancel as before.");
        Require(window.Host<DrainingViewModel>().Transport.Messages.Where(message => message.Kind == "closing")
            .All(message => message.Json == """{"kind":"closing"}"""), "A close without a drain said draining.");
    }

    [Test]
    public async Task WindowsDrainASharedScopeAsync()
    {
        var shared = new AcceptedWorkScope();
        await using var provider = Services(new DrainingSettings { Shared = shared });
        await using var first = await OpenAsync(provider, RunicCloseDrain.UntilDrained);
        var windows = provider.GetRequiredService<IRunicWindowService>();
        await windows.OpenWindowAsync<DrainingWindow>();
        var second = first.OpenedWindows.Single();

        var before = Signal();
        var after = Signal();
        using var release = new Release(() => { before.TrySetResult(); after.TrySetResult(); });
        var earlier = shared.RunAsync(() => before.Task);
        Require(await Wait(first.RequestUserCloseAsync()) && first.IsDraining, "The first Window did not drain.");
        // The scope still accepts work; the first Window does not wait for work accepted after its close.
        var later = shared.RunAsync(() => after.Task);
        Require(await Wait(second.RequestUserCloseAsync()) && second.IsDraining, "The second Window did not drain.");
        before.SetResult();
        await Wait(first.Window.WaitForCloseAsync());
        await Task.Delay(50);
        Require(second.IsOpen, "The second Window did not wait for the work accepted before its close.");
        after.SetResult();
        await Wait(second.Window.WaitForCloseAsync());
        await Wait(Task.WhenAll(earlier, later));
        await shared.DisposeAsync();
    }

    // A failed case must not leave durable work running, which the Window's disposal waits for: released before it.
    private static Release Releasing(DrainingViewModel vm) => new Release(() =>
    {
        vm.Query.Release.TrySetResult();
        vm.Refresh.Release.TrySetResult();
        vm.Save.Release.TrySetResult();
    });

    private sealed class Release(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task Wait(Task task) => await task.WaitScaledAsync(Timeout);

    private static async Task<T> Wait<T>(Task<T> task) => await task.WaitScaledAsync(Timeout);

    private static async Task<T> Wait<T>(ValueTask<T> task) => await task.AsTask().WaitScaledAsync(Timeout);

    private static async Task Wait(ValueTask task) => await task.AsTask().WaitScaledAsync(Timeout);

    private static async Task Until(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Timeout);
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
}
