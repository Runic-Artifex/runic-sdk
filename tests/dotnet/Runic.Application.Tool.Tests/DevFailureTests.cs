using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// D087: a dev that stops on a failure writes it into the session file before it exits, so an IDE shows it after dev
// ended, however dev was started. VS Code removes a background task's problems when its process ends.
internal sealed partial class ToolTests
{
    [Test, DisplayName("the session file has no failure while dev runs, and the failure with stopped after one stopped dev (D087)")]
    public void SessionFileRecordsTheFailure()
    {
        using var workspace = new TestWorkspace();
        string packageDirectory = Path.Combine(workspace.Root, "shared-ui");
        FrontendDependency package = CompiledPackage(packageDirectory);
        DevProjectConfiguration configuration = CreateConfiguration(workspace, "vite");
        var session = new DevSession(configuration, new DevOptions(null, "Debug", true, true, true, false, []), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        session.Start();
        session.Dependencies([package], _ => new DependencyState(DependencyState.Built, "rebuild"));
        using (JsonDocument running = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            Equal(JsonValueKind.Null, running.RootElement.GetProperty("failure").ValueKind);
        }

        string source = Path.Combine(packageDirectory, "src", "index.ts");
        var error = new DevDevelopmentException(FrontendDependencies.FailureCode,
            $"A frontend package that the page depends on failed to build, so dev did not start: {source}(12,14): error TS2322: Type 'string' is not assignable to type 'number'.")
        {
            FailedProject = package.Owner,
            FailedPackage = packageDirectory,
            Diagnostics = DevFailure.Parse($"""
                  src/index.ts(12,14): error TS2322: Type 'string' is not assignable to type 'number'.
                {source}(12,14): error TS2322: Type 'string' is not assignable to type 'number'. [{package.Owner}]
                {source}(3,1): warning TS6133: 'x' is declared but its value is never read. [{package.Owner}]
                Build FAILED.
                {source}(12,14): error TS2322: Type 'string' is not assignable to type 'number'. [{package.Owner}]
                """, configuration.ProjectDirectory),
        };
        // Nothing is written until the stop, which writes the failure and "stopped" at once.
        session.Failed(DevApplication.Failure(configuration, error));
        using (JsonDocument unchanged = JsonDocument.Parse(File.ReadAllBytes(session.Path)))
        {
            Equal("starting", unchanged.RootElement.GetProperty("state").GetString());
            Equal(JsonValueKind.Null, unchanged.RootElement.GetProperty("failure").ValueKind);
        }
        session.Stop();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
        JsonElement root = document.RootElement;
        Equal("stopped", root.GetProperty("state").GetString());
        JsonElement failure = root.GetProperty("failure");
        Equal("RAPPDEV1018", failure.GetProperty("code").GetString());
        Equal(error.Message, failure.GetProperty("message").GetString());
        Equal(Path.GetFullPath(package.Owner!), failure.GetProperty("project").GetString());
        Equal(packageDirectory, failure.GetProperty("package").GetString());
        // tsc's own relative echo and MSBuild's summary repeat are left out; the errors come first, dev's own line last.
        JsonElement[] diagnostics = [.. failure.GetProperty("diagnostics").EnumerateArray()];
        Equal(3, diagnostics.Length);
        Equal((source, 12, 14, "error", "TS2322", "Type 'string' is not assignable to type 'number'."), Diagnostic(diagnostics[0]));
        Equal((source, 3, 1, "warning", "TS6133", "'x' is declared but its value is never read."), Diagnostic(diagnostics[1]));
        Equal(Path.GetFullPath(configuration.ProjectPath), diagnostics[2].GetProperty("file").GetString());
        Equal(JsonValueKind.Null, diagnostics[2].GetProperty("line").ValueKind);
        Equal(JsonValueKind.Null, diagnostics[2].GetProperty("column").ValueKind);
        Equal("RAPPDEV1018", diagnostics[2].GetProperty("code").GetString());
        Equal(error.Message, diagnostics[2].GetProperty("message").GetString());
        // The package that failed is "failed" in dependencies[], with the failure's message.
        JsonElement entry = root.GetProperty("dependencies")[0];
        Equal("failed", entry.GetProperty("state").GetString());
        Equal(error.Message, entry.GetProperty("message").GetString());

        // A stop without a failure (Ctrl+C) writes none, and the next dev's session starts without one.
        var next = new DevSession(configuration, new DevOptions(null, "Debug", true, true, true, false, []), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        next.Start();
        using (JsonDocument started = JsonDocument.Parse(File.ReadAllBytes(next.Path)))
        {
            Equal(JsonValueKind.Null, started.RootElement.GetProperty("failure").ValueKind);
        }
        next.Stop();
        using JsonDocument cancelled = JsonDocument.Parse(File.ReadAllBytes(next.Path));
        Equal(JsonValueKind.Null, cancelled.RootElement.GetProperty("failure").ValueKind);
    }

    private static (string?, int, int, string?, string?, string?) Diagnostic(JsonElement diagnostic) =>
        (diagnostic.GetProperty("file").GetString(), diagnostic.GetProperty("line").GetInt32(), diagnostic.GetProperty("column").GetInt32(),
            diagnostic.GetProperty("severity").GetString(), diagnostic.GetProperty("code").GetString(), diagnostic.GetProperty("message").GetString());

    [Test, DisplayName("a failure lists at most 100 diagnostics, errors first, each message one bounded line (D087)")]
    public void FailureIsBounded()
    {
        string project = Path.GetFullPath("/repo/App/App.csproj");
        string output = string.Join('\n', Enumerable.Range(1, 150).Select(line => $"/repo/App/A.cs({line},1): warning CS0168: unused")) + "\n" +
            "/repo/App/B.cs(7,3): error CS1002: ; expected [/repo/Lib/Lib.csproj]\n" +
            "relative/C.cs(2,2): error CS0103: name\n" +
            "not a path\0(1,1): error CS0001: x\n";
        DevFailure failure = DevFailure.Create("RAPPDEV1006", "The initial build failed:\nsee above. " + new string('x', 3000), project,
            DevFailure.FirstErrorProject(output), diagnostics: DevFailure.Parse(output, Path.GetFullPath("/repo/App")));
        Equal(DevFailure.MaxDiagnostics, failure.Diagnostics.Count);
        Equal(Path.GetFullPath("/repo/Lib/Lib.csproj"), failure.Project);
        Equal(null, failure.Package);
        Equal((Path.GetFullPath("/repo/App/B.cs"), (int?)7, (int?)3, "error", "CS1002"),
            (failure.Diagnostics[0].File, failure.Diagnostics[0].Line, failure.Diagnostics[0].Column, failure.Diagnostics[0].Severity, failure.Diagnostics[0].Code));
        // A relative C# file resolves against the project's directory.
        Equal(Path.GetFullPath("/repo/App/relative/C.cs"), failure.Diagnostics[1].File);
        Equal("warning", failure.Diagnostics[2].Severity);
        Equal("RAPPDEV1006", failure.Diagnostics[^1].Code);
        Equal(project, failure.Diagnostics[^1].File);
        DoesNotContain(failure.Message, "\n");
        Equal(DevFailure.MaxMessageLength, failure.Message.Length);
        True(failure.Message.EndsWith('…'), failure.Message);
        // A project's own error names the project; a build without errors names none.
        Equal(project, DevFailure.FirstErrorProject($"{project} : error RUNICBRIDGE016: The frontend build exited with code 2."));
        Equal(null, DevFailure.FirstErrorProject("/repo/App/A.cs(1,1): warning CS0168: unused"));
    }

    [Test, DisplayName("a dependency package's build error names its project and its package for the failure (D087)")]
    public void PackageErrorNamesThePackage()
    {
        using var workspace = new TestWorkspace();
        string app = workspace.Write("App/App.csproj", "<Project />");
        string frontend = Path.Combine(workspace.Root, "App", "Frontend");
        string library = workspace.Write("SharedUi/SharedUi.csproj", "<Project />");
        workspace.Write("shared-ui/package.json", "{}");
        string source = workspace.Write("shared-ui/src/index.ts", "export const x: number = '';");
        FrontendDependencies.PackageError located = FrontendDependencies.FindPackageError(
            $"{source}(12,14): error TS2322: Type 'string' is not assignable to type 'number'. [{library}]", app, frontend)!;
        Equal($"{source}(12,14): error TS2322: Type 'string' is not assignable to type 'number'.", located.Text);
        Equal(Path.GetFullPath(library), located.Project);
        Equal(Path.Combine(workspace.Root, "shared-ui"), located.Package);
        FrontendDependencies.PackageError bridge = FrontendDependencies.FindPackageError(
            $"{library} : error RUNICBRIDGE016: The frontend build 'bun run --bun build' in {workspace.Root}/shared-ui exited with code 2. [{app}]", app, frontend)!;
        Equal(Path.GetFullPath(library), bridge.Project);
        Equal(null, bridge.Package);

        // An unbuilt package that stops dev at its start names the package and its owner.
        DevDevelopmentException unbuilt = Throws<DevDevelopmentException>(() =>
            FrontendDependencies.RequireBuilt([CompiledPackage(Path.Combine(workspace.Root, "shared-ui"))], built: true));
        Equal(Path.Combine(workspace.Root, "shared-ui"), unbuilt.FailedPackage);
        Equal(Path.Combine(workspace.Root, "shared-ui", "..", "SharedUi", "SharedUi.csproj"), unbuilt.FailedProject);
    }

    [Test, DisplayName("a dev whose first build fails writes the build's errors into the session file before it exits (D087)")]
    public async Task FailedBuildIsInTheSessionFile()
    {
        using var workspace = new TestWorkspace();
        string project = CreateMockGuardApp(workspace, """{ "profiles": { "Acme.Mock": { "commandName": "Project" } } }""");
        string program = workspace.Write("Acme.Mock/Program.cs", "int x = \"text\";\nSystem.Console.WriteLine(x);\n");
        string directory = Path.GetDirectoryName(project)!;

        (int exitCode, string output) = await RunToolAsync(directory, "dev", "-c", "Debug", "--no-dotnet-watch", "--no-frontend-watch");
        Equal(Runic.Application.Tool.Program.DevelopmentFailure, exitCode);
        Contains(output, "error RAPPDEV1006: The initial build failed:");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "obj", "runic", DevSession.FileName)));
        JsonElement root = document.RootElement;
        Equal("stopped", root.GetProperty("state").GetString());
        JsonElement failure = root.GetProperty("failure");
        Equal("RAPPDEV1006", failure.GetProperty("code").GetString());
        Equal(Path.GetFullPath(project), failure.GetProperty("project").GetString());
        Equal(JsonValueKind.Null, failure.GetProperty("package").ValueKind);
        JsonElement[] diagnostics = [.. failure.GetProperty("diagnostics").EnumerateArray()];
        JsonElement compiler = diagnostics.First(diagnostic => diagnostic.GetProperty("code").GetString() == "CS0029");
        Equal((Path.GetFullPath(program), 1, 9, "error"), (compiler.GetProperty("file").GetString(), compiler.GetProperty("line").GetInt32(),
            compiler.GetProperty("column").GetInt32(), compiler.GetProperty("severity").GetString()));
        Equal(1, diagnostics.Count(diagnostic => diagnostic.GetProperty("code").GetString() == "CS0029"));
        Equal("RAPPDEV1006", diagnostics[^1].GetProperty("code").GetString());
    }
}
