using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;

namespace Runic.Application.Tool.Tests;

internal static class Program
{
    public static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("generated dev inputs preserve application arguments", DevOptionsPreserveApplicationArguments),
            ("generated doctor inputs select a project", DoctorOptionsSelectProject),
            ("project discovery accepts a directory", ProjectDiscoveryAcceptsDirectory),
            ("project discovery rejects ambiguity", ProjectDiscoveryRejectsAmbiguity),
            ("commands keep arguments shell-free", CommandsKeepArgumentsShellFree),
            ("package managers use frozen installs and portable scripts", PackageManagersUseFrozenPortableCommands),
            ("Vite server arguments are explicit and loopback-only", ViteArgumentsAreExplicit),
            ("Vite startup skips the production frontend build", ViteStartupSkipsProductionBuild),
            ("Angular server arguments use the supported development builder", AngularArgumentsAreExplicit),
            ("development bootstrap preserves private binding and remote assets", DevelopmentBootstrapIsNativeSafe),
            ("Application Bridge inspector stays bounded and source-aware", InspectorTerminalSinkIsSafe),
            ("compiler rendered-fragment snapshots stay bounded and private", RenderedFragmentSnapshotsAreSafe),
            ("compiler reload comparison separates renderer edits from shape edits", FrontendCompilerReloadComparisonIsSafe),
            ("phase timings are concise and stable", PhaseTimingsAreConcise),
            ("doctor supports a healthy Node-free project", DoctorSupportsNodeFreeProject),
            ("doctor verifies a complete Node contract toolchain", DoctorVerifiesNodeContracts),
            ("doctor supports Bun without a separate Node runtime", DoctorSupportsBunRuntime),
            ("doctor reports actionable frontend failures", DoctorReportsFrontendFailures),
            ("doctor rejects a skewed compatibility set", DoctorRejectsCompatibilitySkew),
            ("doctor rejects npm locks without exact portable integrity", DoctorRejectsNonPortableNpmLock),
            ("support envelope is explicit, deterministic, private, and removable", SupportEnvelopeIsPrivateAndDeterministic),
            ("migration edits exact XML identities and preserves prefixes", MigrationEditsExactXmlIdentities),
        ];

        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} development-tool tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void DevOptionsPreserveApplicationArguments()
    {
        var options = new DevOptions("App.csproj", "Debug", false, true, true, true, false, ["--advanced", "two words"]);
        Equal("App.csproj", options.Project);
        False(options.Restore, "The no-restore option was ignored.");
        if (!options.WatchHost)
        {
            throw new InvalidOperationException("The managed host watcher was disabled by default.");
        }
        SequenceEqual(["--advanced", "two words"], options.ApplicationArguments);

        var once = new DevOptions(null, "Debug", true, true, true, false, false, []);
        if (once.WatchHost)
        {
            throw new InvalidOperationException("--no-dotnet-watch was ignored.");
        }
    }

    private static void DoctorOptionsSelectProject()
    {
        var options = new DoctorOptions("App.csproj", "Release");
        Equal("App.csproj", options.Project);
        Equal("Release", options.Configuration);
    }

    private static void ProjectDiscoveryAcceptsDirectory()
    {
        using var workspace = new TestWorkspace();
        string expected = workspace.Write("Application.csproj", "<Project />");
        Equal(expected, ProjectDiscovery.Find(workspace.Root, workspace.Root));
    }

    private static void ProjectDiscoveryRejectsAmbiguity()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("One.csproj", "<Project />");
        workspace.Write("Two.csproj", "<Project />");
        Throws<DevUsageException>(() => ProjectDiscovery.Find(workspace.Root, null));
    }

    private static void CommandsKeepArgumentsShellFree()
    {
        ProcessStartInfo startInfo = CommandRunner.CreateStartInfo(
            "dotnet",
            Environment.CurrentDirectory,
            ["build", "a project.csproj", "-p:Value=$(not-a-shell)"]);
        Equal(3, startInfo.ArgumentList.Count);
        Equal("a project.csproj", startInfo.ArgumentList[1]);
        Equal("-p:Value=$(not-a-shell)", startInfo.ArgumentList[2]);
        False(startInfo.UseShellExecute, "Commands unexpectedly use a shell.");
    }

    private static void PackageManagersUseFrozenPortableCommands()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("npm/package.json", """{"packageManager":"npm@11.16.0"}""");
        workspace.Write("npm/package-lock.json", "{}");
        JavaScriptPackageManager npm = JavaScriptPackageManager.Resolve(
            Path.Combine(workspace.Root, "npm"),
            Path.Combine(workspace.Root, "npm"));
        SequenceEqual(["ci", "--ignore-scripts"], npm.InstallArguments());
        SequenceEqual(
            ["run", "dev", "--workspace", "@example/app", "--", "--host", "127.0.0.1"],
            npm.RunScriptArguments("dev", "@example/app", ["--host", "127.0.0.1"]));

        workspace.Write("pnpm/package.json", """{"packageManager":"pnpm@11.25.0"}""");
        workspace.Write("pnpm/pnpm-lock.yaml", "lockfileVersion: '9.0'");
        JavaScriptPackageManager pnpm = JavaScriptPackageManager.Resolve(
            Path.Combine(workspace.Root, "pnpm"),
            Path.Combine(workspace.Root, "pnpm"));
        SequenceEqual(
            ["install", "--frozen-lockfile", "--ignore-scripts"],
            pnpm.InstallArguments());
        SequenceEqual(
            ["--filter", "@example/app", "run", "dev", "--host", "127.0.0.1"],
            pnpm.RunScriptArguments("dev", "@example/app", ["--host", "127.0.0.1"]));

        workspace.Write("bun/package.json", """{"packageManager":"bun@1.4.0"}""");
        workspace.Write("bun/bun.lock", "{}");
        JavaScriptPackageManager bun = JavaScriptPackageManager.Resolve(
            Path.Combine(workspace.Root, "bun"),
            Path.Combine(workspace.Root, "bun"));
        SequenceEqual(
            ["install", "--frozen-lockfile", "--ignore-scripts"],
            bun.InstallArguments());
        SequenceEqual(
            ["run", "--filter", "@example/app", "dev", "--host", "127.0.0.1"],
            bun.RunScriptArguments("dev", "@example/app", ["--host", "127.0.0.1"]));
    }

    private static void MigrationEditsExactXmlIdentities()
    {
        using var workspace = new TestWorkspace();
        string project = workspace.Write("App.csproj", """
            <Project><ItemGroup>
              <PackageReference Include="RunicToolkit.Hosting.CsWebUi" Version="0.1.0" />
              <PackageReference Include="RunicToolkit.Hosting.CsWebUi.App"><Version>0.1.0</Version></PackageReference>
              <PackageReference Include="RunicToolkit.Hosting.CsWebUi.ApplicationBridge" />
              <PackageReference Include="RunicToolkit.Hosting.CsWebUi.ApplicationBridge.Client" />
              <PackageReference Include="Runic.Application.Bridge.Client" />
            </ItemGroup><PropertyGroup><RunicToolkitFrontendEnabled>true</RunicToolkitFrontendEnabled></PropertyGroup></Project>
            """);
        global::Runic.Application.Tool.MigrationResult dryRun = global::Runic.Application.Tool.MigrationApplication.Execute(project, apply: false, dryRun: true, check: false);
        if (!dryRun.HasChanges) throw new InvalidOperationException("Migration did not identify legacy XML.");
        string unchanged = File.ReadAllText(project);
        Contains(unchanged, "RunicToolkit.Hosting.CsWebUi\" Version=\"0.1.0");
        Contains(unchanged, "RunicToolkit.Hosting.CsWebUi.ApplicationBridge");
        Contains(unchanged, "Runic.Application.Bridge.Client");

        global::Runic.Application.Tool.MigrationResult applied = global::Runic.Application.Tool.MigrationApplication.Execute(project, apply: true, dryRun: false, check: false);
        if (!applied.HasChanges) throw new InvalidOperationException("Migration apply did not report changes.");
        string migrated = File.ReadAllText(project);
        Contains(migrated, "Runic.Application.Desktop");
        Contains(migrated, "Runic.Application.Desktop\" Version=\"0.2.0");
        Contains(migrated, "<Version>0.2.0</Version>");
        Contains(migrated, "RunicToolkit.Hosting.CsWebUi.ApplicationBridge.Client");
        Contains(migrated, "Runic.Application.Bridge.Client");
        DoesNotContain(migrated, "RunicToolkitFrontendEnabled");
        if (global::Runic.Application.Tool.MigrationApplication.Execute(project, apply: false, dryRun: false, check: true).HasChanges)
        {
            throw new InvalidOperationException("A migrated project still reported pending migration work.");
        }
        Throws<DevUsageException>(() => global::Runic.Application.Tool.MigrationApplication.Execute(project, apply: false, dryRun: false, check: false));
    }

    private static void ViteArgumentsAreExplicit()
    {
        var configuration = new DevProjectConfiguration(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            NodeEnabled: true,
            FrontendCompilerEnabled: false,
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            BridgeSource: "",
            BridgeIr: "",
            BridgeFacade: "",
            FrontendWatchTarget: "RunicToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            FrontendCompilerDiagnosticsPath: "/repo/obj/Debug/net10.0/frontend-compiler/diagnostics.json",
            FrontendCompilerHotReloadPath: "/repo/obj/Debug/net10.0/frontend-compiler/hot-reload.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        IReadOnlyList<string> arguments = ViteDevelopmentServer.CreateArguments(
            configuration,
            43123);
        SequenceEqual(
            [
                "run",
                "dev",
                "--workspace",
                "@example/app",
                "--",
                "--host",
                "127.0.0.1",
                "--port",
                "43123",
                "--strictPort",
            ],
            arguments);
    }

    private static void ViteStartupSkipsProductionBuild()
    {
        var configuration = new DevProjectConfiguration(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            NodeEnabled: true,
            FrontendCompilerEnabled: true,
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            BridgeSource: "",
            BridgeIr: "",
            BridgeFacade: "",
            FrontendWatchTarget: "RunicToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            FrontendCompilerDiagnosticsPath: "/repo/obj/Debug/net10.0/frontend-compiler/diagnostics.json",
            FrontendCompilerHotReloadPath: "/repo/obj/Debug/net10.0/frontend-compiler/hot-reload.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        var options = new DevOptions(null, "Debug", true, true, true, true, false, []);

        IReadOnlyList<string> arguments =
            DevApplication.CreateBuildArguments(configuration, options);

        if (!arguments.Contains(
                "-property:RunicToolkitFrontendBuild=false",
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial Vite development build still enables production assets.");
        }
    }

    private static void AngularArgumentsAreExplicit()
    {
        DevProjectConfiguration configuration = CreateDevelopmentServerConfiguration(
            "angular",
            "simple/index.html;advanced/index.html");
        SequenceEqual(
            [
                "run",
                "dev",
                "--workspace",
                "@example/app",
                "--",
                "--host",
                "127.0.0.1",
                "--port",
                "43124",
                "--hmr",
                "--live-reload",
            ],
            AngularDevelopmentServer.CreateArguments(configuration, 43124));

        var options = new DevOptions(null, "Debug", true, true, true, true, false, []);
        IReadOnlyList<string> build =
            DevApplication.CreateBuildArguments(configuration, options);
        if (!build.Contains(
                "-property:RunicToolkitFrontendBuild=false",
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Angular development startup still enables a production frontend build.");
        }
    }

    private static void DevelopmentBootstrapIsNativeSafe()
    {
        using var workspace = new TestWorkspace();
        string target = Directory.CreateDirectory(
            Path.Combine(workspace.Root, "bin")).FullName;
        DevProjectConfiguration configuration = CreateDevelopmentServerConfiguration(
            "vite",
            "simple/index.html;advanced/index.html",
            target);
        var origin = new Uri("http://127.0.0.1:43125/");
        var inspector = new Uri("http://127.0.0.1:43126/token/events");
        const string source =
            """
            <!doctype html>
            <html><head><base href="/"><script src="/webui.js"></script>
            <script type="module">import { refresh } from "/@react-refresh";</script></head>
            <body><main id="app"></main><script type="module" src="/src/main.ts"></script></body></html>
            """;

        foreach (string document in configuration.DevelopmentServerDocuments)
        {
            FrontendDevelopmentDocument.Write(
                configuration,
                origin,
                inspector,
                document,
                source);
        }

        string simple = File.ReadAllText(
            Path.Combine(target, "www", "simple", "index.html"));
        string advanced = File.ReadAllText(
            Path.Combine(target, "www", "advanced", "index.html"));
        Contains(simple, "<script src=\"/webui.js\"></script>");
        Contains(simple, "http://127.0.0.1:43125/src/main.ts");
        Contains(simple, "<base href=\"/\">");
        Contains(simple, "from \"http://127.0.0.1:43125/@react-refresh\"");
        Contains(simple, "__runicToolkitApplicationBridgeDevelopment");
        Contains(simple, "http://127.0.0.1:43126/token/events");
        Contains(simple, configuration.ProjectDirectory);
        Equal(simple, advanced);
    }

    private static void InspectorTerminalSinkIsSafe()
    {
        DevelopmentInspectorServer server =
            DevelopmentInspectorServer.Start("/repo");
        try
        {
            if (!server.TryFormat(
                    """
                    {
                      "sequence": 7,
                      "direction": "client",
                      "kind": "dispatch",
                      "commandTag": "IncrementCounter",
                      "handler": "Example.CounterBridgeHandler.IncrementCounterAsync",
                      "revision": "4",
                      "bytes": 128,
                      "payload": "must never reach the terminal",
                      "source": {
                        "file": "CounterBridgeHandler.cs",
                        "line": 12,
                        "column": 6
                      }
                    }
                    """,
                    out string? formatted))
            {
                throw new InvalidOperationException(
                    "A valid sanitized inspector event was rejected.");
            }

            Contains(formatted!, "[bridge] #7 client dispatch IncrementCounter");
            Contains(formatted!, "Example.CounterBridgeHandler.IncrementCounterAsync");
            Contains(formatted!, "/repo/CounterBridgeHandler.cs:12:6");
            DoesNotContain(formatted!, "must never reach the terminal");
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void RenderedFragmentSnapshotsAreSafe()
    {
        using var workspace = new TestWorkspace();
        DevelopmentInspectorServer server =
            DevelopmentInspectorServer.Start(workspace.Root);
        try
        {
            if (!server.TryWriteRenderedFragments(
                    """
                    {
                      "contract": "runic-toolkit.frontend-compiler.rendered-fragments/1.0",
                      "fragments": [
                        {
                          "handle": "todo_fragment",
                          "html": "<section id=\"todo_fragment\">ready</section>"
                        }
                      ]
                    }
                    """))
            {
                throw new InvalidOperationException(
                    "A valid rendered-fragment snapshot was rejected.");
            }

            string snapshot = File.ReadAllText(server.RenderedFragmentsSnapshotPath);
            Contains(snapshot, "\"handle\": \"todo_fragment\"");
            Contains(snapshot, "\\u003Csection");
            DoesNotContain(
                server.RenderedFragmentsEndpoint.AbsoluteUri,
                server.Endpoint.AbsoluteUri);
            False(
                server.TryWriteRenderedFragments(
                    """
                    {
                      "contract": "runic-toolkit.frontend-compiler.rendered-fragments/1.0",
                      "fragments": [{ "handle": "../escape", "html": "bad" }]
                    }
                    """),
                "An invalid rendered-fragment handle was accepted.");
        }
        finally
        {
            server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        False(
            File.Exists(server.RenderedFragmentsSnapshotPath),
            "The rendered-fragment snapshot survived the dev session.");
    }

    private static DevProjectConfiguration CreateDevelopmentServerConfiguration(
        string kind,
        string documents,
        string targetDirectory = "/repo/bin/Debug/net10.0") =>
        new(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            NodeEnabled: true,
            FrontendCompilerEnabled: false,
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            BridgeSource: "",
            BridgeIr: "",
            BridgeFacade: "",
            FrontendWatchTarget: "RunicToolkitFrontendWatchAssets",
            ViteDevServerEnabled: kind == "vite",
            ViteDevServerEntry: "/src/main.ts",
            ViteConfigurationPath: "",
            FrontendCompilerDiagnosticsPath: "",
            FrontendCompilerHotReloadPath: "",
            TargetDirectory: targetDirectory)
        {
            DevelopmentServerKind = kind,
            DevelopmentServerDocument = documents,
        };

    private static void FrontendCompilerReloadComparisonIsSafe()
    {
        byte[] baseline = ReloadSnapshot("renderer-one", "shape-one", canRefresh: true);
        byte[] rendererEdit = ReloadSnapshot("renderer-two", "shape-one", canRefresh: true);
        byte[] shapeEdit = ReloadSnapshot("renderer-three", "shape-two", canRefresh: true);
        byte[] nonRefreshableEdit =
            ReloadSnapshot("renderer-two", "shape-one", canRefresh: false);

        ReloadDecision compatible = FrontendCompilerHotReloadCoordinator.Compare(baseline, rendererEdit);
        Equal(ReloadKind.Refresh, compatible.Kind);
        SequenceEqual(["todo_fragment"], compatible.AffectedFragments);
        Equal(
            ReloadKind.Restart,
            FrontendCompilerHotReloadCoordinator.Compare(rendererEdit, shapeEdit).Kind);
        Equal(
            ReloadKind.Restart,
            FrontendCompilerHotReloadCoordinator.Compare(rendererEdit, nonRefreshableEdit).Kind);
        Equal(
            ReloadKind.None,
            FrontendCompilerHotReloadCoordinator.Compare(rendererEdit, rendererEdit).Kind);
    }

    private static byte[] ReloadSnapshot(
        string renderer,
        string shape,
        bool canRefresh) =>
        System.Text.Encoding.UTF8.GetBytes(
            $$"""
            {"contract":"runic-toolkit.frontend-compiler.hot-reload/1.0","templates":[{"logicalPath":"Views/TodoApp.frontend","rendererFingerprint":"{{renderer}}","compatibilityFingerprint":"{{shape}}","canRefreshFragments":{{canRefresh.ToString().ToLowerInvariant()}},"affectedFragments":["todo_fragment"]}]}
            """);

    private static void PhaseTimingsAreConcise()
    {
        Equal("1 ms", PhaseTimer.Format(TimeSpan.Zero));
        Equal("12 ms", PhaseTimer.Format(TimeSpan.FromMilliseconds(12)));
        Equal("1.25 s", PhaseTimer.Format(TimeSpan.FromMilliseconds(1250)));
    }

    private static void DoctorSupportsNodeFreeProject()
    {
        using var workspace = new TestWorkspace();
        string browser = workspace.Write("bin/chromium", "browser");
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(
            CreateDoctorProject(
                workspace,
                nodeEnabled: false,
                frontendCompilerEnabled: true),
            runtime);

        if (!report.IsHealthy)
        {
            throw new InvalidOperationException(
                "A complete Node-free project was reported unhealthy.");
        }

        DoctorCheck runtimeCheck = report.Checks.Single(check => check.Name == "javascript-runtime");
        Equal(DoctorStatus.Pass, runtimeCheck.Status);
        Contains(runtimeCheck.Message, "not required");
        Equal(
            DoctorStatus.Pass,
            report.Checks.Single(check => check.Name == "package-manager").Status);
        Equal(
            DoctorStatus.Pass,
            report.Checks.Single(check => check.Name == "vite").Status);
    }

    private static void DoctorVerifiesNodeContracts()
    {
        using var workspace = new TestWorkspace();
        workspace.Write(
            "package.json",
            """{"packageManager":"npm@11.16.0"}""");
        workspace.Write("package-lock.json", """{"lockfileVersion":3,"packages":{}}""");
        string browser = workspace.Write("bin/chromium", "browser");
        string source = workspace.Write("src/application.bridge.ts", "// contract");
        string ir = workspace.Write("Contract/bridge.ir.json", "{}");
        string facade = workspace.Write("src/application.bridge.generated.ts", "// generated");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: true,
            frontendCompilerEnabled: false) with
        {
            BridgeSource = source,
            BridgeIr = ir,
            BridgeFacade = facade,
        };
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithExecutable("node", "/tools/node")
            .WithExecutable("npm", "/tools/npm")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult("/tools/node", "--version", 0, "v24.18.0")
            .WithResult("/tools/npm", "--version", 0, "11.16.0")
            .WithResult(browser, "--version", 0, "Chromium 150")
            .WithResult("/tools/npm", "contract:check", 0, string.Empty);

        DoctorReport report = InspectDoctor(project, runtime);
        if (!report.IsHealthy)
        {
            throw new InvalidOperationException(
                "A complete generated-contract toolchain was reported unhealthy.");
        }

        Equal(
            DoctorStatus.Pass,
            report.Checks.Single(check => check.Name == "contract-verify").Status);
        if (!runtime.Calls.Any(call =>
                call.Executable == "/tools/npm"
                && call.Arguments.SequenceEqual(["run", "contract:check"], StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Doctor did not execute contract verification.");
        }
    }

    private static void DoctorSupportsBunRuntime()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("package.json", """{"packageManager":"bun@1.4.0"}""");
        workspace.Write("bun.lock", "{}");
        string browser = workspace.Write("bin/chromium", "browser");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: true,
            frontendCompilerEnabled: false);
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithExecutable("bun", "/tools/bun")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult("/tools/bun", "--version", 0, "1.4.0")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(project, runtime);
        if (!report.IsHealthy)
        {
            throw new InvalidOperationException("A complete Bun frontend toolchain was reported unhealthy.");
        }

        Equal(
            DoctorStatus.Pass,
            report.Checks.Single(check => check.Name == "javascript-runtime").Status);
        Equal(
            DoctorStatus.Pass,
            report.Checks.Single(check => check.Name == "package-manager").Status);
    }

    private static void DoctorReportsFrontendFailures()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("package.json", """{"packageManager":"npm@11.16.0"}""");
        string browser = workspace.Write("bin/chromium", "browser");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: true,
            frontendCompilerEnabled: false) with
        {
            ViteDevServerEnabled = true,
            ViteDevServerEntry = "/src/main.ts",
            ViteConfigurationPath = Path.Combine(workspace.Root, "vite.config.mjs"),
        };
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(project, runtime);
        False(report.IsHealthy, "Missing Node frontend prerequisites were not failures.");
        foreach (string checkName in
                 new[] { "javascript-runtime", "package-manager", "lock-file", "vite-config", "vite-entry" })
        {
            DoctorCheck check = report.Checks.Single(item => item.Name == checkName);
            Equal(DoctorStatus.Failure, check.Status);
            if (string.IsNullOrWhiteSpace(check.Remediation))
            {
                throw new InvalidOperationException(
                    $"Failure '{checkName}' did not include remediation.");
            }
        }
    }

    private static DoctorReport InspectDoctor(
        DoctorProjectConfiguration project,
        IDoctorRuntime runtime) =>
        DoctorChecks
            .InspectAsync(project, "dotnet", runtime, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static DoctorProjectConfiguration CreateDoctorProject(
        TestWorkspace workspace,
        bool nodeEnabled,
        bool frontendCompilerEnabled)
    {
        string assetsFile = workspace.Write(
            "obj/project.assets.json",
            """
            {"libraries":{"Runic.Application/1.0.0-preview.1":{"type":"package"},"Runic.Desktop/1.0.0-preview.1":{"type":"package"}}}
            """);
        return new(
            ProjectPath: workspace.Write("App.csproj", "<Project />"),
            ProjectDirectory: workspace.Root,
            TargetFramework: "net10.0",
            FrontendEnabled: true,
            NodeEnabled: nodeEnabled,
            FrontendCompilerEnabled: frontendCompilerEnabled,
            WorkspaceRoot: workspace.Root,
            Workspace: nodeEnabled ? "@example/app" : string.Empty,
            FrontendPackageDirectory: workspace.Root,
            BridgeSource: string.Empty,
            BridgeIr: string.Empty,
            BridgeFacade: string.Empty,
            ViteDevServerEnabled: false,
            ViteDevServerEntry: string.Empty,
            ViteConfigurationPath: string.Empty,
            ProjectAssetsFile: assetsFile,
            RuntimeIdentifier: "linux-x64");
    }

    private static void DoctorRejectsCompatibilitySkew()
    {
        using var workspace = new TestWorkspace();
        string browser = workspace.Write("bin/chromium", "browser");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: false,
            frontendCompilerEnabled: true);
        File.WriteAllText(
            project.ProjectAssetsFile,
            """
            {"libraries":{"Runic.Application/1.0.0-preview.2":{"type":"package"},"Runic.Desktop/1.0.0-preview.1":{"type":"package"}}}
            """);
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(project, runtime);
        DoctorCheck check = report.Checks.Single(item => item.Name == "compatibility-set");
        Equal(DoctorStatus.Failure, check.Status);
        Contains(check.Message, "Runic.Application 1.0.0-preview.2");
        Contains(check.Remediation ?? string.Empty, "isolated feed");
    }

    private static void DoctorRejectsNonPortableNpmLock()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("package.json", """{"packageManager":"npm@11.16.0"}""");
        workspace.Write(
            "package-lock.json",
            """
            {"lockfileVersion":3,"packages":{"node_modules/@runic-artifex/application-bridge":{"version":"1.0.0-preview.1","resolved":"https://registry.example.invalid/application-bridge.tgz"}}}
            """);
        string browser = workspace.Write("bin/chromium", "browser");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: true,
            frontendCompilerEnabled: false);
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithExecutable("node", "/tools/node")
            .WithExecutable("npm", "/tools/npm")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult("/tools/node", "--version", 0, "v24.18.0")
            .WithResult("/tools/npm", "--version", 0, "11.16.0")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(project, runtime);
        DoctorCheck check = report.Checks.Single(item => item.Name == "compatibility-set");
        Equal(DoctorStatus.Failure, check.Status);
        Contains(check.Message, "has no sha512 lock integrity");
        Contains(check.Message, "lock pins a registry host");
        Contains(check.Remediation ?? string.Empty, "isolated feed");
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void SupportEnvelopeIsPrivateAndDeterministic()
    {
        using var workspace = new TestWorkspace();
        string source = Path.Combine(workspace.Root, "editor-diagnostics.zip");
        using (ZipArchive archive = ZipFile.Open(source, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("diagnostics.json").Open()))
        {
            writer.Write(JsonSerializer.Serialize(new
            {
                schema = "runic.translations.editor-diagnostics/1", generatedAt = "2026-08-27T00:00:00Z",
                application = new { product = "Runic Translations Editor", version = "0.1.0", updateChannel = "preview", commit = "abc123", runtime = ".NET 10", runtimeIdentifier = "linux-x64", operatingSystem = "Linux", architecture = "X64" },
                workspace = new { catalogId = "editor", schemaVersion = 2, localeCount = 2, documentCount = 3, messageCount = 4, compilerSuccess = true, reviewStateAvailable = true, pendingTransaction = false, pendingTransactionPathCount = 0, diagnostics = new[] { new { id = "RTR0001", severity = "warning", count = 1 } }, },
            }));
        }
        string first = Path.Combine(workspace.Root, "first.json"), second = Path.Combine(workspace.Root, "second.json");
        SupportCommandResult preview = SupportApplication.ExecuteAsync(new SupportOptions("preview", source, null), CancellationToken.None).GetAwaiter().GetResult();
        Equal(0, preview.OutboundTransportAttempts); Contains(preview.ToHumanOutput(), "workspace-roots");
        SupportCommandResult one = SupportApplication.ExecuteAsync(new SupportOptions("collect", source, first), CancellationToken.None).GetAwaiter().GetResult();
        SupportCommandResult two = SupportApplication.ExecuteAsync(new SupportOptions("collect", source, second), CancellationToken.None).GetAwaiter().GetResult();
        Equal(one.Digest, two.Digest); Equal(File.ReadAllText(first), File.ReadAllText(second));
        SupportCommandResult removed = SupportApplication.ExecuteAsync(new SupportOptions("remove", null, first), CancellationToken.None).GetAwaiter().GetResult();
        Equal(one.Digest, removed.Digest); False(File.Exists(first), "Support removal left the envelope behind.");
        string hostile = Path.Combine(workspace.Root, "hostile.zip");
        using (ZipArchive archive = ZipFile.Open(hostile, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("diagnostics.json").Open())) writer.Write(File.ReadAllText(second).Replace("runic.support-envelope/1", "runic.translations.editor-diagnostics/1", StringComparison.Ordinal));
        Throws<SupportUsageException>(() => SupportApplication.ExecuteAsync(new SupportOptions("preview", hostile, null), CancellationToken.None).GetAwaiter().GetResult());
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }

    private static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Equal(expected[index]!, actual[index]!);
        }
    }

    private static void False(bool value, string message)
    {
        if (value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Expected text containing '{expected}'.");
        }
    }

    private static void DoesNotContain(string value, string unexpected)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected text not to contain '{unexpected}'.");
        }
    }

    private static void Throws<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "runic-toolkit-dev-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath);
            Program.Write(path, content);
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeDoctorRuntime : IDoctorRuntime
    {
        private readonly Dictionary<string, string?> _environment =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _executables =
            new(StringComparer.Ordinal);
        private readonly List<FakeCommand> _results = [];

        internal List<FakeCall> Calls { get; } = [];

        internal FakeDoctorRuntime WithEnvironment(string name, string? value)
        {
            _environment[name] = value;
            return this;
        }

        internal FakeDoctorRuntime WithExecutable(string name, string path)
        {
            _executables[name] = path;
            return this;
        }

        internal FakeDoctorRuntime WithResult(
            string executable,
            string distinguishingArgument,
            int exitCode,
            string standardOutput,
            string standardError = "")
        {
            _results.Add(
                new(
                    executable,
                    distinguishingArgument,
                    new(exitCode, standardOutput, standardError)));
            return this;
        }

        public string? GetEnvironmentVariable(string name) =>
            _environment.GetValueOrDefault(name);

        public string? FindExecutable(string name) =>
            _executables.GetValueOrDefault(name);

        public Task<CommandResult> RunAsync(
            string executable,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(executable, arguments.ToArray()));
            CommandResult result = _results
                .LastOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(candidate.Executable, executable)
                    && arguments.Contains(
                        candidate.DistinguishingArgument,
                        StringComparer.Ordinal))
                ?.Result
                ?? new CommandResult(0, string.Empty, string.Empty);
            return Task.FromResult(result);
        }

        private sealed record FakeCommand(
            string Executable,
            string DistinguishingArgument,
            CommandResult Result);
    }

    private sealed record FakeCall(string Executable, IReadOnlyList<string> Arguments);
}
