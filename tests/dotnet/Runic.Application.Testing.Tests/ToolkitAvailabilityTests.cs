using System.Text.Json;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class ToolkitAvailabilityTests
{
    // A CanExecute property is the command's availability, written once (#305).
    [Test]
    public void ACanExecutePropertyIsTheCommandsAvailability()
    {
        var model = new ToolkitAvailabilityViewModel();
        using var host = new RunicWindowTestHost<ToolkitAvailabilityViewModel>(model, "toolkitAvailability",
            (transport, content, vm) => new ToolkitAvailabilityBridge(transport, vm, content: content), new TestViewLocator());
        using (var idle = host.Snapshot())
        {
            var state = idle.RootElement.GetProperty("state");
            Require(!state.GetProperty("canCreate").GetBoolean() && !state.GetProperty("canArchive").GetBoolean(),
                $"An empty target must leave the commands unavailable: {state}");
            Require(state.EnumerateObject().Count(member => member.Name == "canCreate") == 1,
                $"canCreate was written more than once: {state}");
        }
        using (var refused = JsonDocument.Parse(host.Transport.Call("toolkitAvailabilityCreate")))
            Require(!refused.RootElement.GetProperty("ok").GetBoolean() && model.Created == 0,
                $"An unavailable command ran: {refused.RootElement}");

        // Without NotifyCanExecuteChangedFor: the snapshot asks the command.
        model.Target = "folder";
        using (var ready = host.Snapshot())
            Require(ready.RootElement.GetProperty("state").GetProperty("canCreate").GetBoolean(),
                "canCreate did not follow its property.");
        using (var created = JsonDocument.Parse(host.Transport.Call("toolkitAvailabilityCreate")))
            Require(created.RootElement.GetProperty("ok").GetBoolean() && model.Created == 1,
                $"An available command did not run: {created.RootElement}");

        var client = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "toolkitAvailability.ts")).ReplaceLineEndings("\n");
        Require(Count(client, "readonly canCreate: boolean;") == 1 && client.Contains("readonly canArchive: boolean;", StringComparison.Ordinal)
            && client.Contains("canArchive(name: string): Promise<boolean>;", StringComparison.Ordinal),
            $"The generated client does not declare each availability once:\n{client}");
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
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
