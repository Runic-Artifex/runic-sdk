using ReactiveUI.Primitives;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing.Tests;

// D018.5: a command body resumes on the model context after await, so state it
// writes after await is applied in a model turn and in order, in both MVVM flavours.
internal static class ContinuationTests
{
    private static readonly string[] Body = ["start", "yield", "delay", "inner", "nested"];

    internal static async Task RunAsync()
    {
        await ToolkitCommandResumesOnTheModelContext();
        await ReactiveCommandResumesOnTheModelContext();
        await AwaitedInteractionResumesOnTheModelContext();
    }

    private static async Task ToolkitCommandResumesOnTheModelContext()
    {
        var probe = new ContinuationProbe();
        var model = new ToolkitContinuationViewModel(probe);
        using var host = new RunicWindowTestHost<ToolkitContinuationViewModel>(model, "toolkitContinuation",
            (transport, content, vm) => new ToolkitContinuationBridge(transport, vm, content: content),
            new TestViewLocator());
        probe.Context = host.Content.ModelContext;
        await RunProbeAsync(host.Transport, "toolkitContinuation", probe, "Toolkit");
        Require(model.Status == "b:released", $"The Toolkit command's last write was lost: {model.Status}");
    }

    private static async Task ReactiveCommandResumesOnTheModelContext()
    {
        await using var context = new RunicModelContext();
        var probe = new ContinuationProbe { Context = context };
        var model = new ReactiveContinuationViewModel(context, probe);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<ReactiveContinuationViewModel>(model, "reactiveContinuation",
            (transport, content, vm) => new ReactiveContinuationBridge(transport, vm, content: content),
            new TestViewLocator());
        await RunProbeAsync(host.Transport, "reactiveContinuation", probe, "ReactiveUI");
        Require(model.Status == "b:released", $"The ReactiveUI command's last write was lost: {model.Status}");
    }

    // A ReactiveUI interaction that a frontend answers completes on a bridge
    // thread. Awaiting it in a body that runs in a turn still resumes in the
    // context, so the body sets state after it directly.
    private static async Task AwaitedInteractionResumesOnTheModelContext()
    {
        await using var context = new RunicModelContext();
        var interaction = new ReactiveUI.Binding.Interaction<int, bool>();
        using var handler = interaction.RegisterHandler(async request =>
        {
            await Task.Delay(20).ConfigureAwait(false);
            request.SetOutput(request.Input > 0);
        });
        Task<bool>? body = null;
        await context.InvokeAsync(() => body = Body());
        Require(await body!.WaitAsync(TimeSpan.FromSeconds(5)), "The code after the awaited interaction ran outside the model context.");

        async Task<bool> Body()
        {
            var approved = await interaction.Handle(1);
            return approved && context.IsExecuting;
        }
    }

    // Runs "a" through the awaited command route and "b" through an admitted
    // operation of a second command. Both wait at the same gate; releasing it must apply a's write and
    // then b's, each in its own model turn.
    private static async Task RunProbeAsync(InMemoryViewTransport transport, string route, ContinuationProbe probe, string flavour)
    {
        var first = transport.CallAsync($"{route}Run", new(StringValue: JsonSerializer.Serialize("a"))).AsTask();
        await probe.WaitingFor("a").WaitAsync(TimeSpan.FromSeconds(5));
        using var admission = JsonDocument.Parse(transport.Call($"{route}StartRunNext", new(StringValue:
            JsonSerializer.Serialize(new { requestId = "continuation-b", input = "b" }))));
        Require(admission.RootElement.GetProperty("kind").GetString() == "accepted",
            $"The {flavour} operation was not admitted: {admission.RootElement}");
        await probe.WaitingFor("b").WaitAsync(TimeSpan.FromSeconds(5));
        probe.ReleaseAll();

        using (var reply = JsonDocument.Parse(await first.WaitAsync(TimeSpan.FromSeconds(5))))
            Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"The {flavour} command failed: {reply.RootElement}");
        var contract = admission.RootElement.GetProperty("contract").GetString()!;
        using (var terminal = JsonDocument.Parse(await transport.CallAsync("__runicOperationWait", new(StringValue:
            JsonSerializer.Serialize(new { contract, member = "RunNext", requestId = "continuation-b" })))))
            Require(terminal.RootElement.GetProperty("kind").GetString() == "succeeded",
                $"The {flavour} operation did not succeed: {terminal.RootElement}");

        var steps = probe.Steps;
        string[] expected = [.. Body.Select(step => $"a:{step}"), .. Body.Select(step => $"b:{step}"), "a:released", "b:released"];
        Require(steps.Select(step => step.Label).SequenceEqual(expected),
            $"{flavour} writes after await were not applied in order: {string.Join(", ", steps.Select(step => step.Label))}");
        var outside = steps.Where(step => !step.InTurn).Select(step => step.Label).ToArray();
        Require(outside.Length == 0,
            $"{flavour} writes after await ran outside the model context: {string.Join(", ", outside)}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
