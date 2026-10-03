using System.Text.Json;
using Runic.Application.Testing;

namespace Runic.Application.Testing.Tests;

// Exercises the generated codecs for CodegenShapeViewModel through its routes.
internal static class CodegenShapeTests
{
    internal static void Run()
    {
        var model = new CodegenShapeViewModel();
        using var host = new RunicWindowTestHost<CodegenShapeViewModel>(model, "codegenShape",
            (transport, content, vm) => new CodegenShapeBridge(transport, vm, content: content), new TestViewLocator());

        using (var snapshot = host.Snapshot())
        {
            var state = snapshot.RootElement.GetProperty("state");
            Require(state.GetProperty("mode").GetString() == "None",
                "An enum alias did not write the first declared name for its value.");
            Require(state.GetProperty("numbers").GetArrayLength() == 2 && state.GetProperty("optionalNames").ValueKind == JsonValueKind.Null,
                "ImmutableArray state was not written.");
            Require(state.GetProperty("origin").ValueKind == JsonValueKind.Null
                && state.GetProperty("corner").GetProperty("y").GetInt32() == 4,
                "Nullable or non-nullable struct DTO state was not written.");
            Require(state.GetProperty("labels")[1].ValueKind == JsonValueKind.Null,
                "A nullable array element was not written as null.");
            Require(state.GetProperty("stamp").GetString() == "2026-10-03T12:30:00.0000000+02:00",
                "A DateTimeOffset snapshot lost its offset.");
        }

        // Values produced by new Date().toISOString() and by hand in a browser.
        Set(host, "Stamp", "\"2026-10-03T12:34:56.789Z\"");
        Require(model.Stamp == new DateTimeOffset(2026, 10, 3, 12, 34, 56, 789, TimeSpan.Zero),
            "A browser ISO DateTimeOffset was not accepted.");
        Set(host, "When", "\"2026-10-03T12:34:56.789Z\"");
        Require(model.When == new DateTime(2026, 10, 3, 12, 34, 56, 789, DateTimeKind.Utc) && model.When.Kind is DateTimeKind.Utc,
            "A browser ISO DateTime was not accepted as UTC.");
        Set(host, "At", "\"08:15:00\"");
        Require(model.At == new TimeOnly(8, 15), "An ISO time without fractional seconds was not accepted.");

        Set(host, "Mode", "\"Default\"");
        Require(model.Mode == CodegenShapeMode.Default, "An enum alias name could not be read.");
        Set(host, "Mode", "\"Active\"");
        Require(model.Mode == CodegenShapeMode.Active, "An enum name could not be read.");
        Set(host, "Numbers", "[5,6,7]");
        Require(model.Numbers is [5, 6, 7], "ImmutableArray<int> did not round-trip through its codec.");
        Set(host, "OptionalNames", "[\"a\",null]");
        Require(model.OptionalNames is { } names && names is ["a", null], "ImmutableArray<string?>? did not round-trip.");
        Set(host, "Origin", "{\"x\":1,\"y\":2}");
        Require(model.Origin == new CodegenPoint(1, 2), "A nullable struct DTO could not be read.");
        Set(host, "Origin", "null");
        Require(model.Origin is null, "A nullable struct DTO did not accept null.");
        Set(host, "Tags", "[\"x\",\"y\"]");
        Require(model.Tags is ["x", "y"], "ReadOnlyCollection<string> did not round-trip.");
        Set(host, "Labels", "[null,\"z\"]");
        Require(model.Labels is [null, "z"], "A nullable array element was rejected.");
        Set(host, "Editable", "[\"one\"]");
        model.Editable.Add("two");
        Require(model.Editable.Count == 2, "An IList<T> codec produced a fixed-size collection.");
    }

    internal static async Task RunNullableReactiveAsync()
    {
        var client = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "nullableReactive.ts"));
        foreach (var expected in new[]
        {
            "echo(argument: string | null): Promise<NullableReactiveState>;",
            "export interface NullableReactiveEchoOperation extends BridgeOperation<string | null> {}",
            "view.recoverOperation<string | null>(\"Echo\", requestId,",
            "handle(handler: (input: string, context: NullableReactiveInteractionContext) => string | null | Promise<string | null>): () => void;",
        })
            Require(client.Contains(expected, StringComparison.Ordinal),
                $"The generated client lost a nullable ReactiveUI type argument: missing '{expected}'.");

        using var model = new NullableReactiveViewModel();
        using var host = new RunicWindowTestHost<NullableReactiveViewModel>(model, "nullableReactive",
            (transport, content, vm) => new NullableReactiveBridge(transport, vm, content: content), new TestViewLocator());
        using (var snapshot = host.Snapshot())
            Require(snapshot.RootElement.GetProperty("state").TryGetProperty("isEchoExecuting", out var executing)
                && executing.ValueKind == JsonValueKind.False,
                "A ReactiveUI command with an argument did not write the isEchoExecuting field its client declares.");
        using var accepted = JsonDocument.Parse(host.Transport.Call("nullableReactiveStartEcho",
            new(StringValue: "{\"requestId\":\"echo-null\",\"input\":null}")));
        Require(accepted.RootElement.GetProperty("kind").GetString() is "accepted" or "duplicate",
            $"A null ReactiveUI command input was rejected: {accepted.RootElement}");
        var contract = accepted.RootElement.GetProperty("contract").GetString();
        using var completion = JsonDocument.Parse(await host.Transport.CallAsync("__runicOperationWait",
            new(StringValue: JsonSerializer.Serialize(new { contract, requestId = "echo-null" }))));
        Require(completion.RootElement.GetProperty("kind").GetString() == "succeeded"
            && completion.RootElement.GetProperty("result").ValueKind == JsonValueKind.Null,
            $"A null ReactiveUI command result was not encoded: {completion.RootElement}");
    }

    private static string GeneratedTypeScriptDirectory()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx")))
                return Path.Combine(directory.FullName, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Set(RunicWindowTestHost<CodegenShapeViewModel> host, string property, string json)
    {
        using var reply = JsonDocument.Parse(host.Transport.Call($"codegenShapeSet{property}", new(StringValue: json)));
        Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"Setting {property} to {json} was rejected: {reply.RootElement}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
