using System.Text.Json;
using ReactiveUI.Primitives;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Application.Views.ReactiveUI.Generated;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ReactiveNavigationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // W250-034/035: the editor's bridged Close command asks its ReactiveLeaveConfirmation through the generated
    // interaction; the web View answers no, then yes. Save and Cancel complete and dismiss the request.
    [Test]
    public async Task WebViewAnswersLeaveConfirmationAsync()
    {
        Require(ReactiveSchedulers.InstallMainThreadScheduler(),
            "The model-context main-thread scheduler was not installed.");
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var home = new NavHomeViewModel();
        using var model = new ReactiveNavShellViewModel(navigator, context, home);
        using var host = new RunicWindowTestHost<ReactiveNavShellViewModel>(model,
            (transport, content, vm) => new ReactiveNavShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        var contract = $"{BridgeContractShape.Compute(typeof(ReactiveNavEditorViewModel))}:interaction:ConfirmDiscard";

        // The browser's trusted identity; an interaction is delivered only to the connection whose command asked.
        const string client = "editor-client";
        const string connection = "editor-connection";

        async Task<(NavigationResultRequest<INavPageViewModel, string> Request, RunicViewDriver<ReactiveNavEditorViewModel> View, string Mount)> OpenAsync()
        {
            var request = model.Main.PushForResult<string>(
                NavigationTarget.Create<INavPageViewModel>((_, entry) => new ReactiveNavEditorViewModel(entry)));
            Require(await request.Transition.AsTask().WaitAsync(Timeout) is NavigationResult<INavPageViewModel>.Committed,
                "The editor was not pushed.");
            await Until(() => host.Root.Snapshot().Reference(vm => vm.Main)?.Kind == "reactiveNavEditor");
            var view = host.Root.View<ReactiveNavEditorViewModel>(vm => vm.Main);
            return (request, view, view.Mount(clientKey: client, connectionKey: connection));
        }

        static string Outcome(string kind, JsonElement? result) =>
            kind != "succeeded" ? kind
            : result!.Value.GetProperty("kind").GetString()!.ToLowerInvariant()
                + (result.Value.GetProperty("rejection").ValueKind == JsonValueKind.Null ? ""
                    : ":" + result.Value.GetProperty("rejection").GetString()!.ToLowerInvariant());

        // Close with a draft from the browser: the same connection receives the typed prompt and answers.
        async Task<string> CloseAnsweringAsync(RunicViewDriver<ReactiveNavEditorViewModel> view, string mount, bool answer)
        {
            var wait = host.Transport.CallAsync(BridgeInteractionRouter.WaitRoute, new(StringValue: JsonSerializer.Serialize(new
            {
                route = view.Route,
                presentationId = mount,
                handlers = new[] { new { name = "confirmDiscard", contract } },
            }), ClientKey: client, ConnectionKey: connection)).AsTask();
            await Task.Yield();
            var requestId = $"close-{answer}";
            using var admission = JsonDocument.Parse(host.Transport.Call($"{view.Route}StartClose",
                new(StringValue: requestId, ClientKey: client, ConnectionKey: connection)));
            Require(admission.RootElement.GetProperty("kind").GetString() == "accepted",
                $"The web Close command was not admitted: {admission.RootElement}.");
            var operation = admission.RootElement.GetProperty("contract").GetString()!;
            using (var request = JsonDocument.Parse(await wait.WaitAsync(Timeout)))
            {
                var root = request.RootElement;
                Require(root.GetProperty("kind").GetString() == "request"
                    && root.GetProperty("input").GetProperty("title").GetString() == "reactive editor"
                    && root.GetProperty("input").GetProperty("draft").GetString() == "changed",
                    $"The web View did not receive the typed leave prompt: {root}.");
                var reply = JsonSerializer.Serialize(new
                {
                    kind = "answered",
                    requestId = root.GetProperty("requestId").GetString(),
                    route = view.Route,
                    presentationId = mount,
                    ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
                    name = "confirmDiscard",
                    contract,
                    output = answer,
                });
                using var response = JsonDocument.Parse(host.Transport.Call(BridgeInteractionRouter.ReplyRoute,
                    new(StringValue: reply, ClientKey: client, ConnectionKey: connection)));
                Require(response.RootElement.GetProperty("kind").GetString() == "ok", $"The web answer was rejected: {response.RootElement}.");
            }
            using var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
                new(StringValue: JsonSerializer.Serialize(new { contract = operation, requestId }))).AsTask().WaitAsync(Timeout));
            var status = completion.RootElement;
            return Outcome(status.GetProperty("kind").GetString()!, status.TryGetProperty("result", out var result) ? result : null);
        }

        var (closing, editorView, mount) = await OpenAsync();
        var editor = (ReactiveNavEditorViewModel)model.Main.Current!;
        editorView.Set(vm => vm.Draft, "changed");
        var kept = await CloseAnsweringAsync(editorView, mount, answer: false);
        Require(kept == "rejected:guard" && model.Main.Current == editor && editor.Draft == "changed" && !closing.Completion.IsCompleted,
            $"A web no gave {kept}; the editor and its request must stay.");
        var left = await CloseAnsweringAsync(editorView, mount, answer: true);
        Require(left == "committed" && model.Main.Current == home && editor.Draft == ""
            && await closing.Completion.WaitAsync(Timeout) is NavigationCompletion<string>.Dismissed,
            $"A web yes gave {left}; the editor must leave, discard and dismiss its request.");

        // Save completes the request with the draft, without asking.
        var (saving, saveView, _) = await OpenAsync();
        saveView.Set(vm => vm.Draft, "saved draft");
        var saveStatus = await saveView.Start(vm => vm.SaveCommand).WaitAsync().AsTask().WaitAsync(Timeout);
        var saved = Outcome(saveStatus.Kind, saveStatus.Result);
        Require(saved == "committed" && model.Main.Current == home
            && await saving.Completion.WaitAsync(Timeout) is NavigationCompletion<string>.Completed { Value: "saved draft" },
            $"The web Save command gave {saved}, not a completed request.");

        // Cancel dismisses a clean editor's request.
        var (cancelling, cancelView, _) = await OpenAsync();
        var cancelStatus = await cancelView.Start(vm => vm.CancelCommand).WaitAsync().AsTask().WaitAsync(Timeout);
        var cancelled = Outcome(cancelStatus.Kind, cancelStatus.Result);
        Require(cancelled == "committed" && model.Main.Current == home
            && await cancelling.Completion.WaitAsync(Timeout) is NavigationCompletion<string>.Dismissed,
            $"The web Cancel command gave {cancelled}, not a dismissed request.");
    }

    // The back command needs no scheduler and returns a NavigationOutcome, which the generated web client
    // receives as a typed result (W250-014). It reports a failed arrival hook of the page it went back to by its
    // state only, without the exception (#385).
    [Test]
    public async Task BridgedBackCommandReturnsOutcomeAsync()
    {
        Require(ReactiveSchedulers.InstallMainThreadScheduler(),
            "The model-context main-thread scheduler was not installed.");
        await using var context = new RunicModelContext();
        await using var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context });
        var home = new NavHomeViewModel();
        using var model = new ReactiveNavShellViewModel(navigator, context, home);
        using var host = new RunicWindowTestHost<ReactiveNavShellViewModel>(model,
            (transport, content, vm) => new ReactiveNavShellBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new NavViewLocator() });
        home.Activate = () => throw new InvalidOperationException("internal reload detail");
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
            && root.GetProperty("result").GetProperty("rejection").ValueKind == JsonValueKind.Null
            && root.GetProperty("result").GetProperty("arrival").GetString() == "Failed"
            && !root.GetRawText().Contains("internal reload detail", StringComparison.Ordinal),
            $"The back command did not return a committed outcome with a failed arrival and no exception: {root}.");
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
