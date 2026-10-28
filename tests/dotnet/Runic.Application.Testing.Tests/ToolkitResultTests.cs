using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ToolkitResultTests
{
    [Test]
    [Skip("P5: needs start and watch served")]
    public async Task AToolkitTaskResultIsTheOperationResultAsync()
    {
        var model = new ToolkitResultViewModel();
        using var host = Host(model);

        using (var label = await CompleteAsync(host, vm => vm.LabelCommand, "label-null", null))
            Require(label.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
                $"A null Task<string?> result was not delivered: {label.RootElement}");
        using (var estimate = await CompleteAsync(host, vm => vm.EstimateCommand, "estimate-3", "{\"retention\":3}"))
        {
            var result = estimate.RootElement.GetProperty("result");
            Require(result.GetProperty("retention").GetInt32() == 3 && result.GetProperty("bytes").GetDouble() == 3073.5 && model.Runs == 1,
                $"The Task<T> result of a [RelayCommand] method was not delivered: {estimate.RootElement}");
        }
        using (var label = await CompleteAsync(host, vm => vm.LabelCommand, "label-runs", null))
            Require(label.RootElement.GetProperty("result").GetString() == "runs 1", $"The second result was not delivered: {label.RootElement}");
        using (var refresh = await CompleteAsync(host, vm => vm.RefreshCommand, "refresh", null))
            Require(!refresh.RootElement.TryGetProperty("result", out _), $"A Task command reported a result: {refresh.RootElement}");
    }

    // The awaited command, and the generated declarations of the results.
    [Test]
    public async Task AToolkitTaskCommandRunsAsACommandAsync()
    {
        var model = new ToolkitResultViewModel();
        using var host = Host(model);
        // The command reply carries the revision; the state at that revision includes its effects.
        using (var awaited = JsonDocument.Parse(await host.Transport.CommandAsync("toolkitResult", "estimate", "{\"retention\":1}")))
            Require(awaited.RootElement.GetProperty("ok").GetBoolean() && awaited.RootElement.TryGetProperty("revision", out _),
                $"The awaited command failed: {awaited.RootElement}");
        using (var state = host.RawSnapshot())
            Require(state.RootElement.GetProperty("state").GetProperty("runs").GetInt32() == 1 && model.Runs == 1,
                $"The awaited command's effects were not in the state: {state.RootElement}");

        var client = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "toolkitResult.ts")).ReplaceLineEndings("\n");
        foreach (var expected in new[]
        {
            "export interface ToolkitResultEstimateOperation extends BridgeOperation<ToolkitEstimate> {}",
            "export interface ToolkitResultLabelOperation extends BridgeOperation<string | null> {}",
            "export interface ToolkitResultRefreshOperation extends BridgeOperation<void> {}",
        })
            Require(client.Contains(expected, StringComparison.Ordinal), $"The generated client is missing '{expected}':\n{client}");
        Require(BridgeContractShape.Parts(typeof(ToolkitResultViewModel)).Any(part => part.StartsWith("wire:ToolkitResultViewModel.EstimateCommand.result:type:", StringComparison.Ordinal)),
            "The contract fingerprint does not include the Task<T> result.");
    }

    private static RunicWindowTestHost<ToolkitResultViewModel> Host(ToolkitResultViewModel model) =>
        new(model, (transport, content, vm) => new ToolkitResultBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "toolkitResult", ViewLocator = new TestViewLocator() });

    // The terminal status of the operation.
    private static async Task<JsonDocument> CompleteAsync(RunicWindowTestHost<ToolkitResultViewModel> host,
        System.Linq.Expressions.Expression<Func<ToolkitResultViewModel, object?>> command, string requestId, string? input)
    {
        var operation = await host.Root.StartAsync(command, input, requestId);
        Require(operation.Admission == RunicOperationAdmissionKind.Accepted, $"{operation.Member} was not accepted: {operation.AdmissionJson}");
        var completion = await operation.WaitAsync();
        Require(completion.Kind == RunicOperationStatusKind.Succeeded, $"{operation.Member} did not succeed: {completion.Json}");
        return JsonDocument.Parse(completion.Json);
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
