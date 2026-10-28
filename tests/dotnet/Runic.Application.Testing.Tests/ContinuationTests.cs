using ReactiveUI.Primitives;
using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// D018.5: a command body resumes on the model context after await, so state it
// writes after await is applied in a model turn and in order, in both MVVM flavours.
internal sealed class ContinuationTests
{
    private static readonly string[] Body = ["start", "yield", "delay", "inner", "nested"];

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ToolkitCommandResumesOnTheModelContext(bool operation)
    {
        if (operation) SkipUntilOperationsAreServed();
        var probe = new ContinuationProbe();
        var model = new ToolkitContinuationViewModel(probe);
        using var host = new RunicWindowTestHost<ToolkitContinuationViewModel>(model,
            (transport, content, vm) => new ToolkitContinuationBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "toolkitContinuation", ViewLocator = new TestViewLocator() });
        probe.Context = host.Content.ModelContext;
        await RunProbeAsync(host.Transport, "toolkitContinuation", typeof(ToolkitContinuationViewModel), probe, "Toolkit", operation);
        Require(model.Status == "b:released", $"The Toolkit command's last write was lost: {model.Status}");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReactiveCommandResumesOnTheModelContext(bool operation)
    {
        if (operation) SkipUntilOperationsAreServed();
        await using var context = new RunicModelContext();
        var probe = new ContinuationProbe { Context = context };
        var model = new ReactiveContinuationViewModel(context, probe);
        using var contextLease = RunicModelContextRegistry.Shared.Bind(context, model);
        using var host = new RunicWindowTestHost<ReactiveContinuationViewModel>(model,
            (transport, content, vm) => new ReactiveContinuationBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "reactiveContinuation", ViewLocator = new TestViewLocator() });
        await RunProbeAsync(host.Transport, "reactiveContinuation", typeof(ReactiveContinuationViewModel), probe, "ReactiveUI", operation);
        Require(model.Status == "b:released", $"The ReactiveUI command's last write was lost: {model.Status}");
    }

    // A ReactiveUI interaction that a frontend answers completes on a bridge
    // thread. Awaiting it in a body that runs in a turn still resumes in the
    // context, so the body sets state after it directly.
    [Test]
    public async Task AwaitedInteractionResumesOnTheModelContext()
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

    // Runs "a" through an awaited command request and "b" through a second command, as an awaited command request
    // or an admitted operation. Both wait at the same gate; releasing it must apply a's write and then b's, each in
    // its own model turn.
    private static async Task RunProbeAsync(InMemoryBridgeTransport transport, string route, Type model, ContinuationProbe probe,
        string flavour, bool operation)
    {
        var first = transport.CommandAsync(route, "run", JsonSerializer.Serialize("a")).AsTask();
        await probe.WaitingFor("a").WaitAsync(TimeSpan.FromSeconds(5));
        var contract = $"{model.FullName}:{BridgeContractShape.Compute(model)}";
        Task<string>? second = null;
        if (operation)
        {
            using var admission = JsonDocument.Parse(transport.Send(OperationRequest("start", route, contract, JsonSerializer.Serialize("b"))));
            Require(admission.RootElement.GetProperty("ok").GetBoolean()
                && admission.RootElement.GetProperty("admission").GetString() == "accepted",
                $"The {flavour} operation was not admitted: {admission.RootElement}");
        }
        else second = transport.CommandAsync(route, "runNext", JsonSerializer.Serialize("b")).AsTask();
        await probe.WaitingFor("b").WaitAsync(TimeSpan.FromSeconds(5));
        probe.ReleaseAll();

        using (var reply = JsonDocument.Parse(await first.WaitAsync(TimeSpan.FromSeconds(5))))
            Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"The {flavour} command failed: {reply.RootElement}");
        if (second is not null)
        {
            using var reply = JsonDocument.Parse(await second.WaitAsync(TimeSpan.FromSeconds(5)));
            Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"The second {flavour} command failed: {reply.RootElement}");
        }
        else
        {
            using var terminal = await TerminalAsync(transport, route, contract);
            Require(terminal.RootElement.GetProperty("kind").GetString() == "succeeded",
                $"The {flavour} operation did not succeed: {terminal.RootElement}");
        }

        var steps = probe.Steps;
        string[] expected = [.. Body.Select(step => $"a:{step}"), .. Body.Select(step => $"b:{step}"), "a:released", "b:released"];
        Require(steps.Select(step => step.Label).SequenceEqual(expected),
            $"{flavour} writes after await were not applied in order: {string.Join(", ", steps.Select(step => step.Label))}");
        var outside = steps.Where(step => !step.InTurn).Select(step => step.Label).ToArray();
        Require(outside.Length == 0,
            $"{flavour} writes after await ran outside the model context: {string.Join(", ", outside)}");
    }

    private static void SkipUntilOperationsAreServed() =>
        Skip.Test("P5: needs start and status served");

    // The operation's terminal status, read with status requests.
    private static async Task<JsonDocument> TerminalAsync(InMemoryBridgeTransport transport, string route, string contract)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            using var reply = JsonDocument.Parse(transport.Send(OperationRequest("status", route, contract, null)));
            var status = reply.RootElement.GetProperty("status");
            if (status.GetProperty("kind").GetString() != "running") return JsonDocument.Parse(status.GetRawText());
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The operation did not end.");
            await Task.Delay(10);
        }
    }

    private static string OperationRequest(string kind, string route, string contract, string? inputJson) => BridgeRequests.Json(writer =>
    {
        writer.WriteString("kind", kind);
        writer.WriteString("route", route);
        writer.WriteString("member", "runNext");
        writer.WriteString("operation", "continuation-b");
        writer.WriteString("contract", contract);
        if (inputJson is null) return;
        writer.WritePropertyName("input");
        writer.WriteRawValue(inputJson);
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
