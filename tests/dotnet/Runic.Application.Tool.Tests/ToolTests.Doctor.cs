using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// devtools-library-dx S6: doctor checks the frontend's JavaScript workspace (its root, membership and lock file), that
// one package manager is named throughout, and the frontend packages that the page depends on; it takes the package
// manager from the workspace, as the build does; and it gives split CI's EnableWindowsTargeting advice (D082 S4).
internal sealed partial class ToolTests
{
    private static readonly string[] WorkspaceWithApp = ["App/Frontend", "shared-ui"];
    private static readonly string[] WorkspaceWithoutApp = ["shared-ui"];

    /// <summary>A Bun workspace at the test root with App/Frontend and shared-ui, and a doctor project of the app.</summary>
    private static DoctorProjectConfiguration CreateWorkspaceDoctorProject(TestWorkspace workspace, out CompatibilitySetAuthority authority,
        bool member = true, DoctorFrontendGraph? graph = null)
    {
        authority = CompatibilitySetAuthority.Current;
        workspace.Write("package.json", JsonSerializer.Serialize(new
        {
            @private = true,
            workspaces = member ? WorkspaceWithApp : WorkspaceWithoutApp,
            packageManager = $"bun@{authority.Toolchain.Bun}",
        }));
        workspace.Write("bun.lock", "{}");
        workspace.Write("shared-ui/package.json", """{"name":"@acme/shared-ui","scripts":{"build":"tsc"},"exports":{".":"./dist/index.js"}}""");
        string frontend = Path.Combine(workspace.Root, "App", "Frontend");
        workspace.Write("App/Frontend/package.json", """{"name":"app","dependencies":{"@acme/shared-ui":"workspace:*"}}""");
        workspace.Write("App/Frontend/vite.config.ts", "export default {};");
        workspace.Write("App/Frontend/src/main.ts", "export {};");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Root));
        return new DoctorProjectConfiguration(
            Path.Combine(workspace.Root, "App", "App.csproj"), Path.Combine(workspace.Root, "App"), "net10.0", true,
            frontend, Path.Combine(workspace.Root, "App", "obj", "project.assets.json"), "linux-x64", "/src/main.ts",
            Path.Combine(frontend, "vite.config.ts"), true, RunicViewsHost.Desktop)
        {
            JavaScriptWorkspaceRoot = JavaScriptWorkspace.FindRoot(frontend),
            FrontendGraph = graph ?? new DoctorFrontendGraph([], member, root,
                member ? [frontend, Path.Combine(root, "shared-ui")] : [Path.Combine(root, "shared-ui")], null),
        };
    }

    private static FakeDoctorRuntime BunRuntime(CompatibilitySetAuthority authority, string os = "linux") =>
        new(authority.Toolchain)
        {
            Platform = new(os, "x64", false, "6.12"),
            Executables = ["bun"],
            Commands = (executable, arguments) => executable == "bun" && arguments.SequenceEqual(["--version"])
                ? new CommandResult(0, authority.Toolchain.Bun, string.Empty)
                : null,
        };

    private static DoctorReport InspectDoctor(DoctorProjectConfiguration project, FakeDoctorRuntime runtime) =>
        DoctorChecks.InspectAsync(project, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult();

    [Test, DisplayName("doctor takes the package manager and lock file from the JavaScript workspace root")]
    public void DoctorUsesTheWorkspacePackageManager()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateWorkspaceDoctorProject(workspace, out CompatibilitySetAuthority authority);
        // The frontend declares no package manager and has no lock file: before S6 doctor picked npm and failed lock-file.
        DoctorReport report = InspectDoctor(project, BunRuntime(authority));
        DoctorCheck runtime = Check(report, "javascript-runtime");
        Equal(DoctorStatus.Pass, runtime.Status);
        Contains(runtime.Message, "bun");
        Equal(DoctorStatus.Pass, Check(report, "package-manager").Status);
        Contains(Check(report, "package-manager").Message, "bun");
        DoctorCheck lockFile = Check(report, "lock-file");
        Equal(DoctorStatus.Pass, lockFile.Status);
        Contains(lockFile.Message, Path.Combine(project.EffectiveWorkspaceRoot, "bun.lock"));
        True(report.IsHealthy, "A Bun workspace member should pass doctor; failures: " +
            string.Join("; ", report.Checks.Where(check => check.Status == DoctorStatus.Failure).Select(check => check.Message)));

        // Without the root's lock file, lock-file names the root, where the workspace's install needs it.
        File.Delete(Path.Combine(workspace.Root, "bun.lock"));
        DoctorCheck missing = Check(InspectDoctor(project, BunRuntime(authority)), "lock-file");
        Equal(DoctorStatus.Failure, missing.Status);
        Contains(missing.Message, $"at the root of the bun workspace {project.EffectiveWorkspaceRoot}");
        Contains(missing.Remediation ?? string.Empty, $"Run 'bun install' in {project.EffectiveWorkspaceRoot}");

        // Bun missing: javascript-runtime asks for Bun, not Node.
        DoctorCheck noBun = Check(InspectDoctor(project, new FakeDoctorRuntime(authority.Toolchain)), "javascript-runtime");
        Equal(DoctorStatus.Failure, noBun.Status);
        Contains(noBun.Message, "bun is required by the bun workflow");
    }

    [Test, DisplayName("doctor frontend-workspace checks the workspace root and that it lists the frontend")]
    public void DoctorChecksTheFrontendWorkspace()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateWorkspaceDoctorProject(workspace, out _);
        string root = project.EffectiveWorkspaceRoot;
        DoctorCheck member = DoctorWorkspaceChecks.CheckWorkspace(project);
        Equal(DoctorStatus.Pass, member.Status);
        Contains(member.Message, $"is a member of the bun workspace at {root}");

        // The root's workspaces leave the frontend out (RUNICBRIDGE087).
        DoctorCheck outside = DoctorWorkspaceChecks.CheckWorkspace(project with
        {
            FrontendGraph = new DoctorFrontendGraph([], false, root, [], null),
        });
        Equal(DoctorStatus.Failure, outside.Status);
        Contains(outside.Message, "do not include it");
        Contains(outside.Message, "RUNICBRIDGE087");
        Contains(outside.Remediation ?? string.Empty, "Add 'App/Frontend' to the workspaces of " + Path.Combine(root, "package.json"));
        Contains(outside.Remediation ?? string.Empty, "RunicApplicationFrontendWorkspaceRoot=none");
        // The same from the query's error, when the graph could not be read.
        Equal(DoctorStatus.Failure, DoctorWorkspaceChecks.CheckWorkspace(project with
        {
            FrontendGraph = new DoctorFrontendGraph([], null, null, [], "RUNICBRIDGE087: The frontend … is below the bun workspace"),
        }).Status);

        // Without a graph (an unrestored package consumer), membership is not checked.
        DoctorCheck unknown = DoctorWorkspaceChecks.CheckWorkspace(project with
        {
            FrontendGraph = new DoctorFrontendGraph([], null, null, [], "MSB4057: The target \"RunicViewsGetFrontendDependencies\" does not exist in the project."),
        });
        Equal(DoctorStatus.Warning, unknown.Status);
        Contains(unknown.Message, "MSB4057");
        Contains(unknown.Remediation ?? string.Empty, "dotnet build");

        // A frontend with its own lock file below a workspace installs on its own.
        workspace.Write("App/Frontend/package-lock.json", "{}");
        DoctorProjectConfiguration own = project with { JavaScriptWorkspaceRoot = JavaScriptWorkspace.FindRoot(project.FrontendPackageDirectory) };
        DoctorCheck separate = DoctorWorkspaceChecks.CheckWorkspace(own);
        Equal(DoctorStatus.Warning, separate.Status);
        Contains(separate.Message, $"not with the JavaScript workspace at {root}");
        Contains(separate.Remediation ?? string.Empty, "Delete package-lock.json");
        // RunicApplicationFrontendWorkspaceRoot=none says that is intended.
        Equal(DoctorStatus.Pass, DoctorWorkspaceChecks.CheckWorkspace(own with { WorkspaceRootSetting = "none" }).Status);

        // Outside any workspace the frontend is its own root.
        File.Delete(Path.Combine(workspace.Root, "package.json"));
        DoctorCheck standalone = DoctorWorkspaceChecks.CheckWorkspace(own);
        Equal(DoctorStatus.Pass, standalone.Status);
        Contains(standalone.Message, "is its own JavaScript workspace root");
    }

    [Test, DisplayName("doctor package-manager-agreement checks the root's packageManager, lock files and members")]
    public void DoctorChecksPackageManagerAgreement()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateWorkspaceDoctorProject(workspace, out CompatibilitySetAuthority authority);
        string root = project.EffectiveWorkspaceRoot;
        DoctorCheck agreed = DoctorWorkspaceChecks.CheckPackageManagerAgreement(project);
        Equal(DoctorStatus.Pass, agreed.Status);
        Contains(agreed.Message, $"The workspace at {root} and its 2 member(s) that doctor read use bun (packageManager in {Path.Combine(root, "package.json")})");

        // A member that declares another package manager (RUNICBRIDGE089).
        workspace.Write("shared-ui/package.json", """{"name":"@acme/shared-ui","packageManager":"npm@12.2.0","scripts":{"build":"tsc"}}""");
        DoctorCheck member = DoctorWorkspaceChecks.CheckPackageManagerAgreement(project);
        Equal(DoctorStatus.Failure, member.Status);
        Contains(member.Message, $"{Path.Combine(root, "shared-ui", "package.json")} declares npm (packageManager), but its workspace {root} uses bun");
        Contains(member.Message, "RUNICBRIDGE089");
        Contains(member.Remediation ?? string.Empty, "Remove the packageManager of");
        workspace.Write("shared-ui/package.json", """{"name":"@acme/shared-ui","scripts":{"build":"tsc"}}""");

        // A member's own lock file, which the workspace's install does not read.
        workspace.Write("shared-ui/package-lock.json", "{}");
        DoctorCheck memberLock = DoctorWorkspaceChecks.CheckPackageManagerAgreement(project);
        Equal(DoctorStatus.Warning, memberLock.Status);
        Contains(memberLock.Message, "has its own lock file");
        File.Delete(Path.Combine(workspace.Root, "shared-ui", "package-lock.json"));

        // Lock files of two managers at the root.
        workspace.Write("package-lock.json", "{}");
        DoctorCheck twoLocks = DoctorWorkspaceChecks.CheckPackageManagerAgreement(project);
        Equal(DoctorStatus.Warning, twoLocks.Status);
        Contains(twoLocks.Message, "bun.lock, package-lock.json");
        Contains(twoLocks.Remediation ?? string.Empty, "Delete the lock files");

        // packageManager says bun, but the only lock file is npm's.
        File.Delete(Path.Combine(workspace.Root, "bun.lock"));
        DoctorCheck wrongLock = DoctorWorkspaceChecks.CheckPackageManagerAgreement(project);
        Equal(DoctorStatus.Failure, wrongLock.Status);
        Contains(wrongLock.Message, $"declares bun (packageManager), but the lock file {Path.Combine(root, "package-lock.json")} is npm's");
        Contains(wrongLock.Remediation ?? string.Empty, $"Run 'bun install' in {root}");
        File.Delete(Path.Combine(workspace.Root, "package-lock.json"));
        workspace.Write("bun.lock", "{}");

        // RunicApplicationFrontendPackageManager that differs from the workspace's.
        DoctorCheck property = DoctorWorkspaceChecks.CheckPackageManagerAgreement(project with { PackageManagerName = "npm" });
        Equal(DoctorStatus.Warning, property.Status);
        Contains(property.Message, "RunicApplicationFrontendPackageManager=npm");
        Contains(property.Remediation ?? string.Empty, "make it bun");

        // A frontend on its own: its packageManager and lock file agree.
        string frontend = Path.Combine(workspace.Root, "Standalone");
        workspace.Write("Standalone/package.json", $$"""{"packageManager":"npm@{{authority.Toolchain.Npm}}"}""");
        workspace.Write("Standalone/pnpm-lock.yaml", "");
        DoctorProjectConfiguration standalone = project with { FrontendPackageDirectory = frontend, JavaScriptWorkspaceRoot = frontend, FrontendGraph = null };
        Equal(DoctorStatus.Failure, DoctorWorkspaceChecks.CheckPackageManagerAgreement(standalone).Status);
        File.Delete(Path.Combine(frontend, "pnpm-lock.yaml"));
        workspace.Write("Standalone/package-lock.json", "{}");
        DoctorCheck own = DoctorWorkspaceChecks.CheckPackageManagerAgreement(standalone);
        Equal(DoctorStatus.Pass, own.Status);
        Contains(own.Message, "The frontend uses npm");
    }

    [Test, DisplayName("doctor frontend-dependencies checks that each package the page depends on is built or buildable")]
    public void DoctorChecksFrontendDependencies()
    {
        using var workspace = new TestWorkspace();
        string library = Path.Combine(workspace.Root, "shared-ui");
        string tokens = Path.Combine(workspace.Root, "tokens");
        FrontendDependency compiled = new(library, "@acme/shared-ui", "compiled", Path.Combine(workspace.Root, "SharedUi", "SharedUi.csproj"),
            "SharedUi", true, "bun run --bun build", string.Empty) { Exports = ["./dist/index.js"] };
        FrontendDependency source = new(tokens, "@acme/tokens", "source", null, null, true, string.Empty, string.Empty) { Exports = ["./src/index.ts"] };
        DoctorCheck Inspect(params FrontendDependency[] dependencies) =>
            DoctorWorkspaceChecks.CheckDependencies(new DoctorFrontendGraph(dependencies, true, workspace.Root, [], null));

        Equal(DoctorStatus.Pass, Inspect().Status);
        Contains(Inspect().Message, "depends on no library or workspace frontend package");

        // Not built yet: the build builds it, so this warns.
        workspace.Write("tokens/src/index.ts", "export {};");
        DoctorCheck unbuilt = Inspect(compiled, source);
        Equal(DoctorStatus.Warning, unbuilt.Status);
        Contains(unbuilt.Message, "@acme/shared-ui (the frontend package of SharedUi) is not built yet: shared-ui/dist/index.js is missing");
        Contains(unbuilt.Remediation ?? string.Empty, "'bun run --bun build'");

        workspace.Write("shared-ui/dist/index.js", "export {};");
        DoctorCheck built = Inspect(compiled, source);
        Equal(DoctorStatus.Pass, built.Status);
        Contains(built.Message, "@acme/shared-ui (built), @acme/tokens (source)");

        // The package build is off in its project, and nothing else builds it.
        File.Delete(Path.Combine(library, "dist", "index.js"));
        DoctorCheck off = Inspect(compiled with { BuildEnabled = false, BuildCommand = string.Empty });
        Equal(DoctorStatus.Failure, off.Status);
        Contains(off.Message, "its package build is off in SharedUi");
        Contains(off.Remediation ?? string.Empty, "Remove RunicApplicationFrontendBuildEnabled=false from SharedUi");

        // A source package whose exports name files that nothing writes needs a build script.
        DoctorCheck missing = Inspect(source with { Exports = ["./dist/index.js"] });
        Equal(DoctorStatus.Failure, missing.Status);
        Contains(missing.Message, "@acme/tokens (a workspace package that no .NET project owns) exports tokens/dist/index.js, which does not exist");
        Contains(missing.Remediation ?? string.Empty, "Add a 'build' script");

        // The query failed: not checked.
        DoctorCheck failed = DoctorWorkspaceChecks.CheckDependencies(new DoctorFrontendGraph([], null, null, [], "MSB4057: The target does not exist."));
        Equal(DoctorStatus.Warning, failed.Status);
        Contains(failed.Remediation ?? string.Empty, "dotnet restore");
    }

    [Test, DisplayName("doctor reads the frontend graph from the dependency query and its graph file")]
    public void DoctorReadsTheFrontendGraph()
    {
        string frontend = Path.GetFullPath("/repo/App/Frontend");
        string graph = """
            { "schema": "rax.frontend-graph/1", "workspaceRoot": "/repo", "member": true,
              "application": { "directory": "/repo/App/Frontend" },
              "members": [ { "directory": "/repo/App/Frontend" }, { "directory": "/repo/shared-ui" } ],
              "packages": [ { "directory": "/repo/shared-ui", "package": "@acme/shared-ui", "kind": "compiled", "exports": ["./dist/index.js"], "outputs": ["./dist"] } ] }
            """;
        DoctorFrontendGraph read = DoctorFrontendGraph.Create(0, string.Empty, DependenciesResult, graph, frontend);
        Equal(null, read.Error);
        Equal(true, read.Member);
        Equal(2, read.Members.Count);
        Equal(2, read.Dependencies.Count);
        SequenceEqual(["./dist/index.js"], read.Dependencies[0].Exports);

        // RUNICBRIDGE087 fails the query after the resolver wrote the graph, which still says why.
        string notMember = graph.Replace("\"member\": true", "\"member\": false", StringComparison.Ordinal);
        DoctorFrontendGraph rejected = DoctorFrontendGraph.Create(1,
            "/repo/App/App.csproj : error RUNICBRIDGE087: The frontend /repo/App/Frontend is below the bun workspace at /repo, but … [/repo/App/App.csproj]\n",
            string.Empty, notMember, frontend);
        Equal("RUNICBRIDGE087", rejected.ErrorCode);
        Equal(false, rejected.Member);
        Equal(0, rejected.Dependencies.Count);
        DoesNotContain(rejected.Error!, "[/repo/App/App.csproj]");

        // A target that does not exist (an unrestored package consumer): the graph file may be an older build's.
        DoctorFrontendGraph unrestored = DoctorFrontendGraph.Create(1,
            "MSBUILD : error MSB4057: The target \"RunicViewsGetFrontendDependencies\" does not exist in the project.\n", string.Empty, graph, frontend);
        Equal("MSB4057", unrestored.ErrorCode);
        Equal(null, unrestored.Member);

        // Another frontend's graph says nothing about this one.
        Equal(null, DoctorFrontendGraph.Create(0, string.Empty, DependenciesResult, graph, Path.GetFullPath("/repo/Other")).Member);
    }

    private static readonly string[] DistIndexExport = ["./dist/index.js"];
    private static readonly string[] DistOutput = ["./dist"];

    [Test, DisplayName("before the first restore doctor reads the graph that the resolver prints")]
    public void DoctorReadsTheResolverGraph()
    {
        using var workspace = new TestWorkspace();
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Root));
        string frontend = Path.Combine(root, "App", "Frontend");
        string library = Path.Combine(root, "shared-ui");
        string graph = JsonSerializer.Serialize(new
        {
            schema = "rax.frontend-graph/1",
            workspaceRoot = root,
            member = true,
            application = new { directory = frontend },
            members = new[] { new { directory = frontend }, new { directory = library } },
            packages = new[] { new { directory = library, package = "@acme/shared-ui", kind = "compiled", build = "bun run --bun build",
                watch = (string?)null, exports = DistIndexExport, outputs = DistOutput } },
        });
        DoctorFrontendGraph read = DoctorFrontendGraph.FromGraph(graph, frontend)!;
        Equal(true, read.Member);
        Equal(null, read.Error);
        False(read.OwnersKnown, "The resolver does not know the .NET projects that own the packages.");
        FrontendDependency dependency = read.Dependencies.Single();
        Equal("compiled", dependency.Kind);
        Equal("bun run --bun build", dependency.BuildCommand);
        True(dependency.IsBuilt, "A compiled package with a build command is built by the build.");
        // Without an owner, the message names the package alone.
        DoctorCheck check = DoctorWorkspaceChecks.CheckDependencies(read);
        Equal(DoctorStatus.Warning, check.Status);
        Contains(check.Message, "@acme/shared-ui is not built yet: shared-ui/dist/index.js is missing");
        Equal(null, DoctorFrontendGraph.FromGraph("not json", frontend));
    }

    [Test, DisplayName("doctor advises EnableWindowsTargeting for a Windows target framework on Linux and macOS")]
    public void DoctorAdvisesWindowsTargeting()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateDoctorProject(workspace, null, out CompatibilitySetAuthority authority) with
        {
            TargetFramework = "net10.0-windows",
            TargetPlatformIdentifier = "Windows",
        };
        DoctorCheck? Inspect(DoctorProjectConfiguration candidate, string os)
        {
            var checks = new List<DoctorCheck>();
            DoctorWorkspaceChecks.CheckWindowsTargeting(checks, candidate, new FakeDoctorRuntime(authority.Toolchain) { Platform = new(os, "x64", false, "1") });
            return checks.SingleOrDefault();
        }
        DoctorCheck linux = Inspect(project, "linux")!;
        Equal(DoctorStatus.Failure, linux.Status);
        Contains(linux.Message, "net10.0-windows is a Windows target framework, which the .NET SDK builds on Linux only with EnableWindowsTargeting=true");
        Contains(linux.Message, "NETSDK1100");
        Contains(linux.Remediation ?? string.Empty, "<EnableWindowsTargeting Condition=\"'$(EnableWindowsTargeting)' == '' and !$([MSBuild]::IsOSPlatform('Windows'))\">true</EnableWindowsTargeting>");
        Contains(linux.Remediation ?? string.Empty, "Directory.Build.props");
        DoctorCheck macOS = Inspect(project with { EnableWindowsTargeting = true }, "osx")!;
        Equal(DoctorStatus.Pass, macOS.Status);
        Contains(macOS.Message, "runs only on Windows");
        Equal(null, Inspect(project, "win"));
        Equal(null, Inspect(project with { TargetFramework = "net10.0", TargetPlatformIdentifier = string.Empty }, "linux"));
        // InspectAsync reports it with the other checks.
        Equal(DoctorStatus.Failure, Check(InspectDoctor(project, new FakeDoctorRuntime(authority.Toolchain)), "windows-targeting").Status);
    }
}
