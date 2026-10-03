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
        }

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
