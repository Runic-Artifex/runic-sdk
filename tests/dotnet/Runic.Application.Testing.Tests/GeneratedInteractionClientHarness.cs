namespace Runic.Application.Testing.Tests;

/// <summary>Runs lifecycle checks against the generated Interaction browser client.</summary>
internal static class GeneratedInteractionClientHarness
{
    internal static async Task RunAsync()
    {
        var root = FindWorkspaceRoot();
        var script = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "GeneratedInteractionClientHarness.ts");
        var generated = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj",
            "bridge-frontend", "generated");
        await GeneratedHarnessProcess.RunAsync("bun", [script, generated], root).ConfigureAwait(false);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }
}
