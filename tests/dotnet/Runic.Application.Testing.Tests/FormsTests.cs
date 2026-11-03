using System.Text.Json;
using Runic.Application.Testing;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

internal sealed class FormsTests
{
    [Test]
    public void EnumsHaveValueListsAndFieldsCarryTheirDataAnnotations()
    {
        var directory = GeneratedTypeScriptDirectory();
        var types = File.ReadAllText(Path.Combine(directory, "types.ts")).ReplaceLineEndings("\n");
        var forms = File.ReadAllText(Path.Combine(directory, "forms.ts")).ReplaceLineEndings("\n");
        var shape = File.ReadAllText(Path.Combine(directory, "codegenShape.ts")).ReplaceLineEndings("\n");
        foreach (var (file, text, expected) in new[]
        {
            // C8: the names .NET writes, so an alias (Urgent = High) is not listed twice.
            ("types.ts", types, "export type FormsPriority = \"Low\" | \"Normal\" | \"High\" | \"Urgent\";\n"
                + "/** The values of {@link FormsPriority} that .NET writes, in declaration order. */\n"
                + "export const FormsPriorityValues = [\"Low\", \"Normal\", \"High\"] as const satisfies readonly FormsPriority[];"),
            ("types.ts", types, "export const CodegenShapeModeValues = [\"None\", \"Active\"] as const satisfies readonly CodegenShapeMode[];"),
            // Each module re-exports the value lists of the enums it uses, beside their types.
            ("forms.ts", forms, "export { FormsPriorityValues } from \"./types.js\";\n"),
            ("codegenShape.ts", shape, "export { CodegenShapeModeValues } from \"./types.js\";\n"),
            ("forms.ts", forms, "    name: \"name\", kind: \"text\", constraints: { required: true, minLength: 2, maxLength: 40 },\n"),
            ("forms.ts", forms, "    name: \"tag\", kind: \"text\", constraints: { maxLength: 8 },\n"),
            ("forms.ts", forms, "    name: \"quantity\", kind: \"number\", integer: true, constraints: { min: 1, max: 10 },\n"),
            ("forms.ts", forms, "    name: \"price\", kind: \"decimal\", constraints: { min: \"0.01\", max: \"999.99\", minExclusive: true },\n"),
            ("forms.ts", forms, "    name: \"code\", kind: \"text\", constraints: { pattern: \"^[A-Z]{3}$\" },\n"),
            ("forms.ts", forms, "    name: \"loose\", kind: \"text\",\n"),
            ("forms.ts", forms, "    name: \"priority\", kind: \"choice\", choices: [\"Low\", \"Normal\", \"High\"],\n"),
            // C7: an Int64 key is the decimal form of the bigint.
            ("forms.ts", forms, "item => String(item.id))"),
        })
            Require(text.Contains(expected, StringComparison.Ordinal), $"Generated {file} is missing:\n{expected}\n--- {file} ---\n{text}");
    }

    [Test]
    public async Task Int64CollectionKeysTravelAsTheirDecimalFormAsync()
    {
        var model = new FormsViewModel();
        using var transport = new InMemoryBridgeTransport();
        using var bridge = new FormsBridge(transport, model, "forms");
        using (transport.SnapshotState("forms")) { }
        model.Items.Add(new(9_007_199_254_740_993, "beyond 2^53"));
        model.Items.Add(new(-5, "negative"));
        var keys = new List<string?>();
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (keys.Count < 2 && DateTime.UtcNow < timeout)
        {
            foreach (var push in transport.TakePushes("forms"))
                using (push)
                    foreach (var change in push.RootElement.GetProperty("changes").EnumerateArray())
                        if (change.GetProperty("field").GetString() == "rows" && change.GetProperty("kind").GetString() == "add")
                        {
                            keys.AddRange(change.GetProperty("keys").EnumerateArray().Select(key => key.GetString()));
                            Require(change.GetProperty("items")[0].GetProperty("id").ValueKind == JsonValueKind.String,
                                $"An Int64 row field is not a JSON string: {change.GetRawText()}");
                        }
            await Task.Delay(5);
        }
        Require(keys.SequenceEqual(["9007199254740993", "-5"]), $"Int64 keys were not their decimal form: {string.Join(", ", keys)}");
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
