using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            ("project discovery accepts a directory", ProjectDiscoveryAcceptsDirectory),
            ("project discovery rejects ambiguity", ProjectDiscoveryRejectsAmbiguity),
            ("commands keep arguments shell-free", CommandsKeepArgumentsShellFree),
            ("package managers use locked, portable commands", PackageManagersUseLockedCommands),
            ("MSBuild evaluation requires the Views Window opt-in", EvaluationRequiresViewsWindow),
            ("Views Window watch restarts the native process", ViewsWindowWatchRestartsNativeProcess),
            ("Vite and Angular bind their development servers to loopback", DevelopmentServersBindLoopback),
            ("Vite readiness reports useful failures and honors cancellation", ViteReadinessIsActionable),
            ("development documents rewrite assets for the native bootstrap", DevelopmentDocumentIsNativeSafe),
            ("size inventory counts every file and preserves hashes", SizeInventoryAccountsForBytes),
            ("doctor accepts a healthy Views Window project", DoctorAcceptsViewsWindowProject),
            ("compatibility authority includes the public Views packages", CompatibilityAuthorityIncludesViews),
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

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} dotnet-runic tests passed.");
        return failures == 0 ? 0 : 1;
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
            "dotnet", Environment.CurrentDirectory,
            ["build", "a project.csproj", "-p:Value=$(not-a-shell)"]);
        Equal(3, startInfo.ArgumentList.Count);
        Equal("a project.csproj", startInfo.ArgumentList[1]);
        Equal("-p:Value=$(not-a-shell)", startInfo.ArgumentList[2]);
        False(startInfo.UseShellExecute, "Commands unexpectedly use a shell.");
    }

    private static void PackageManagersUseLockedCommands()
    {
        using var workspace = new TestWorkspace();
        string npm = Path.Combine(workspace.Root, "npm");
        string pnpm = Path.Combine(workspace.Root, "pnpm");
        string bun = Path.Combine(workspace.Root, "bun");
        workspace.Write("npm/package.json", """{"packageManager":"npm@12.0.2"}""");
        workspace.Write("npm/package-lock.json", "{}");
        workspace.Write("pnpm/package.json", """{"packageManager":"pnpm@12.3.4"}""");
        workspace.Write("pnpm/pnpm-lock.yaml", "lockfileVersion: '9.0'");
        workspace.Write("bun/package.json", """{"packageManager":"bun@1.4.2"}""");
        workspace.Write("bun/bun.lock", "{}");

        JavaScriptPackageManager npmManager = JavaScriptPackageManager.Resolve(npm, npm);
        Equal("npm", npmManager.Name);
        SequenceEqual(["ci", "--ignore-scripts"], npmManager.InstallArguments());
        SequenceEqual(["run", "dev", "--", "--host", "127.0.0.1"],
            npmManager.RunScriptArguments("dev", ".", ["--host", "127.0.0.1"]));

        JavaScriptPackageManager pnpmManager = JavaScriptPackageManager.Resolve(pnpm, pnpm);
        Equal("pnpm", pnpmManager.Name);
        SequenceEqual(["install", "--frozen-lockfile", "--ignore-scripts"], pnpmManager.InstallArguments());

        JavaScriptPackageManager bunManager = JavaScriptPackageManager.Resolve(bun, bun);
        Equal("bun", bunManager.Name);
        SequenceEqual(["install", "--frozen-lockfile", "--ignore-scripts"], bunManager.InstallArguments());
    }

    private static void EvaluationRequiresViewsWindow()
    {
        IReadOnlyList<string> arguments = DevProjectConfiguration.CreateEvaluationArguments("App.csproj", "Debug");
        string properties = string.Join(" ", arguments);
        Contains(properties, "RunicViewsWindowProject");
        Contains(properties, "RunicBridgeFrontendDir");
    }

    private static void ViewsWindowWatchRestartsNativeProcess()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        var options = new DevOptions(null, "Debug", false, true, true, false, []);
        IReadOnlyList<string> arguments = HostProcessController.CreateWatchArguments(configuration, options);
        Contains(string.Join(" ", arguments), "--no-hot-reload");
        Contains(string.Join(" ", arguments), "watch");

        IReadOnlyDictionary<string, string?> environment = HostProcessController.CreateDevelopmentEnvironment(
            configuration, new Dictionary<string, string?>());
        Equal("1", environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"]);
    }

    private static void DevelopmentServersBindLoopback()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("Frontend/package.json", """{"packageManager":"npm@12.0.2"}""");
        workspace.Write("Frontend/package-lock.json", "{}");
        string frontend = Path.Combine(workspace.Root, "Frontend");
        var vite = CreateConfiguration(workspace, "vite", frontend);
        SequenceEqual(["run", "dev", "--", "--host", "127.0.0.1", "--port", "5173", "--strictPort"],
            ViteDevelopmentServer.CreateArguments(vite, 5173));
        Equal("none", ViteDevelopmentServer.CreateEnvironment(vite)["BROWSER"]);

        var angular = CreateConfiguration(workspace, "angular", frontend);
        SequenceEqual(["run", "dev", "--", "--host", "127.0.0.1", "--port", "4200", "--hmr", "--live-reload"],
            AngularDevelopmentServer.CreateArguments(angular, 4200));
    }

    private static void ViteReadinessIsActionable() => VerifyViteReadinessAsync().GetAwaiter().GetResult();

    private static async Task VerifyViteReadinessAsync()
    {
        var origin = new Uri("http://127.0.0.1:12345/");
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failingClient = new HttpClient(new ReadinessHandler());
        try
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(
                origin, "src/main.ts", running.Task, failingClient,
                TimeSpan.FromMilliseconds(100), CancellationToken.None);
            throw new InvalidOperationException("A non-responsive Vite server was accepted as ready.");
        }
        catch (DevDevelopmentException error)
        {
            Equal("RAPPDEV1007", error.Code);
            Contains(error.Message, "Vite client: no response");
            Contains(error.Message, "dotnet runic doctor");
            DoesNotContain(error.Message, origin.AbsoluteUri);
        }

        using var readyClient = new HttpClient(new ReadinessHandler(ready: true));
        await ViteDevelopmentServer.WaitUntilReadyAsync(
            origin, "src/main.ts", running.Task, readyClient,
            TimeSpan.FromSeconds(1), CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(
                origin, "src/main.ts", running.Task, readyClient,
                TimeSpan.FromSeconds(1), cancellation.Token);
            throw new InvalidOperationException("Caller cancellation was ignored.");
        }
        catch (OperationCanceledException) { }

        try
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(
                origin, "src/main.ts", Task.FromResult(7), readyClient,
                TimeSpan.FromSeconds(1), CancellationToken.None);
            throw new InvalidOperationException("An exited server was accepted as ready.");
        }
        catch (DevDevelopmentException error)
        {
            Contains(error.Message, "code 7");
        }
    }

    private sealed class ReadinessHandler(bool ready = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!ready)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static void DevelopmentDocumentIsNativeSafe()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        string html = "<html><head><title>App</title></head><body><script type=\"module\" src=\"/src/main.ts\"></script><link href=\"/src/app.css\"></body></html>";
        FrontendDevelopmentDocument.Write(configuration,
            new Uri("http://127.0.0.1:5173/"), "index.html", html);
        string generated = File.ReadAllText(Path.Combine(configuration.RuntimeWebRoot, "index.html"));
        Contains(generated, "<base href=\"./\">");
        Contains(generated, "http://127.0.0.1:5173/src/main.ts");
        Contains(generated, "http://127.0.0.1:5173/src/app.css");
        Throws<DevUsageException>(() => FrontendDevelopmentDocument.Write(configuration,
            new Uri("http://127.0.0.1:5173/"), "../escape.html", html));
    }

    private static void SizeInventoryAccountsForBytes()
    {
        using var workspace = new TestWorkspace();
        string root = Path.Combine(workspace.Root, "publish");
        Directory.CreateDirectory(Path.Combine(root, "runtimes", "win-x64", "native"));
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
        string hash = files.Single(file => file.Path == "Example").Sha256;
        File.WriteAllBytes(Path.Combine(root, "Example"), [3, 2, 1]);
        False(hash == SizeApplication.Inventory(root, "Example", "linux-x64")
            .Single(file => file.Path == "Example").Sha256,
            "Same-length mutations must change the content hash.");
    }

    private static void DoctorAcceptsViewsWindowProject()
    {
        using var workspace = new TestWorkspace();
        string frontend = workspace.Write("Frontend/package.json", "{}");
        string frontendDirectory = Path.GetDirectoryName(frontend)!;
        CompatibilitySetAuthority authority = CompatibilitySetAuthority.Current;
        CompatibilityPackage nuget = authority.NuGetPackages["Runic.Application.CsWebUi"];
        CompatibilityPackage npm = authority.NpmPackages["@runic-artifex/svelte"];
        Write(Path.Combine(frontendDirectory, "package.json"), JsonSerializer.Serialize(new
        {
            packageManager = $"npm@{authority.Toolchain.Npm}",
            dependencies = new Dictionary<string, string> { [npm.Identity] = npm.Version },
        }));
        Write(Path.Combine(frontendDirectory, "package-lock.json"), "{}");
        Write(Path.Combine(frontendDirectory, "vite.config.ts"), "export default {};");
        Write(Path.Combine(frontendDirectory, "src/main.ts"), "export {};");
        string assets = Path.Combine(workspace.Root, "obj", "project.assets.json");
        Write(assets, JsonSerializer.Serialize(new
        {
            libraries = new Dictionary<string, object>
            {
                [$"{nuget.Identity}/{nuget.Version}"] = new { type = "package" },
                ["CsWebUi/2.5.0-beta.4.4"] = new { type = "package" },
                ["CsWebUi.Native/2.5.0-beta.4.4"] = new { type = "package" },
            },
        }));

        var project = new DoctorProjectConfiguration(
            Path.Combine(workspace.Root, "App.csproj"), workspace.Root, "net10.0", true,
            frontendDirectory, assets, "linux-x64", "/src/main.ts",
            Path.Combine(frontendDirectory, "vite.config.ts"), true);
        DoctorReport report = DoctorChecks.InspectAsync(
            project, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
            .GetAwaiter().GetResult();
        True(report.IsHealthy, "A correctly locked Views Window project should pass doctor; failures: " +
            string.Join("; ", report.Checks.Where(check => check.Status == DoctorStatus.Failure).Select(check => check.Message)));
        Equal(DoctorStatus.Pass, report.Checks.Single(check => check.Name == "views-window").Status);
        Equal(DoctorStatus.Pass, report.Checks.Single(check => check.Name == "compatibility-set").Status);
    }

    private sealed class FakeDoctorRuntime(CompatibilityToolchain toolchain) : IDoctorRuntime
    {
        public string? GetEnvironmentVariable(string name) => null;

        public string? FindExecutable(string name) => name is "dotnet" or "node" or "npm" ? name : null;

        public Task<CommandResult> RunAsync(
            string executable, string workingDirectory, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            string version = executable switch
            {
                "dotnet" => toolchain.DotNetSdk,
                "node" => toolchain.Node,
                "npm" => toolchain.Npm,
                _ => throw new InvalidOperationException($"Unexpected executable {executable}"),
            };
            return Task.FromResult(new CommandResult(0, version, string.Empty));
        }
    }

    private static void CompatibilityAuthorityIncludesViews()
    {
        CompatibilitySetAuthority authority = CompatibilitySetAuthority.Current;
        True(authority.NuGetPackages.ContainsKey("Runic.Application.CsWebUi"),
            "The certified NuGet set must include the Views Window host package.");
        True(authority.NpmPackages.ContainsKey("@runic-artifex/svelte"),
            "The certified npm set must include the Svelte Views outlet.");
        True(authority.NpmPackages.ContainsKey("@runic-artifex/angular"),
            "The certified npm set must include the Angular Views outlet.");
    }

    private static DevProjectConfiguration CreateConfiguration(
        TestWorkspace workspace, string serverKind, string? frontendDirectory = null)
    {
        string frontend = frontendDirectory ?? Path.Combine(workspace.Root, "Frontend");
        Directory.CreateDirectory(frontend);
        string target = Path.Combine(workspace.Root, "bin", "Debug", "net10.0");
        Directory.CreateDirectory(target);
        return new DevProjectConfiguration(
            Path.Combine(workspace.Root, "App.csproj"), workspace.Root,
            NodeEnabled: true, FrontendCompilerEnabled: false,
            WorkspaceRoot: frontend, Workspace: ".", FrontendPackageDirectory: frontend,
            FrontendOutputDirectory: Path.Combine(frontend, "dist"), FrontendWebRoot: "www",
            FrontendWatchTarget: string.Empty, ViteDevServerEnabled: serverKind == "vite",
            ViteDevServerEntry: serverKind == "vite" ? "/src/main.ts" : string.Empty,
            ViteConfigurationPath: Path.Combine(frontend, "vite.config.ts"),
            FrontendCompilerDiagnosticsPath: Path.Combine(workspace.Root, "obj", "diagnostics.json"),
            FrontendCompilerHotReloadPath: Path.Combine(workspace.Root, "obj", "hot-reload.json"),
            TargetDirectory: target)
        {
            IsViewsWindowProject = true,
            DevelopmentServerKind = serverKind,
            DevelopmentServerDocument = "index.html",
        };
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Contains(string value, string expected)
    {
        if (!value.Contains(expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{value}' to contain '{expected}'.");
    }

    private static void DoesNotContain(string value, string unexpected)
    {
        if (value.Contains(unexpected, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{value}' not to contain '{unexpected}'.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', received '{actual}'.");
    }

    private static void SequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], received [{string.Join(", ", actual)}].");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class TestWorkspace : IDisposable
    {
        internal TestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "runic-tool-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        internal string Root { get; }

        internal string Write(string relativePath, string content)
        {
            string path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Program.Write(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
