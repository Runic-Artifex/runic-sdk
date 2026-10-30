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

        var opened = 0;
        // The editor's presentation registers its confirmDiscard handler with its open (generation 0).
        async Task<(NavigationResultRequest<INavPageViewModel, string> Request, RunicViewDriver<ReactiveNavEditorViewModel> View, string Mount)> OpenAsync()
        {
            var request = model.Main.PushForResult<string>(
                NavigationTarget.Create<INavPageViewModel>((_, entry) => new ReactiveNavEditorViewModel(entry)));
            Require(await request.Transition.AsTask().WaitScaledAsync(Timeout) is NavigationResult<INavPageViewModel>.Committed,
                "The editor was not pushed.");
            await Until(() => host.Root.Snapshot().Reference(vm => vm.Main)?.Kind == "reactiveNavEditor");
            var view = host.Root.View<ReactiveNavEditorViewModel>(vm => vm.Main);
            var presentation = $"editor{++opened}";
            var open = host.Transport.Open(view.Route, presentation, connection, client,
                JsonSerializer.Serialize(new[] { new { name = "confirmDiscard", contract } }));
            Require(open.Contains("\"presentation\":\"ok\"", StringComparison.Ordinal)
                && open.Contains("\"registration\":\"ok\"", StringComparison.Ordinal), $"The editor did not open with its handler: {open}");
            return (request, view, presentation);
        }

        static string StatusOutcome(RunicOperationStatus status) =>
            Outcome(status.Kind == RunicOperationStatusKind.Succeeded ? "succeeded" : status.Kind.ToString(), status.Result);
        static string Outcome(string kind, JsonElement? result) =>
            kind != "succeeded" ? kind
            : result!.Value.GetProperty("kind").GetString()!.ToLowerInvariant()
                + (result.Value.GetProperty("rejection").ValueKind == JsonValueKind.Null ? ""
                    : ":" + result.Value.GetProperty("rejection").GetString()!.ToLowerInvariant());

        // Close with a draft from the browser: the same connection receives the typed prompt and answers.
        async Task<string> CloseAnsweringAsync(RunicViewDriver<ReactiveNavEditorViewModel> view, string mount, bool answer)
        {
            var operation = $"close-{answer}";
            var operationContract = BridgeRequests.OperationContract<ReactiveNavEditorViewModel>();
            using var admission = JsonDocument.Parse(host.Transport.Start(view.Route, "close", operation, operationContract,
                connectionKey: connection, clientKey: client));
            Require(admission.RootElement.GetProperty("ok").GetBoolean() && admission.RootElement.GetProperty("admission").GetString() == "accepted",
                $"The web Close command was not admitted: {admission.RootElement}.");
            using (var request = await host.Transport.TakePushAsync(connection, "interaction", Timeout))
            {
                var root = request.RootElement;
                Require(root.GetProperty("route").GetString() == view.Route && root.GetProperty("presentation").GetString() == mount
                    && root.GetProperty("input").GetProperty("title").GetString() == "reactive editor"
                    && root.GetProperty("input").GetProperty("draft").GetString() == "changed",
                    $"The web View did not receive the typed leave prompt: {root}.");
                var reply = JsonSerializer.Serialize(new
                {
                    kind = "interaction-reply",
                    route = view.Route,
                    presentation = mount,
                    requestId = root.GetProperty("requestId").GetString(),
                    ownerEpoch = root.GetProperty("ownerEpoch").GetInt64(),
                    name = "confirmDiscard",
                    contract,
                    outcome = "answered",
                    output = answer,
                });
                using var response = JsonDocument.Parse(host.Transport.Send(reply, connection, client));
                Require(response.RootElement.GetProperty("ok").GetBoolean() && response.RootElement.GetProperty("result").GetString() == "ok",
                    $"The web answer was rejected: {response.RootElement}.");
            }
            using var completion = await host.Transport.WaitForOperationAsync(view.Route, "close", operation, operationContract, Timeout,
                connection);
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
            && await closing.Completion.WaitScaledAsync(Timeout) is NavigationCompletion<string>.Dismissed,
            $"A web yes gave {left}; the editor must leave, discard and dismiss its request.");

        // Save completes the request with the draft, without asking.
        var (saving, saveView, _) = await OpenAsync();
        saveView.Set(vm => vm.Draft, "saved draft");
        var saveStatus = await saveView.Start(vm => vm.SaveCommand).WaitAsync().WaitScaledAsync(Timeout);
        var saved = StatusOutcome(saveStatus);
        Require(saved == "committed" && model.Main.Current == home
            && await saving.Completion.WaitScaledAsync(Timeout) is NavigationCompletion<string>.Completed { Value: "saved draft" },
            $"The web Save command gave {saved}, not a completed request.");

        // Cancel dismisses a clean editor's request.
        var (cancelling, cancelView, _) = await OpenAsync();
        var cancelStatus = await cancelView.Start(vm => vm.CancelCommand).WaitAsync().WaitScaledAsync(Timeout);
        var cancelled = StatusOutcome(cancelStatus);
        Require(cancelled == "committed" && model.Main.Current == home
            && await cancelling.Completion.WaitScaledAsync(Timeout) is NavigationCompletion<string>.Dismissed,
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
        var operation = host.Root.Start(vm => vm.BackCommand, requestId: "back");
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"The web back command was not admitted: {operation.AdmissionJson}");
        var status = await operation.WaitAsync().WaitScaledAsync(Timeout);
        var result = status.Result;
        Require(status.Kind == RunicOperationStatusKind.Succeeded && result is { } outcome
            && outcome.GetProperty("kind").GetString() is "committed" or "Committed"
            && outcome.GetProperty("rejection").ValueKind == JsonValueKind.Null
            && outcome.GetProperty("arrival").GetString() == "Failed"
            && !status.Json.Contains("internal reload detail", StringComparison.Ordinal),
            $"The back command did not return a committed outcome with a failed arrival and no exception: {status.Json}.");
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
        RunicTestOperation? operation = null;
        Task<RxVoid>? native = null;
        if (web)
        {
            operation = host.Root.Start(vm => vm.OpenCommand, requestId: "navigation");
            Require(operation.Admission == RunicOperationAdmissionKind.Accepted, "The web navigation command was not admitted.");
        }
        else native = ReactiveCommandExecution.Execute(model.OpenCommand, RxVoid.Default, cancellation.Token);
        await entered.Task.WaitScaledAsync(Timeout);
        try
        {
            await Until(() => host.Root.Snapshot().Read(vm => vm.Busy)
                && !ReactiveCommandExecution.CanExecute(model.OpenCommand, RxVoid.Default));
            Require(model.Main.Current == home && model.Main.History.Count == 0,
                "The framework command committed before its guard answered.");
            if (web)
            {
                var unavailable = host.Root.Start(vm => vm.OpenCommand, requestId: "second");
                Require(unavailable.Admission == RunicOperationAdmissionKind.Rejected && unavailable.Reason == "unavailable",
                    "The generated web bridge ignored observable command availability.");
            }
            if (outcome == "cancelled")
            {
                if (web) _ = operation!.Cancel();
                else cancellation.Cancel();
            }
            else release.SetResult(outcome == "committed");

            if (web)
            {
                var completion = await operation!.WaitAsync().WaitScaledAsync(Timeout);
                Require(completion.Kind == (outcome == "cancelled" ? RunicOperationStatusKind.Cancelled : RunicOperationStatusKind.Succeeded),
                    "The bridge command ended with the wrong operation terminal.");
            }
            else
            {
                try { await native!.WaitScaledAsync(Timeout); }
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
        var deadline = DateTime.UtcNow + TestTimeouts.Scale(Timeout);
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
