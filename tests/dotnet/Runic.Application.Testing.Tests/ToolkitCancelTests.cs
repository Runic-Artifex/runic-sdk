using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ToolkitCancelTests
{
    private const string Route = "toolkitCancel";

    // The asynchronous commands sent as command requests: the generated cancel command cancels the running request,
    // which replies cancelled.
    [Test]
    public async Task GeneratedCancelCommandsCancelToolkitCommandsAsync()
    {
        var model = new ToolkitCancelViewModel();
        using var host = CreateHost(model);
        using (var idle = host.RawSnapshot())
            Require(!idle.RootElement.GetProperty("state").GetProperty("canSaveCancel").GetBoolean(),
                "The generated cancel command must be unavailable while idle.");
        using (var unavailable = JsonDocument.Parse(host.Transport.Command(Route, "saveCancel")))
            Require(!unavailable.RootElement.GetProperty("ok").GetBoolean(),
                "An idle generated cancel command must reject execution.");

        await CancelCommandAsync(host, model, "save", nativeCancel: false);
        await CancelCommandAsync(host, model, "export", nativeCancel: true);
        Require(model.ExportInput == "document", "The paired async command lost its typed input.");
        Require(model.CancelledCount == 2, "Generated cancellation did not reach both actual Toolkit commands.");
        await CancelCommandAsync(host, model, "background", nativeCancel: false);

        // A native invocation has no bridge request, but the same generated
        // cancel property must cancel it and publish its CanExecuteChanged.
        var nativeExecution = model.SaveCommand.ExecuteAsync(null);
        Require(await PublishedAsync(host, "canSaveCancel", true),
            "Native Toolkit execution did not publish generated cancel availability.");
        using (var cancellation = JsonDocument.Parse(host.Transport.Command(Route, "saveCancel")))
            Require(cancellation.RootElement.GetProperty("ok").GetBoolean(),
                "The bridge could not cancel a native Toolkit execution.");
        await RequireCancelledAsync(nativeExecution);
        Require(await PublishedAsync(host, "canSaveCancel", false),
            "Generated cancel availability did not return to false after cancellation.");

        var lifetimeModel = new ToolkitCancelViewModel();
        var lifetimeHost = CreateHost(lifetimeModel);
        // The route publishes frames after its first state.
        lifetimeHost.RawSnapshot().Dispose();
        var pending = lifetimeHost.Transport.CommandAsync(Route, "save").AsTask();
        Require(await PublishedAsync(lifetimeHost, "canSaveCancel", true),
            "The lifetime-owned Toolkit command did not begin execution.");
        var lifetimeExecution = lifetimeModel.SaveCommand.ExecutionTask!;
        lifetimeHost.Dispose();
        await RequireCancelledAsync(lifetimeExecution);
        Require(lifetimeModel.CancelledCount == 1, "View disposal no longer cancelled the actual Toolkit command.");
        _ = await pending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // The same cancellation of commands started as operations.
    [Test]
    public async Task GeneratedCancelCommandsCancelToolkitOperationsAsync()
    {
        var model = new ToolkitCancelViewModel();
        using var host = CreateHost(model);
        // Frames follow the state the page read.
        host.RawSnapshot().Dispose();
        await CancelOperationAsync(host, model, vm => vm.SaveCommand, "save", null, "bridge-cancel", nativeCancel: false);
        await CancelOperationAsync(host, model, vm => vm.ExportCommand, "export", JsonSerializer.Serialize("document"), "typed-native-cancel", nativeCancel: true);
        Require(model.ExportInput == "document", "The paired async command lost its typed input.");
        Require(model.CancelledCount == 2, "Generated cancellation did not reach both actual Toolkit commands.");
        await CancelOperationAsync(host, model, vm => vm.BackgroundCommand, "background", null, "static-source-cancel", nativeCancel: false);

        var lifetimeModel = new ToolkitCancelViewModel();
        var lifetimeHost = CreateHost(lifetimeModel);
        lifetimeHost.RawSnapshot().Dispose();
        var admission = lifetimeHost.Root.Start(vm => vm.SaveCommand, requestId: "lifetime");
        Require(admission.Admission == RunicOperationAdmissionKind.Accepted, $"The lifetime operation was not admitted: {admission.AdmissionJson}");
        Require(await PublishedAsync(lifetimeHost, "canSaveCancel", true),
            "The lifetime-owned Toolkit operation did not begin execution.");
        var lifetimeExecution = lifetimeModel.SaveCommand.ExecutionTask!;
        lifetimeHost.Dispose();
        await RequireCancelledAsync(lifetimeExecution);
        Require(lifetimeModel.CancelledCount == 1, "View disposal no longer cancelled the actual Toolkit operation.");
    }

    private static RunicWindowTestHost<ToolkitCancelViewModel> CreateHost(ToolkitCancelViewModel model) =>
        new(model, (transport, content, vm) => new ToolkitCancelBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = Route, ViewLocator = new TestViewLocator() });

    private static async Task CancelCommandAsync(RunicWindowTestHost<ToolkitCancelViewModel> host,
        ToolkitCancelViewModel model, string member, bool nativeCancel)
    {
        var cancel = $"can{char.ToUpperInvariant(member[0])}{member[1..]}Cancel";
        var pending = host.Transport.CommandAsync(Route, member, member == "export" ? JsonSerializer.Serialize("document") : null).AsTask();
        Require(await PublishedAsync(host, cancel, true), "The generated Toolkit cancel command did not publish its availability.");
        if (nativeCancel)
            model.ExportCancelCommand.Execute(null);
        else
        {
            using var cancellation = JsonDocument.Parse(host.Transport.Command(Route, $"{member}Cancel"));
            Require(cancellation.RootElement.GetProperty("ok").GetBoolean(), $"The generated cancel command failed through the bridge: {cancellation.RootElement}");
        }
        using var terminal = JsonDocument.Parse(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Require(!terminal.RootElement.GetProperty("ok").GetBoolean()
            && terminal.RootElement.GetProperty("error").GetProperty("kind").GetString() == "cancelled",
            $"Toolkit cancel execution did not finish the original command request as cancelled: {terminal.RootElement}");
        using var idle = host.RawSnapshot();
        Require(!idle.RootElement.GetProperty("state").GetProperty(cancel).GetBoolean(),
            "A finished Toolkit command left its generated cancel command available.");
    }

    private static async Task CancelOperationAsync(RunicWindowTestHost<ToolkitCancelViewModel> host, ToolkitCancelViewModel model,
        System.Linq.Expressions.Expression<Func<ToolkitCancelViewModel, object?>> command, string member, string? input,
        string requestId, bool nativeCancel)
    {
        var cancel = $"can{char.ToUpperInvariant(member[0])}{member[1..]}Cancel";
        var operation = host.Root.Start(command, input, requestId);
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"The async Toolkit operation was not admitted: {operation.AdmissionJson}");
        Require(operation.Contract.Split(':')[1] == BridgeContractShape.Compute(typeof(ToolkitCancelViewModel)),
            "The generated cancel property disagreed with the reconstructed bridge fingerprint.");
        Require(await PublishedAsync(host, cancel, true), "The generated Toolkit cancel command did not publish its availability.");
        if (nativeCancel)
            model.ExportCancelCommand.Execute(null);
        else
        {
            using var cancellation = JsonDocument.Parse(host.Transport.Command(Route, $"{member}Cancel"));
            Require(cancellation.RootElement.GetProperty("ok").GetBoolean(), "The generated cancel command failed through the bridge.");
        }
        var terminal = await operation.WaitAsync();
        Require(terminal.Kind == RunicOperationStatusKind.Cancelled,
            $"Toolkit cancel execution did not finish the original bridge operation as cancelled: {terminal.Json}");
        using var idle = host.RawSnapshot();
        Require(!idle.RootElement.GetProperty("state").GetProperty(cancel).GetBoolean(),
            "A finished Toolkit operation left its generated cancel command available.");
    }

    private static async Task RequireCancelledAsync(Task execution)
    {
        try { await execution.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("The actual Toolkit execution was not cancelled.");
    }

    // Whether the route publishes the member with the value: a frame's set, or a full state.
    private static async Task<bool> PublishedAsync(RunicWindowTestHost<ToolkitCancelViewModel> host, string member, bool value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var publication in host.Root.TakePublications())
            {
                if (publication.Sets.Any(set => set.Field == member && set.Value.ValueKind is JsonValueKind.True or JsonValueKind.False
                        && set.Value.GetBoolean() == value)
                    || publication.State is { } state && state.TryGetProperty(member, out var current) && current.GetBoolean() == value)
                    return true;
            }
            await Task.Delay(10);
        }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
