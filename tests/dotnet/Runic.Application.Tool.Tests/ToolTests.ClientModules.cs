using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// devtools-library-dx S6b: doctor checks that each specifier the page imports from a library or workspace package
// resolves from the frontend (client-modules), and that the Vite configuration dedupes @runic-artifex/views when a
// linked package imports it (views-dedupe).
internal sealed partial class ToolTests
{
    private const string SharedUiExports =
        """{"name":"@acme/shared-ui","scripts":{"build":"tsc"},"exports":{".":"./dist/index.js","./generated":"./src/generated/runic-clients.ts"}}""";

    /// <summary>The Bun workspace of <see cref="CreateWorkspaceDoctorProject"/>, with SharedUi's client module and its package.</summary>
    private static DoctorProjectConfiguration CreateClientModuleProject(TestWorkspace workspace)
    {
        DoctorProjectConfiguration project = CreateWorkspaceDoctorProject(workspace, out _);
        workspace.Write("shared-ui/package.json", SharedUiExports);
        string root = project.EffectiveWorkspaceRoot;
        FrontendDependency library = new(Path.Combine(root, "shared-ui"), "@acme/shared-ui", "compiled", Path.Combine(root, "SharedUi", "SharedUi.csproj"),
            "SharedUi", true, "bun run --bun build", string.Empty)
        {
            Specifier = "@acme/shared-ui/generated",
            Exports = ["./dist/index.js"],
        };
        return project with
        {
            ClientModules = [new DoctorClientModule("SharedUi", "@acme/shared-ui/generated")],
            FrontendGraph = new DoctorFrontendGraph([library], true, root, [project.FrontendPackageDirectory, library.Directory], null),
        };
    }

    /// <summary>Links <paramref name="link"/> to <paramref name="target"/> as a workspace install does; false where the OS refuses.</summary>
    private static bool Link(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Windows without Developer Mode cannot create a symbolic link.
            return false;
        }
    }

    [Test, DisplayName("doctor client-modules resolves each specifier the page imports from a library or workspace package")]
    public void DoctorChecksClientModules()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateClientModuleProject(workspace);
        string root = project.EffectiveWorkspaceRoot;
        string frontend = project.FrontendPackageDirectory;
        DoctorCheck Inspect(DoctorProjectConfiguration candidate) => DoctorModuleChecks.CheckClientModules(candidate)!;

        // Before the install there is no node_modules: the build installs it, so this warns.
        DoctorCheck uninstalled = Inspect(project);
        Equal(DoctorStatus.Warning, uninstalled.Status);
        Contains(uninstalled.Message, "@acme/shared-ui/generated (the client module of SharedUi) does not resolve yet: the frontend's dependencies are not installed");
        Contains(uninstalled.Remediation ?? string.Empty, "'bun install'");

        // Installed without the package: the frontend declares it, so the install is out of date.
        Directory.CreateDirectory(Path.Combine(root, "node_modules"));
        DoctorCheck notLinked = Inspect(project);
        Equal(DoctorStatus.Failure, notLinked.Status);
        Contains(notLinked.Message, "declares @acme/shared-ui, but no node_modules folder there or above has it");
        Contains(notLinked.Remediation ?? string.Empty, $"Run 'bun install' in {root}");
        // Not declared either: the fix adds it.
        workspace.Write("App/Frontend/package.json", """{"name":"app"}""");
        DoctorCheck undeclared = Inspect(project);
        Equal(DoctorStatus.Failure, undeclared.Status);
        Contains(undeclared.Remediation ?? string.Empty, "Add \"@acme/shared-ui\": \"workspace:*\" to the dependencies of " + Path.Combine(frontend, "package.json"));
        workspace.Write("App/Frontend/package.json", """{"name":"app","dependencies":{"@acme/shared-ui":"workspace:*"}}""");

        if (!Link(Path.Combine(root, "node_modules", "@acme", "shared-ui"), Path.Combine(root, "shared-ui"))) return;

        // Linked, but the generator has not written the clients yet; SharedUi's package build is on.
        DoctorCheck unbuilt = Inspect(project);
        Equal(DoctorStatus.Warning, unbuilt.Status);
        Contains(unbuilt.Message, "resolves to ./src/generated/runic-clients.ts, which @acme/shared-ui's build has not written yet");

        workspace.Write("shared-ui/src/generated/runic-clients.ts", "export {};");
        DoctorCheck resolved = Inspect(project);
        Equal(DoctorStatus.Pass, resolved.Status);
        Contains(resolved.Message, "The 2 specifier(s)");
        Contains(resolved.Message, "@acme/shared-ui/generated → shared-ui/src/generated/runic-clients.ts");
        Contains(resolved.Message, "@acme/shared-ui → shared-ui");

        // The package does not export the subpath.
        workspace.Write("shared-ui/package.json", """{"name":"@acme/shared-ui","exports":{".":"./dist/index.js"}}""");
        DoctorCheck unexported = Inspect(project);
        Equal(DoctorStatus.Failure, unexported.Status);
        Contains(unexported.Message, "exports no './generated' for the page");
        Contains(unexported.Remediation ?? string.Empty, "Add \"./generated\" to the exports of");
        workspace.Write("shared-ui/package.json", SharedUiExports);

        // A specifier that names a file outside any package.
        DoctorCheck relative = Inspect(project with { ClientModules = [new DoctorClientModule("SharedUi", "../shared-ui/src/generated/runic-clients")] });
        Equal(DoctorStatus.Failure, relative.Status);
        Contains(relative.Message, "is not a package specifier (RUNICBRIDGE082)");
    }

    [Test, DisplayName("doctor client-modules warns when the frontend links another copy of a workspace package")]
    public void DoctorClientModulesNamesAnotherCopy()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateClientModuleProject(workspace);
        string root = project.EffectiveWorkspaceRoot;
        // A published copy in node_modules rather than the workspace's package.
        workspace.Write("node_modules/@acme/shared-ui/package.json", SharedUiExports);
        workspace.Write("node_modules/@acme/shared-ui/src/generated/runic-clients.ts", "export {};");
        DoctorCheck other = DoctorModuleChecks.CheckClientModules(project)!;
        Equal(DoctorStatus.Warning, other.Status);
        Contains(other.Message, $"resolves to {Path.Combine(root, "node_modules", "@acme", "shared-ui")}, not to @acme/shared-ui at {Path.Combine(root, "shared-ui")}, which SharedUi builds");
        Contains(other.Remediation ?? string.Empty, "Declare \"@acme/shared-ui\": \"workspace:*\"");
    }

    [Test, DisplayName("doctor client-modules accepts a tsconfig paths alias that the Vite configuration matches")]
    public void DoctorClientModulesAcceptsPathsAliases()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateClientModuleProject(workspace);
        Directory.CreateDirectory(Path.Combine(workspace.Root, "node_modules"));
        workspace.Write("shared-ui/src/generated/runic-clients.ts", "export {};");
        workspace.Write("shared-ui/src/index.ts", "export {};");
        // As examples/shared-library-clients maps them: comments and trailing commas are allowed in tsconfig.
        workspace.Write("App/Frontend/tsconfig.json", """
            {
              // The shared package, resolved from its folder.
              "compilerOptions": {
                "paths": {
                  "@acme/shared-ui/generated": ["../../shared-ui/src/generated/runic-clients.ts"],
                  "@acme/shared-ui": ["../../shared-ui/src/index"],
                },
              },
            }
            """);
        DoctorCheck noAlias = DoctorModuleChecks.CheckClientModules(project)!;
        Equal(DoctorStatus.Warning, noAlias.Status);
        Contains(noAlias.Message, "resolves through the paths of " + Path.Combine(project.FrontendPackageDirectory, "tsconfig.json"));
        Contains(noAlias.Message, "has no alias for @acme/shared-ui, so Vite would not resolve it");

        workspace.Write("App/Frontend/vite.config.ts", """
            export default { resolve: { alias: [{ find: /^@acme\/shared-ui\/generated$/, replacement: "x" }] } };
            """);
        DoctorCheck aliased = DoctorModuleChecks.CheckClientModules(project)!;
        Equal(DoctorStatus.Pass, aliased.Status);
        Contains(aliased.Message, "@acme/shared-ui/generated → shared-ui/src/generated/runic-clients.ts (tsconfig paths and a Vite alias)");
        Contains(aliased.Message, "@acme/shared-ui → shared-ui/src/index.ts");

        // A paths entry whose file does not exist.
        File.Delete(Path.Combine(workspace.Root, "shared-ui", "src", "generated", "runic-clients.ts"));
        DoctorCheck missing = DoctorModuleChecks.CheckClientModules(project)!;
        Equal(DoctorStatus.Failure, missing.Status);
        Contains(missing.Message, "whose target ../../shared-ui/src/generated/runic-clients.ts does not exist");

        // A page that imports no library or workspace package gets no check.
        Equal(null, DoctorModuleChecks.CheckClientModules(project with
        {
            ClientModules = [],
            FrontendGraph = new DoctorFrontendGraph([], true, project.EffectiveWorkspaceRoot, [], null),
        }));
    }

    [Test, DisplayName("doctor resolves package exports with Vite's conditions and patterns")]
    public void DoctorResolvesPackageExports()
    {
        (bool, string?) Resolve(string exports, string subpath)
        {
            using JsonDocument document = JsonDocument.Parse(exports);
            return DoctorModuleChecks.ResolveExport(document.RootElement, subpath);
        }
        Equal((true, "./index.js"), Resolve("\"./index.js\"", "."));
        Equal((false, null), Resolve("\"./index.js\"", "./generated"));
        Equal((true, "./dist/index.js"), Resolve("""{"types":"./dist/index.d.ts","import":"./dist/index.js"}""", "."));
        Equal((true, "./src/generated/runic-clients.ts"),
            Resolve("""{".":"./dist/index.js","./generated":{"types":"./g.d.ts","development":"./src/generated/runic-clients.ts","default":"./dist/g.js"}}""", "./generated"));
        Equal((true, "./src/features/a.ts"), Resolve("""{"./*":"./src/*.ts","./features/*":"./src/features/*.ts"}""", "./features/a"));
        Equal((true, null), Resolve("""{"./internal/*":null,"./*":"./src/*.ts"}""", "./internal/x"));
        Equal((true, "./b.js"), Resolve("""{".":[{"node":"./a.cjs"},"./b.js"]}""", "."));

        Equal(("@acme/shared-ui", "./generated"), DoctorModuleChecks.SplitSpecifier("@acme/shared-ui/generated"));
        Equal(("lodash", "."), DoctorModuleChecks.SplitSpecifier("lodash"));
        Equal(null, DoctorModuleChecks.SplitSpecifier("../shared-ui"));
        Equal(null, DoctorModuleChecks.SplitSpecifier("@acme"));

        using JsonDocument evaluation = JsonDocument.Parse("""
            {"Items":{"RunicApplicationFrontendClientModule":[{"Identity":"SharedUi","Specifier":"@acme/shared-ui/generated"}]}}
            """);
        DoctorClientModule module = DoctorProjectConfiguration.ReadClientModules(evaluation.RootElement).Single();
        Equal(new DoctorClientModule("SharedUi", "@acme/shared-ui/generated"), module);
    }

    [Test, DisplayName("doctor views-dedupe asks for resolve.dedupe of @runic-artifex/views when a linked package imports it")]
    public void DoctorChecksViewsDedupe()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateClientModuleProject(workspace);
        string root = project.EffectiveWorkspaceRoot;
        string configuration = project.ViteConfigurationPath;

        // shared-ui does not import the runtime: no check.
        Equal(null, DoctorModuleChecks.CheckViewsDedupe(project));

        workspace.Write("shared-ui/package.json",
            """{"name":"@acme/shared-ui","peerDependencies":{"@runic-artifex/views":"*"},"exports":{".":"./dist/index.js"}}""");
        DoctorCheck hint = DoctorModuleChecks.CheckViewsDedupe(project)!;
        Equal(DoctorStatus.Warning, hint.Status);
        Contains(hint.Message, $"The linked package(s) @acme/shared-ui import @runic-artifex/views, which Vite resolves from each package's own folder, and {configuration} does not dedupe it");
        Contains(hint.Remediation ?? string.Empty, "Add resolve: { dedupe: [\"@runic-artifex/views\"] } to " + configuration);

        // The package has its own copy beside the frontend's.
        workspace.Write("node_modules/@runic-artifex/views/package.json", "{}");
        workspace.Write("shared-ui/node_modules/@runic-artifex/views/package.json", "{}");
        DoctorCheck twoCopies = DoctorModuleChecks.CheckViewsDedupe(project)!;
        Equal(DoctorStatus.Warning, twoCopies.Status);
        Contains(twoCopies.Message, $"The page would load two copies of @runic-artifex/views: the frontend resolves {Path.Combine(root, "node_modules", "@runic-artifex", "views")}");
        Contains(twoCopies.Message, $"@acme/shared-ui resolves {Path.Combine(root, "shared-ui", "node_modules", "@runic-artifex", "views")}");

        workspace.Write("App/Frontend/vite.config.ts", """
            import { defineConfig } from "vite";
            export default defineConfig({ resolve: { dedupe: ["react", "@runic-artifex/views"] } });
            """);
        DoctorCheck deduped = DoctorModuleChecks.CheckViewsDedupe(project)!;
        Equal(DoctorStatus.Pass, deduped.Status);
        Contains(deduped.Message, "dedupes @runic-artifex/views, so the page loads one copy for the linked package(s) @acme/shared-ui");

        // InspectAsync reports both checks with the workspace checks.
        DoctorReport report = InspectDoctor(project, BunRuntime(CompatibilitySetAuthority.Current));
        Equal(DoctorStatus.Pass, Check(report, "views-dedupe").Status);
        True(report.Checks.Any(static check => check.Name == "client-modules"), "doctor should report client-modules.");
    }
}
