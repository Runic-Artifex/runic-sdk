using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// Pins the TypeScript of the DualRunFixture.cs Views, which the emitter must keep writing once it replaces the .NET generator's
// TypeScript (R7 G6). Golden file names are ASCII. Set RUNIC_UPDATE_GOLDEN=1 to rewrite them after an intended change.
internal sealed class DualRunGoldenTests
{
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
        ]);
    }
}
