using System.Globalization;
using System.Text.Json;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class BridgeLimitsTests
{
    private const int DefaultLimit = WindowContentSessionOptions.DefaultMaximumInputBytes;
    private static readonly JsonSerializerOptions Unescaped = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The limit counts the UTF-8 bytes of the whole request, which embeds the value (specs/application/README.md,
    // invalid-input): "at the limit" is a value whose request has exactly the limit's size.
    [Test]
    public void SetterAndCommandValuesShareTheDefaultLimit()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model);
        var route = host.RootRoute;
        var large = new string('x', DefaultLimit + 1);

        RequireRejected(SetText(host, large), "invalid-input", "A setter value over 1 MiB was not rejected.");
        Require(model.Text.Length == 0, "A setter value over 1 MiB was applied.");
        // The limit counts UTF-8 bytes: 600 000 two-byte characters are 1.2 MB.
        RequireRejected(SetText(host, new string('é', 600_000)), "invalid-input",
            "A setter value of 1.2 MB in UTF-8 was not rejected.");
        // Above the former 64 KiB limit of checked writes.
        RequireOk(host.Transport.Write(route, "text", Guid.NewGuid().ToString("N"), 0, JsonSerializer.Serialize(model.Text),
            JsonSerializer.Serialize(new string('y', 100_000))), "A checked write of 100 KB was refused.");
        RequireOk(SetText(host, AtLimit(DefaultLimit, value => SetTextRequest(route, value, 1_000))),
            "A setter request of 1 MiB was refused.");
        RequireOk(SetText(host, new string('y', 100_000)), "A setter value of 100 KB was refused.");

        RequireRejected(host.Transport.Command(route, "echo", JsonSerializer.Serialize(large)), "invalid-input",
            "A command argument over 1 MiB was not rejected.");
        Require(model.Text.Length == 100_000, "A command argument over 1 MiB ran the command.");
    }

    [Test]
    public void ALoweredLimitAppliesToEveryInput()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model, maximumInputBytes: 4_096);
        var route = host.RootRoute;
        var over = new string('x', 4_097);

        RequireRejected(SetText(host, over), "invalid-input", "A setter value over a lowered limit was not rejected.");
        var atLimit = AtLimit(4_096, value => SetTextRequest(route, value, 1_000));
        RequireOk(SetText(host, atLimit), "A setter request at a lowered limit was refused.");
        RequireRejected(host.Transport.Write(route, "text", Guid.NewGuid().ToString("N"), 0, JsonSerializer.Serialize(model.Text),
            JsonSerializer.Serialize(over)), "invalid-input", "A checked write over a lowered limit was not rejected.");
        RequireRejected(host.Transport.Command(route, "echo", JsonSerializer.Serialize(over)), "invalid-input",
            "A command argument over a lowered limit was not rejected.");
        Require(model.Text == atLimit, "An input over a lowered limit changed the model.");

        // An interaction reply is limited too: invalid before its identity is looked at.
        var reply = BridgeRequests.Json(writer =>
        {
            writer.WriteString("kind", "interaction-reply");
            writer.WriteString("route", route);
            writer.WriteString("presentation", "page-1");
            writer.WriteString("requestId", "unknown");
            writer.WriteNumber("ownerEpoch", 1);
            writer.WriteString("name", "confirm");
            writer.WriteString("contract", "c");
            writer.WriteString("outcome", "answered");
            writer.WriteString("output", new string('z', 4_096));
        });
        RequireRejected(host.Transport.Send(reply), "invalid-input", "An interaction reply over a lowered limit was not rejected.");
        using var defaultHost = Host(new BridgeLimitsViewModel());
        Require(!defaultHost.Transport.Send(reply).Contains("invalid-input", StringComparison.Ordinal),
            "An interaction reply under the default limit was refused for its size.");
    }

    [Test]
    public void ARaisedLimitAcceptsLargerInputs()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model, maximumInputBytes: 4 * DefaultLimit);
        var value = new string('x', 2 * DefaultLimit);
        RequireOk(SetText(host, value), "A setter value under a raised limit was refused.");
        RequireOk(host.Transport.Command(host.RootRoute, "echo", JsonSerializer.Serialize(value)),
            "A command argument under a raised limit was refused.");
        Require(model.Text.Length == value.Length, "A value under a raised limit was not applied.");
    }

    [Test]
    public void LimitsOutsideTheRangeAreRefused()
    {
        foreach (var invalid in new[] { 0, 1_023, 64 * 1_048_576 + 1 })
        {
            using var transport = new InMemoryBridgeTransport();
            RequireThrows<ArgumentOutOfRangeException>(() =>
                new WindowContentSession(transport, new WindowContentSessionOptions { MaximumInputBytes = invalid }).Dispose(),
                $"A session accepted the input limit {invalid}.");
            RequireThrows<ArgumentOutOfRangeException>(() => new RunicWindowOptions { MaximumInputBytes = invalid }.ThrowIfInvalid(),
                $"Window options accepted the input limit {invalid}.");
        }
        using var minimum = new InMemoryBridgeTransport();
        new WindowContentSession(minimum, new WindowContentSessionOptions { MaximumInputBytes = 1_024 }).Dispose();
        new RunicWindowOptions { MaximumInputBytes = 64 * 1_048_576 }.ThrowIfInvalid();
    }

    private static string SetText(RunicWindowTestHost<BridgeLimitsViewModel> host, string value) =>
        host.Transport.Send(SetTextRequest(host.RootRoute, value, NextEdit()));

    private static long _edit = 1_000;
    private static long NextEdit() => Interlocked.Increment(ref _edit);

    private static string SetTextRequest(string route, string value, long edit) => BridgeRequests.Json(writer =>
    {
        writer.WriteString("kind", "set");
        writer.WriteString("route", route);
        writer.WriteString("member", "text");
        writer.WritePropertyName("value");
        // Unescaped, so that a two-byte character stays two bytes of the request.
        writer.WriteRawValue(JsonSerializer.Serialize(value, Unescaped), skipInputValidation: true);
        writer.WriteNumber("edit", edit);
    });

    // A value of ASCII characters whose request has exactly limit bytes (edit numbers of four digits).
    private static string AtLimit(int limit, Func<string, string> request) =>
        new('x', limit - System.Text.Encoding.UTF8.GetByteCount(request("")));

    private static void RequireThrows<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    [Test]
    public void OutOfRangeNumbersAreRejected()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model);
        var route = host.RootRoute;

        RequireRejected(host.Transport.Set(route, "count", ((long)int.MaxValue + 1).ToString(CultureInfo.InvariantCulture)), "invalid-input",
            "An out-of-range int setter value was not rejected.");
        RequireRejected(host.Transport.Command(route, "take", "4294967296"), "invalid-input",
            "An out-of-range int command argument was not rejected.");
        Require(model.Count == 0, "An out-of-range value was applied.");

        RequireRejected(host.Transport.Set(route, "large", JsonSerializer.Serialize(new string('9', 4_097))),
            "invalid-input", "A number string over 4 096 characters was not rejected.");
        var digits = new string('9', 4_096);
        RequireOk(host.Transport.Set(route, "large", JsonSerializer.Serialize(digits)),
            "A number string of 4 096 characters was refused.");
        Require(model.Large.ToString(CultureInfo.InvariantCulture) == digits, "A 4 096-digit number was not read.");
    }

    [Test]
    public async Task AwaitedCommandsAreBoundedPerWindowAsync()
    {
        var model = new BridgeLimitsViewModel();
        using var host = Host(model);
        var route = host.RootRoute;
        var running = Enumerable.Range(0, 64).Select(_ => host.Transport.CommandAsync(route, "hold").AsTask()).ToArray();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (model.Held < 64 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(model.Held == 64, $"Only {model.Held} awaited commands started.");

        // Protocol 4 names the limit: rejected with the reason capacity.
        RequireRejected(await host.Transport.CommandAsync(route, "hold"), "capacity", "A 65th awaited command was not rejected.");
        Require(model.Held == 64, "The 65th awaited command ran.");

        model.Release();
        foreach (var reply in await Task.WhenAll(running)) RequireOk(reply, "A held awaited command failed.");
        RequireOk(await host.Transport.CommandAsync(route, "hold"), "An awaited command was refused after the others ended.");
    }

    [Test]
    public void NumberStringsAreBounded()
    {
        using var accepted = JsonDocument.Parse(JsonSerializer.Serialize(new string('1', 4_096)));
        Require(BridgeWire.ReadBigInteger(accepted.RootElement).ToString(CultureInfo.InvariantCulture).Length == 4_096, "A 4 096-digit integer was refused.");
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

    private static RunicWindowTestHost<BridgeLimitsViewModel> Host(BridgeLimitsViewModel model, int? maximumInputBytes = null) =>
        new(model, (transport, content, vm) => new BridgeLimitsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions
            {
                RootRoute = "bridgeLimits", ViewLocator = new TestViewLocator(), MaximumInputBytes = maximumInputBytes,
            });

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
