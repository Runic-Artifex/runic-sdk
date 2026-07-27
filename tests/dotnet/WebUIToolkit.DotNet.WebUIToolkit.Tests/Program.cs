using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DotNet.WebUIToolkit;

namespace WebUIToolkit.DotNet.WebUIToolkit.Tests;

internal static class Program
{
    private const string NativeLibraryEnvironmentVariable =
        "CSWEB" + "UI_NATIVE_LIBRARY";

    public static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("dev options preserve application arguments", DevOptionsPreserveApplicationArguments),
            ("application help remains an application argument", ApplicationHelpRemainsApplicationArgument),
            ("dev options reject unknown switches", DevOptionsRejectUnknownSwitches),
            ("doctor options select a project", DoctorOptionsSelectProject),
            ("project discovery accepts a directory", ProjectDiscoveryAcceptsDirectory),
            ("project discovery rejects ambiguity", ProjectDiscoveryRejectsAmbiguity),
            ("commands keep arguments shell-free", CommandsKeepArgumentsShellFree),
            ("Vite server arguments are explicit and loopback-only", ViteArgumentsAreExplicit),
            ("Vite startup skips the production frontend build", ViteStartupSkipsProductionBuild),
            ("Vite bridge forwards cwhtml diagnostics through the native overlay", ViteBridgeForwardsDiagnostics),
            ("cwhtml reload comparison separates renderer edits from shape edits", CwhtmlReloadComparisonIsSafe),
            ("asset mirroring updates its owned graph", AssetMirroringUpdatesOwnedGraph),
            ("phase timings are concise and stable", PhaseTimingsAreConcise),
            ("doctor supports a healthy Node-free project", DoctorSupportsNodeFreeProject),
            ("doctor verifies a complete Node contract toolchain", DoctorVerifiesNodeContracts),
            ("doctor reports actionable frontend failures", DoctorReportsFrontendFailures),
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
        DevOptions options = DevOptions.Parse(
            ["dev", "--project", "App.csproj", "--no-restore", "--", "--advanced", "two words"]);
        Equal("App.csproj", options.Project);
        False(options.Restore, "The no-restore option was ignored.");
        if (!options.WatchHost)
        {
            throw new InvalidOperationException("The managed host watcher was disabled by default.");
        }
        SequenceEqual(["--advanced", "two words"], options.ApplicationArguments);

        DevOptions once = DevOptions.Parse(["dev", "--no-dotnet-watch"]);
        if (once.WatchHost)
        {
            throw new InvalidOperationException("--no-dotnet-watch was ignored.");
        }
    }

    private static void DevOptionsRejectUnknownSwitches()
    {
        Throws<DevUsageException>(() => DevOptions.Parse(["dev", "--wat"]));
    }

    private static void DoctorOptionsSelectProject()
    {
        DoctorOptions options = DoctorOptions.Parse(
            ["doctor", "--project", "App.csproj", "--configuration", "Release"]);
        Equal("App.csproj", options.Project);
        Equal("Release", options.Configuration);
        if (!DoctorOptions.RequestsHelp(["doctor", "--help"]))
        {
            throw new InvalidOperationException("Doctor help was not recognized.");
        }

        Throws<DevUsageException>(() => DoctorOptions.Parse(["doctor", "--wat"]));
    }

    private static void ApplicationHelpRemainsApplicationArgument()
    {
        False(
            DevOptions.RequestsHelp(["dev", "--", "--help"]),
            "Application help was consumed by the development tool.");
        DevOptions options = DevOptions.Parse(["dev", "--", "--help"]);
        SequenceEqual(["--help"], options.ApplicationArguments);
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

    private static void ViteArgumentsAreExplicit()
    {
        var configuration = new DevProjectConfiguration(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            NodeEnabled: true,
            CwhtmlEnabled: false,
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            ContractSource: "",
            ContractCSharpOutput: "",
            ContractTypeScriptOutput: "",
            ContractTool: "",
            FrontendWatchTarget: "WebUIToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            CwhtmlDiagnosticsPath: "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json",
            CwhtmlHotReloadPath: "/repo/obj/Debug/net10.0/cwhtml/hot-reload.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        IReadOnlyList<string> arguments = ViteDevelopmentServer.CreateArguments(
            configuration,
            43123,
            "/tmp/webuitoolkit/vite.config.mjs");
        SequenceEqual(
            [
                "run",
                "dev",
                "--workspace",
                "@example/app",
                "--",
                "--config",
                "/tmp/webuitoolkit/vite.config.mjs",
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
            CwhtmlEnabled: true,
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            ContractSource: "",
            ContractCSharpOutput: "",
            ContractTypeScriptOutput: "",
            ContractTool: "",
            FrontendWatchTarget: "WebUIToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            CwhtmlDiagnosticsPath: "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json",
            CwhtmlHotReloadPath: "/repo/obj/Debug/net10.0/cwhtml/hot-reload.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        DevOptions options = DevOptions.Parse(["dev"]);

        IReadOnlyList<string> arguments =
            DevApplication.CreateBuildArguments(configuration, options);

        if (!arguments.Contains(
                "-property:WebUIToolkitFrontendBuild=false",
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial Vite development build still enables production assets.");
        }
    }

    private static void ViteBridgeForwardsDiagnostics()
    {
        var configuration = new DevProjectConfiguration(
            ProjectPath: "/repo/App.csproj",
            ProjectDirectory: "/repo",
            NodeEnabled: true,
            CwhtmlEnabled: true,
            WorkspaceRoot: "/repo",
            Workspace: "@example/app",
            FrontendPackageDirectory: "/repo/frontend",
            FrontendOutputDirectory: "/repo/frontend/dist",
            FrontendWebRoot: "www",
            ContractSource: "",
            ContractCSharpOutput: "",
            ContractTypeScriptOutput: "",
            ContractTool: "",
            FrontendWatchTarget: "WebUIToolkitFrontendWatchAssets",
            ViteDevServerEnabled: true,
            ViteDevServerEntry: "/src/main.js",
            ViteConfigurationPath: "/repo/frontend/vite.config.mjs",
            CwhtmlDiagnosticsPath: "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json",
            CwhtmlHotReloadPath: "/repo/obj/Debug/net10.0/cwhtml/hot-reload.json",
            TargetDirectory: "/repo/bin/Debug/net10.0");
        string source = ViteConfigurationBridge.CreateSource(configuration);
        Contains(source, "webuitoolkit.cwhtml.diagnostics/1.0");
        Contains(source, "webuitoolkit:cwhtml-diagnostics");
        Contains(source, "server.ws.send({ type: \"error\"");
        Contains(source, "document.querySelector(\"vite-error-overlay\")?.remove()");
        Contains(source, "/repo/obj/Debug/net10.0/cwhtml/diagnostics.json");
        Contains(source, "webuitoolkit.cwhtml.hot-reload/1.0");
        Contains(source, "webuitoolkit:cwhtml-fragments");
        Contains(source, "/_webui/htmx/dev-refresh/");
        Contains(source, "/repo/frontend/src/main.js");
    }

    private static void CwhtmlReloadComparisonIsSafe()
    {
        byte[] baseline = ReloadSnapshot("renderer-one", "shape-one", canRefresh: true);
        byte[] rendererEdit = ReloadSnapshot("renderer-two", "shape-one", canRefresh: true);
        byte[] shapeEdit = ReloadSnapshot("renderer-three", "shape-two", canRefresh: true);
        byte[] nonRefreshableEdit =
            ReloadSnapshot("renderer-two", "shape-one", canRefresh: false);

        ReloadDecision compatible = CwhtmlHotReloadCoordinator.Compare(baseline, rendererEdit);
        Equal(ReloadKind.Refresh, compatible.Kind);
        SequenceEqual(["todo_fragment"], compatible.AffectedFragments);
        Equal(
            ReloadKind.Restart,
            CwhtmlHotReloadCoordinator.Compare(rendererEdit, shapeEdit).Kind);
        Equal(
            ReloadKind.Restart,
            CwhtmlHotReloadCoordinator.Compare(rendererEdit, nonRefreshableEdit).Kind);
        Equal(
            ReloadKind.None,
            CwhtmlHotReloadCoordinator.Compare(rendererEdit, rendererEdit).Kind);
    }

    private static byte[] ReloadSnapshot(
        string renderer,
        string shape,
        bool canRefresh) =>
        System.Text.Encoding.UTF8.GetBytes(
            $$"""
            {"contract":"webuitoolkit.cwhtml.hot-reload/1.0","templates":[{"logicalPath":"Views/TodoApp.cwhtml","rendererSha256":"{{renderer}}","compatibilitySha256":"{{shape}}","canRefreshFragments":{{canRefresh.ToString().ToLowerInvariant()}},"affectedFragments":["todo_fragment"]}]}
            """);

    private static void AssetMirroringUpdatesOwnedGraph()
    {
        using var workspace = new TestWorkspace();
        string source = Directory.CreateDirectory(Path.Combine(workspace.Root, "source")).FullName;
        string destination = Directory.CreateDirectory(Path.Combine(workspace.Root, "destination")).FullName;
        Write(Path.Combine(source, "assets", "app.js"), "one");
        Write(Path.Combine(source, "simple", "index.html"), "simple");
        Write(Path.Combine(destination, "vendor", "bootstrap.css"), "vendor");
        var mirror = new AssetMirror(source, destination);
        Equal(2, mirror.Synchronize());

        Write(Path.Combine(source, "assets", "app.js"), "two");
        File.Delete(Path.Combine(source, "simple", "index.html"));
        Write(Path.Combine(source, "advanced", "index.html"), "advanced");
        Equal(3, mirror.Synchronize());

        Equal("two", File.ReadAllText(Path.Combine(destination, "assets", "app.js")));
        False(
            File.Exists(Path.Combine(destination, "simple", "index.html")),
            "A stale owned asset was retained.");
        Equal("advanced", File.ReadAllText(Path.Combine(destination, "advanced", "index.html")));
        Equal("vendor", File.ReadAllText(Path.Combine(destination, "vendor", "bootstrap.css")));
    }

    private static void PhaseTimingsAreConcise()
    {
        Equal("1 ms", PhaseTimer.Format(TimeSpan.Zero));
        Equal("12 ms", PhaseTimer.Format(TimeSpan.FromMilliseconds(12)));
        Equal("1.25 s", PhaseTimer.Format(TimeSpan.FromMilliseconds(1250)));
    }

    private static void DoctorSupportsNodeFreeProject()
    {
        using var workspace = new TestWorkspace();
        string native = workspace.Write("native/libwebui-2.so", "native");
        string browser = workspace.Write("bin/chromium", "browser");
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment(NativeLibraryEnvironmentVariable, native)
            .WithEnvironment("WEBUI_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(
            CreateDoctorProject(
                workspace,
                nodeEnabled: false,
                cwhtmlEnabled: true),
            runtime);

        if (!report.IsHealthy)
        {
            throw new InvalidOperationException(
                "A complete Node-free project was reported unhealthy.");
        }

        DoctorCheck node = report.Checks.Single(check => check.Name == "node");
        Equal(DoctorStatus.Pass, node.Status);
        Contains(node.Message, "not required");
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
        workspace.Write("package-lock.json", "{}");
        string native = workspace.Write("native/libwebui-2.so", "native");
        string browser = workspace.Write("bin/chromium", "browser");
        string source = workspace.Write("contract.json", "{}");
        string csharp = workspace.Write("generated/Contract.g.cs", "// generated");
        string typescript = workspace.Write("generated/contract.g.ts", "// generated");
        string tool = workspace.Write("tools/generate.mjs", "// generator");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: true,
            cwhtmlEnabled: false) with
        {
            ContractSource = source,
            ContractCSharpOutput = csharp,
            ContractTypeScriptOutput = typescript,
            ContractTool = tool,
        };
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment(NativeLibraryEnvironmentVariable, native)
            .WithEnvironment("WEBUI_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithExecutable("node", "/tools/node")
            .WithExecutable("npm", "/tools/npm")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult("/tools/node", "--version", 0, "v24.18.0")
            .WithResult(browser, "--version", 0, "Chromium 150")
            .WithResult("/tools/node", "--verify", 0, string.Empty);

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
                call.Executable == "/tools/node"
                && call.Arguments.Contains("--verify", StringComparer.Ordinal)))
        {
            throw new InvalidOperationException("Doctor did not execute contract verification.");
        }
    }

    private static void DoctorReportsFrontendFailures()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("package.json", """{"packageManager":"npm@11.16.0"}""");
        string native = workspace.Write("native/libwebui-2.so", "native");
        string browser = workspace.Write("bin/chromium", "browser");
        DoctorProjectConfiguration project = CreateDoctorProject(
            workspace,
            nodeEnabled: true,
            cwhtmlEnabled: false) with
        {
            ViteDevServerEnabled = true,
            ViteDevServerEntry = "/src/main.ts",
            ViteConfigurationPath = Path.Combine(workspace.Root, "vite.config.mjs"),
        };
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment(NativeLibraryEnvironmentVariable, native)
            .WithEnvironment("WEBUI_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.302")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(project, runtime);
        False(report.IsHealthy, "Missing Node frontend prerequisites were not failures.");
        foreach (string checkName in
                 new[] { "node", "package-manager", "lock-file", "vite-config", "vite-entry" })
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
        bool cwhtmlEnabled) =>
        new(
            ProjectPath: workspace.Write("App.csproj", "<Project />"),
            ProjectDirectory: workspace.Root,
            TargetFramework: "net10.0",
            FrontendEnabled: true,
            NodeEnabled: nodeEnabled,
            CwhtmlEnabled: cwhtmlEnabled,
            WorkspaceRoot: workspace.Root,
            Workspace: nodeEnabled ? "@example/app" : string.Empty,
            FrontendPackageDirectory: workspace.Root,
            ContractSource: string.Empty,
            ContractCSharpOutput: string.Empty,
            ContractTypeScriptOutput: string.Empty,
            ContractTool: string.Empty,
            ViteDevServerEnabled: false,
            ViteDevServerEntry: string.Empty,
            ViteConfigurationPath: string.Empty,
            ProjectAssetsFile: Path.Combine(workspace.Root, "obj", "project.assets.json"),
            RuntimeIdentifier: "linux-x64");

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
                "webuitoolkit-dev-tests",
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
