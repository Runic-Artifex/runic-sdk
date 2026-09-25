using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
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
            ("host selection is scoped across child processes", HostSelectionIsScoped),
            ("package managers use frozen installs and portable scripts", PackageManagersUseFrozenPortableCommands),
            ("Vite server arguments are explicit and loopback-only", ViteArgumentsAreExplicit),
            ("Vite readiness identifies failed probes and preserves caller cancellation", ViteReadinessFailuresAreActionable),
            ("Vite startup skips the production frontend build", ViteStartupSkipsProductionBuild),
            ("compiled-discovery owner is session-scoped and reaches every MSBuild child", DiscoveryBuildOwnerFlowsThroughDevelopmentCommands),
            ("compiled-discovery cleanup removes only the evaluated session owner", DiscoveryBuildOwnerCleanupIsNarrow),
            ("opt-in View Bridge handoff exposes paths without impersonating the host", ViewBridgeHandoffIsHostOwned),
            ("only opt-in View Bridge disables managed Hot Reload", ViewBridgeWatchRegeneratesCompiledContract),
            ("dotnet watch stays alive after a clean child exit", WatcherOutlivesCleanChildExit),
            ("Angular server arguments use the supported development builder", AngularArgumentsAreExplicit),
            ("development bootstrap preserves private binding and remote assets", DevelopmentBootstrapIsNativeSafe),
            ("Application Bridge inspector stays bounded and source-aware", InspectorTerminalSinkIsSafe),
            ("compiler rendered-fragment snapshots stay bounded and private", RenderedFragmentSnapshotsAreSafe),
            ("compiler reload comparison separates renderer edits from shape edits", FrontendCompilerReloadComparisonIsSafe),
            ("phase timings are concise and stable", PhaseTimingsAreConcise),
            ("size inventories account for all bytes and preserve file hashes", SizeInventoryAccountsForBytes),
            ("doctor supports a healthy Node-free project", DoctorSupportsNodeFreeProject),
            ("doctor verifies a complete Node contract toolchain", DoctorVerifiesNodeContracts),
            ("doctor supports Bun without a separate Node runtime", DoctorSupportsBunRuntime),
            ("doctor reports actionable frontend failures", DoctorReportsFrontendFailures),
            ("embedded compatibility describes the complete SDK", EmbeddedCompatibilityDescribesSdk),
            ("doctor rejects a skewed compatibility set", DoctorRejectsCompatibilitySkew),
            ("doctor rejects npm locks without exact portable integrity", DoctorRejectsNonPortableNpmLock),
            ("support envelope is explicit, deterministic, private, and removable", SupportEnvelopeIsPrivateAndDeterministic),
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

    private static void ViteReadinessFailuresAreActionable()
        => VerifyViteReadinessAsync().GetAwaiter().GetResult();

    private static async Task VerifyViteReadinessAsync()
    {
        var origin = new Uri("http://127.0.0.1:12345/");
        var running = new TaskCompletionSource<int>();
        foreach (bool clientReady in new[] { false, true })
        {
            using var client = new HttpClient(new ReadinessHandler(clientReady));
            try
            {
                await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts",
                    running.Task, client, TimeSpan.FromMilliseconds(200), CancellationToken.None);
                throw new InvalidOperationException("A non-responsive module was accepted as ready.");
            }
            catch (DevDevelopmentException error)
            {
                Equal("RAPPDEV1007", error.Code);
                Contains(error.Message, clientReady ? "application entry: no response" : "Vite client: no response");
                // URLs trigger the public command fault sanitizer's drive-path check.
                DoesNotContain(error.Message, origin.AbsoluteUri);
                Contains(error.Message, "dotnet runic doctor");
            }
        }

        using var readyClient = new HttpClient(new ReadinessHandler(true, true));
        await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts", running.Task,
            readyClient, TimeSpan.FromSeconds(1), CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts", running.Task,
                readyClient, TimeSpan.FromSeconds(1), cancellation.Token);
            throw new InvalidOperationException("Caller cancellation was ignored.");
        }
        catch (OperationCanceledException) { }

        try
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts", Task.FromResult(7),
                readyClient, TimeSpan.FromSeconds(1), CancellationToken.None);
            throw new InvalidOperationException("An exited server was accepted as ready.");
        }
        catch (DevDevelopmentException error) { Contains(error.Message, "code 7"); }
    }

    private sealed class ReadinessHandler(bool clientReady, bool entryReady = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!(request.RequestUri!.AbsolutePath == "/@vite/client" ? clientReady : entryReady))
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static void SizeInventoryAccountsForBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "runic-size-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "runtimes", "win-x64", "native"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Example"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(root, "libwebui-2.so"), [4, 5]);
            File.WriteAllBytes(Path.Combine(root, "Example.dbg"), [6]);
            File.WriteAllBytes(Path.Combine(root, "runtimes", "win-x64", "native", "WebView2Loader.dll"), [7]);
            var files = SizeApplication.Inventory(root, "Example", "linux-x64");
            Equal(4, files.Count);
            Equal(7L, files.Sum(file => file.Bytes));
            Equal("main-executable", files.Single(file => file.Path == "Example").Category);
            Equal("native-dependency", files.Single(file => file.Path == "libwebui-2.so").Category);
            Equal("debug-symbols", files.Single(file => file.Path == "Example.dbg").Category);
            Equal("other-rid", files.Single(file => file.Path.StartsWith("runtimes/", StringComparison.Ordinal)).Category);
            string previous = files.Single(file => file.Path == "Example").Sha256;
            File.WriteAllBytes(Path.Combine(root, "Example"), [3, 2, 1]);
            False(previous == SizeApplication.Inventory(root, "Example", "linux-x64").Single(file => file.Path == "Example").Sha256,
                "Same-length mutations must change the content hash.");
            Equal("frontend-assets", SizeApplication.Classify("www/index.html", "Example", "linux-x64"));
            Equal("runtime-metadata", SizeApplication.Classify("Example.runtimeconfig.json", "Example", "linux-x64"));
        }
        finally { Directory.Delete(root, recursive: true); }
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

    private static void HostSelectionIsScoped()
    {
        static string? ReadHost() => CommandRunner.CreateStartInfo("dotnet", "/tmp", []).Environment.TryGetValue("RunicHost", out var value) ? value : null;
        string? original = ReadHost();
        using (new HostSelectionScope("cswebui"))
        {
            var start = CommandRunner.CreateStartInfo("dotnet", "/tmp", []);
            Equal("cswebui", start.Environment["RunicHost"]);
            Equal("cswebui", start.Environment["VITE_RUNIC_HOST"]);
            using (new HostSelectionScope("desktop"))
                Equal("desktop", CommandRunner.CreateStartInfo("dotnet", "/tmp", []).Environment["RunicHost"]);
            Equal("cswebui", CommandRunner.CreateStartInfo("dotnet", "/tmp", []).Environment["RunicHost"]);
        }
        Equal(original, ReadHost());
        try { using var invalid = new HostSelectionScope("unknown"); throw new InvalidOperationException("Invalid host was accepted."); }
        catch (DevUsageException error) { Equal("RAPPDEV1008", error.Code); }
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
        workspace.Write("npm/package.json", """{"packageManager":"npm@12.0.2"}""");
        workspace.Write("npm/package-lock.json", "{}");
        JavaScriptPackageManager npm = JavaScriptPackageManager.Resolve(
            Path.Combine(workspace.Root, "npm"),
            Path.Combine(workspace.Root, "npm"));
        SequenceEqual(["ci", "--ignore-scripts"], npm.InstallArguments());
        SequenceEqual(
            ["run", "dev", "--workspace", "@example/app", "--", "--host", "127.0.0.1"],
            npm.RunScriptArguments("dev", "@example/app", ["--host", "127.0.0.1"]));

        workspace.Write("pnpm/package.json", """{"packageManager":"pnpm@12.3.4"}""");
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

        workspace.Write("bun/package.json", """{"packageManager":"bun@1.4.2"}""");
        workspace.Write("bun/bun.lock", "{}");
        JavaScriptPackageManager bun = JavaScriptPackageManager.Resolve(
            Path.Combine(workspace.Root, "bun"),
            Path.Combine(workspace.Root, "bun"));
        SequenceEqual(
            ["install", "--frozen-lockfile", "--ignore-scripts"],
            bun.InstallArguments());
        SequenceEqual(
            ["run", "--bun", "--filter", "@example/app", "dev", "--host", "127.0.0.1"],
            bun.RunScriptArguments("dev", "@example/app", ["--host", "127.0.0.1"]));
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
            FrontendWatchTarget: "RunicApplicationFrontendWatchAssets",
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
            FrontendWatchTarget: "RunicApplicationFrontendWatchAssets",
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
                "-property:RunicApplicationFrontendBuild=false",
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The initial Vite development build still enables production assets.");
        }
    }

    private static void DiscoveryBuildOwnerFlowsThroughDevelopmentCommands()
    {
        const string owner = "0123456789abcdef0123456789abcdef";
        DevProjectConfiguration ordinary = CreateDevelopmentServerConfiguration("vite", "index.html");
        False(DiscoveryBuildSession.CreateFor(ordinary) is not null,
            "An ordinary project created a compiled-discovery owner.");

        var session = new DiscoveryBuildSession(owner);
        IReadOnlyList<string> ownerEvaluation = DevProjectConfiguration.CreateEvaluationArguments(
            ordinary.ProjectPath,
            "Debug",
            session);
        if (!ownerEvaluation.Contains("-p:RunicPostMvvmDiscoveryBuildOwner=" + owner) ||
            !ownerEvaluation.Contains("-p:RunicPostMvvmDiscoveryOwnerDriver=true") ||
            !ownerEvaluation[^1].Contains("_RunicPostMvvmDiscoveryOwnerRoot", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The owner-aware evaluation did not receive its discovery properties.");
        }
        False(
            DevProjectConfiguration.CreateEvaluationArguments(
                ordinary.ProjectPath,
                "Debug",
                null).Any(IsDiscoveryBuildProperty),
            "The read-only project probe received discovery build properties.");

        DevProjectConfiguration discovery = ordinary with
        {
            UsesPostMvvmDiscovery = true,
            DiscoveryBuildOwner = owner,
            FrontendCompilerHotReloadTarget = "CompileChangedFrontend",
        };
        if (DiscoveryBuildSession.CreateFor(discovery) is null)
        {
            throw new InvalidOperationException("The compiled-discovery probe did not create a build session.");
        }

        var options = new DevOptions(null, "Debug", true, true, true, true, false, []);
        DevApplication.RequireDiscoveryBuildRestore(ordinary, options with { Restore = false });
        DevApplication.RequireDiscoveryBuildRestore(discovery, options);
        try
        {
            DevApplication.RequireDiscoveryBuildRestore(discovery, options with { Restore = false });
            throw new InvalidOperationException("The owner-scoped build accepted --no-restore.");
        }
        catch (DevUsageException error)
        {
            Equal("RAPPDEV1012", error.Code);
            Contains(error.Message, "cannot use --no-restore");
            Contains(error.Message, "owner-scoped project.assets.json");
        }

        AssertOneOwner(
            DevApplication.CreateRestoreArguments(discovery, "Debug"),
            "-p:");
        AssertOneOwner(
            DevApplication.CreateBuildArguments(discovery, options),
            "-p:");
        AssertOneOwner(
            HostProcessController.CreateRestartBuildArguments(discovery, options),
            "-p:");
        AssertOneOwner(
            HostProcessController.CreateRunArguments(discovery, options),
            "-p:");
        AssertOneOwner(
            HostProcessController.CreateWatchArguments(discovery, options),
            "--property:");
        AssertOneOwner(
            DevApplication.CreateFrontendCompilerArguments(discovery, "Debug"),
            "-p:");
        AssertOneOwner(
            DevApplication.CreateFrontendWatcherArguments(discovery, "Debug"),
            "-p:");

        False(
            DevApplication.CreateBuildArguments(ordinary, options)
                .Any(IsDiscoveryBuildProperty),
            "The ordinary build received discovery build properties.");

        static void AssertOneOwner(IReadOnlyList<string> arguments, string prefix)
        {
            string expected = prefix + DiscoveryBuildSession.OwnerProperty + "=" + owner;
            Equal(1, arguments.Count(argument => argument == expected));
            string driver = prefix + DiscoveryBuildSession.OwnerDriverProperty + "=true";
            Equal(1, arguments.Count(argument => argument == driver));
        }

        static bool IsDiscoveryBuildProperty(string argument) =>
            argument.Contains(DiscoveryBuildSession.OwnerProperty, StringComparison.Ordinal) ||
            argument.Contains(DiscoveryBuildSession.OwnerDriverProperty, StringComparison.Ordinal);
    }

    private static void DiscoveryBuildOwnerCleanupIsNarrow()
    {
        using var workspace = new TestWorkspace();
        const string owner = "0123456789abcdef0123456789abcdef";
        const string otherOwner = "fedcba9876543210fedcba9876543210";
        string sdkRoot = Path.Combine(workspace.Root, "sdk");
        string owners = Path.Combine(sdkRoot, "obj", "pmd", "ordinary");
        string ownedRoot = Path.Combine(owners, owner);
        string otherRoot = Path.Combine(owners, otherOwner);
        string ownedFile = Path.Combine(ownedRoot, "outer", "obj", "project.assets.json");
        Write(ownedFile, "owned");
        string otherFile = Path.Combine(otherRoot, "outer", "obj", "project.assets.json");
        Write(otherFile, "other");
        string ordinaryFile = Path.Combine(workspace.Root, "ordinary", "obj", "keep.txt");
        Write(ordinaryFile, "ordinary");

        DevProjectConfiguration configuration = CreateDevelopmentServerConfiguration("vite", "index.html") with
        {
            UsesPostMvvmDiscovery = true,
            DiscoveryBuildOwner = owner,
            DiscoverySdkRoot = sdkRoot,
            DiscoveryOutputKey = "ordinary",
            DiscoveryOwnerRoot = ownedRoot,
        };

        False(DiscoveryBuildSession.CleanupOwnedOutputs(configuration with
        {
            DiscoveryOwnerRoot = otherRoot,
        }), "A different GUID owner was removed.");
        False(DiscoveryBuildSession.CleanupOwnedOutputs(configuration with
        {
            DiscoveryOutputKey = "../ordinary",
        }), "An unsafe output key was accepted.");
        False(DiscoveryBuildSession.CleanupOwnedOutputs(configuration with
        {
            UsesPostMvvmDiscovery = false,
        }), "An ordinary project removed fixture outputs.");
        False(DiscoveryBuildSession.CleanupOwnedOutputs(configuration with
        {
            DiscoveryOwnerRoot = Path.Combine(workspace.Root, "ordinary", "obj"),
        }), "An ordinary project obj directory was removed.");
        if (!File.Exists(ownedFile) || !File.Exists(otherFile) || !File.Exists(ordinaryFile))
            throw new InvalidOperationException("Rejected cleanup changed another build's files.");

        if (OperatingSystem.IsLinux())
        {
            string linkedSdkRoot = Path.Combine(workspace.Root, "linked-sdk");
            Directory.CreateDirectory(linkedSdkRoot);
            Directory.CreateSymbolicLink(Path.Combine(linkedSdkRoot, "obj"),
                Path.Combine(sdkRoot, "obj"));
            False(DiscoveryBuildSession.CleanupOwnedOutputs(configuration with
            {
                DiscoverySdkRoot = linkedSdkRoot,
                DiscoveryOwnerRoot = Path.Combine(linkedSdkRoot, "obj", "pmd", "ordinary", owner),
            }), "A linked fixture ancestor was accepted.");
            if (!File.Exists(ownedFile))
                throw new InvalidOperationException("The linked path removed its target.");
        }

        if (!DiscoveryBuildSession.CleanupOwnedOutputs(configuration) ||
            Directory.Exists(ownedRoot) ||
            !File.Exists(otherFile) || !File.Exists(ordinaryFile))
        {
            throw new InvalidOperationException("Cleanup did not isolate the exact session owner.");
        }
        False(DiscoveryBuildSession.CleanupOwnedOutputs(configuration),
            "Cleanup reported deletion twice.");
    }

    private static void ViewBridgeHandoffIsHostOwned()
    {
        using var workspace = new TestWorkspace();
        string ready = Path.Combine(workspace.Root, "obj", "runic", "view-bridge.ready.json");
        string hostReady = Path.Combine(workspace.Root, "obj", "runic", "view-bridge-host.fingerprint");
        DevProjectConfiguration configuration = CreateDevelopmentServerConfiguration("vite", "index.html") with
        {
            ViewBridgeReadyManifest = ready,
            ViewBridgeHostReadyPath = hostReady,
        };
        configuration.ValidateViewBridgeHandoff();
        var options = new DevOptions(null, "Debug", true, true, true, true, false, []);
        False(DevApplication.ShouldBuildCanonicalFrontend(configuration, options),
            "The View Bridge frontend built before MSBuild generated its modules.");
        if (!DevApplication.ShouldBuildCanonicalFrontend(configuration, options with { WatchFrontend = false }) ||
            !DevApplication.ShouldBuildCanonicalFrontend(
                configuration with { ViewBridgeReadyManifest = "", ViewBridgeHostReadyPath = "" }, options))
        {
            throw new InvalidOperationException("The existing frontend build path changed for non-opt-in development.");
        }
        Throws<DevUsageException>(() => (configuration with { ViewBridgeHostReadyPath = "" })
            .ValidateViewBridgeHandoff());
        Throws<DevUsageException>(() => (configuration with { ViewBridgeHostReadyPath = ready })
            .ValidateViewBridgeHandoff());
        Throws<DevUsageException>(() => (configuration with { DevelopmentServerKind = "angular" })
            .ValidateViewBridgeHandoff());
        Throws<DevUsageException>(() => (configuration with { BridgeSource = "/repo/App.csproj" })
            .ValidateViewBridgeHandoff());
        Throws<DevUsageException>(() => ViewBridgeDevelopmentHandoff.Prepare(configuration));
        Write(ready, """{"fingerprint":"first"}""");
        Write(hostReady, "stale\n");
        ViewBridgeDevelopmentHandoff.Prepare(configuration);
        False(File.Exists(hostReady), "A stale host marker survived startup.");

        var vite = ViteDevelopmentServer.CreateEnvironment(configuration,
            new Uri("http://127.0.0.1:12345/events"), null);
        Equal(ready, vite[ViteDevelopmentServer.ViewBridgeReadyManifestEnvironmentVariable]);
        Equal(hostReady, vite[ViteDevelopmentServer.ViewBridgeHostReadyEnvironmentVariable]);
        var host = HostProcessController.CreateDevelopmentEnvironment(configuration, vite);
        Equal(hostReady, host[ViteDevelopmentServer.ViewBridgeHostReadyEnvironmentVariable]);
        Equal(ready, host[ViteDevelopmentServer.ViewBridgeReadyManifestEnvironmentVariable]);
        var defaultVite = ViteDevelopmentServer.CreateEnvironment(
            configuration with { ViewBridgeReadyManifest = "", ViewBridgeHostReadyPath = "" },
            new Uri("http://127.0.0.1:12345/events"), null);
        Equal<string?>(null, defaultVite[ViteDevelopmentServer.ViewBridgeHostReadyEnvironmentVariable]);
        var defaultHost = HostProcessController.CreateDevelopmentEnvironment(configuration, defaultVite);
        Equal<string?>(null, defaultHost[ViteDevelopmentServer.ViewBridgeHostReadyEnvironmentVariable]);

        False(File.Exists(hostReady), "The dev tool impersonated the managed host.");
        Write(ready, """{"fingerprint":" "}""");
        Equal<string?>(null, ViewBridgeDevelopmentHandoff.ReadFingerprint(ready));
        Equal<string?>(null, ViewBridgeDevelopmentHandoff.ReadFingerprint(workspace.Write("bad.json", "[]")));
    }

    private static void ViewBridgeWatchRegeneratesCompiledContract()
    {
        DevProjectConfiguration ordinary = CreateDevelopmentServerConfiguration("vite", "index.html");
        DevProjectConfiguration viewBridge = ordinary with
        {
            ViewBridgeReadyManifest = "/repo/obj/view-bridge.ready.json",
            ViewBridgeHostReadyPath = "/repo/obj/view-bridge-host.fingerprint",
        };
        var options = new DevOptions(null, "Debug", true, true, true, true, false, []);
        IReadOnlyList<string> bridgeWatch = HostProcessController.CreateWatchArguments(viewBridge, options);
        IReadOnlyList<string> ordinaryWatch = HostProcessController.CreateWatchArguments(ordinary, options);
        Equal("watch", bridgeWatch[0]);
        Equal("--no-hot-reload", bridgeWatch[1]);
        Equal(1, bridgeWatch.Count(value => value == "--no-hot-reload"));
        False(ordinaryWatch.Contains("--no-hot-reload", StringComparer.Ordinal),
            "Managed Hot Reload changed for an ordinary project.");
        SequenceEqual((IReadOnlyList<string>)bridgeWatch.Where(value => value != "--no-hot-reload").ToArray(), ordinaryWatch);
    }

    private static void WatcherOutlivesCleanChildExit() =>
        WatcherOutlivesCleanChildExitAsync().GetAwaiter().GetResult();

    private static async Task WatcherOutlivesCleanChildExitAsync()
    {
        using var workspace = new TestWorkspace();
        string marker = Path.Combine(workspace.Root, "child-exited");
        string project = workspace.Write("WatchExit.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        workspace.Write("Program.cs", """
            string marker = Environment.GetEnvironmentVariable("RUNIC_TEST_WATCH_EXIT_MARKER")
                ?? throw new InvalidOperationException("The exit marker is required.");
            File.WriteAllText(marker, "child exited");
            Console.WriteLine("WATCH_CHILD_EXITING");
            """);
        CommandResult restore = await CommandRunner.RunAsync("dotnet", workspace.Root,
            ["restore", project], CancellationToken.None).ConfigureAwait(false);
        if (restore.ExitCode != 0)
            throw new InvalidOperationException($"The dotnet watch fixture did not restore: {restore.CombinedOutput}");

        var configuration = new DevProjectConfiguration(
            ProjectPath: project,
            ProjectDirectory: workspace.Root,
            NodeEnabled: false,
            FrontendCompilerEnabled: false,
            WorkspaceRoot: workspace.Root,
            Workspace: "",
            FrontendPackageDirectory: "",
            FrontendOutputDirectory: "",
            FrontendWebRoot: "www",
            BridgeSource: "",
            BridgeIr: "",
            BridgeFacade: "",
            FrontendWatchTarget: "",
            ViteDevServerEnabled: false,
            ViteDevServerEntry: "",
            ViteConfigurationPath: "",
            FrontendCompilerDiagnosticsPath: "",
            FrontendCompilerHotReloadPath: "",
            TargetDirectory: Path.Combine(workspace.Root, "bin"))
        {
            DevelopmentServerKind = "vite",
            ViewBridgeReadyManifest = Path.Combine(workspace.Root, "ready.json"),
            ViewBridgeHostReadyPath = Path.Combine(workspace.Root, "host-ready"),
        };
        var options = new DevOptions(null, "Debug", false, false, false, true, false, []);
        await using var host = new HostProcessController("dotnet", configuration, options,
            new Dictionary<string, string?> { ["RUNIC_TEST_WATCH_EXIT_MARKER"] = marker });
        await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
        for (int attempt = 0; attempt < 200 && !File.Exists(marker); attempt++)
            await Task.Delay(25).ConfigureAwait(false);
        if (!File.Exists(marker))
            throw new InvalidOperationException("The watched child did not reach its clean exit.");

        // `dotnet watch` reports the child as exited, then keeps its own process
        // alive to observe the next edit. Completion is for that outer process.
        await Task.Delay(500).ConfigureAwait(false);
        if (host.Completion.IsCompleted)
            throw new InvalidOperationException("A clean watched child exit terminated the dotnet watch controller.");
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
                "-property:RunicApplicationFrontendBuild=false",
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
        Contains(simple, "<script src=\"runic-desktop.js\"></script>");
        Contains(simple, "http://127.0.0.1:43125/src/main.ts");
        Contains(simple, "<base href=\"./\">");
        Contains(simple, "from \"http://127.0.0.1:43125/@react-refresh\"");
        Contains(simple, "__runicApplicationApplicationBridgeDevelopment");
        Contains(simple, "http://127.0.0.1:43126/token/events");
        Contains(simple, configuration.ProjectDirectory);
        Equal(simple, advanced);
        FrontendDevelopmentDocument.Write(configuration with { Host = "cswebui" }, origin, inspector, "cs.html", source);
        string cs = File.ReadAllText(Path.Combine(target, "www", "cs.html"));
        False(cs.Contains("/runic-desktop.js", StringComparison.Ordinal), "CS-WebUI inherited a Desktop bootstrap.");
        False(cs.Contains("/webui.js", StringComparison.Ordinal), "The native host must inject WebUI exactly once.");
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
            Contains(formatted!,
                Path.Combine(Path.GetFullPath("/repo"), "CounterBridgeHandler.cs") + ":12:6");
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
                      "contract": "runic.application.frontend-compiler.rendered-fragments/1.0",
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
                      "contract": "runic.application.frontend-compiler.rendered-fragments/1.0",
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
            FrontendWatchTarget: "RunicApplicationFrontendWatchAssets",
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
            {"contract":"runic.application.frontend-compiler.hot-reload/1.0","templates":[{"logicalPath":"Views/TodoApp.frontend","rendererFingerprint":"{{renderer}}","compatibilityFingerprint":"{{shape}}","canRefreshFragments":{{canRefresh.ToString().ToLowerInvariant()}},"affectedFragments":["todo_fragment"]}]}
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
            .WithResult("/tools/dotnet", "--version", 0, "10.0.400")
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
            """{"packageManager":"npm@12.0.2"}""");
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
            .WithResult("/tools/dotnet", "--version", 0, "10.0.400")
            .WithResult("/tools/node", "--version", 0, "v24.20.0")
            .WithResult("/tools/npm", "--version", 0, "12.0.2")
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
        workspace.Write("package.json", """{"packageManager":"bun@1.4.2"}""");
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
            .WithResult("/tools/dotnet", "--version", 0, "10.0.400")
            .WithResult("/tools/bun", "--version", 0, "1.4.2")
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
        workspace.Write("package.json", """{"packageManager":"npm@12.0.2"}""");
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
            .WithResult("/tools/dotnet", "--version", 0, "10.0.400")
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
            {"libraries":{"Runic.Application/0.3.0-preview.1":{"type":"package"},"Runic.Desktop/0.3.0-preview.1":{"type":"package"}}}
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

    private static void EmbeddedCompatibilityDescribesSdk()
    {
        CompatibilitySetAuthority authority = CompatibilitySetAuthority.Current;
        Equal($"runic-sdk-{authority.ReleaseTrainVersion}", authority.Id);
        Equal(33, authority.NuGetPackages.Count);
        Equal(8, authority.NpmPackages.Count);
        foreach (CompatibilityPackage package in authority.NuGetPackages.Values.Concat(authority.NpmPackages.Values))
        {
            Equal(authority.ReleaseTrainVersion, package.Version);
        }
        Equal("Runic.Platform.MacOS", authority.NuGetPackages["runic.platform.macos"].Identity);
        Equal("@runic-artifex/angular", authority.NpmPackages["@runic-artifex/angular"].Identity);
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
            {"libraries":{"Runic.Application/0.3.0-preview.2":{"type":"package"},"Runic.Desktop/0.3.0-preview.1":{"type":"package"}}}
            """);
        var runtime = new FakeDoctorRuntime()
            .WithEnvironment("RUNIC_BROWSER_PATH", browser)
            .WithExecutable("dotnet", "/tools/dotnet")
            .WithResult("/tools/dotnet", "--version", 0, "10.0.400")
            .WithResult(browser, "--version", 0, "Chromium 150");

        DoctorReport report = InspectDoctor(project, runtime);
        DoctorCheck check = report.Checks.Single(item => item.Name == "compatibility-set");
        Equal(DoctorStatus.Failure, check.Status);
        Contains(check.Message, "Runic.Application 0.3.0-preview.2");
        Contains(check.Remediation ?? string.Empty, "isolated feed");
    }

    private static void DoctorRejectsNonPortableNpmLock()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("package.json", """{"packageManager":"npm@12.0.2"}""");
        workspace.Write(
            "package-lock.json",
            """
            {"lockfileVersion":3,"packages":{"node_modules/@runic-artifex/application-bridge":{"version":"0.3.0-preview.1","resolved":"https://registry.example.invalid/application-bridge.tgz"}}}
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
            .WithResult("/tools/dotnet", "--version", 0, "10.0.400")
            .WithResult("/tools/node", "--version", 0, "v24.20.0")
            .WithResult("/tools/npm", "--version", 0, "12.0.2")
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

    [SuppressMessage("Performance", "CA1859:Change type of parameter",
        Justification = "The helper intentionally compares arrays and non-array IReadOnlyList test results.")]
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
                "runic-application-dev-tests",
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
