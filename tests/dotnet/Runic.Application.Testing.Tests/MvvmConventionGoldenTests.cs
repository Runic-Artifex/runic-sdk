using Runic.Application.Views;

namespace Runic.Application.Testing.Tests;

// W250-030: CommunityToolkit and ReactiveUI recognition lives in per-library
// convention modules. These goldens pin the generated contracts of both
// flavours, and the fingerprint inputs of their ViewModels, so moving that
// recognition cannot change what an application receives. Set
// RUNIC_UPDATE_GOLDEN=1 to rewrite them after an intended change.
internal static class MvvmConventionGoldenTests
{
    private static readonly (Type Model, string Module)[] Models =
    [
        (typeof(ToolkitTypedViewModel), "toolkitTyped"),
        (typeof(ToolkitCancelViewModel), "toolkitCancel"),
        (typeof(DocumentedViewModel), "documented"),
        (typeof(TypedReactiveViewModel), "typedReactive"),
        (typeof(GeneratedReactiveViewModel), "generatedReactive"),
        (typeof(GeneratedInteractionViewModel), "generatedInteraction"),
    ];

    internal static void Run()
    {
        var project = Path.Combine(DomainFailureCodegenTests.FindWorkspaceRoot(), "tests", "dotnet", "Runic.Application.Testing.Tests");
        var generated = Path.Combine(project, "obj", "bridge-frontend", "generated");
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(MvvmConventionGoldenTests).Assembly)?.Configuration ?? "Debug";
        var bridges = Path.Combine(project, "obj", configuration, "net10.0", "runic-bridge");
        var outputs = Models.SelectMany(entry => new (string Golden, string Actual)[]
        {
            (entry.Module + ".ts", Path.Combine(generated, entry.Module + ".ts")),
            (entry.Model.Name + ".Bridge.g.cs", Path.Combine(bridges, entry.Model.FullName + ".Bridge.g.cs")),
        }).ToList();

        // The fingerprint hashes every model of the assembly; keep only the
        // sections of these models so an unrelated fixture does not change it.
        var shape = Path.Combine(project, "obj", "mvvm-conventions-contract-shape.txt");
        File.WriteAllText(shape, string.Join('\n', ModelSections(BridgeContractShape.Parts(Models[0].Model),
            Models.Select(entry => entry.Model.FullName!).ToHashSet(StringComparer.Ordinal))) + "\n");
        outputs.Add(("contract-shape.txt", shape));
        DomainFailureCodegenTests.CompareGolden(project, "mvvm-conventions", [.. outputs]);
    }

    // A model section starts at its "model:" line and ends before the next
    // "model:" or "view:" line.
    private static IEnumerable<string> ModelSections(IEnumerable<string> parts, IReadOnlySet<string> models)
    {
        var include = false;
        foreach (var part in parts)
        {
            if (part.StartsWith("model:", StringComparison.Ordinal))
                include = models.Contains(part["model:".Length..part.IndexOf(":public-name:", StringComparison.Ordinal)]);
            else if (part.StartsWith("view:", StringComparison.Ordinal))
                include = false;
            if (include) yield return part;
        }
    }
}
