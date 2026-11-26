using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

// devtools-library-dx S5: dev reads the frontend packages that the page depends on, checks them at the start, watches
// them, fails fast with Vite's own error, collapses repeated lines, writes them to the session file, and runs the dev
// server with the workspace's package manager.
internal sealed partial class ToolTests
{
    [Test, DisplayName("the JavaScript workspace root is the nearest folder above that declares a workspace")]
    public void JavaScriptWorkspaceRootIsFound()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("package.json", """{"private":true,"workspaces":["App/Frontend","shared-ui"],"packageManager":"bun@1.4.4"}""");
        workspace.Write("bun.lock", "{}");
        workspace.Write("App/Frontend/package.json", """{"name":"app"}""");
        string frontend = Path.Combine(workspace.Root, "App", "Frontend");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Root));
        Equal(root, JavaScriptWorkspace.FindRoot(frontend));
        // A frontend with a lock file of its own, and RunicApplicationFrontendWorkspaceRoot=none, are their own root.
        Equal(frontend, JavaScriptWorkspace.FindRoot(frontend, "none"));
        workspace.Write("App/Frontend/package-lock.json", "{}");
        Equal(frontend, JavaScriptWorkspace.FindRoot(frontend));
        File.Delete(Path.Combine(frontend, "package-lock.json"));
        // pnpm-workspace.yaml declares one too; outside any workspace the frontend is its own root.
        File.Delete(Path.Combine(workspace.Root, "package.json"));
        Equal(frontend, JavaScriptWorkspace.FindRoot(frontend));
        workspace.Write("pnpm-workspace.yaml", "packages:\n  - App/Frontend\n");
        Equal(root, JavaScriptWorkspace.FindRoot(frontend));
    }

    [Test, DisplayName("dev runs the development server with the workspace's package manager")]
    public void DevServerUsesTheWorkspacePackageManager()
    {
        using var workspace = new TestWorkspace();
        // S3's follow-up: a Bun workspace whose member declares no packageManager ran 'npm run dev'.
        workspace.Write("package.json", """{"private":true,"workspaces":["Frontend"]}""");
        workspace.Write("bun.lock", "{}");
        workspace.Write("Frontend/package.json", """{"name":"app","scripts":{"dev":"vite"}}""");
        string frontend = Path.Combine(workspace.Root, "Frontend");
        DevProjectConfiguration vite = CreateConfiguration(workspace, "vite", frontend) with
        {
            JavaScriptWorkspaceRoot = JavaScriptWorkspace.FindRoot(frontend),
        };
        Equal("bun", vite.PackageManager.Name);
        SequenceEqual(["run", "--bun", "dev", "--host", "127.0.0.1", "--port", "5173", "--strictPort"],
            ViteDevelopmentServer.CreateArguments(vite, 5173));
        // The root's lock file wins over the member's own declaration, as in the build (RUNICBRIDGE089).
        workspace.Write("Frontend/package.json", """{"name":"app","packageManager":"npm@12.2.0"}""");
        Equal("bun", vite.PackageManager.Name);
        // RunicApplicationFrontendPackageManager wins over both.
        Equal("pnpm", (vite with { PackageManagerName = "pnpm" }).PackageManager.Name);
        // Outside a workspace nothing changes: the frontend's own declaration, then its lock file, then npm.
        Equal("npm", CreateConfiguration(workspace, "vite", frontend).PackageManager.Name);
        (JavaScriptPackageManager manager, string source) = JavaScriptPackageManager.ResolveWithSource(workspace.Root, frontend);
        Equal("bun", manager.Name);
        Equal(Path.Combine(workspace.Root, "bun.lock"), source);
    }

    [Test, DisplayName("dev takes RunicApplicationFrontendPackageManager only when the project sets it")]
    public void ExplicitPackageManagerIsTheProjectsOwn()
    {
        // Targets of protocol 1 say whether the project set it; older restored targets inferred it per folder.
        Equal("bun", DevProjectConfiguration.ExplicitPackageManager(1, "bun", "true", restored: true));
        Equal("", DevProjectConfiguration.ExplicitPackageManager(1, "npm", "", restored: true));
        Equal("", DevProjectConfiguration.ExplicitPackageManager(0, "npm", "", restored: true));
        // Before the restore the targets are not imported, so the value is the project's.
        Equal("pnpm", DevProjectConfiguration.ExplicitPackageManager(0, " pnpm ", "", restored: false));
    }

    private const string DependenciesResult = """
        {
          "TargetResults": {
            "RunicViewsGetFrontendDependencies": {
              "Result": "Success",
              "Items": [
                { "Identity": "/repo/shared-ui", "PackageName": "@acme/shared-ui", "Kind": "compiled", "Owner": "/repo/SharedUi/SharedUi.csproj",
                  "OwnerName": "SharedUi", "BuildEnabled": "auto", "BuildCommand": "bun run --bun build", "WatchCommand": "",
                  "Specifier": "@acme/shared-ui/generated" },
                { "Identity": "/repo/tokens", "PackageName": "@acme/tokens", "Kind": "source", "BuildEnabled": "", "BuildCommand": "", "WatchCommand": "" },
                { "Identity": "/repo/App/Frontend", "PackageName": "app", "Kind": "application", "Owner": "/repo/App/App.csproj" }
              ]
            }
          }
        }
        """;

    private const string DependenciesGraph = """
        { "schema": "rax.frontend-graph/1", "packages": [
          { "directory": "/repo/shared-ui", "package": "@acme/shared-ui", "kind": "compiled", "exports": ["./dist/index.js", "./dist/generated/runic-clients.js"], "outputs": ["./dist"] },
          { "directory": "/repo/tokens", "package": "@acme/tokens", "kind": "source", "exports": ["./src/index.ts"], "outputs": [] } ] }
        """;

    [Test, DisplayName("dev reads the frontend dependencies from MSBuild and the frontend graph")]
    public void FrontendDependenciesAreParsed()
    {
        IReadOnlyList<FrontendDependency> dependencies = FrontendDependencies.Parse(DependenciesResult, DependenciesGraph);
        Equal(2, dependencies.Count);
        FrontendDependency shared = dependencies[0];
        Equal(Path.GetFullPath("/repo/shared-ui"), shared.Directory);
        Equal("@acme/shared-ui", shared.PackageName);
        Equal("compiled", shared.Kind);
        Equal("SharedUi", shared.OwnerName);
        Equal("@acme/shared-ui/generated", shared.Specifier);
        Equal("shared-ui", shared.Label);
        True(shared.IsBuilt, "A compiled package with a build command is built.");
        SequenceEqual(["./dist/index.js", "./dist/generated/runic-clients.js"], shared.Exports);
        SequenceEqual(["./dist"], shared.Outputs);
        Equal("the frontend package of SharedUi", shared.Description);
        FrontendDependency tokens = dependencies[1];
        Equal("source", tokens.Kind);
        Equal<string?>(null, tokens.Owner);
        False(tokens.IsBuilt, "A source package is not built.");
        Equal("a workspace package that no .NET project owns", tokens.Description);
        // A build that is off is not watched; dotnet msbuild's banner before the JSON is skipped.
        string off = DependenciesResult.Replace("\"BuildEnabled\": \"auto\"", "\"BuildEnabled\": \"false\"");
        False(FrontendDependencies.Parse("MSBuild version 18\n" + off, null)[0].IsBuilt, "A package whose build is off is not built.");
        Equal(0, FrontendDependencies.Parse("{}", null).Count);
        SequenceEqual(["msbuild", "/repo/App/App.csproj", "-nologo", "-property:Configuration=Debug", "-getTargetResult:RunicViewsGetFrontendDependencies"],
            FrontendDependencies.CreateQueryArguments(CreateRepoConfiguration(), "Debug"));
    }

    private static DevProjectConfiguration CreateRepoConfiguration() => new(
        "/repo/App/App.csproj", "/repo/App", NodeEnabled: true, FrontendCompilerEnabled: false,
        WorkspaceRoot: "/repo/App/Frontend", Workspace: ".", FrontendPackageDirectory: "/repo/App/Frontend",
        FrontendOutputDirectory: "/repo/App/Frontend/dist", FrontendWebRoot: "www", FrontendWatchTarget: string.Empty,
        ViteDevServerEnabled: true, ViteDevServerEntry: "/src/main.ts", ViteConfigurationPath: string.Empty,
        FrontendCompilerDiagnosticsPath: string.Empty, FrontendCompilerHotReloadPath: string.Empty, TargetDirectory: "/repo/App/bin");

    private static FrontendDependency CompiledPackage(string directory, string buildCommand = "bun run --bun build", bool buildEnabled = true) =>
        new(directory, "@acme/shared-ui", "compiled", Path.Combine(directory, "..", "SharedUi", "SharedUi.csproj"), "SharedUi", buildEnabled,
            buildCommand, string.Empty)
        {
            Exports = ["./dist/index.js"],
            Outputs = ["./dist"],
        };

    [Test, DisplayName("dev stops at the start when a compiled dependency package has not been built (RAPPDEV1018)")]
    public void UnbuiltDependencyStopsDev()
    {
        using var workspace = new TestWorkspace();
        string directory = Path.Combine(workspace.Root, "shared-ui");
        FrontendDependency package = CompiledPackage(directory);
        DevDevelopmentException error = Throws<DevDevelopmentException>(() => FrontendDependencies.RequireBuilt([package], built: true));
        Equal("RAPPDEV1018", error.Code);
        Contains(error.Message, "@acme/shared-ui is the frontend package of SharedUi, and the page imports it, but it has not been built: shared-ui/dist/index.js is missing");
        Contains(error.Message, "its build 'bun run --bun build' did not write it");
        Contains(Throws<DevDevelopmentException>(() => FrontendDependencies.RequireBuilt([package], built: false)).Message, "--no-build ran no build");
        Contains(Throws<DevDevelopmentException>(() => FrontendDependencies.RequireBuilt([CompiledPackage(directory, buildEnabled: false)], built: true)).Message,
            "its package build is off in SharedUi (RunicApplicationFrontendBuildEnabled=false)");
        workspace.Write("shared-ui/dist/index.js", "export {};");
        FrontendDependencies.RequireBuilt([package], built: true);
        Equal(DependencyState.Built, DependencyState.Initial(package, watching: true).State);
        Equal("rebuild", DependencyState.Initial(package, watching: true).Watch);
        Equal("script", DependencyState.Initial(package with { WatchCommand = "bun run --bun watch" }, watching: true).Watch);
        Equal(DependencyState.NotBuilt, DependencyState.Initial(CompiledPackage(directory, buildEnabled: false), watching: true).State);
    }

    [Test, DisplayName("a failed initial build in a dependency package is RAPPDEV1018, other build failures stay RAPPDEV1006's")]
    public void DependencyBuildFailuresAreRecognized()
    {
        string app = Path.GetFullPath("/repo/App/App.csproj");
        string frontend = Path.GetFullPath("/repo/App/Frontend");
        string Find(string output) => FrontendDependencies.FindPackageFailure(output, app, frontend)!;
        string sharedUi = Path.GetFullPath("/repo/shared-ui/src/index.ts");
        // The targets report a library package build's located errors with the library's project, after the tool's own line.
        string library = $"""
              src/index.ts(3,1): error TS1005: ';' expected.
              npm error code 2
            {sharedUi}(3,1): error TS1005: ';' expected. [{Path.GetFullPath("/repo/SharedUi/SharedUi.csproj")}]
            """;
        Equal($"{sharedUi}(3,1): error TS1005: ';' expected.", Find(library));
        Equal("RUNICBRIDGE016: The frontend build 'bun run --bun build' in /repo/shared-ui exited with code 2. Its output is above.",
            Find("/repo/SharedUi/SharedUi.csproj : error RUNICBRIDGE016: The frontend build 'bun run --bun build' in /repo/shared-ui exited with code 2. Its output is above. [/repo/App/App.csproj]"));
        // A workspace package that no project owns is built by the application, and RUNICBRIDGE088 is a dependency's.
        Contains(Find($"{app} : error RUNICBRIDGE016: The build 'npm run build' of the frontend package @acme/tokens in /repo/tokens, which /repo/App/Frontend depends on, exited with code 1. Its output is above."),
            "of the frontend package @acme/tokens");
        Contains(Find($"  1>{app} : error RUNICBRIDGE088: /repo/App/Frontend depends on @acme/shared-ui"), "RUNICBRIDGE088");
        // The served project's own install, its own frontend sources, and C# errors are not.
        Equal<string?>(null, FrontendDependencies.FindPackageFailure(
            $"{app} : error RUNICBRIDGE016: The frontend install 'bun install' in /repo exited with code 1.", app, frontend));
        Equal<string?>(null, FrontendDependencies.FindPackageFailure(
            $"{Path.Combine(frontend, "src", "main.ts")}(1,1): error TS2344: x [{app}]", app, frontend));
        Equal<string?>(null, FrontendDependencies.FindPackageFailure(
            $"{Path.GetFullPath("/repo/SharedUi/About.cs")}(3,1): error CS1002: ; expected [{Path.GetFullPath("/repo/SharedUi/SharedUi.csproj")}]", app, frontend));
    }

    [Test, DisplayName("a Vite error that names a dependency package explains its state")]
    public void ViteErrorsNameTheDependencyPackage()
    {
        using var workspace = new TestWorkspace();
        FrontendDependency package = CompiledPackage(Path.Combine(workspace.Root, "shared-ui")) with { Specifier = "@acme/shared-ui/generated" };
        string error = "Failed to resolve import \"@acme/shared-ui\" from \"src/main.ts\". Does the file exist?";
        Equal("@acme/shared-ui is the frontend package of SharedUi: it has not been built (shared-ui/dist/index.js is missing).",
            FrontendDependencies.Explain([package], error));
        Equal(package, FrontendDependencies.FindImported([package], "Failed to resolve import \"@acme/shared-ui/generated/mocks\" from \"src/x.ts\""));
        Equal<string?>(null, FrontendDependencies.Explain([package], "Failed to resolve import \"@acme/other\" from \"src/main.ts\""));
        Equal<string?>(null, FrontendDependencies.Explain([package], "Transform failed with 1 error"));
    }

    [Test, DisplayName("repeated output lines are printed once, then counted")]
    public void RepeatedLinesAreCollapsed()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var printed = new List<string>();
        using (var filter = new RepeatedLineFilter("vite", printed.Add, time, flushPeriodically: false))
        {
            var shown = new List<string>();
            // F6: one unresolved import, 1,107 times in 30 s, each with Vite's clock time and colours.
            for (int index = 0; index < 1_107; index++)
            {
                string line = $"\u001b[2m10:42:{index % 60:00} AM\u001b[22m \u001b[31m[vite] Internal server error: Failed to resolve import \"@acme/shared-ui\"\u001b[39m";
                if (!filter.Suppress(line)) shown.Add(line);
                if (index % 100 == 0) time.Advance(TimeSpan.FromMilliseconds(500));
            }
            Equal(1, shown.Count);
            True(!filter.Suppress("[vite] hmr update /src/App.svelte"), "Another line is printed.");
            filter.Flush();
            Equal(1, printed.Count);
            Equal("[vite] (repeated 1,106 more times) [vite] Internal server error: Failed to resolve import \"@acme/shared-ui\"", printed[0]);
            // After the window the line is printed again; blank lines are never held.
            time.Advance(RepeatedLineFilter.Window);
            False(filter.Suppress("10:43:00 AM [vite] Internal server error: Failed to resolve import \"@acme/shared-ui\""), "The line returns after the window.");
            False(filter.Suppress(""), "Blank lines pass.");
            False(filter.Suppress(""), "Blank lines pass again.");
            filter.Flush();
            Equal(1, printed.Count);
            False(filter.Suppress("[vite] hmr update /src/App.svelte"), "The other line's window has passed too.");
            True(filter.Suppress("[vite] hmr update /src/App.svelte"), "The other line is held now.");
        }
        Equal("[vite] (repeated once more) [vite] hmr update /src/App.svelte", printed[^1]);
        var errors = new FirstErrorLine();
        errors.Observe("  VITE v8.3.4  ready in 256 ms");
        errors.Observe("\u001b[31m10:42:07 AM [vite] Internal server error: Failed to resolve import \"x\"\u001b[39m");
        errors.Observe("error RUNICP008: later");
        Equal("[vite] Internal server error: Failed to resolve import \"x\"", errors.Line);
    }

    // Vite's error middleware answers a module request it cannot transform with HTTP 500 and an overlay page.
    private const string ViteErrorPage = """

                <!DOCTYPE html>
                <html lang="en">
                  <head>
                    <meta charset="UTF-8" />
                    <title>Error</title>
                    <script type="module">
                      const error = {"message":"Failed to resolve import \"@acme/shared-ui\" from \"src/main.ts\". Does the file exist?","stack":"  at x","id":"/repo/App/Frontend/src/main.ts","frame":"1 | import \"@acme/shared-ui\";\n  |        ^","plugin":"vite:import-analysis","loc":{"file":"/repo/App/Frontend/src/main.ts","line":1,"column":7}}
                      try {
        """;

    [Test, DisplayName("Vite readiness fails fast with Vite's error when the entry answers HTTP 500 twice")]
    public void ViteReadinessFailsFastOnServerErrors() => VerifyViteFailFastAsync().GetAwaiter().GetResult();

    private static async Task VerifyViteFailFastAsync()
    {
        Equal("[vite:import-analysis] Failed to resolve import \"@acme/shared-ui\" from \"src/main.ts\". Does the file exist? (in /repo/App/Frontend/src/main.ts)",
            ViteDevelopmentServer.ReadErrorPage(ViteErrorPage));
        Equal<string?>(null, ViteDevelopmentServer.ReadErrorPage("<html>not Vite's</html>"));

        var origin = new Uri("http://127.0.0.1:12345/");
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new EntryErrorHandler(ViteErrorPage);
        using var client = new HttpClient(handler);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        DevDevelopmentException error = await ThrowsDevAsync(() => ViteDevelopmentServer.WaitUntilReadyAsync(
            origin, "src/main.ts", running.Task, client, TimeSpan.FromSeconds(30), CancellationToken.None,
            explain: message => message.Contains("@acme/shared-ui", StringComparison.Ordinal) ? "@acme/shared-ui is the frontend package of SharedUi." : null));
        True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Vite's error stops the wait at once, not after 30 s ({timer.Elapsed}).");
        Equal("RAPPDEV1007", error.Code);
        Equal(2, handler.EntryRequests);
        Contains(error.Message, "cannot serve the application entry '/src/main.ts' (HTTP 500): [vite:import-analysis] Failed to resolve import \"@acme/shared-ui\"");
        Contains(error.Message, "@acme/shared-ui is the frontend package of SharedUi.");

        // An error page that is not Vite's: the first error line of its output stands for it.
        using var plain = new HttpClient(new EntryErrorHandler("<html></html>"));
        error = await ThrowsDevAsync(() => ViteDevelopmentServer.WaitUntilReadyAsync(
            origin, "src/main.ts", running.Task, plain, TimeSpan.FromSeconds(30), CancellationToken.None,
            outputError: () => "[vite] Internal server error: Transform failed"));
        Contains(error.Message, "(HTTP 500): [vite] Internal server error: Transform failed");

        // A server that exits reports the first error line of its output with the exit code.
        error = await ThrowsDevAsync(() => ViteDevelopmentServer.WaitUntilReadyAsync(
            origin, "src/main.ts", Task.FromResult(1), plain, TimeSpan.FromSeconds(30), CancellationToken.None,
            outputError: () => "error when starting dev server: Error: Cannot find module 'vite'"));
        Contains(error.Message, "exited before readiness with code 1: error when starting dev server: Error: Cannot find module 'vite'");
    }

    private static async Task<DevDevelopmentException> ThrowsDevAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (DevDevelopmentException error)
        {
            return error;
        }
        throw new InvalidOperationException("Expected a DevDevelopmentException.");
    }

    private sealed class EntryErrorHandler(string body) : HttpMessageHandler
    {
        internal int EntryRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/@vite/client", StringComparison.Ordinal)) return Task.FromResult(Module());
            EntryRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
            });
        }
    }

    [Test, DisplayName("the session file lists the dependency packages and their state")]
    public void SessionFileListsDependencies()
    {
        using var workspace = new TestWorkspace();
        string directory = Path.Combine(workspace.Root, "shared-ui");
        FrontendDependency package = CompiledPackage(directory) with { Specifier = "@acme/shared-ui/generated" };
        DevProjectConfiguration configuration = CreateConfiguration(workspace, "vite") with { FrontendDependencies = [package] };
        var session = new DevSession(configuration, new DevOptions(null, "Debug", true, true, true, false, []), [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using (JsonDocument empty = JsonDocument.Parse(session.Render()))
        {
            Equal("runic.application.tool.dev-session/1", empty.RootElement.GetProperty("schema").GetString());
            Equal(0, empty.RootElement.GetProperty("dependencies").GetArrayLength());
        }
        session.Dependencies([package], dependency => DependencyState.Initial(dependency, watching: true));
        session.DependencyChanged(package, new DependencyState(DependencyState.Failed, "rebuild", "The build 'bun run --bun build' of @acme/shared-ui exited with code 2."));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(session.Path));
        JsonElement entry = document.RootElement.GetProperty("dependencies")[0];
        Equal("@acme/shared-ui", entry.GetProperty("package").GetString());
        Equal(directory, entry.GetProperty("directory").GetString());
        Equal("compiled", entry.GetProperty("kind").GetString());
        Equal("SharedUi", entry.GetProperty("ownerName").GetString());
        Equal("@acme/shared-ui/generated", entry.GetProperty("specifier").GetString());
        Equal("bun run --bun build", entry.GetProperty("buildCommand").GetString());
        Equal(JsonValueKind.Null, entry.GetProperty("watchCommand").ValueKind);
        Equal(Path.Combine(directory, "dist", "index.js"), entry.GetProperty("exports")[0].GetString());
        Equal("failed", entry.GetProperty("state").GetString());
        Equal("rebuild", entry.GetProperty("watch").GetString());
        Contains(entry.GetProperty("message").GetString()!, "exited with code 2");
    }

    [Test, DisplayName("package commands run without a shell unless they need one")]
    public void PackageCommandsAvoidTheShell()
    {
        (string executable, IReadOnlyList<string> arguments) = ShellCommand.Parse(" bun run --bun build ");
        Equal("bun", executable);
        SequenceEqual(["run", "--bun", "build"], arguments);
        (executable, arguments) = ShellCommand.Parse("tsc -p tsconfig.build.json && cp a b");
        if (OperatingSystem.IsWindows())
        {
            Equal("cmd.exe", executable);
            SequenceEqual(["/d", "/c", "tsc -p tsconfig.build.json && cp a b"], arguments);
        }
        else
        {
            Equal("/bin/sh", executable);
            SequenceEqual(["-c", "tsc -p tsconfig.build.json && cp a b"], arguments);
        }
    }

    [Test, DisplayName("a dependency package's fingerprint ignores its outputs, node_modules and build info")]
    public void DependencyFingerprintIgnoresOutputs()
    {
        using var workspace = new TestWorkspace();
        string directory = Path.Combine(workspace.Root, "shared-ui");
        workspace.Write("shared-ui/src/index.ts", "export const a = 1;");
        FrontendDependency package = CompiledPackage(directory);
        string before = DependencyWatchers.Fingerprint(package);
        workspace.Write("shared-ui/dist/index.js", "export const a = 1;");
        workspace.Write("shared-ui/node_modules/x/index.js", "x");
        workspace.Write("shared-ui/.cache/x", "x");
        workspace.Write("shared-ui/tsconfig.build.tsbuildinfo", "{}");
        Equal(before, DependencyWatchers.Fingerprint(package));
        workspace.Write("shared-ui/src/other.ts", "export const b = 2;");
        True(before != DependencyWatchers.Fingerprint(package), "A new source file changes the fingerprint.");
    }

    [Test, DisplayName("dev rebuilds a changed dependency package and keeps the last good build when the rebuild fails")]
    public void DependencyRebuildsKeepTheLastGoodBuild() => VerifyDependencyRebuildsAsync().GetAwaiter().GetResult();

    private static async Task VerifyDependencyRebuildsAsync()
    {
        using var workspace = new TestWorkspace();
        string directory = Path.Combine(workspace.Root, "shared-ui");
        workspace.Write("shared-ui/src/index.ts", "export const version = 1;");
        // The package's build copies its source to dist/, and with a 'fail' file writes a broken output and exits with 2.
        string build = WritePackageBuild(directory);
        FrontendDependency package = CompiledPackage(directory, build);
        workspace.Write("shared-ui/dist/index.js", "export const version = 1;");
        DevProjectConfiguration configuration = CreateConfiguration(workspace, "vite") with { FrontendDependencies = [package] };
        var states = new List<DependencyState>();
        string Output() => File.ReadAllText(Path.Combine(directory, "dist", "index.js")).Trim();
        async Task WaitForAsync(Func<bool> condition, string what)
        {
            for (var timer = System.Diagnostics.Stopwatch.StartNew(); !condition(); await Task.Delay(50))
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new InvalidOperationException($"Timed out waiting for {what}; states: {string.Join(", ", Snapshot(states).Select(static state => state.State))}.");
        }
        await using (DependencyWatchers watchers = DependencyWatchers.Start(configuration, (_, state) => { lock (states) states.Add(state); }, CancellationToken.None))
        {
            await Task.Delay(DependencyWatchers.PollInterval * 2);
            workspace.Write("shared-ui/src/index.ts", "export const version = 2;");
            await WaitForAsync(() => Snapshot(states).Any(static state => state.State == DependencyState.Built), "the rebuild");
            Equal("export const version = 2;", Output());

            workspace.Write("shared-ui/fail", "");
            workspace.Write("shared-ui/src/index.ts", "export const version = ;");
            await WaitForAsync(() => Snapshot(states).Any(static state => state.State == DependencyState.Failed), "the failed rebuild");
            DependencyState failed = Snapshot(states).Last(static state => state.State == DependencyState.Failed);
            Contains(failed.Message!, "exited with code 2");
            // The build wrote a broken output; dev put the last good one back.
            Equal("export const version = 2;", Output());

            File.Delete(Path.Combine(directory, "fail"));
            int count = Snapshot(states).Count;
            workspace.Write("shared-ui/src/index.ts", "export const version = 3;");
            await WaitForAsync(() => Snapshot(states).Skip(count).Any(static state => state.State == DependencyState.Built), "the repaired rebuild");
            Equal("export const version = 3;", Output());
        }
        // The backups are gone once a build succeeded.
        string backups = Path.Combine(configuration.SessionDirectory, "dependency-builds");
        True(!Directory.Exists(backups) || !Directory.EnumerateFileSystemEntries(backups).Any(), "No backup is left.");
    }

    [Test, DisplayName("dev's package rebuild takes the build's lock of the package folder")]
    public void PackageRebuildsShareTheBuildLock() => VerifyPackageBuildLockAsync().GetAwaiter().GetResult();

    private static async Task VerifyPackageBuildLockAsync()
    {
        using var workspace = new TestWorkspace();
        string directory = Path.Combine(workspace.Root, "shared-ui");
        // The targets' RunicViewsInstallFrontendOnce names it the same way: runic-install-<24 hex digits>.lock in the temporary folder.
        string path = PackageBuildLock.PathFor(directory);
        Equal(Path.GetTempPath(), Path.GetDirectoryName(path) + Path.DirectorySeparatorChar);
        True(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), "^runic-install-[0-9a-f]{24}\\.lock$"), path);
        Equal(path, PackageBuildLock.PathFor(directory + Path.DirectorySeparatorChar + "."));
        FileStream? held = await PackageBuildLock.AcquireAsync(directory, CancellationToken.None);
        True(held is not null, "The lock is free.");
        Task<FileStream?> waiting;
        using (held)
        {
            waiting = PackageBuildLock.AcquireAsync(directory, CancellationToken.None);
            await Task.Delay(500);
            False(waiting.IsCompleted, "A second holder waits.");
        }
        using FileStream? second = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        True(second is not null, "The waiting holder gets the lock once it is free.");
    }

    private static List<DependencyState> Snapshot(List<DependencyState> states)
    {
        lock (states) return [.. states];
    }

    /// <summary>A package build script: copies src/index.ts to dist/index.js, or with a 'fail' file breaks dist/ and exits with 2.</summary>
    private static string WritePackageBuild(string directory)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            string batch = Path.Combine(directory, "build.cmd");
            File.WriteAllText(batch,
                "@echo off\r\n" +
                "if exist fail (\r\n  echo src/index.ts: error TS1109: Expression expected.\r\n  if exist dist rmdir /s /q dist\r\n  mkdir dist\r\n  echo broken> dist\\index.js\r\n  exit /b 2\r\n)\r\n" +
                "if not exist dist mkdir dist\r\ncopy /y src\\index.ts dist\\index.js >nul\r\n");
            return batch;
        }
        string script = Path.Combine(directory, "build.sh");
        File.WriteAllText(script,
            "#!/bin/sh\n" +
            "if [ -f fail ]; then echo 'src/index.ts: error TS1109: Expression expected.'; rm -rf dist; mkdir dist; echo broken > dist/index.js; exit 2; fi\n" +
            "mkdir -p dist && cp src/index.ts dist/index.js\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }
}
