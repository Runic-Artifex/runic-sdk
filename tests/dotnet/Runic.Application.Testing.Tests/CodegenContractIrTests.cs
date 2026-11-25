using System.Text.Json;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// The contract IR (specs/application/contract-ir.md): the generator writes <c>contract.ir.json</c> beside the
/// generated C#, with the sources of the development source map, and records it as a cached output.
/// </summary>
internal sealed partial class CodegenDiagnosticsTests
{
    [Test]
    public async Task ContractIrIsWrittenBesideTheBridgesAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-contract-ir-{Guid.NewGuid():N}");
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "with-pdb", symbols: true).ConfigureAwait(false);
            using var ir = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "cs", "contract.ir.json")));
            var root = ir.RootElement;
            Require(root.GetProperty("format").GetString() == "runic.views.contract-ir/1"
                && root.GetProperty("producer").GetProperty("unit").GetString() == "FixtureSourceMap",
                $"The IR has the wrong format or unit: {root}");
            var view = root.GetProperty("views").EnumerateArray().Single();
            Require(view.GetProperty("id").GetString() == "Fixture.MapViewModel" && view.GetProperty("route").GetString() == "map",
                $"The IR has the wrong View: {view}");
            // UpperCamelCase names beside the wire names; commands named without their Command suffix.
            var members = view.GetProperty("members").EnumerateArray().Select(member => (member.GetProperty("name").GetString(), member.GetProperty("wire").GetString())).ToArray();
            Require(members.Contains(("Count", "count")) && members.Contains(("Title", "title")) && members.Contains(("Total", "total")),
                $"The IR's members are not named as expected: {string.Join(", ", members)}");
            var commands = view.GetProperty("commands").EnumerateArray().Select(command => command.GetProperty("name").GetString()).ToArray();
            Require(commands.SequenceEqual(["Save", "Reset", "Echo", "Shout", "Archive"]), $"The IR's commands: {string.Join(", ", commands)}");
            // The fingerprint is the one the generated client carries.
            var client = File.ReadAllText(Path.Combine(directory, "ts", "map.ts"));
            Require(client.Contains($"\"Fixture.MapViewModel:{view.GetProperty("fingerprint").GetString()}\"", StringComparison.Ordinal),
                "The IR's fingerprint is not the generated client's.");
            // Sources come from the development source map, with the fixture's file.
            var source = Path.GetFullPath(Path.Combine(directory, "Fixture.cs"));
            var count = root.GetProperty("sources").GetProperty("Fixture.MapViewModel/state/count");
            Require(count.GetProperty("file").GetString() == source && count.GetProperty("line").GetInt32() > 0,
                $"The IR's source of count is not in {source}: {count}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Test]
    public async Task ContractIrIsACachedOutputAsync()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-contract-ir-cache-{Guid.NewGuid():N}");
        try
        {
            var directory = await GenerateSourceMapFixtureAsync(temporaryRoot, "cached", symbols: true, cache: true).ConfigureAwait(false);
            var ir = Path.Combine(directory, "cs", "contract.ir.json");
            var written = File.ReadAllText(ir);
            var (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: true).ConfigureAwait(false);
            Require(exitCode == 0 && output.Contains("Bridge generation is up to date.", StringComparison.Ordinal),
                $"An unchanged generation was not a cache hit.\n{output}");
            File.Delete(ir);
            (exitCode, output) = await RunSourceMapGeneratorAsync(directory, cache: true).ConfigureAwait(false);
            Require(exitCode == 0 && !output.Contains("up to date", StringComparison.Ordinal) && File.ReadAllText(ir) == written,
                $"A deleted contract IR did not regenerate.\n{output}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }
}
