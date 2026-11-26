using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Pins the TypeScript of the DualRunFixture.cs Views, which the emitter writes from the contract IR (R7 G6). Golden file
// names are ASCII. Set RUNIC_UPDATE_GOLDEN=1 to rewrite them after an intended change.
internal sealed class DualRunGoldenTests
{
    // The modules whose names start with or contain a non-ASCII letter.
    private static readonly string[] NonAsciiModules = ["übersicht", "größen"];

    [Test]
    public void DualRunFixturesMatchTheGoldenFiles()
    {
        var project = Path.Combine(DomainFailureCodegenTests.FindWorkspaceRoot(), "tests", "dotnet", "Runic.Application.Testing.Tests");
        var generated = Path.Combine(project, "obj", "bridge-frontend", "generated");
        DomainFailureCodegenTests.CompareGolden(project, "dual-run",
        [
            ("dualRunBounds.ts", Path.Combine(generated, "dualRunBounds.ts")),
            ("groessen.ts", Path.Combine(generated, "größen.ts")),
            ("groessen.contract.d.ts", Path.Combine(generated, "größen.contract.d.ts")),
            ("uebersicht.ts", Path.Combine(generated, "übersicht.ts")),
            ("uebersicht.contract.d.ts", Path.Combine(generated, "übersicht.contract.d.ts")),
        ]);
    }

    // #1355: the clients of Views whose names start with a non-ASCII letter, or contain one, type-check with the
    // modules they import, as the generated client of a page does. tsc runs as a generated-client harness child.
    [Test]
    [NotInParallel("GeneratedHarnessProcess")]
    public async Task NonAsciiClientsTypeCheckAsync()
    {
        var root = DomainFailureCodegenTests.FindWorkspaceRoot();
        var generated = Path.Combine(root, "tests", "dotnet", "Runic.Application.Testing.Tests", "obj", "bridge-frontend", "generated");
        await GeneratedHarnessProcess.RunAsync("bun", [Path.Combine(root, "node_modules", "typescript", "bin", "tsc"),
            "--noEmit", "--strict", "--noPropertyAccessFromIndexSignature", "--noImplicitReturns",
            "--noFallthroughCasesInSwitch", "--target", "ES2022", "--module", "ESNext",
            "--moduleResolution", "bundler", "--skipLibCheck",
            .. NonAsciiModules.SelectMany(module => new[] { Path.Combine(generated, module + ".ts"), Path.Combine(generated, module + ".mock.ts") })],
            root).ConfigureAwait(false);
    }
}
