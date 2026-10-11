using System.Text.Json;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ReactiveNavigationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The back command needs no scheduler and returns a NavigationOutcome, which the generated web client
    // receives as a typed result (W250-014).
    [Test]
    public async Task BridgedBackCommandReturnsOutcomeAsync()
    {
        Require(RunicReactiveSchedulerProvider.InstallMainThreadScheduler(),
            "The model-context main-thread scheduler was not installed.");
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var home = new NavHomeViewModel();
        using var model = new ReactiveNavShellViewModel(navigator, context, home);
        using var host = new RunicWindowTestHost<ReactiveNavShellViewModel>(model,
            (transport, content, vm) => new ReactiveNavShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        Require(!ReactiveCommandExecution.CanExecute(model.BackCommand, RxVoid.Default), "Back was enabled without history.");
        await model.Main.PushAsync(NavigationTarget.Own<INavPageViewModel>(new NavEditorViewModel("editor")));
        await Until(() => ReactiveCommandExecution.CanExecute(model.BackCommand, RxVoid.Default));

        var executing = new List<RunicModelContext?>();
        using var observed = model.BackCommand.IsExecuting.Subscribe(new Observer<bool>(_ => { lock (executing) executing.Add(RunicModelContext.Current); }));
        const string requestId = "back";
        using var admission = JsonDocument.Parse(host.Transport.Call("reactiveNavShellStartBack", new(StringValue: requestId)));
        Require(admission.RootElement.GetProperty("kind").GetString() == "accepted", "The web back command was not admitted.");
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        using var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))).AsTask().WaitAsync(Timeout));
        var root = completion.RootElement;
        Require(root.GetProperty("kind").GetString() == "succeeded"
            && root.GetProperty("result").GetProperty("kind").GetString() is "committed" or "Committed"
            && root.GetProperty("result").GetProperty("rejection").ValueKind == JsonValueKind.Null,
            $"The back command did not return a committed outcome: {root}.");
        Require(model.Main.Current == home, "The bridged back command did not go back.");
        await Until(() => { lock (executing) return executing.Count >= 3; });
        lock (executing)
            Require(executing.Skip(1).All(current => current == context),
                $"The back command's IsExecuting was not delivered on the window's model context: {string.Join(", ", executing.Select(c => c is null ? "null" : c == context ? "window" : "other"))}.");
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) => throw new InvalidOperationException("The sequence failed.", error);
        public void OnNext(T value) => next(value);
    }

    [Test]
    [Arguments(false, "committed")]
    [Arguments(false, "guard")]
    [Arguments(false, "cancelled")]
    [Arguments(true, "committed")]
    [Arguments(true, "guard")]
    [Arguments(true, "cancelled")]
    [DisplayName("FrameworkCommandPreservesNavigationAsync(web: $web, outcome: $outcome)")]
    public async Task FrameworkCommandPreservesNavigationAsync(bool web, string outcome)
    {
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = new NavHomeViewModel
        {
            Guard = async token => { entered.TrySetResult(); return await release.Task.WaitAsync(token); },
        };
        using var model = new ReactiveNavShellViewModel(navigator, context, home);
        using var host = new RunicWindowTestHost<ReactiveNavShellViewModel>(model,
            (transport, content, vm) => new ReactiveNavShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        Require(!host.Root.Snapshot().Read(vm => vm.Busy), "The initial observable property was busy.");
        await Until(() => ReactiveCommandExecution.CanExecute(model.OpenCommand, RxVoid.Default));

        using var cancellation = new CancellationTokenSource();
        const string requestId = "navigation";
        string? contract = null;
        Task<RxVoid>? native = null;
        if (web)
        {
            using var admission = JsonDocument.Parse(host.Transport.Call("reactiveNavShellStartOpen", new(StringValue: requestId)));
            Require(admission.RootElement.GetProperty("kind").GetString() == "accepted", "The web navigation command was not admitted.");
            contract = admission.RootElement.GetProperty("contract").GetString()!;
        }
        else native = ReactiveCommandExecution.Execute(model.OpenCommand, RxVoid.Default, cancellation.Token);
        await entered.Task.WaitAsync(Timeout);
        try
        {
            await Until(() => host.Root.Snapshot().Read(vm => vm.Busy)
                && !ReactiveCommandExecution.CanExecute(model.OpenCommand, RxVoid.Default));
            Require(model.Main.Current == home && model.Main.History.Count == 0,
                "The framework command committed before its guard answered.");
            if (web)
            {
                using var unavailable = JsonDocument.Parse(host.Transport.Call("reactiveNavShellStartOpen", new(StringValue: "second")));
                Require(unavailable.RootElement.GetProperty("kind").GetString() == "rejected"
                    && unavailable.RootElement.GetProperty("reason").GetString() == "unavailable",
                    "The generated web bridge ignored observable command availability.");
            }
            if (outcome == "cancelled")
            {
                if (web)
                    host.Transport.Call("__runicOperationCancel", new(StringValue: JsonSerializer.Serialize(new { contract, requestId })));
                else cancellation.Cancel();
            }
            else release.SetResult(outcome == "committed");

            if (web)
            {
                using var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
                    new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))).AsTask().WaitAsync(Timeout));
                Require(completion.RootElement.GetProperty("kind").GetString() == (outcome == "cancelled" ? "cancelled" : "succeeded"),
                    "The bridge command ended with the wrong operation terminal.");
            }
            else
            {
                try { await native!.WaitAsync(Timeout); }
                catch (OperationCanceledException) when (outcome == "cancelled") { }
            }
            await Until(() => host.Root.Snapshot().Read(vm => vm.Outcome) == outcome
                && !host.Root.Snapshot().Read(vm => vm.Busy)
                && ReactiveCommandExecution.CanExecute(model.OpenCommand, RxVoid.Default));
            Require(outcome == "committed"
                ? model.Main.Current is NavEditorViewModel && model.Main.History.Single().Content == home
                : model.Main.Current == home && model.Main.History.Count == 0,
                $"The {(web ? "web" : "native")} consumer did not preserve the {outcome} navigation outcome.");
        }
        finally { release.TrySetResult(false); }
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Navigation command state did not settle.");
            await Task.Delay(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
