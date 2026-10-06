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
            ("commands end input and tolerate invalid UTF-8", CommandsEndInputAndTolerateInvalidUtf8),
            ("package managers use locked, portable commands", PackageManagersUseLockedCommands),
            ("MSBuild evaluation requires the Views Window opt-in", EvaluationRequiresViewsWindow),
            ("Views Window watch restarts the native process", ViewsWindowWatchRestartsNativeProcess),
            ("Vite and Angular bind their development servers to loopback", DevelopmentServersBindLoopback),
            ("Vite readiness reports useful failures and honors cancellation", ViteReadinessIsActionable),
            ("development documents rewrite assets for the native bootstrap", DevelopmentDocumentIsNativeSafe),
            ("size inventory counts every file and preserves hashes", SizeInventoryAccountsForBytes),
            ("doctor accepts a healthy Views Window project", DoctorAcceptsViewsWindowProject),
            ("doctor guides an unrestored project instead of failing", DoctorGuidesUnrestoredProject),
            ("doctor accepts the Runic Desktop Views host", DoctorAcceptsDesktopHost),
            ("doctor scopes the browser check to the Views host", DoctorScopesBrowserCheckToHost),
            ("doctor detects the Views host from evaluated references", DoctorDetectsHostFromReferences),
            ("doctor JSON payload lists every check", DoctorJsonListsChecks),
            ("--no-restore keeps MSBuild from installing frontend packages", NoRestoreSkipsFrontendInstall),
            ("child process failures name the program and directory", ChildFailuresNameProgramAndDirectory),
            ("every command option is described", EveryOptionIsDescribed),
            ("doctor JSON reports unhealthy projects through the CLI", DoctorJsonThroughCli),
            ("doctor --fail-on decides the exit in both modes", DoctorFailOnDecidesExit),
            ("--no-restore requires installed frontend packages", NoRestoreRequiresInstalledPackages),
            ("JSON faults never contain absolute project paths", JsonFaultsOmitProjectPaths),
            ("the fault backstop detects any rooted path", FaultBackstopDetectsRootedPaths),
            ("development servers are inferred from the frontend", DevelopmentServersAreInferred),
            ("a development server owns the frontend build", DevelopmentServerOwnsFrontendBuild),
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

    private static void CommandsEndInputAndTolerateInvalidUtf8()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The child waits for end-of-input like an interactive prompt, then writes an invalid UTF-8 byte.
        Task<CommandResult> run = CommandRunner.RunAsync(
            "/bin/sh", Environment.CurrentDirectory,
            ["-c", "cat >/dev/null; printf 'a\\377b'; printf 'c\\377' >&2"],
            CancellationToken.None);
        True(run.Wait(TimeSpan.FromSeconds(30)), "A command waiting on standard input did not finish.");
        Equal(0, run.Result.ExitCode);
        Equal("a�b", run.Result.StandardOutput);
        Equal("c�", run.Result.StandardError);
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
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")))
        {
            Equal("Development", environment["DOTNET_ENVIRONMENT"]);
        }
        else
        {
            Equal(false, environment.ContainsKey("DOTNET_ENVIRONMENT"));
        }
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
        // The Vite development server serves index.html with root-absolute script paths.
        string html = "<html><head><title>App</title><script src=\"./runic-desktop.js\"></script><script src=\"/webui.js\"></script><script src=\"runic-cswebui.js\"></script><script src=\"./runic-desktop-views.js\"></script></head><body><script type=\"module\" src=\"/src/main.ts\"></script><link href=\"/src/app.css\"></body></html>";
        FrontendDevelopmentDocument.Write(configuration,
            new Uri("http://127.0.0.1:5173/"), "index.html", html);
        string generated = File.ReadAllText(Path.Combine(configuration.RuntimeWebRoot, "index.html"));
        Contains(generated, "<base href=\"./\">");
        Contains(generated, "http://127.0.0.1:5173/src/main.ts");
        Contains(generated, "http://127.0.0.1:5173/src/app.css");
        Contains(generated, "<script src=\"webui.js\">");
        Contains(generated, "<script src=\"runic-cswebui.js\">");
        Contains(generated, "<script src=\"runic-desktop-views.js\">");
        Contains(generated, "<script src=\"runic-desktop.js\">");
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
            dependencies = new Dictionary<string, string>
            {
                [npm.Identity] = npm.Version,
                ["@runic-artifex/vite-plugin-runic-translations"] = "0.6.0-preview.1",
            },
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
                ["Runic.CommandLine/0.6.0-preview.1"] = new { type = "package" },
                ["Runic.Translations/0.6.0-preview.1"] = new { type = "package" },
                ["dotnet-runic-translations/0.6.0-preview.1"] = new { type = "package" },
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

    private static DoctorProjectConfiguration CreateDoctorProject(
        TestWorkspace workspace, string? hostPackage, out CompatibilitySetAuthority authority)
    {
        authority = CompatibilitySetAuthority.Current;
        string frontendDirectory = Path.Combine(workspace.Root, "Frontend");
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
        if (hostPackage is not null)
        {
            CompatibilityPackage nuget = authority.NuGetPackages[hostPackage];
            Write(assets, JsonSerializer.Serialize(new
            {
                libraries = new Dictionary<string, object>
                {
                    [$"{nuget.Identity}/{nuget.Version}"] = new { type = "package" },
                },
            }));
        }
        return new DoctorProjectConfiguration(
            Path.Combine(workspace.Root, "App.csproj"), workspace.Root, "net10.0", true,
            frontendDirectory, assets, "linux-x64", "/src/main.ts",
            Path.Combine(frontendDirectory, "vite.config.ts"), true,
            hostPackage switch
            {
                "Runic.Application.Desktop" => RunicViewsHost.Desktop,
                "Runic.Application.CsWebUi" => RunicViewsHost.CsWebUi,
                _ => RunicViewsHost.Unknown,
            });
    }

    private static void DoctorScopesBrowserCheckToHost()
    {
        using (var workspace = new TestWorkspace())
        {
            DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Desktop", out var authority);
            DoctorReport report = DoctorChecks.InspectAsync(
                desktop, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
                .GetAwaiter().GetResult();
            Equal(DoctorStatus.Pass, report.Checks.Single(check => check.Name == "browser").Status);
            foreach (DoctorCheck check in report.Checks)
            {
                DoesNotContain(check.Message + " " + check.Remediation, "CS-WebUI");
                DoesNotContain(check.Message + " " + check.Remediation, "WebView");
            }
        }

        using (var workspace = new TestWorkspace())
        {
            DoctorProjectConfiguration csWebUi = CreateDoctorProject(workspace, "Runic.Application.CsWebUi", out var authority);
            DoctorCheck browser = DoctorChecks.InspectAsync(
                csWebUi, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
                .GetAwaiter().GetResult().Checks.Single(check => check.Name == "browser");
            Equal(DoctorStatus.Warning, browser.Status);
            Contains(browser.Remediation ?? string.Empty, "CS-WebUI");
        }
    }

    private static void DoctorDetectsHostFromReferences()
    {
        static RunicViewsHost Detect(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return DoctorProjectConfiguration.DetectHost(document.RootElement);
        }

        Equal(RunicViewsHost.Desktop, Detect("""{"Properties":{},"Items":{"PackageReference":[{"Identity":"Runic.Application.Desktop"}]}}"""));
        Equal(RunicViewsHost.CsWebUi, Detect("""{"Items":{"PackageReference":[{"Identity":"Runic.Application.CsWebUi"}]}}"""));
        Equal(RunicViewsHost.CsWebUi, Detect("""{"Items":{"ProjectReference":[{"Identity":"..\\..\\packages\\Runic.Application.Views.CsWebUi\\Runic.Application.Views.CsWebUi.csproj"}]}}"""));
        Equal(RunicViewsHost.Desktop, Detect("""{"Items":{"ProjectReference":[{"Identity":"../Runic.Application.Desktop/Runic.Application.Desktop.csproj"}]}}"""));
        Equal(RunicViewsHost.Unknown, Detect("""{"Properties":{}}"""));
    }

    private static void DoctorJsonListsChecks()
    {
        var project = new DoctorProjectConfiguration(
            "/work/App.csproj", "/work", "net10.0", true, "/work/Frontend", "/work/obj/project.assets.json",
            "linux-x64", "/src/main.ts", "/work/Frontend/vite.config.ts", true, RunicViewsHost.Desktop);
        var report = new DoctorReport(
        [
            new DoctorCheck(DoctorStatus.Pass, "views-window", "The project uses the Runic Views Window model."),
            new DoctorCheck(DoctorStatus.Warning, "compatibility-set", "Not restored.", "Run 'dotnet runic dev'."),
            new DoctorCheck(DoctorStatus.Failure, "lock-file", "The workspace has no 'package-lock.json'.", "Run npm and commit its lock file."),
        ]);
        DoctorCommandResult result = DoctorCommandResult.Create(project, report, "human text");
        Equal("human text", result.ToString());
        // The command codec may build the context with its own options; names must not depend on them.
        string json = JsonSerializer.Serialize(result, new DoctorCommandJsonContext(new JsonSerializerOptions()).DoctorCommandResult);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        SequenceEqual(["project", "host", "healthy", "summary", "checks"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Equal("desktop", root.GetProperty("host").GetString());
        False(root.GetProperty("healthy").GetBoolean(), "A failing check makes the payload unhealthy.");
        Equal(1, root.GetProperty("summary").GetProperty("failed").GetInt32());
        JsonElement[] checks = [.. root.GetProperty("checks").EnumerateArray()];
        Equal(3, checks.Length);
        SequenceEqual(["id", "status", "message", "remediation"],
            checks[0].EnumerateObject().Select(property => property.Name).ToArray());
        Equal(JsonValueKind.Null, checks[0].GetProperty("remediation").ValueKind);
        SequenceEqual(["pass", "warn", "fail"], checks.Select(check => check.GetProperty("status").GetString()!).ToArray());
        Equal("lock-file", checks[2].GetProperty("id").GetString());

        IReadOnlyList<Runic.CommandLine.CommandDiagnostic> diagnostics =
            DoctorCommandResult.CreateDiagnostics(report, new Runic.CommandLine.CommandPath(["doctor"]));
        SequenceEqual(["doctor-check-failed", "doctor-check-warning"], diagnostics.Select(diagnostic => diagnostic.Kind).ToArray());
        SequenceEqual(["RCLI8101", "RCLI8102"], diagnostics.Select(diagnostic => diagnostic.Code).ToArray());
        SequenceEqual(["doctor.lock-file.failed", "doctor.compatibility-set.warning"],
            diagnostics.Select(diagnostic => diagnostic.MessageKey).ToArray());
        SequenceEqual(["lock-file"], diagnostics[0].Arguments);
        True(diagnostics.All(diagnostic => diagnostic.Severity == Runic.CommandLine.CommandDiagnosticSeverity.Warning),
            "A successful JSON envelope cannot carry error diagnostics.");
        Equal(Runic.CommandLine.CommandDiagnosticSeverity.Error,
            DoctorCommandResult.CreateDiagnostics(report, new Runic.CommandLine.CommandPath(["doctor"]), DoctorStatus.Failure)[0].Severity);
    }

    private static void NoRestoreSkipsFrontendInstall()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        var noRestore = new DevOptions(null, "Debug", false, true, true, false, []);
        string build = string.Join(" ", DevApplication.CreateBuildArguments(configuration, noRestore));
        Contains(build, "-property:RunicBridgeInstallFrontend=false");
        Contains(build, "--no-restore");
        Contains(string.Join(" ", HostProcessController.CreateWatchArguments(configuration, noRestore)),
            "--property:RunicBridgeInstallFrontend=false");
        Contains(string.Join(" ", HostProcessController.CreateRestartBuildArguments(configuration, noRestore)),
            "-property:RunicBridgeInstallFrontend=false");

        var restore = new DevOptions(null, "Debug", true, true, true, false, []);
        DoesNotContain(string.Join(" ", DevApplication.CreateBuildArguments(configuration, restore)),
            "RunicBridgeInstallFrontend");
    }

    private static void ChildFailuresNameProgramAndDirectory()
    {
        string failure = CommandRunner.DescribeFailure(
            "/usr/bin/dotnet", ["build", "/work/App.csproj"], 1);
        Equal("'dotnet build' exited with code 1.", failure);
        Equal("Working directory: /work\nhint\n", CommandRunner.LocalDetail("/work", "hint"));
        try
        {
            CommandRunner.RunAsync("/nonexistent/runic-tool", "/tmp", ["build"], CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("A missing executable started.");
        }
        catch (DevUsageException error)
        {
            Equal("RAPPDEV1004", error.Code);
            DoesNotContain(error.Message, "/");
            Contains(error.LocalDetail ?? string.Empty, "/nonexistent/runic-tool");
        }
        Equal("npm", CommandRunner.DescribeProgram("npm", ["--version"]));
        Contains(CommandRunner.DoctorHint("/work/App.csproj"), "dotnet runic doctor --project \"/work/App.csproj\"");
    }

    private static string CreateCliDoctorProject(TestWorkspace workspace)
    {
        // A Desktop project without a lock file: doctor completes and reports lock-file as failing.
        string project = workspace.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RunicViewsWindowProject>true</RunicViewsWindowProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Runic.Application.Desktop" Version="0.6.0-preview.1" />
              </ItemGroup>
            </Project>
            """);
        workspace.Write("Frontend/package.json", """{"packageManager":"npm@10.0.0"}""");
        return project;
    }

    private static (int ExitCode, string Output) RunCli(params string[] arguments)
    {
        var console = new CapturingConsole();
        int exitCode = Runic.Application.Tool.Program.RunAsync(arguments, console).GetAwaiter().GetResult();
        return (exitCode, console.Output);
    }

    private static void DoctorJsonThroughCli()
    {
        using var workspace = new TestWorkspace();
        string project = CreateCliDoctorProject(workspace);
        (int exitCode, string output) = RunCli("doctor", "--project", project, "--output", "json");
        Equal(0, exitCode);
        var response = Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(output), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult);
        True(response.Success, "JSON doctor reports a completed inspection as a successful envelope.");
        DoctorCommandResult payload = response.Payload!;
        False(payload.Healthy, "A missing lock file makes the project unhealthy.");
        Equal("desktop", payload.Host);
        Equal("fail", payload.Checks.Single(check => check.Id == "lock-file").Status);
        Equal("pass", payload.Checks.Single(check => check.Id == "browser").Status);
        True(response.Diagnostics.Any(diagnostic => diagnostic.MessageKey == "doctor.lock-file.failed"),
            "Each failing check is mirrored as a diagnostic.");
    }

    private static void DoctorFailOnDecidesExit()
    {
        using var workspace = new TestWorkspace();
        string project = CreateCliDoctorProject(workspace);
        (int exitCode, string output) = RunCli("doctor", "--project", project, "--output", "json", "--fail-on", "fail");
        Equal(1, exitCode);
        using (JsonDocument document = JsonDocument.Parse(output))
        {
            JsonElement root = document.RootElement;
            False(root.GetProperty("success").GetBoolean(), "--fail-on fail fails the envelope.");
            Equal(JsonValueKind.Null, root.GetProperty("payload").ValueKind);
            JsonElement fault = root.GetProperty("fault");
            Equal("RAPPCLI1009", fault.GetProperty("code").GetString());
            Equal("fail", fault.GetProperty("details").GetProperty("lock-file").GetString());
            JsonElement[] diagnostics = [.. root.GetProperty("diagnostics").EnumerateArray()];
            True(diagnostics.Any(diagnostic => diagnostic.GetProperty("severity").GetString() == "error" &&
                diagnostic.GetProperty("messageKey").GetString() == "doctor.lock-file.failed"),
                "The failing check is an error diagnostic.");
        }
        Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(output), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult);

        Equal(1, RunCli("doctor", "--project", project).ExitCode);
        Equal(0, RunCli("doctor", "--project", project, "--fail-on", "never").ExitCode);
        Equal(2, RunCli("doctor", "--project", project, "--fail-on", "sometimes").ExitCode);
    }

    private static void NoRestoreRequiresInstalledPackages()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        Write(Path.Combine(configuration.FrontendPackageDirectory, "package.json"), """{"packageManager":"npm@10.0.0"}""");
        try
        {
            DevApplication.RequireInstalledFrontendPackages(configuration);
            throw new InvalidOperationException("A missing node_modules was accepted with --no-restore.");
        }
        catch (DevUsageException error)
        {
            Equal("RAPPDEV1008", error.Code);
            Contains(error.Message, "npm install");
            DoesNotContain(error.Message, workspace.Root);
        }
        Directory.CreateDirectory(Path.Combine(configuration.FrontendPackageDirectory, "node_modules"));
        DevApplication.RequireInstalledFrontendPackages(configuration);
    }

    private static void JsonFaultsOmitProjectPaths()
    {
        foreach (string project in new[] { "/run/user/1000/runic-missing/App.csproj", @"C:\Users\someone\App\App.csproj" })
        {
            (int exitCode, string output) = RunCli("doctor", "--output", "json", "--project", project);
            Equal(2, exitCode);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement fault = document.RootElement.GetProperty("fault");
            Equal("RAPPDEV1002", fault.GetProperty("code").GetString());
            string text = fault.GetRawText();
            DoesNotContain(text, "/run/");
            DoesNotContain(text, "C:");
            DoesNotContain(text, "Users");
            DoesNotContain(text, Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
            Equal("The selected project does not exist.", fault.GetProperty("message").GetString());
        }

        using var workspace = new TestWorkspace();
        string broken = workspace.Write("Broken.csproj", "<Project>not xml");
        (int brokenExit, string brokenOutput) = RunCli("doctor", "--output", "json", "--project", broken);
        Equal(2, brokenExit);
        using JsonDocument brokenDocument = JsonDocument.Parse(brokenOutput);
        JsonElement brokenFault = brokenDocument.RootElement.GetProperty("fault");
        Equal("RAPPDEV1003", brokenFault.GetProperty("code").GetString());
        DoesNotContain(brokenFault.GetRawText(), workspace.Root);
    }

    private static void FaultBackstopDetectsRootedPaths()
    {
        foreach (string message in new[]
        {
            "Project '/run/user/1000/x/App.csproj' does not exist.",
            "/opt/app failed",
            @"Project 'C:\work\App.csproj' does not exist.",
            "Project 'D:/work/App.csproj' does not exist.",
            @"Share \\server\share failed.",
            "Could not evaluate (/var/lib/x).",
        })
        {
            True(Runic.Application.Tool.Program.ContainsRootedPath(message), $"'{message}' contains a rooted path.");
            var outcome = Runic.Application.Tool.Program.Failure<ToolCommandResult>(
                Runic.CommandLine.CommandExitCategory.Usage, "RAPPDEV1002", message);
            Equal("The command could not be completed. See the local detail above.", outcome.Fault!.Message);
            Contains(outcome.HumanOutput ?? string.Empty, message);
        }
        foreach (string message in new[]
        {
            "'dotnet build' exited with code 1.",
            "Use npm/pnpm or Bun.",
            "Timed out waiting for the Vite development server (Vite client: no response).",
            "See https://example.com/docs for details.",
        })
        {
            False(Runic.Application.Tool.Program.ContainsRootedPath(message), $"'{message}' contains no rooted path.");
        }
    }

    private sealed class CapturingConsole : Runic.CommandLine.ICommandConsole
    {
        private readonly System.Text.StringBuilder _output = new();
        internal string Output => _output.ToString();
        public bool IsInteractive => false;
        public bool IsInputRedirected => true;
        public bool IsOutputRedirected => true;
        public bool IsErrorRedirected => true;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            _output.Append(value.Span);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            _output.Append(System.Text.Encoding.UTF8.GetString(value.Span));
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static void EveryOptionIsDescribed()
    {
        Runic.CommandLine.CommandCatalog catalog = Runic.CommandLine.Generated.GeneratedCommandCatalog.Create();
        foreach (Runic.CommandLine.CommandDescriptor command in catalog.Commands)
        {
            True(!string.IsNullOrWhiteSpace(command.Help.Description), $"{command.Name} needs a description.");
            foreach (var option in command.Options)
            {
                True(!string.IsNullOrWhiteSpace(option.Help.Description), $"{command.Name} {option.Name} needs a description.");
                if (option.Name == "--configuration")
                {
                    True(option.Aliases.Contains("-c"), $"{command.Name} --configuration needs the -c alias.");
                }
            }
            foreach (var argument in command.Arguments)
            {
                True(!string.IsNullOrWhiteSpace(argument.Help.Description), $"{command.Name} {argument.Name} needs a description.");
            }
        }
    }

    private static void DoctorGuidesUnrestoredProject()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateDoctorProject(workspace, hostPackage: null, out var authority);
        DoctorReport report = DoctorChecks.InspectAsync(
            project, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
            .GetAwaiter().GetResult();
        True(report.IsHealthy, "A freshly generated project must pass doctor before its first restore.");
        DoctorCheck compatibility = report.Checks.Single(check => check.Name == "compatibility-set");
        Equal(DoctorStatus.Warning, compatibility.Status);
        Contains(compatibility.Remediation ?? string.Empty, "dotnet runic dev");
    }

    private static void DoctorAcceptsDesktopHost()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateDoctorProject(workspace, "Runic.Application.Desktop", out var authority);
        DoctorReport report = DoctorChecks.InspectAsync(
            project, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
            .GetAwaiter().GetResult();
        Equal(DoctorStatus.Pass, report.Checks.Single(check => check.Name == "compatibility-set").Status);
    }

    private static void DevelopmentServersAreInferred()
    {
        using var workspace = new TestWorkspace();
        string react = Path.GetDirectoryName(workspace.Write("React/vite.config.ts", "export default {};"))!;
        workspace.Write("React/src/main.tsx", "export {};");
        var vite = DevProjectConfiguration.InferDevelopmentServer(react, "", false, "", "");
        Equal("vite", vite.Kind);
        True(vite.ViteEnabled, "A Vite configuration enables the Vite development server.");
        Equal("/src/main.tsx", vite.ViteEntry);
        Equal(Path.Combine(react, "vite.config.ts"), vite.ViteConfiguration);

        string angular = Path.GetDirectoryName(workspace.Write("Angular/angular.json", "{}"))!;
        var angularServer = DevProjectConfiguration.InferDevelopmentServer(angular, "", false, "", "");
        Equal("angular", angularServer.Kind);
        False(angularServer.ViteEnabled, "Angular uses its own development server.");

        var explicitEntry = DevProjectConfiguration.InferDevelopmentServer(react, "", true, "/src/app.ts", "");
        Equal("/src/app.ts", explicitEntry.ViteEntry);
        string plain = Path.GetDirectoryName(workspace.Write("Plain/package.json", "{}"))!;
        Equal(string.Empty, DevProjectConfiguration.InferDevelopmentServer(plain, "", false, "", "").Kind);
    }

    private static void DevelopmentServerOwnsFrontendBuild()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        var watching = new DevOptions(null, "Debug", true, true, true, false, []);
        string build = string.Join(" ", DevApplication.CreateBuildArguments(configuration, watching));
        Contains(build, "-property:RunicBridgeBuildFrontend=false");
        Contains(build, "-property:RunicBridgeCopyFrontend=false");
        Contains(string.Join(" ", HostProcessController.CreateWatchArguments(configuration, watching)),
            "--property:RunicBridgeBuildFrontend=false");
        Contains(string.Join(" ", HostProcessController.CreateRestartBuildArguments(configuration, watching)),
            "-property:RunicBridgeCopyFrontend=false");

        var staticFrontend = new DevOptions(null, "Debug", true, false, true, false, []);
        DoesNotContain(string.Join(" ", DevApplication.CreateBuildArguments(configuration, staticFrontend)),
            "RunicBridgeBuildFrontend");
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
