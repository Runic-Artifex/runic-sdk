using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive;
using System.Text.Json;
using ReactiveUI;
using ReactiveUI.Binding.Reactive;
using ReactiveUI.Reactive;
using ReactiveUiReactiveFlavorProof;
using Runic.Application.Views;
using Runic.Application.Views.Generated.Bridges;
using Runic.Application.Views.ReactiveUI.Reactive;
using Runic.Application.Views.ReactiveUI.Reactive.Generated;
using Runic.Application.Testing;
using Splat;
using Runic.Navigation;

AppLocator.CurrentMutable.RegisterConstant(new NullLogger(), typeof(ILogger));
AppLocator.CurrentMutable.RegisterConstant(new DefaultLogManager(AppLocator.Current), typeof(ILogManager));
await using var context = new RunicModelContext();
IScheduler commandScheduler = ReactiveSchedulers.For(context);
var model = new FlavorModel(commandScheduler);
using var modelLease = RunicModelContextRegistry.Shared.Bind(context, model);
model.Name = "ready";
if (model.Name != "ready") throw new InvalidOperationException("The ReactiveUI source generator did not create the reactive property.");
var locator = new DefaultViewLocator();
locator.CreateMappingBuilder().Map<FlavorModel>(() => new FlavorView());
var view = new ReactiveRunicViewLocator(locator).Locate<FlavorView, FlavorModel>();
view.ViewModel = model;
if (!ReferenceEquals(view.DataContext, model)) throw new InvalidOperationException("The Reactive flavor view did not retain its model.");

var command = ReactiveCommand.Create(() => 42, outputScheduler: commandScheduler);
var commandResult = await ReactiveCommandExecution.Execute(command, Unit.Default, CancellationToken.None);
if (commandResult != 42) throw new InvalidOperationException("The Reactive flavor command adapter lost its typed result.");

using (var transport = new InMemoryBridgeTransport())
using (new FlavorModelBridge(transport, model))
{
    _ = await transport.RequestAsync("""{"kind":"hello","protocol":4,"session":"flavor","client":{"name":"flavor-proof","version":"1.0.0"}}""");
    for (var execution = 0; execution < 100; execution++)
    {
        var replyJson = await transport.RequestAsync(Command("count"));
        using var reply = JsonDocument.Parse(replyJson);
        if (!reply.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"Generated Reactive bridge rejected sequential command {execution}: {replyJson}");
    }

    using (var disablingReply = JsonDocument.Parse(await transport.RequestAsync(Command("disable"))))
    {
        if (!disablingReply.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("The command that disables itself did not run.");
    }

    using var unavailableReply = JsonDocument.Parse(await transport.RequestAsync(Command("disable")));
    if (unavailableReply.RootElement.GetProperty("ok").GetBoolean())
        throw new InvalidOperationException("A genuinely disabled command was admitted after its first execution.");

    // #472: a RunicCommandKey command of the System.Reactive flavor.
    using (var unresolved = JsonDocument.Parse(await transport.RequestAsync(Command("remove", "8"))))
    {
        var error = unresolved.RootElement.GetProperty("error");
        if (error.GetProperty("kind").GetString() != "rejected" || error.GetProperty("reason").GetString() != "unresolved-key" || model.Item.Removed)
            throw new InvalidOperationException($"An unresolved key was not rejected with unresolved-key: {unresolved.RootElement}");
    }
    using (var removed = JsonDocument.Parse(await transport.RequestAsync(Command("remove", "7"))))
        if (!removed.RootElement.GetProperty("ok").GetBoolean() || !model.Item.Removed)
            throw new InvalidOperationException($"The keyed command did not run with its resolved item: {removed.RootElement}");
}

// #132: the window binds its root and each child it presents, including one the root
// creates in a command; a child bound to another context fails, naming its type.
var viewLocator = new ReactiveRunicViewLocator(locator);
var shell = new FlavorShellModel(commandScheduler);
using (var host = new RunicWindowTestHost<FlavorShellModel>(shell,
    (transport, content, vm) => new FlavorShellModelBridge(transport, vm, content: content),
    new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = viewLocator }))
{
    if (!BoundTo(shell)) throw new InvalidOperationException("The window did not bind its root ViewModel.");
    var opened = await host.Root.ExecuteAsync(vm => vm.OpenCommand);
    if (!opened.Succeeded) throw new InvalidOperationException($"The shell command failed: {opened.Json}");
    var page = host.Root.Snapshot().Reference(vm => vm.Main)
        ?? throw new InvalidOperationException("The shell did not present its created child.");
    if (!BoundTo(shell.Main!)) throw new InvalidOperationException("The window did not bind a child created by its ViewModel.");
    var counted = await host.View<FlavorModel>(page).ExecuteAsync(vm => vm.CountCommand);
    if (!counted.Succeeded) throw new InvalidOperationException($"The created child's command failed: {counted.Json}");

    await using var other = new RunicModelContext();
    var foreign = new FlavorModel(commandScheduler) { Name = "foreign" };
    using (RunicModelContextRegistry.Shared.Bind(other, foreign))
    {
        try
        {
            _ = host.Content.Present(shell, "Foreign", "flavor", foreign,
                (transport, vm, route) => new FlavorModelBridge(transport, vm, route, content: host.Content));
            throw new InvalidOperationException("A child bound to another context was presented.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains(typeof(FlavorModel).FullName!, StringComparison.Ordinal)) { }
    }
}
if (RunicModelContextRegistry.Shared.TryGet(shell, out _)) throw new InvalidOperationException("Closing the window kept its root bound.");

bool BoundTo(object model) =>
    RunicModelContextRegistry.Shared.TryGet(model, out var bound) && ReferenceEquals(bound, context);

IScheduler scheduler = ReactiveSchedulers.For(context);
scheduler.Schedule("scheduled", static (_, value) =>
{
    Scheduled.TrySetResult(value);
    return Disposable.Empty;
});
await Scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
if (!string.Equals(Scheduled.Task.Result, "scheduled", StringComparison.Ordinal))
    throw new InvalidOperationException("The System.Reactive scheduler did not execute in the model context.");

var cancelled = 0;
using (scheduler.Schedule(0, TimeSpan.FromMilliseconds(100), (_, _) =>
{
    Interlocked.Increment(ref cancelled);
    return Disposable.Empty;
}))
{
}
await Task.Delay(200);
if (Volatile.Read(ref cancelled) != 0) throw new InvalidOperationException("Disposed scheduled work still ran.");

// #478: the generated Bridge reads the ValidationContext of the System.Reactive flavor of
// ReactiveUI.Validation, so each message appears once and the object rule at the object's path.
var validationModel = new FlavorValidationModel { NewPassword = "abc", ConfirmPassword = "abd" };
using (var validationHost = new RunicWindowTestHost<FlavorValidationModel>(validationModel,
    (transport, content, vm) => new FlavorValidationModelBridge(transport, vm, content: content),
    new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = viewLocator }))
{
    var errors = validationHost.Root.Snapshot()["validation"].GetProperty("errors");
    var actual = errors.EnumerateArray()
        .Select(error => $"[{string.Join(",", error.GetProperty("path").EnumerateArray().Select(segment => segment.GetString()))}] {error.GetProperty("message").GetString()}")
        .Order(StringComparer.Ordinal).ToArray();
    string[] expected =
    [
        $"[] {FlavorValidationModel.Common}",
        $"[confirmPassword] {FlavorValidationModel.Mismatch}",
        $"[newPassword] {FlavorValidationModel.TooShort}",
    ];
    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        throw new InvalidOperationException($"The Reactive flavor's validation summary was {errors}.");

    // A rule that names no member changes neither ErrorsChanged nor HasErrors here: the change is a pushed frame.
    _ = validationHost.Root.TakePublications();
    await context.InvokeAsync(() => validationModel.AcceptPolicy(false));
    var pushed = await validationHost.Root.NextPublicationAsync(TimeSpan.FromSeconds(5));
    if (pushed.Sets.FirstOrDefault(set => set.Field == "validation") is not { } policy
        || !policy.Value.GetProperty("errors").EnumerateArray().Any(error =>
            error.GetProperty("message").GetString() == FlavorValidationModel.Policy && error.GetProperty("path").GetArrayLength() == 0))
        throw new InvalidOperationException($"The Reactive flavor did not push a rule that names no member: {pushed.Json}");
}

// #284: a Cancel operation of a navigation page is cancelled when the page's entry retires, and the page is
// disposed after the operation's work ended (I-192): the Bridge replies `cancelled` at once, while the work can still run.
locator.CreateMappingBuilder().Map<FlavorRetirePageModel>(() => new FlavorRetirePageView());
await using (var navigator = new RunicNavigator(new RunicNavigatorOptions { ModelContext = context }))
{
    var retireShell = new FlavorRetireShellModel(navigator);
    var retirePage = new FlavorRetirePageModel(commandScheduler);
    using var retireHost = new RunicWindowTestHost<FlavorRetireShellModel>(retireShell,
        (transport, content, vm) => new FlavorRetireShellModelBridge(transport, vm, content: content),
        new RunicWindowTestHostOptions { ModelContext = context, ViewLocator = new ReactiveRunicViewLocator(locator) });
    if (await retireShell.Main.PushAsync(NavigationTarget.Own(retirePage)) is not NavigationResult<FlavorRetirePageModel>.Committed)
        throw new InvalidOperationException("The retirement page was not pushed.");
    var upload = retireHost.Root.View<FlavorRetirePageModel>(vm => vm.Main).Start(vm => vm.UploadCommand);
    var late = retireHost.Root.View<FlavorRetirePageModel>(vm => vm.Main).Start(vm => vm.LateCommand);
    await retirePage.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
    await retirePage.LateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
    if (await retireShell.Main.ClearAsync() is not NavigationResult<FlavorRetirePageModel>.Committed)
        throw new InvalidOperationException("The retirement page was not cleared.");
    var uploadStatus = await upload.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    if (uploadStatus.Kind is not RunicOperationStatusKind.Cancelled)
        throw new InvalidOperationException($"Retiring the page did not cancel its Cancel operation: {uploadStatus.Json}");
    if ((await late.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10))).Kind is not RunicOperationStatusKind.Cancelled)
        throw new InvalidOperationException("Retiring the page did not cancel its late Cancel operation.");
    // The late work still runs, so the page is not disposed yet.
    for (var turn = 0; turn < 5; turn++)
    {
        await Task.Delay(20);
        await context.InvokeAsync(() => { });
    }
    if (retirePage.Disposed != 0)
        throw new InvalidOperationException("The navigator disposed the page while the work of its late Cancel operation ran.");

    // I-188: the page's Bridge detached; the late work now reports its cancellation on ThrownExceptions, which the
    // application does not observe. The process must survive it.
    var lateIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (retirePage.LateCommand.IsExecuting.Subscribe(executing => { if (!executing) lateIdle.TrySetResult(); }))
    {
        retirePage.LateRelease.TrySetResult();
        await lateIdle.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    var disposeDeadline = DateTime.UtcNow.AddSeconds(10);
    while (retirePage.Disposed == 0 && DateTime.UtcNow < disposeDeadline) await Task.Delay(10);
    if (retirePage.Disposed != 1 || !retirePage.EndedBeforeDispose)
        throw new InvalidOperationException("The navigator did not dispose the page after the work of its Cancel operations ended.");
    for (var turn = 0; turn < 5; turn++)
    {
        await Task.Delay(50);
        await context.InvokeAsync(() => { });
    }
}

Console.WriteLine("REACTIVEUI_REACTIVE_FLAVOR_OK");

// A command request of the flavor model's route, with its input as wire JSON.
static string Command(string member, string? input = null) => input is null
    ? $$"""{"kind":"command","route":"flavorModel","member":"{{member}}"}"""
    : $$"""{"kind":"command","route":"flavorModel","member":"{{member}}","input":{{input}}}""";

file static class Scheduled
{
    public static TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static Task<string> Task => Completion.Task;
    public static void TrySetResult(string value) => Completion.TrySetResult(value);
}
