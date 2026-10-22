using System.Text.Json;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class BridgeLimitsTests
{
    private const int MaximumPayload = 64 * 1024;

    [Test]
    public void OversizedSetterAndCommandValuesAreRejected()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model);
        var route = host.RootRoute;
        var large = new string('x', MaximumPayload + 1);

        RequireRejected(host.Transport.Call($"{route}SetText", new(StringValue: large)), "invalid-input",
            "A setter value over 64 KiB was not rejected.");
        Require(model.Text.Length == 0, "A setter value over 64 KiB was applied.");
        RequireOk(host.Transport.Call($"{route}SetText", new(StringValue: new string('x', MaximumPayload))),
            "A setter value of 64 KiB was refused.");

        RequireRejected(host.Transport.Call($"{route}Echo", new(StringValue: JsonSerializer.Serialize(large))), "invalid-input",
            "A command argument over 64 KiB was not rejected.");
        Require(model.Text.Length == MaximumPayload, "A command argument over 64 KiB ran the command.");
    }

    [Test]
    public void OutOfRangeNumbersAreRejected()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model);
        var route = host.RootRoute;

        RequireRejected(host.Transport.Call($"{route}SetCount", new(Int64Value: (long)int.MaxValue + 1)), "invalid-input",
            "An out-of-range int setter value was not rejected.");
        RequireRejected(host.Transport.Call($"{route}Take", new(StringValue: "4294967296")), "invalid-input",
            "An out-of-range int command argument was not rejected.");
        Require(model.Count == 0, "An out-of-range value was applied.");

        RequireRejected(host.Transport.Call($"{route}SetLarge", new(StringValue: JsonSerializer.Serialize(new string('9', 4_097)))),
            "invalid-input", "A number string over 4 096 characters was not rejected.");
        var digits = new string('9', 4_096);
        RequireOk(host.Transport.Call($"{route}SetLarge", new(StringValue: JsonSerializer.Serialize(digits))),
            "A number string of 4 096 characters was refused.");
        Require(model.Large.ToString() == digits, "A 4 096-digit number was not read.");
    }

    [Test]
    public async Task AwaitedCommandsAreBoundedPerWindowAsync()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model);
        var route = host.RootRoute;
        var running = Enumerable.Range(0, 64).Select(_ => host.Transport.CallAsync($"{route}Hold").AsTask()).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (model.Held < 64 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(model.Held == 64, $"Only {model.Held} awaited commands started.");

        using (var refused = JsonDocument.Parse(await host.Transport.CallAsync($"{route}Hold")))
        {
            var error = refused.RootElement.GetProperty("error");
            Require(!refused.RootElement.GetProperty("ok").GetBoolean() && error.GetProperty("kind").GetString() == "rejected"
                && !error.TryGetProperty("reason", out _), $"A 65th awaited command was not rejected: {refused.RootElement}");
        }
        Require(model.Held == 64, "The 65th awaited command ran.");

        model.Release();
        foreach (var reply in await Task.WhenAll(running)) RequireOk(reply, "A held awaited command failed.");
        RequireOk(await host.Transport.CallAsync($"{route}Hold"), "An awaited command was refused after the others ended.");
    }

    [Test]
    public void NumberStringsAreBounded()
    {
        using var accepted = JsonDocument.Parse(JsonSerializer.Serialize(new string('1', 4_096)));
        Require(BridgeWire.ReadBigInteger(accepted.RootElement).ToString().Length == 4_096, "A 4 096-digit integer was refused.");
        foreach (var read in new Func<JsonElement, object>[]
        {
            value => BridgeWire.ReadBigInteger(value), value => BridgeWire.ReadDecimal(value),
            value => BridgeWire.ReadInt64(value), value => BridgeWire.ReadUInt64(value),
        })
        {
            using var rejected = JsonDocument.Parse(JsonSerializer.Serialize(new string('1', 4_097)));
            try
            {
                read(rejected.RootElement);
                throw new InvalidOperationException("A number string over 4 096 characters was read.");
            }
            catch (FormatException) { }
        }
    }

    private static RunicWindowTestHost<BridgeLimitsViewModel> Host(BridgeLimitsViewModel model) =>
        new(model, (transport, content, vm) => new BridgeLimitsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "bridgeLimits", ViewLocator = new TestViewLocator() });

    private static void RequireOk(string reply, string message)
    {
        using var document = JsonDocument.Parse(reply);
        Require(document.RootElement.GetProperty("ok").GetBoolean(), $"{message} {reply}");
    }

    private static void RequireRejected(string reply, string reason, string message)
    {
        using var document = JsonDocument.Parse(reply);
        var error = document.RootElement.GetProperty("error");
        Require(error.ValueKind == JsonValueKind.Object && error.GetProperty("kind").GetString() == "rejected"
            && error.GetProperty("reason").GetString() == reason, $"{message} {reply}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
