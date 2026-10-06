namespace Runic.Application.Testing.Tests;

/// <summary>Runs the Bun harness against a transcript emitted by this built test application.</summary>
internal static class GeneratedClientHarness
{
    internal static async Task RunAsync()
    {
        var root = FindWorkspaceRoot();
        var generated = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
            "obj", "bridge-frontend", "generated");
        var fixture = Path.Combine(Path.GetTempPath(), $"runic-generated-client-{Guid.NewGuid():N}.json");
        try
        {
            // Match Angular's strict consumer flags across every fixture,
            // including receipts and validation that the template may not use.
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "node_modules", "typescript", "bin", "tsc"),
                "--noEmit", "--strict", "--noPropertyAccessFromIndexSignature", "--noImplicitReturns",
                "--noFallthroughCasesInSwitch", "--target", "ES2022", "--module", "ESNext",
                "--moduleResolution", "bundler", "--skipLibCheck", .. Directory.GetFiles(generated, "*.ts"),
                Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "GeneratedTypesCheck.ts")], root)
                .ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("dotnet", [typeof(GeneratedClientHarness).Assembly.Location,
                "--export-generated-client-fixture", fixture], root).ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
                "GeneratedClientHarness.ts"), fixture, generated], root).ConfigureAwait(false);
            await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests",
                "GeneratedReconnectClientHarness.ts"), generated], root).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(fixture); }
            catch (IOException) { }
        }
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }
}
