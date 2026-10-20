using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ToolkitResultTests
{
    [Test]
    public async Task AToolkitTaskResultIsTheOperationResultAsync()
    {
        var model = new ToolkitResultViewModel();
        using var host = new RunicWindowTestHost<ToolkitResultViewModel>(model,
            (transport, content, vm) => new ToolkitResultBridge(transport, vm, content: content), new RunicWindowTestHostOptions { RootRoute = "toolkitResult", ViewLocator = new TestViewLocator() });

        using (var label = await CompleteAsync(host, "Label", "label-null", null))
            Require(label.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
                $"A null Task<string?> result was not delivered: {label.RootElement}");
        using (var estimate = await CompleteAsync(host, "Estimate", "estimate-3", "{\"retention\":3}"))
        {
            var result = estimate.RootElement.GetProperty("result");
            Require(result.GetProperty("retention").GetInt32() == 3 && result.GetProperty("bytes").GetDouble() == 3073.5 && model.Runs == 1,
                $"The Task<T> result of a [RelayCommand] method was not delivered: {estimate.RootElement}");
        }
        using (var label = await CompleteAsync(host, "Label", "label-runs", null))
            Require(label.RootElement.GetProperty("result").GetString() == "runs 1", $"The second result was not delivered: {label.RootElement}");
        using (var refresh = await CompleteAsync(host, "Refresh", "refresh", null))
            Require(!refresh.RootElement.TryGetProperty("result", out _), $"A Task command reported a result: {refresh.RootElement}");

        // The awaited command route still replies with the state.
        using (var awaited = JsonDocument.Parse(await host.Transport.CallAsync("toolkitResultEstimate", new(StringValue: "{\"retention\":1}"))))
            Require(awaited.RootElement.GetProperty("ok").GetBoolean() && awaited.RootElement.GetProperty("state").GetProperty("runs").GetInt32() == 2,
                $"The awaited command route failed: {awaited.RootElement}");

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

    private static async Task<JsonDocument> CompleteAsync(RunicWindowTestHost<ToolkitResultViewModel> host, string command, string requestId, string? input)
    {
        var start = input is null ? requestId : $"{{\"requestId\":{JsonSerializer.Serialize(requestId)},\"input\":{input}}}";
        using var accepted = JsonDocument.Parse(host.Transport.Call($"toolkitResultStart{command}", new(StringValue: start)));
        Require(accepted.RootElement.GetProperty("kind").GetString() == "accepted", $"{command} was not accepted: {accepted.RootElement}");
        var contract = accepted.RootElement.GetProperty("contract").GetString();
        var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId }))));
        Require(completion.RootElement.GetProperty("kind").GetString() == "succeeded", $"{command} did not succeed: {completion.RootElement}");
        return completion;
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
