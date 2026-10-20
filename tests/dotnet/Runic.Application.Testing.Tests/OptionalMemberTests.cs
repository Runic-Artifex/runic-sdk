using System.Text.Json;
using Runic.Application.Testing;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class OptionalMemberTests
{
    [Test]
    public void MissingMembersTakeTheirDotNetDefaults()
    {
        var model = new OptionalMemberViewModel();
        using var host = Host(model);

        Call(host, "Submit", """{"kind":"build"}""");
        Require(model.LastRequest is { Kind: "build", Label: null, Retention: 3, Edits: null, Extra.ValueKind: JsonValueKind.Undefined,
            Scope: OptionalScope.Home, Ratio: 0.5, Note: "none" }, $"Missing members did not take their defaults: {model.LastRequest}");
        Call(host, "Submit", """{"kind":"switch","label":"l","retention":1,"edits":true,"extra":null,"scope":"System","ratio":2,"note":"n"}""");
        Require(model.LastRequest is { Kind: "switch", Label: "l", Retention: 1, Edits: true, Extra.ValueKind: JsonValueKind.Null,
            Scope: OptionalScope.System, Ratio: 2, Note: "n" }, $"Given members were not read: {model.LastRequest}");
        Call(host, "Submit", """{"kind":"boot","extra":{"a":[1]}}""");
        Require(model.LastRequest is { Extra.ValueKind: JsonValueKind.Object } && model.LastRequest.Extra.GetProperty("a")[0].GetInt32() == 1,
            "A JsonElement member was not read.");
        Rejected(host, "Submit", """{"label":"no kind"}""");
        Rejected(host, "Submit", """{"kind":"build","retention":null}""");

        // A missing member of a settable DTO keeps the value a new instance has.
        // A non-nullable member without a constructor default stays required: .NET cannot tell its initializer.
        Rejected(host, "Configure", "{}");
        Call(host, "Configure", """{"name":"named"}""");
        Require(model.LastSettings is { Name: "named", Limit: 7, Note: null }, "Missing init members lost their initializers.");
        Call(host, "Configure", """{"name":"given","limit":null,"note":"n"}""");
        Require(model.LastSettings is { Name: "given", Limit: null, Note: "n" }, "Given init members were not read.");
        Rejected(host, "Configure", """{"name":null}""");

        using var snapshot = host.RawSnapshot();
        var state = snapshot.RootElement.GetProperty("state");
        Require(state.GetProperty("summary").GetRawText() == """{"runs":3,"text":"3 runs"}""",
            $"An output-only DTO was not written: {state}");
        Require(state.GetProperty("versions").GetRawText() == """[{"name":"new"},{"name":"removed","previous":null},{"name":"updated","previous":"1.0"}]""",
            $"A JsonElement member without a value was not left out: {state}");

        var types = File.ReadAllText(Path.Combine(GeneratedTypeScriptDirectory(), "types.ts")).ReplaceLineEndings("\n");
        foreach (var expected in new[]
        {
            "export interface OptionalRequest {\n  readonly kind: string;\n  readonly label?: string | null;\n  readonly retention?: number;\n  readonly edits?: boolean | null;\n  readonly extra?: unknown;\n  readonly scope?: OptionalScope;\n  readonly ratio?: number;\n  readonly note?: string;\n}",
            "export interface OptionalSettings {\n  readonly name: string;\n  readonly limit?: number | null;\n  readonly note?: string | null;\n}",
            // Reaches the page: every member is present, except a JsonElement's.
            "export interface OptionalSummary {\n  readonly runs: number;\n  readonly text: string;\n}",
            "export interface OptionalVersion {\n  readonly name: string;\n  readonly previous?: unknown;\n}",
        })
            Require(types.Contains(expected, StringComparison.Ordinal), $"types.ts is missing:\n{expected}\n---\n{types}");
    }

    private static RunicWindowTestHost<OptionalMemberViewModel> Host(OptionalMemberViewModel model) =>
        new(model, (transport, content, vm) => new OptionalMemberBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { RootRoute = "optionalMember", ViewLocator = new TestViewLocator() });

    private static void Call(RunicWindowTestHost<OptionalMemberViewModel> host, string command, string input)
    {
        using var reply = JsonDocument.Parse(host.Transport.Call($"optionalMember{command}", new(StringValue: input)));
        Require(reply.RootElement.GetProperty("ok").GetBoolean(), $"{command}({input}) failed: {reply.RootElement}");
    }

    private static void Rejected(RunicWindowTestHost<OptionalMemberViewModel> host, string command, string input)
    {
        using var reply = JsonDocument.Parse(host.Transport.Call($"optionalMember{command}", new(StringValue: input)));
        Require(!reply.RootElement.GetProperty("ok").GetBoolean(), $"{command}({input}) was accepted: {reply.RootElement}");
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
