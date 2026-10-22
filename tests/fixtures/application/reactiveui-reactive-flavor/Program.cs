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
    for (var execution = 0; execution < 100; execution++)
    {
        var replyJson = await transport.CallAsync("flavorModelCount");
        using var reply = JsonDocument.Parse(replyJson);
        if (!reply.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"Generated Reactive bridge rejected sequential command {execution}: {replyJson}");
    }

    using (var disablingReply = JsonDocument.Parse(await transport.CallAsync("flavorModelDisable")))
    {
        if (!disablingReply.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("The command that disables itself did not run.");
    }

    using var unavailableReply = JsonDocument.Parse(await transport.CallAsync("flavorModelDisable"));
    if (unavailableReply.RootElement.GetProperty("ok").GetBoolean())
        throw new InvalidOperationException("A genuinely disabled command was admitted after its first execution.");

    // #472: a RunicCommandKey command of the System.Reactive flavor.
    using (var unresolved = JsonDocument.Parse(await transport.CallAsync("flavorModelRemove", new BridgeTestArguments(StringValue: "8"))))
    {
        var error = unresolved.RootElement.GetProperty("error");
        if (error.GetProperty("kind").GetString() != "rejected" || error.GetProperty("reason").GetString() != "unresolved-key" || model.Item.Removed)
            throw new InvalidOperationException($"An unresolved key was not rejected with unresolved-key: {unresolved.RootElement}");
    }
    using (var removed = JsonDocument.Parse(await transport.CallAsync("flavorModelRemove", new BridgeTestArguments(StringValue: "7"))))
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
    using (var opened = JsonDocument.Parse(await host.Transport.CallAsync($"{host.RootRoute}Open")))
        if (!opened.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"The shell command failed: {opened.RootElement}");
    var page = host.Root.Snapshot().Reference(vm => vm.Main)
        ?? throw new InvalidOperationException("The shell did not present its created child.");
    if (!BoundTo(shell.Main!)) throw new InvalidOperationException("The window did not bind a child created by its ViewModel.");
    using (var counted = JsonDocument.Parse(await host.Transport.CallAsync($"content{page.Id}Count")))
        if (!counted.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException($"The created child's command failed: {counted.RootElement}");

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
    using var validationSnapshot = JsonDocument.Parse(validationHost.Transport.Call($"{validationHost.RootRoute}Snapshot"));
    var errors = validationSnapshot.RootElement.GetProperty("state").GetProperty("validation").GetProperty("errors");
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
}

Console.WriteLine("REACTIVEUI_REACTIVE_FLAVOR_OK");

file static class Scheduled
{
    public static TaskCompletionSource<string> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public static Task<string> Task => Completion.Task;
    public static void TrySetResult(string value) => Completion.TrySetResult(value);
}
