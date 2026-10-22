using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool;
using TUnit.Core;

namespace Runic.Application.Tool.Tests;

internal sealed class ToolTests
{
    [Test, DisplayName("project discovery accepts a directory")]
    public void ProjectDiscoveryAcceptsDirectory()
    {
        using var workspace = new TestWorkspace();
        string expected = workspace.Write("Application.csproj", "<Project />");
        Equal(expected, ProjectDiscovery.Find(workspace.Root, workspace.Root));
    }

    [Test, DisplayName("project discovery rejects ambiguity")]
    public void ProjectDiscoveryRejectsAmbiguity()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("One.csproj", "<Project />");
        workspace.Write("Two.csproj", "<Project />");
        Throws<DevUsageException>(() => ProjectDiscovery.Find(workspace.Root, null));
    }

    [Test, DisplayName("commands keep arguments shell-free")]
    public void CommandsKeepArgumentsShellFree()
    {
        ProcessStartInfo startInfo = CommandRunner.CreateStartInfo(
            "dotnet", Environment.CurrentDirectory,
            ["build", "a project.csproj", "-p:Value=$(not-a-shell)"]);
        Equal(3, startInfo.ArgumentList.Count);
        Equal("a project.csproj", startInfo.ArgumentList[1]);
        Equal("-p:Value=$(not-a-shell)", startInfo.ArgumentList[2]);
        False(startInfo.UseShellExecute, "Commands unexpectedly use a shell.");
    }

    [Test, DisplayName("commands end input and tolerate invalid UTF-8")]
    public void CommandsEndInputAndTolerateInvalidUtf8()
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

    [Test, DisplayName("package managers use locked, portable commands")]
    public void PackageManagersUseLockedCommands()
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

    [Test, DisplayName("MSBuild evaluation requires the Views Window opt-in")]
    public void EvaluationRequiresViewsWindow()
    {
        IReadOnlyList<string> arguments = DevProjectConfiguration.CreateEvaluationArguments("App.csproj", "Debug");
        string properties = string.Join(" ", arguments);
        Contains(properties, "RunicApplicationFrontendWindowProject");
        Contains(properties, "RunicApplicationFrontendDirectory");
    }

    [Test, DisplayName("dev reads only the current property names and the build's output directory and web root")]
    public void EvaluationReadsCurrentPropertiesOnly()
    {
        // W319-003 D15: the 0.7.0-preview.6 names fail the build with RUNICBRIDGE017; dev does not read them either.
        using var workspace = new TestWorkspace();
        string former = workspace.Write("Former/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RunicViewsWindowProject>true</RunicViewsWindowProject>
                <RunicBridgeFrontendDir>$(MSBuildProjectDirectory)/Client</RunicBridgeFrontendDir>
              </PropertyGroup>
            </Project>
            """);
        workspace.Write("Former/Client/package.json", """{"packageManager":"npm@10.0.0"}""");
        var rejected = Throws<DevUsageException>(() => DevProjectConfiguration.EvaluateAsync(
            "dotnet", former, "Debug", CancellationToken.None).GetAwaiter().GetResult());
        Equal("RAPPDEV1005", rejected.Code);
        string arguments = string.Join(" ", DevProjectConfiguration.CreateEvaluationArguments(former, "Debug"));
        foreach (string removed in new[] { "RunicViewsWindowProject", "RunicBridgeFrontendDir", "RunicAssetsFrontendDirectory", "RunicAssetsDist",
            "RunicApplicationFrontendPackageDirectory", "RunicApplicationFrontendViteDevServerEnabled", ",RunicApplicationFrontendCompiler" })
        {
            DoesNotContain(arguments, removed);
        }

        // D16: the output directory is relative to the frontend directory and the web root to the build output, as
        // the Views targets define them for build and publish.
        string current = workspace.Write("Current/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>
                <RunicApplicationFrontendDirectory>Client</RunicApplicationFrontendDirectory>
                <RunicApplicationFrontendOutputDirectory>build</RunicApplicationFrontendOutputDirectory>
                <RunicApplicationFrontendWebRoot>wwwroot</RunicApplicationFrontendWebRoot>
                <!-- What the CS-WebUI host's build targets add once the project is restored. -->
                <_RunicApplicationHostNames>;cswebui</_RunicApplicationHostNames>
              </PropertyGroup>
            </Project>
            """);
        workspace.Write("Current/Client/package.json", """{"packageManager":"npm@10.0.0"}""");
        DevProjectConfiguration configuration = DevProjectConfiguration.EvaluateAsync(
            "dotnet", current, "Debug", CancellationToken.None).GetAwaiter().GetResult();
        // A temporary directory can resolve through a symbolic link (macOS /var), so compare the folders.
        Equal("Client", Path.GetFileName(configuration.WorkspaceRoot));
        Equal(configuration.WorkspaceRoot, configuration.FrontendPackageDirectory);
        Equal(Path.Combine(configuration.WorkspaceRoot, "build"), configuration.FrontendOutputDirectory);
        Equal("wwwroot", Path.GetFileName(configuration.RuntimeWebRoot));
        // The Views host decides whether the frontend output is served live.
        SequenceEqual(["cswebui"], configuration.ApplicationHosts);
        True(configuration.RunsOnCsWebUiOnly, "A CS-WebUI project was taken for a Runic Desktop one.");
    }

    [Test, DisplayName("Views Window watch restarts the native process")]
    public void ViewsWindowWatchRestartsNativeProcess()
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

        // Unset: the Window runs as a development host.
        IReadOnlyDictionary<string, string?> unset = HostProcessController.CreateDevelopmentEnvironment(
            configuration, new Dictionary<string, string?>(), _ => null);
        Equal("Development", unset["DOTNET_ENVIRONMENT"]);
        // dotnet watch gives the Window the same grace before a restart; a developer's value wins.
        Equal("5000", unset[HostProcessController.WatchCleanupTimeoutVariable]);
        Equal(false, HostProcessController.CreateDevelopmentEnvironment(configuration, new Dictionary<string, string?>(),
            name => name == HostProcessController.WatchCleanupTimeoutVariable ? "100" : null)
            .ContainsKey(HostProcessController.WatchCleanupTimeoutVariable));
        // A developer's environment wins: the child inherits it unchanged.
        foreach (string chosen in new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" })
        {
            IReadOnlyDictionary<string, string?> kept = HostProcessController.CreateDevelopmentEnvironment(
                configuration, new Dictionary<string, string?>(), name => name == chosen ? "Staging" : null);
            Equal(false, kept.ContainsKey("DOTNET_ENVIRONMENT"));
        }
    }

    [Test, DisplayName("Vite and Angular bind their development servers to loopback")]
    public void DevelopmentServersBindLoopback()
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

    [Test, DisplayName("Vite readiness reports useful failures and honors cancellation")]
    public void ViteReadinessIsActionable() => VerifyViteReadinessAsync().GetAwaiter().GetResult();

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

    [Test, DisplayName("dev server connection resets are retried and an unserved page is named")]
    public void DevServerConnectionFailuresAreReported() => VerifyDevServerConnectionFailuresAsync().GetAwaiter().GetResult();

    private static async Task VerifyDevServerConnectionFailuresAsync()
    {
        var origin = new Uri("http://127.0.0.1:12345/");
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Vite and the Angular CLI listen on the port briefly before they bind it. A
        // connection reset there reaches HttpClient's caller as a bare SocketException.
        static HttpStatusCode Reset() => throw new SocketException((int)SocketError.NotConnected);
        var resetThenReady = new ScriptedHandler(Reset, () => HttpStatusCode.OK);
        using (var client = new HttpClient(resetThenReady))
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(
                origin, "src/main.ts", running.Task, client, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        Equal(3, resetThenReady.Requests);

        foreach ((HttpMessageHandler handler, string cause) in new (HttpMessageHandler, string)[]
        {
            (new ScriptedHandler(() => HttpStatusCode.NotFound), "404"),
            (new ScriptedHandler(Reset), "not connected"),
        })
        {
            using var client = new HttpClient(handler);
            try
            {
                await FrontendDevelopmentDocument.ReadAsync(client, origin, "settings.html", "Vite", CancellationToken.None);
                throw new InvalidOperationException("A page the server did not serve was accepted.");
            }
            catch (DevDevelopmentException error)
            {
                Equal("RAPPDEV1007", error.Code);
                Contains(error.Message, "'settings.html'");
                Contains(error.Message, cause);
            }
        }
    }

    private sealed class ScriptedHandler(params Func<HttpStatusCode>[] steps) : HttpMessageHandler
    {
        internal int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(steps[Math.Min(Requests++, steps.Length - 1)]()));
    }

    [Test, DisplayName("dev server bind failures are recognized from the server output")]
    public void PortInUseMessagesAreRecognized()
    {
        True(LoopbackPort.IsInUseMessage("Error: Port 5173 is already in use"), "Vite's message was not recognized.");
        True(LoopbackPort.IsInUseMessage("An unhandled exception occurred: Port 4200 is already in use. Use '--port' to specify a different port."),
            "Angular's message was not recognized.");
        True(LoopbackPort.IsInUseMessage("Error: listen EADDRINUSE: address already in use 127.0.0.1:4200"), "Node's message was not recognized.");
        False(LoopbackPort.IsInUseMessage("Local: http://127.0.0.1:5173/"), "A ready message was taken for a bind failure.");
        False(LoopbackPort.IsInUseMessage("error TS2307: Cannot find module"), "A compile error was taken for a bind failure.");
    }

    [Test, DisplayName("a dev server that lost its port race is started again on another port")]
    public void LostPortRaceStartsOnAnotherPort() => VerifyLostPortRaceAsync().GetAwaiter().GetResult();

    private static async Task VerifyLostPortRaceAsync()
    {
        int[] reserved = [41001, 41002, 41003, 41004];
        int next = 0;
        int ReservePort() => reserved[next++];

        // The first two picks are taken before the server binds them; the third works.
        var tried = new List<int>();
        string started = await LoopbackPort.StartAsync("test server", port =>
        {
            tried.Add(port);
            return tried.Count < 3 ? throw new LoopbackPort.PortInUseException() : Task.FromResult($"port {port}");
        }, ReservePort);
        Equal("port 41003", started);
        SequenceEqual([41001, 41002, 41003], tried);

        // A port that keeps being taken fails with a diagnosis after the bounded number of attempts.
        next = 0;
        tried.Clear();
        try
        {
            await LoopbackPort.StartAsync<string>("test server", port =>
            {
                tried.Add(port);
                throw new LoopbackPort.PortInUseException();
            }, ReservePort);
            throw new InvalidOperationException("An endless port conflict was accepted.");
        }
        catch (DevDevelopmentException error)
        {
            Equal("RAPPDEV1007", error.Code);
            Contains(error.Message, "test server");
            Equal(LoopbackPort.StartAttempts, tried.Count);
        }

        // Any other failure is reported at once instead of being retried.
        next = 0;
        tried.Clear();
        try
        {
            await LoopbackPort.StartAsync<string>("test server", port =>
            {
                tried.Add(port);
                throw new DevDevelopmentException("RAPPDEV1007", "The server crashed.");
            }, ReservePort);
            throw new InvalidOperationException("A crash was accepted.");
        }
        catch (DevDevelopmentException error)
        {
            Equal("The server crashed.", error.Message);
            Equal(1, tried.Count);
        }
    }

    [Test, DisplayName("an exited dev server is blamed on its port only when it reported the conflict")]
    public void ExitedServerIsBlamedOnItsPort() => VerifyPortConflictWatchAsync().GetAwaiter().GetResult();

    private static async Task VerifyPortConflictWatchAsync()
    {
        using var workspace = new TestWorkspace();
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        var conflict = new PortConflictWatch();
        RunningProcess lost = RunningProcess.Start(
            "port-test", executable, workspace.Root,
            [.. prefix, StopTestChild.Argument, StopTestChild.PortInUseRole, workspace.Root],
            observeLine: conflict.Observe);
        Equal(1, await lost.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        True(await conflict.ExplainsExitAsync(lost), "The reported port conflict was not detected.");
        await lost.DisposeAsync();

        // The "run" role stays alive, so an unfinished process is never blamed on its port.
        var quiet = new PortConflictWatch();
        RunningProcess running = StartStopChild("run", workspace.Root);
        WaitForStopChild(workspace.Root);
        False(await quiet.ExplainsExitAsync(running), "A running process was blamed on its port.");
        await running.StopAsync();
        False(await quiet.ExplainsExitAsync(running), "A process that never reported a conflict was blamed on its port.");
        await running.DisposeAsync();
    }

    [Test, DisplayName("Vite readiness needs the dev session that vite-plugin-runic confirms")]
    public void ViteReadinessConfirmsTheSession() => VerifyViteSessionAsync().GetAwaiter().GetResult();

    private static async Task VerifyViteSessionAsync()
    {
        var origin = new Uri("http://127.0.0.1:12345/");
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        string session = ViteDevelopmentServer.CreateSession();
        True(System.Text.RegularExpressions.Regex.IsMatch(session, "^[0-9a-f]{64}$"), session);
        False(session == ViteDevelopmentServer.CreateSession(), "Each start gets a new value.");

        // The server this dev session started answers /__runic/state with the value it was given.
        var own = new SessionHandler(session);
        using (var client = new HttpClient(own))
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts", running.Task, client,
                TimeSpan.FromSeconds(1), CancellationToken.None, session);
        }
        True(own.Paths.Contains("/__runic/state"), string.Join(", ", own.Paths));

        // Another process on the port serves pages but cannot know the value, so it is never ready.
        foreach (string? answer in new[] { null, ViteDevelopmentServer.CreateSession(), session[..32] })
        {
            using var client = new HttpClient(new SessionHandler(answer));
            try
            {
                await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts", running.Task, client,
                    TimeSpan.FromMilliseconds(200), CancellationToken.None, session);
                throw new InvalidOperationException("A server without this session's value was accepted as ready.");
            }
            catch (DevDevelopmentException error)
            {
                Equal("RAPPDEV1007", error.Code);
                Contains(error.Message, "confirm this dev session");
                Contains(error.Message, "vite-plugin-runic");
                DoesNotContain(error.Message, session);
            }
        }

        // Without a session (no runic() plugin) readiness stays as before.
        using (var client = new HttpClient(new SessionHandler(null)))
        {
            await ViteDevelopmentServer.WaitUntilReadyAsync(origin, "src/main.ts", running.Task, client,
                TimeSpan.FromSeconds(1), CancellationToken.None);
        }

        // An exited Vite process (the port was taken) fails readiness, so dev retries on another port.
        Throws<DevDevelopmentException>(() => ViteDevelopmentServer.ThrowIfExited(Task.FromResult(1)));
        ViteDevelopmentServer.ThrowIfExited(running.Task);
    }

    [Test, DisplayName("dev asks for the session confirmation when the Vite configuration has runic()")]
    public void DevConfirmsSessionWithTheRunicPlugin()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        True(ViteBuildInputs.ParseRunicPlugin("RUNIC_VITE_INPUTS={\"root\":\"/app\",\"inputs\":[],\"runic\":true}") == true, "runic() is reported.");
        True(ViteBuildInputs.ParseRunicPlugin("RUNIC_VITE_INPUTS={\"root\":\"/app\",\"inputs\":[],\"runic\":false}") == false, "A configuration without runic() is reported.");
        True(ViteBuildInputs.ParseRunicPlugin("RUNIC_VITE_INPUTS={\"root\":\"/app\",\"inputs\":[]}") is null, "An older probe says nothing.");
        Contains(ViteBuildInputs.CreateProbe(""), "plugin?.name === \"runic\"");

        True(ViteDevelopmentServer.ConfirmsSession(configuration with { UsesRunicVitePlugin = true }), "The resolved configuration decides.");
        False(ViteDevelopmentServer.ConfirmsSession(configuration with { UsesRunicVitePlugin = false }), "Without runic() nothing can confirm.");
        // Unresolved (an external frontend compiler): the declared dependency decides.
        False(ViteDevelopmentServer.ConfirmsSession(configuration), "No package.json declares the plugin.");
        workspace.Write("Frontend/package.json", """{"devDependencies":{"@runic-artifex/vite-plugin-runic":"0.11.0"}}""");
        True(ViteDevelopmentServer.ConfirmsSession(configuration), "A declared vite-plugin-runic is expected to confirm.");

        string session = ViteDevelopmentServer.CreateSession();
        Equal(session, ViteDevelopmentServer.CreateEnvironment(configuration, session)[ViteDevelopmentServer.SessionEnvironmentVariable]);
        // The Window never gets the value.
        Equal(false, HostProcessController.CreateDevelopmentEnvironment(configuration, new Dictionary<string, string?>())
            .ContainsKey(ViteDevelopmentServer.SessionEnvironmentVariable));
    }

    private sealed class SessionHandler(string? answer) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.RequestUri!.AbsolutePath == "/__runic/state" && answer is not null)
                response.Headers.Add(ViteDevelopmentServer.SessionHeader, answer);
            return Task.FromResult(response);
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

    [Test, DisplayName("development documents rewrite assets for the native bootstrap")]
    public void DevelopmentDocumentIsNativeSafe()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        // The Vite development server serves index.html with root-absolute script paths.
        string html = "<html><head><title>App</title><script src=\"./runic-desktop.js\"></script><script src=\"/webui.js\"></script><script src=\"runic-cswebui.js\"></script><script src=\"./runic-desktop-views.js\"></script><script src=\"/runic-views.js\"></script></head><body><script type=\"module\" src=\"/src/main.ts\"></script><link href=\"/src/app.css\"></body></html>";
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
        Contains(generated, "<script src=\"runic-views.js\">");
        Throws<DevUsageException>(() => FrontendDevelopmentDocument.Write(configuration,
            new Uri("http://127.0.0.1:5173/"), "../escape.html", html));

        // A nested page loads the host scripts from the web root and its own
        // relative assets from its folder, whether or not the Vite plugin
        // already pointed them at the root.
        string nested = "<html><head><script src=\"webui.js\"></script><script src=\"../runic-desktop-views.js\"></script><script src=\"./runic-desktop.js\"></script><link href=\"settings.css\"></head><body><script type=\"module\" src=\"/src/settings.ts\"></script></body></html>";
        FrontendDevelopmentDocument.Write(configuration,
            new Uri("http://127.0.0.1:5173/"), "pages/settings.html", nested);
        string page = File.ReadAllText(Path.Combine(configuration.RuntimeWebRoot, "pages", "settings.html"));
        Contains(page, "<script src=\"../webui.js\">");
        Contains(page, "<script src=\"../runic-desktop-views.js\">");
        Contains(page, "<script src=\"../runic-desktop.js\">");
        Contains(page, "http://127.0.0.1:5173/pages/settings.css");
        Contains(page, "http://127.0.0.1:5173/src/settings.ts");
        Equal(null, FrontendDevelopmentDocument.HostScriptName("vendor/webui.js"));

        // SvelteKit's development page starts its client with import() of paths relative to the page, which
        // would resolve against the native host.
        string kit = "<html><head><link href=\"./_app/env.js\" rel=\"modulepreload\"></head><body><script>import(\"./_app/env.js\").then(() => import(\"./@fs/app/node_modules/@sveltejs/kit/src/runtime/client/entry.js\")); import('/src/start.js');</script></body></html>";
        FrontendDevelopmentDocument.Write(configuration,
            new Uri("http://127.0.0.1:5173/"), "index.html", kit);
        string routed = File.ReadAllText(Path.Combine(configuration.RuntimeWebRoot, "index.html"));
        Contains(routed, "href=\"http://127.0.0.1:5173/_app/env.js\"");
        Contains(routed, "import(\"http://127.0.0.1:5173/_app/env.js\")");
        Contains(routed, "import(\"http://127.0.0.1:5173/@fs/app/node_modules/@sveltejs/kit/src/runtime/client/entry.js\")");
        Contains(routed, "import('http://127.0.0.1:5173/src/start.js')");
    }

    [Test, DisplayName("development documents follow every Vite HTML build input")]
    public void DevelopmentDocumentsFollowViteInputs()
    {
        using var workspace = new TestWorkspace();
        string root = Path.Combine(workspace.Root, "Frontend");
        workspace.Write("Frontend/index.html", "<html></html>");
        workspace.Write("Frontend/pages/settings.html", "<html></html>");
        // The entry document comes first, whatever the input order; script inputs build no page.
        SequenceEqual(["index.html", "pages/settings.html"], ViteBuildInputs.SelectDocuments(root,
            [Path.Combine(root, "pages", "settings.html"), Path.Combine(root, "src", "worker.ts"), Path.Combine(root, "index.html")],
            null));
        // An explicit RunicApplicationFrontendDevServerDocument list wins.
        SequenceEqual(["pages/settings.html"], ViteBuildInputs.SelectDocuments(root,
            [Path.Combine(root, "index.html")], ["pages/settings.html"]));
        SequenceEqual(["index.html"], ViteBuildInputs.SelectDocuments(root, [Path.Combine(root, "src", "lib.ts")], null));
        try
        {
            ViteBuildInputs.SelectDocuments(root, [Path.Combine(workspace.Root, "outside.html")], null);
            throw new InvalidOperationException("An input outside the Vite root was accepted.");
        }
        catch (DevUsageException error)
        {
            Equal("RAPPDEV1005", error.Code);
            DoesNotContain(error.Message, workspace.Root);
        }

        // Without the property, Angular and the frontend compiler keep index.html.
        var configuration = CreateConfiguration(workspace, "vite") with { DevelopmentServerDocument = string.Empty };
        False(configuration.HasExplicitDevelopmentServerDocuments, "An empty property selects the Vite inputs.");
        SequenceEqual(["index.html"], configuration.DevelopmentServerDocuments);

        (string parsedRoot, IReadOnlyList<string> inputs, bool routed) = ViteBuildInputs.Parse(
            "plugin noise\nRUNIC_VITE_INPUTS={\"root\":\"/app\",\"inputs\":[\"/app/index.html\"]}\n");
        Equal("/app", parsedRoot);
        SequenceEqual(["/app/index.html"], inputs);
        False(routed, "A configuration without SvelteKit builds its pages from HTML inputs.");
        True(ViteBuildInputs.Parse("RUNIC_VITE_INPUTS={\"root\":\"/app\",\"inputs\":[\"/app/index.html\"],\"routed\":true}").Routed,
            "SvelteKit's plugin marks a routed configuration.");
        Contains(ViteBuildInputs.CreateProbe(""), "vite-plugin-sveltekit-setup");

        // SvelteKit serves index.html from its routes, so a routed configuration needs no index.html file; an
        // explicit document list is still checked.
        string kitRoot = Path.Combine(workspace.Root, "Kit");
        Directory.CreateDirectory(kitRoot);
        SequenceEqual(["index.html"], ViteBuildInputs.SelectDocuments(kitRoot, [Path.Combine(kitRoot, "index.html")], null, routed: true));
        Throws<DevUsageException>(() => ViteBuildInputs.SelectDocuments(kitRoot, [], ["settings.html"], routed: true));
        try
        {
            ViteBuildInputs.Parse("no marker");
            throw new InvalidOperationException("Output without inputs was accepted.");
        }
        catch (DevDevelopmentException error)
        {
            Equal("RAPPDEV1010", error.Code);
        }
    }

    [Test, DisplayName("a missing window entry fails with entry not found")]
    public void MissingWindowEntryIsNamed()
    {
        using var workspace = new TestWorkspace();
        string root = Path.Combine(workspace.Root, "Frontend");
        workspace.Write("Frontend/index.html", "<html></html>");
        foreach (IReadOnlyList<string>? explicitDocuments in new IReadOnlyList<string>?[] { null, ["index.html", "pages/settings.html"] })
        {
            try
            {
                ViteBuildInputs.SelectDocuments(root,
                    [Path.Combine(root, "index.html"), Path.Combine(root, "pages", "settings.html")], explicitDocuments);
                throw new InvalidOperationException("A missing window entry was accepted.");
            }
            catch (DevUsageException error)
            {
                Equal("RAPPDEV1009", error.Code);
                Contains(error.Message, "Window entry not found: 'pages/settings.html'");
                DoesNotContain(error.Message, "presentation-connection-timeout");
                DoesNotContain(error.Message, workspace.Root);
                Contains(error.LocalDetail ?? string.Empty, root);
            }
        }
    }

    [Test, DisplayName("dev reads the Vite build inputs through the frontend runtime")]
    public void DevReadsViteBuildInputs() => VerifyDevReadsViteBuildInputsAsync().GetAwaiter().GetResult();

    private static async Task VerifyDevReadsViteBuildInputsAsync()
    {
        using var workspace = new TestWorkspace();
        // Bun runs the probe for a Bun frontend; a stand-in vite package records
        // the configuration file and returns a multi-page build input.
        workspace.Write("Frontend/package.json", """{"packageManager":"bun@1.4.2"}""");
        workspace.Write("Frontend/node_modules/vite/package.json", """{"name":"vite","type":"module","main":"index.js"}""");
        workspace.Write("Frontend/node_modules/vite/index.js", """
            export async function resolveConfig(inline, command, mode) {
              if (command !== "build" || mode !== "production" || !inline.configFile.endsWith("vite.config.ts"))
                throw new Error(`unexpected ${command} ${mode} ${inline.configFile}`);
              console.log("plugin output");
              return { root: process.cwd(), build: { rolldownOptions: { input: { settings: "pages/settings.html", main: "index.html" } } } };
            }
            """);
        workspace.Write("Frontend/vite.config.ts", "export default {};");
        workspace.Write("Frontend/index.html", "<html></html>");
        var configuration = CreateConfiguration(workspace, "vite") with { DevelopmentServerDocument = string.Empty };
        try
        {
            await ViteBuildInputs.ApplyAsync(configuration, CancellationToken.None);
            throw new InvalidOperationException("A missing second page was accepted.");
        }
        catch (DevUsageException error)
        {
            Equal("RAPPDEV1009", error.Code);
            Contains(error.Message, "'pages/settings.html'");
        }

        workspace.Write("Frontend/pages/settings.html", "<html></html>");
        DevProjectConfiguration applied = await ViteBuildInputs.ApplyAsync(configuration, CancellationToken.None);
        SequenceEqual(["index.html", "pages/settings.html"], applied.DevelopmentServerDocuments);
    }

    [Test, DisplayName("without a development server the Desktop host serves the frontend output live")]
    public void DevelopmentAssetsFollowTheFrontendMode()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        // Mode 1: a Vite or Angular development server serves the frontend, so the variable is cleared.
        IReadOnlyDictionary<string, string?> server = HostProcessController.CreateDevelopmentEnvironment(
            configuration, new Dictionary<string, string?> { ["RUNIC_APPLICATION_VITE_DEV_SERVER"] = "http://127.0.0.1:5173/" });
        True(server.ContainsKey(HostProcessController.DevelopmentAssetsVariable), "Mode 1 did not clear the variable.");
        Equal(null, server[HostProcessController.DevelopmentAssetsVariable]);
        // The development document of earlier releases is no longer passed.
        True(!server.ContainsKey("RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT"), "RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT is still set.");
        Equal("[dev] Assets: " + configuration.FrontendOutputDirectory,
            DevApplication.DescribeAssets(configuration, new DevOptions(null, "Debug", true, true, true, false, [])));

        // Modes 2 and 3: a frontend watcher, or --no-frontend-watch, writes the output directory.
        IReadOnlyDictionary<string, string?> watcher = HostProcessController.CreateDevelopmentEnvironment(
            configuration, new Dictionary<string, string?>());
        Equal(Path.GetFullPath(configuration.FrontendOutputDirectory), watcher[HostProcessController.DevelopmentAssetsVariable]);
        var staticFrontend = new DevOptions(null, "Debug", true, false, true, false, []);
        Equal($"[dev] Assets: {configuration.FrontendOutputDirectory} (served live; windows reload on change)",
            DevApplication.DescribeAssets(configuration, staticFrontend));
        var noServer = CreateConfiguration(workspace, string.Empty) with { ApplicationHosts = ["desktop"] };
        var watching = new DevOptions(null, "Debug", true, true, true, false, []);
        Equal($"[dev] Assets: {noServer.FrontendOutputDirectory} (served live; windows reload on change)",
            DevApplication.DescribeAssets(noServer, watching));
        // Both hosts referenced: Runic Desktop serves it live.
        Equal($"[dev] Assets: {noServer.FrontendOutputDirectory} (served live; windows reload on change)",
            DevApplication.DescribeAssets(noServer with { ApplicationHosts = ["cswebui", "desktop"] }, watching));
        Equal("[dev] Assets: the CS-WebUI host shows the initial build; restart to see frontend changes",
            DevApplication.DescribeAssets(noServer with { ApplicationHosts = ["cswebui"] }, watching));
    }

    [Test, DisplayName("size inventory counts every file and preserves hashes")]
    public void SizeInventoryAccountsForBytes()
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

    [Test, DisplayName("doctor accepts a healthy Views Window project")]
    public void DoctorAcceptsViewsWindowProject()
    {
        using var workspace = new TestWorkspace();
        string frontend = workspace.Write("Frontend/package.json", "{}");
        string frontendDirectory = Path.GetDirectoryName(frontend)!;
        CompatibilitySetAuthority authority = CompatibilitySetAuthority.Current;
        CompatibilityPackage nuget = authority.NuGetPackages["Runic.Application.Views.CsWebUi"];
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

    // D031.3: the starters and the npm packages that run in Node declare ^24.18.0 || ^26.0.0, so doctor accepts
    // Node 26 with a warning, and still rejects Node 25 and an older Node 24.
    [Test, DisplayName("doctor accepts Node 26 beside the certified Node 24")]
    public void DoctorAcceptsNode26()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateDoctorProject(workspace, null, out CompatibilitySetAuthority authority);
        DoctorCheck Runtime(string node) => Check(DoctorChecks.InspectAsync(project, "dotnet",
            new FakeDoctorRuntime(authority.Toolchain) { NodeVersion = node }, CancellationToken.None).GetAwaiter().GetResult(),
            "javascript-runtime");
        Equal(DoctorStatus.Pass, Runtime(authority.Toolchain.Node).Status);
        DoctorCheck node26 = Runtime("v26.12.0");
        Equal(DoctorStatus.Warning, node26.Status);
        Contains(node26.Message, "26.12.0 is supported");
        Equal(DoctorStatus.Failure, Runtime("25.2.0").Status);
        Equal(DoctorStatus.Failure, Runtime("24.0.0").Status);
        Contains(Runtime("27.0.0").Remediation ?? string.Empty, "Node 26");
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
                "Runic.Application.Views.Desktop" => RunicViewsHost.Desktop,
                "Runic.Application.Views.CsWebUi" => RunicViewsHost.CsWebUi,
                _ => RunicViewsHost.Unknown,
            });
    }

    [Test, DisplayName("doctor scopes the browser check to the Views host")]
    public void DoctorScopesBrowserCheckToHost()
    {
        using (var workspace = new TestWorkspace())
        {
            DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority);
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
            DoctorProjectConfiguration csWebUi = CreateDoctorProject(workspace, "Runic.Application.Views.CsWebUi", out var authority);
            DoctorCheck browser = DoctorChecks.InspectAsync(
                csWebUi, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
                .GetAwaiter().GetResult().Checks.Single(check => check.Name == "browser");
            Equal(DoctorStatus.Warning, browser.Status);
            Contains(browser.Remediation ?? string.Empty, "CS-WebUI");
            Contains(browser.Remediation ?? string.Empty, "CSWEBUI_BROWSER_PATH");
        }

        // CS-WebUI's own browser override is checked; WebUI reads no browser variable.
        using (var workspace = new TestWorkspace())
        {
            DoctorProjectConfiguration csWebUi = CreateDoctorProject(workspace, "Runic.Application.Views.CsWebUi", out var authority);
            var runtime = new FakeDoctorRuntime(authority.Toolchain)
            {
                Environment = { ["CSWEBUI_BROWSER_PATH"] = Path.Combine(workspace.Root, "missing", "chromium") },
            };
            DoctorCheck browser = DoctorChecks.InspectAsync(csWebUi, "dotnet", runtime, CancellationToken.None)
                .GetAwaiter().GetResult().Checks.Single(check => check.Name == "browser");
            Equal(DoctorStatus.Failure, browser.Status);
            Contains(browser.Message, "CSWEBUI_BROWSER_PATH");
        }
    }

    [Test, DisplayName("doctor detects the Views host from evaluated references")]
    public void DoctorDetectsHostFromReferences()
    {
        static RunicViewsHost Detect(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return DoctorProjectConfiguration.DetectHost(document.RootElement);
        }

        Equal(RunicViewsHost.Desktop, Detect("""{"Properties":{},"Items":{"PackageReference":[{"Identity":"Runic.Application.Views.Desktop"}]}}"""));
        Equal(RunicViewsHost.CsWebUi, Detect("""{"Items":{"PackageReference":[{"Identity":"Runic.Application.Views.CsWebUi"}]}}"""));
        Equal(RunicViewsHost.CsWebUi, Detect("""{"Items":{"ProjectReference":[{"Identity":"..\\..\\packages\\Runic.Application.Views.CsWebUi\\Runic.Application.Views.CsWebUi.csproj"}]}}"""));
        Equal(RunicViewsHost.Desktop, Detect("""{"Items":{"ProjectReference":[{"Identity":"../Runic.Application.Views.Desktop/Runic.Application.Views.Desktop.csproj"}]}}"""));
        Equal(RunicViewsHost.Unknown, Detect("""{"Properties":{}}"""));

        static RunicViewsHost[] DetectAll(string json)
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return [.. DoctorProjectConfiguration.DetectHosts(document.RootElement)];
        }

        SequenceEqual([RunicViewsHost.CsWebUi, RunicViewsHost.Desktop], DetectAll("""
            {"Items":{"PackageReference":[{"Identity":"Runic.Application.Views.Desktop"},{"Identity":"Runic.Application.Views.CsWebUi"}]}}
            """));
        SequenceEqual([RunicViewsHost.Desktop, RunicViewsHost.Wpf], DetectAll("""
            {"Items":{"ProjectReference":[{"Identity":"../Runic.Application.Views.Wpf/Runic.Application.Views.Wpf.csproj"}],"PackageReference":[{"Identity":"Runic.Application.Views.Desktop"}]}}
            """));
        Equal(0, DetectAll("""{"Properties":{}}""").Length);
    }

    [Test, DisplayName("dev and size payloads keep their camelCase contracts")]
    public void ToolCommandPayloadsKeepTheirContracts()
    {
        var dev = new DevCommandResult("/work/App.csproj", "/work/Frontend", "/work/Frontend/dist", "/work/bin/Debug/net10.0/www", "vite", true)
        {
            HumanOutput = "[dev] Project: /work/App.csproj",
        };
        var size = new SizeCommandResult("/work/runic-size.json", "linux-x64", 0, 1234, 567, new SizeCommandVerification("passed", 0));
        // The command codec serializes through the context's Default instance or through its own options; names must not
        // depend on either, and human output never reaches JSON.
        foreach (Func<JsonSerializerOptions>? options in new Func<JsonSerializerOptions>?[] { null, () => new JsonSerializerOptions(), () => new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            DevCommandJsonContext devContext = options is null ? DevCommandJsonContext.Default : new DevCommandJsonContext(options());
            Equal("""{"project":"/work/App.csproj","frontendDirectory":"/work/Frontend","frontendOutputDirectory":"/work/Frontend/dist","webRoot":"/work/bin/Debug/net10.0/www","developmentServer":"vite","dryRun":true}""",
                JsonSerializer.Serialize(dev, devContext.DevCommandResult));
            SizeCommandJsonContext sizeContext = options is null ? SizeCommandJsonContext.Default : new SizeCommandJsonContext(options());
            Equal("""{"report":"/work/runic-size.json","runtime":"linux-x64","publishExitCode":0,"totalBytes":1234,"compressedBytes":567,"verification":{"status":"passed","exitCode":0}}""",
                JsonSerializer.Serialize(size, sizeContext.SizeCommandResult));
        }
        Equal("runic.application.tool.dev/1", DevCommandResult.PayloadType);
        Equal("runic.application.tool.size/1", SizeCommandResult.PayloadType);
        Equal("[dev] Project: /work/App.csproj", dev.ToString());
    }

    [Test, DisplayName("the runic.size/2 report keeps its schema")]
    public void SizeReportKeepsItsSchema()
    {
        IReadOnlyList<SizeInstalledRuntime> runtimes = SizeApplication.ParseInstalledRuntimes(
            "Microsoft.AspNetCore.App 10.0.1 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\nMicrosoft.NETCore.App 10.0.1 [C:\\Program Files\\dotnet\\shared\\Microsoft.NETCore.App]\n\nunexpected line\n");
        Equal(3, runtimes.Count);
        Equal(new SizeInstalledRuntime("Microsoft.AspNetCore.App", "10.0.1", "/usr/share/dotnet/shared/Microsoft.AspNetCore.App"), runtimes[0]);
        Equal(@"C:\Program Files\dotnet\shared\Microsoft.NETCore.App", runtimes[1].Directory);
        Equal(new SizeInstalledRuntime("unexpected line", "", ""), runtimes[2]);

        var report = new SizeReport(SizeReport.SchemaId, new DateTime(2026, 10, 20, 7, 15, 0, DateTimeKind.Utc), "/work/App.csproj", "linux-x64",
            "10.0.401", "Linux", "X64", [runtimes[0]], "abc", false,
            new Dictionary<string, string> { ["PublishAot"] = "true" }, new Dictionary<string, string> { ["Runic.Desktop/1.0.0"] = "package" },
            "/work/run/publish", 0, "/work/run/publish.log", "/work/run/distribution.zip", 3, 2,
            [new SizeFile("App", "main-executable", 3, "00")], new SizeVerification("not-run", null, null, []), ["note"]);
        Equal("runic.size/2", report.Schema);
        // The report file is indented; compare its content without whitespace.
        using JsonDocument written = JsonDocument.Parse(JsonSerializer.Serialize(report, SizeJsonContext.Default.SizeReport));
        string json = JsonSerializer.Serialize(written.RootElement);
        Equal("""{"schema":"runic.size/2","createdAt":"2026-10-20T07:15:00Z","project":"/work/App.csproj","runtime":"linux-x64","sdk":"10.0.401","buildOperatingSystem":"Linux","buildArchitecture":"X64","installedRuntimes":[{"name":"Microsoft.AspNetCore.App","version":"10.0.1","directory":"/usr/share/dotnet/shared/Microsoft.AspNetCore.App"}],"sourceRevision":"abc","sourceDirty":false,"effectiveProperties":{"PublishAot":"true"},"libraries":{"Runic.Desktop/1.0.0":"package"},"publishDirectory":"/work/run/publish","publishExitCode":0,"publishLogPath":"/work/run/publish.log","archivePath":"/work/run/distribution.zip","totalBytes":3,"compressedBytes":2,"files":[{"path":"App","category":"main-executable","bytes":3,"sha256":"00"}],"verification":{"status":"not-run","exitCode":null,"executable":null,"arguments":[]},"notes":["note"]}""",
            json);
    }

    [Test, DisplayName("--format selects the output and --output stays a hidden alias")]
    public void FormatSelectsOutputWithHiddenAlias()
    {
        string missing = Path.Combine(Path.GetTempPath(), "runic-missing-" + Guid.NewGuid().ToString("N"), "App.csproj");
        foreach (string[] selector in new[] { new[] { "--format", "json" }, ["--format=json"], ["--output", "json"], ["--output=json"] })
        {
            (int exitCode, string output) = RunCli(["doctor", .. selector, "--project", missing]);
            Equal(2, exitCode);
            using JsonDocument document = JsonDocument.Parse(output);
            Equal("RAPPDEV1002", document.RootElement.GetProperty("fault").GetProperty("code").GetString());
        }
        // The selector may also precede the command.
        (int leadingExit, string leadingOutput) = RunCli("--output", "json", "doctor", "--project", missing);
        Equal(2, leadingExit);
        using (JsonDocument document = JsonDocument.Parse(leadingOutput))
        {
            Equal("RAPPDEV1002", document.RootElement.GetProperty("fault").GetProperty("code").GetString());
        }
        // Help names only --format.
        (_, string help) = RunCli("doctor", "--help");
        Contains(help, "--format");
        DoesNotContain(help, "--output");
        // The catalog declares the selector and its hidden alias, so completion and help read the same spelling.
        Runic.CommandLine.CommandCatalog catalog = Runic.Application.Tool.Program.CreateCatalog();
        Equal("--format", catalog.OutputSelector);
        SequenceEqual(["--output"], catalog.OutputSelectorAliases);
    }

    [Test, DisplayName("every Runic tool exit code follows one table")]
    public void ExitCodesFollowTheRunicTable()
    {
        // dotnet runic uses Command Line's default policy, whose codes are the Runic tool table.
        Runic.CommandLine.DefaultExitCodePolicy policy = Runic.CommandLine.DefaultExitCodePolicy.Instance;
        Equal(0, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.Success));
        Equal(1, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.CommandFailure));
        Equal(1, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.Unavailable));
        Equal(2, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.Usage));
        Equal(2, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.Validation));
        Equal(3, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.HostFailure));
        Equal(130, policy.GetExitCode(Runic.CommandLine.CommandExitCategory.Cancelled));
    }

    [Test, DisplayName("support forwards to runic-translations support")]
    public void SupportForwardsToTranslations()
    {
        True(SupportForwarding.TryParse(["support", "--mode", "collect", "--editor-diagnostics", "e.zip", "--destination", "s.json"], out SupportForwarding? collect), "support is recognized.");
        SequenceEqual(["support", "collect", "--editor-diagnostics", "e.zip", "--destination", "s.json"], collect!.Arguments);
        True(SupportForwarding.TryParse(["--format", "json", "support", "--editor-diagnostics=e.zip"], out SupportForwarding? preview), "support follows a leading --format.");
        SequenceEqual(["support", "preview", "--format", "json", "--editor-diagnostics=e.zip"], preview!.Arguments);
        True(SupportForwarding.TryParse(["support", "--mode=Remove", "--destination", "s.json", "--format", "json"], out SupportForwarding? remove), "--mode= selects the subcommand.");
        SequenceEqual(["support", "remove", "--destination", "s.json", "--format", "json"], remove!.Arguments);
        True(SupportForwarding.TryParse(["--output", "json", "support", "--output=json", "--", "--output"], out SupportForwarding? alias), "support follows a leading --output.");
        SequenceEqual(["support", "preview", "--format", "json", "--format=json", "--", "--output"], alias!.Arguments);
        False(SupportForwarding.TryParse(["doctor", "support"], out _), "support must be the command.");
        False(SupportForwarding.TryParse([], out _), "No arguments run dev.");

        using var workspace = new TestWorkspace();
        string nested = Path.Combine(workspace.Root, "app", "src");
        Directory.CreateDirectory(nested);
        Equal(null, SupportForwarding.ResolveTool(nested, _ => null, "dotnet"));
        Equal(("/opt/runic-translations", 0),
            SupportForwarding.ResolveTool(nested, name => name == "runic-translations" ? "/opt/runic-translations" : null, "dotnet") is { } global
                ? (global.Executable, global.Prefix.Count) : default);
        workspace.Write(Path.Combine(".config", "dotnet-tools.json"), """{"version":1,"isRoot":true,"tools":{"dotnet-runic-translations":{"version":"1.0.0","commands":["runic-translations"]}}}""");
        var local = SupportForwarding.ResolveTool(nested, _ => null, "/usr/bin/dotnet");
        Equal("/usr/bin/dotnet", local?.Executable);
        // dotnet tool run passes options such as --help on only after --.
        SequenceEqual(["tool", "run", "runic-translations", "--"], local!.Value.Prefix);

        // Without the Translations tool, the hidden command names where support went.
        var outcome = Runic.Application.Tool.Program.Support();
        False(outcome.Succeeded, "support without the Translations tool fails.");
        Equal(Runic.CommandLine.CommandExitCategory.Unavailable, outcome.ExitCategory);
        Equal("RAPPCLI1012", outcome.Fault!.Code);
        Runic.CommandLine.CommandCatalog catalog = Runic.Application.Tool.Program.CreateCatalog();
        True(catalog.Commands.Single(command => command.Name == "support").Help.Hidden, "support is a hidden alias.");
    }

    [Test, DisplayName("doctor JSON payload lists every check")]
    public void DoctorJsonListsChecks()
    {
        var project = new DoctorProjectConfiguration(
            "/work/App.csproj", "/work", "net10.0", true, "/work/Frontend", "/work/obj/project.assets.json",
            "linux-x64", "/src/main.ts", "/work/Frontend/vite.config.ts", true, RunicViewsHost.CsWebUi)
        {
            Hosts = [RunicViewsHost.CsWebUi, RunicViewsHost.Desktop],
        };
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
        // hosts was added after the first release of the payload, so it follows the original properties.
        SequenceEqual(["project", "host", "target", "healthy", "summary", "checks", "hosts"],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Equal("cswebui", root.GetProperty("host").GetString());
        SequenceEqual(["cswebui", "desktop"], root.GetProperty("hosts").EnumerateArray().Select(host => host.GetString()!).ToArray());
        Equal(JsonValueKind.Null, root.GetProperty("target").ValueKind);
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

    [Test, DisplayName("dev keeps MSBuild from installing frontend packages")]
    public void NoRestoreSkipsFrontendInstall()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        var noRestore = new DevOptions(null, "Debug", false, true, true, false, []);
        string build = string.Join(" ", DevApplication.CreateBuildArguments(configuration, noRestore));
        Contains(build, "-property:RunicApplicationFrontendInstallEnabled=false");
        Contains(build, "--no-restore");
        Contains(string.Join(" ", HostProcessController.CreateWatchArguments(configuration, noRestore)),
            "--property:RunicApplicationFrontendInstallEnabled=false");
        Contains(string.Join(" ", HostProcessController.CreateRestartBuildArguments(configuration, noRestore)),
            "-property:RunicApplicationFrontendInstallEnabled=false");

        // With a restore, dev has installed the packages itself before the build.
        var restore = new DevOptions(null, "Debug", true, true, true, false, []);
        Contains(string.Join(" ", DevApplication.CreateBuildArguments(configuration, restore)),
            "-property:RunicApplicationFrontendInstallEnabled=false");
    }

    [Test, DisplayName("dev hides the exit code of an application it stopped")]
    public void ExpectedTerminationIsNotReported()
    {
        // dotnet watch stops the application with SIGTERM before each restart.
        True(HostProcessController.IsExpectedTerminationNotice("dotnet watch ❌ [App (net10.0)] Exited with error code 143"), "143");
        True(HostProcessController.IsExpectedTerminationNotice("dotnet watch ❌ Exited with error code 130"), "130");
        True(HostProcessController.IsExpectedTerminationNotice("\u001b[31mdotnet watch ❌ Exited with error code 143\u001b[39m"), "coloured");
        True(HostProcessController.IsExpectedTerminationNotice("dotnet watch ❌ Exited with error code -1073741510"), "Windows Ctrl+C");
        True(!HostProcessController.IsExpectedTerminationNotice("dotnet watch ❌ Exited with error code 1"), "A crash stays visible.");
        True(!HostProcessController.IsExpectedTerminationNotice("dotnet watch ❌ Exited with error code 1430"), "1430");
        True(!HostProcessController.IsExpectedTerminationNotice("dotnet watch ❌ Exited with error code 137"), "A kill after the grace period stays visible.");
        True(!HostProcessController.IsExpectedTerminationNotice("[app] Processed 143 items"), "Application output");
    }

    [Test, DisplayName("child process failures name the program and directory")]
    public void ChildFailuresNameProgramAndDirectory()
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
                <RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Runic.Application.Views.Desktop" Version="0.6.0-preview.1" />
              </ItemGroup>
            </Project>
            """);
        workspace.Write("Frontend/package.json", """{"packageManager":"npm@10.0.0"}""");
        return project;
    }

    [Test, DisplayName("dev passes --verbosity, --launch-profile, --no-build and -e on")]
    public void DevPassesBuildAndRunOptions()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        var defaults = new DevOptions(null, "Debug", true, true, true, false, []);
        // Without the new options the commands are those of 0.10.
        DoesNotContain(string.Join(" ", DevApplication.CreateBuildArguments(configuration, defaults)), "--verbosity");
        DoesNotContain(string.Join(" ", DevApplication.CreateRestoreArguments(configuration, "Debug")), "--verbosity");
        string watch = string.Join(" ", HostProcessController.CreateWatchArguments(configuration, defaults));
        True(watch.EndsWith("run --no-launch-profile", StringComparison.Ordinal), watch);
        True(string.Join(" ", HostProcessController.CreateRunArguments(configuration, defaults)).EndsWith("--no-launch-profile", StringComparison.Ordinal),
            "dotnet run uses no launch profile by default.");

        DevOptions options = defaults with
        {
            Verbosity = "detailed",
            LaunchProfile = "Staging Profile",
            Build = false,
            Environment = ToolOptionValues.ParseEnvironment(["FEATURE_FLAG=on", "DOTNET_ENVIRONMENT=Staging", "EMPTY=", "URL=a=b"]),
        };
        IReadOnlyList<string> build = DevApplication.CreateBuildArguments(configuration, options);
        SequenceEqual(["--verbosity", "detailed"], build.Skip(build.Count - 2).ToArray());
        SequenceEqual(["--verbosity", "minimal"], DevApplication.CreateRestoreArguments(configuration, "Debug", "minimal").Skip(3).ToArray());
        IReadOnlyList<string> watchArguments = HostProcessController.CreateWatchArguments(configuration, options);
        Contains(string.Join(" ", watchArguments), "--verbosity detailed --no-restore --non-interactive run --launch-profile");
        Equal("Staging Profile", watchArguments[^1]);
        DoesNotContain(string.Join(" ", watchArguments), "--no-launch-profile");
        IReadOnlyList<string> run = HostProcessController.CreateRunArguments(configuration, options);
        SequenceEqual(["--launch-profile", "Staging Profile"], run.Skip(run.Count - 2).ToArray());
        Contains(string.Join(" ", HostProcessController.CreateRestartBuildArguments(configuration, options)), "--verbosity detailed");

        // -e values reach the Window and win over dev's defaults, as dotnet run -e does.
        IReadOnlyDictionary<string, string?> environment = HostProcessController.CreateDevelopmentEnvironment(
            configuration, new Dictionary<string, string?>(), _ => null, options.Environment);
        Equal("on", environment["FEATURE_FLAG"]);
        Equal("Staging", environment["DOTNET_ENVIRONMENT"]);
        Equal("", environment["EMPTY"]);
        Equal("a=b", environment["URL"]);
        // An ASP.NET Core environment given with -e counts as the developer's choice too.
        Equal(false, HostProcessController.CreateDevelopmentEnvironment(configuration, new Dictionary<string, string?>(), _ => null,
            ToolOptionValues.ParseEnvironment(["ASPNETCORE_ENVIRONMENT=Staging"])).ContainsKey("DOTNET_ENVIRONMENT"));

        foreach (string malformed in new[] { "NAME", "=value", "TWO WORDS=x", " LEADING=x" })
            Equal(ToolOptionValues.EnvironmentFormatCode, Throws<DevUsageException>(() => ToolOptionValues.ParseEnvironment([malformed])).Code);
        foreach (string reserved in new[] { "RUNIC_APPLICATION_VITE_DEV_SERVER=x", "runic_application_frontend_kind=x",
            "RUNIC_APPLICATION_DEV_PROJECT=x", "RUNIC_APPLICATION_DEVELOPMENT_ASSETS=x" })
            Equal(ToolOptionValues.ReservedEnvironmentCode, Throws<DevUsageException>(() => ToolOptionValues.ParseEnvironment([reserved])).Code);
    }

    [Test, DisplayName("size passes --property, --self-contained and --verbosity on")]
    public void SizePassesPublishOptions()
    {
        var defaults = new SizeOptions(null, "linux-x64", "Release", "", true, "", []);
        SequenceEqual(["-p:Configuration=Release", "-p:RuntimeIdentifier=linux-x64", "-p:PublishAot=true",
            "-p:SelfContained=true", "-p:OptimizationPreference=Size"], SizeApplication.CreateSettings(defaults));
        var options = defaults with
        {
            SelfContained = false,
            Properties = ToolOptionValues.ParseProperties(["OptimizationPreference=Speed", "DefineConstants=A;B,C", "Empty=", "_Private-Name=x"]),
        };
        SequenceEqual(["-p:Configuration=Release", "-p:RuntimeIdentifier=linux-x64", "-p:PublishAot=true",
            "-p:SelfContained=false", "-p:OptimizationPreference=Size",
            // After size's own properties, so they override its defaults; separators are escaped, one property each.
            "-property:OptimizationPreference=Speed", "-property:DefineConstants=A%3BB%2CC", "-property:Empty=", "-property:_Private-Name=x"],
            SizeApplication.CreateSettings(options));
        foreach (string malformed in new[] { "Name", "=x", "1Name=x", "Na me=x", "Name;Other=x" })
            Equal(ToolOptionValues.PropertyFormatCode, Throws<DevUsageException>(() => ToolOptionValues.ParseProperties([malformed])).Code);
        foreach (string reserved in new[] { "Configuration=Debug", "runtimeidentifier=win-x64", "PublishAot=false", "SelfContained=false" })
            Equal(ToolOptionValues.ReservedPropertyCode, Throws<DevUsageException>(() => ToolOptionValues.ParseProperties([reserved])).Code);
    }

    [Test, DisplayName("the 0.11 options are in help and completion and are checked")]
    public void NewOptionsAreDeclared()
    {
        (_, string devHelp) = RunCli("dev", "--help");
        foreach (string option in new[] { "--launch-profile", "--no-build", "--environment", "-e", "--verbosity", "-v" })
            Contains(devHelp, option);
        (_, string sizeHelp) = RunCli("size", "--help");
        foreach (string option in new[] { "--property", "--self-contained", "--no-self-contained", "--verbosity" })
            Contains(sizeHelp, option);
        (int completionExit, string completion) = RunCli("completion", "bash");
        Equal(0, completionExit);
        foreach (string option in new[] { "--launch-profile", "--no-build", "--environment", "--verbosity", "--property", "--no-self-contained", "diagnostic" })
            Contains(completion, option);

        string missing = Path.Combine(Path.GetTempPath(), "runic-missing-" + Guid.NewGuid().ToString("N"), "App.csproj");
        // A verbosity outside MSBuild's levels and conflicting switches are usage errors before any work.
        Equal(2, RunCli("dev", "--verbosity", "loud", "--project", missing).ExitCode);
        Equal(2, RunCli("size", "--self-contained", "--no-self-contained", "--runtime", "linux-x64", "--project", missing).ExitCode);
        (int environmentExit, string environmentOutput) = RunCli("dev", "--format", "json", "-e", "NAME", "--project", missing);
        Equal(2, environmentExit);
        using (JsonDocument document = JsonDocument.Parse(environmentOutput))
            Equal(ToolOptionValues.EnvironmentFormatCode, document.RootElement.GetProperty("fault").GetProperty("code").GetString());
        (int propertyExit, string propertyOutput) = RunCli("size", "--format", "json", "--runtime", "linux-x64", "--property", "SelfContained=false", "--project", missing);
        Equal(2, propertyExit);
        using (JsonDocument document = JsonDocument.Parse(propertyOutput))
            Equal(ToolOptionValues.ReservedPropertyCode, document.RootElement.GetProperty("fault").GetProperty("code").GetString());
    }

    [Test, DisplayName("JSON faults link their catalog entry in fault.helpUri")]
    public void JsonFaultsCarryHelpUri()
    {
        string missing = Path.Combine(Path.GetTempPath(), "runic-missing-" + Guid.NewGuid().ToString("N"), "App.csproj");
        (int exitCode, string output) = RunCli("doctor", "--format", "json", "--project", missing);
        Equal(2, exitCode);
        using (JsonDocument document = JsonDocument.Parse(output))
        {
            string helpUri = document.RootElement.GetProperty("fault").GetProperty("helpUri").GetString()!;
            True(helpUri.StartsWith("https://github.com/Runic-Artifex/runic-sdk/blob/", StringComparison.Ordinal)
                && helpUri.EndsWith("/docs/diagnostics.md#rappdev1002", StringComparison.Ordinal), helpUri);
        }
    }

    [Test, DisplayName("doctor compares the installed @runic-artifex/views with Runic.Application.Views")]
    public void DoctorComparesViewsPackageVersions()
    {
        using var workspace = new TestWorkspace();
        string assets = workspace.Write("obj/project.assets.json",
            """{"libraries":{"Runic.Application.Views/0.11.0":{"type":"package"},"Runic.Application.Views.Desktop/0.11.0":{"type":"package"}}}""");
        string frontend = Path.Combine(workspace.Root, "Frontend");
        Directory.CreateDirectory(frontend);
        var project = new DoctorProjectConfiguration(
            Path.Combine(workspace.Root, "App.csproj"), workspace.Root, "net10.0", true, frontend, assets,
            "linux-x64", "/src/main.ts", Path.Combine(frontend, "vite.config.ts"), true, RunicViewsHost.Desktop);
        List<DoctorCheck> checks = [];
        // Not installed yet: nothing to compare, and compatibility-set already reports an unrestored project.
        DoctorChecks.CheckViewsPackageVersions(checks, project);
        Equal(0, checks.Count);
        // A workspace root's node_modules counts, as Node.js resolves it.
        workspace.Write("node_modules/@runic-artifex/views/package.json", """{"name":"@runic-artifex/views","version":"0.11.0"}""");
        DoctorChecks.CheckViewsPackageVersions(checks, project);
        Equal(DoctorStatus.Pass, checks.Single().Status);
        Equal("views-package-versions", checks.Single().Name);
        // The frontend's own node_modules is nearer; a stale install warns and names both versions.
        workspace.Write("Frontend/node_modules/@runic-artifex/views/package.json", """{"name":"@runic-artifex/views","version":"0.10.0"}""");
        checks.Clear();
        DoctorChecks.CheckViewsPackageVersions(checks, project);
        DoctorCheck stale = checks.Single();
        Equal(DoctorStatus.Warning, stale.Status);
        Contains(stale.Message, "@runic-artifex/views 0.10.0");
        Contains(stale.Message, "Runic.Application.Views 0.11.0");
        // A prerelease label distinguishes releases.
        workspace.Write("Frontend/node_modules/@runic-artifex/views/package.json", """{"version":"0.11.0-preview.1"}""");
        checks.Clear();
        DoctorChecks.CheckViewsPackageVersions(checks, project);
        Equal(DoctorStatus.Warning, checks.Single().Status);
    }

    [Test, DisplayName("doctor checks the ApplicationId format")]
    public void DoctorChecksApplicationId()
    {
        DoctorStatus Check(string id)
        {
            var project = new DoctorProjectConfiguration(
                "/work/App.csproj", "/work", "net10.0", true, "/work/Frontend", "/work/obj/project.assets.json",
                "linux-x64", "/src/main.ts", "/work/Frontend/vite.config.ts", true) { ApplicationId = id };
            List<DoctorCheck> checks = [];
            DoctorChecks.CheckApplicationId(checks, project);
            Equal("application-id", checks.Single().Name);
            return checks.Single().Status;
        }
        // The rule of the template's packaging script (tests/engineering/template-packaging.test.mjs).
        foreach (string id in new[] { "org.example.Notes", "com.example.App2048Game", "a.b", "" })
            Equal(DoctorStatus.Pass, Check(id));
        foreach (string id in new[] { "Notes", "org.example.my-notes", "org.example.2048", "org..example", "org.example.Notes_App", $"org.example.{new string('a', 40)}" })
            Equal(DoctorStatus.Warning, Check(id));
    }

    private static (int ExitCode, string Output) RunCli(params string[] arguments)
    {
        var console = new CapturingConsole();
        int exitCode = Runic.Application.Tool.Program.RunAsync(arguments, console).GetAwaiter().GetResult();
        return (exitCode, console.Output);
    }

    [Test, DisplayName("doctor JSON reports unhealthy projects through the CLI")]
    public void DoctorJsonThroughCli()
    {
        using var workspace = new TestWorkspace();
        string project = CreateCliDoctorProject(workspace);
        Equal(1, RunCli("doctor", "--project", project, "--format", "json").ExitCode);
        (int exitCode, string output) = RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "never");
        Equal(0, exitCode);
        var response = Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(output), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult);
        True(response.Succeeded, "--fail-on never reports a completed inspection as a successful envelope.");
        DoctorCommandResult payload = response.Payload!;
        False(payload.Healthy, "A missing lock file makes the project unhealthy.");
        Equal("desktop", payload.Host);
        SequenceEqual(["desktop"], payload.Hosts);
        True(payload.Checks.Any(check => check.Id == "desktop-runtime"), "The Desktop host gets its native runtime check.");
        Equal("fail", payload.Checks.Single(check => check.Id == "lock-file").Status);
        Equal("pass", payload.Checks.Single(check => check.Id == "browser").Status);
        True(response.Diagnostics.Any(diagnostic => diagnostic.MessageKey == "doctor.lock-file.failed"),
            "Each failing check is mirrored as a diagnostic.");
    }

    [Test, DisplayName("doctor --fail-on decides the exit in both modes")]
    public void DoctorFailOnDecidesExit()
    {
        using var workspace = new TestWorkspace();
        string project = CreateCliDoctorProject(workspace);
        (int exitCode, string output) = RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "fail");
        Equal(1, exitCode);
        using (JsonDocument document = JsonDocument.Parse(output))
        {
            JsonElement root = document.RootElement;
            False(root.GetProperty("success").GetBoolean(), "--fail-on fail fails the envelope.");
            Equal(JsonValueKind.Null, root.GetProperty("payload").ValueKind);
            JsonElement fault = root.GetProperty("fault");
            Equal("RAPPCLI1009", fault.GetProperty("code").GetString());
            Equal("fail", fault.GetProperty("details").GetProperty("lock-file").GetString());
            // The checks travel in fault.data, so a JSON reader needs no second run with --fail-on never.
            JsonElement data = fault.GetProperty("data");
            Equal(DoctorCommandResult.PayloadType, data.GetProperty("type").GetString());
            Equal("fail", data.GetProperty("payload").GetProperty("checks").EnumerateArray()
                .Single(check => check.GetProperty("id").GetString() == "lock-file").GetProperty("status").GetString());
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

    [Test, DisplayName("--no-restore requires installed frontend packages")]
    public void NoRestoreRequiresInstalledPackages()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        Write(Path.Combine(configuration.FrontendPackageDirectory, "package.json"), """{"packageManager":"npm@10.0.0","devDependencies":{"vite":"8.3.2"}}""");
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

        // npm creates no node_modules for a package.json without dependencies.
        Directory.Delete(Path.Combine(configuration.FrontendPackageDirectory, "node_modules"));
        Write(Path.Combine(configuration.FrontendPackageDirectory, "package.json"),
            """{"packageManager":"npm@10.0.0","dependencies":{},"scripts":{"build":"node build.mjs"}}""");
        DevApplication.RequireInstalledFrontendPackages(configuration);
        True(DevApplication.DeclaresDependencies(Path.Combine(workspace.Root, "missing.json")),
            "An unreadable package.json still requires installed packages.");
    }

    [Test, DisplayName("JSON faults never contain absolute project paths")]
    public void JsonFaultsOmitProjectPaths()
    {
        foreach (string project in new[] { "/run/user/1000/runic-missing/App.csproj", @"C:\Users\someone\App\App.csproj" })
        {
            (int exitCode, string output) = RunCli("doctor", "--format", "json", "--project", project);
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
        (int brokenExit, string brokenOutput) = RunCli("doctor", "--format", "json", "--project", broken);
        Equal(2, brokenExit);
        using JsonDocument brokenDocument = JsonDocument.Parse(brokenOutput);
        JsonElement brokenFault = brokenDocument.RootElement.GetProperty("fault");
        Equal("RAPPDEV1003", brokenFault.GetProperty("code").GetString());
        DoesNotContain(brokenFault.GetRawText(), workspace.Root);
    }

    [Test, DisplayName("the fault backstop detects any rooted path")]
    public void FaultBackstopDetectsRootedPaths()
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
            var outcome = Runic.Application.Tool.Program.Failure<DevCommandResult>(
                Runic.CommandLine.CommandExitCategory.Usage, "RAPPDEV1002", message);
            Equal("The command could not be completed. See the local detail above.", outcome.Fault!.Message);
            Contains(outcome.HumanOutput ?? string.Empty, message);
            // The fault links the code's catalog entry at the release tag: fault.helpUri in JSON, a "Help for" line
            // after the fault in human output. The link is not part of the local detail.
            string helpUri = outcome.Fault.HelpUri!.AbsoluteUri;
            True(helpUri.StartsWith("https://github.com/Runic-Artifex/runic-sdk/blob/v", StringComparison.Ordinal)
                && helpUri.EndsWith("/docs/diagnostics.md#rappdev1002", StringComparison.Ordinal), helpUri);
            DoesNotContain(outcome.HumanOutput!, "Help for");
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

    [Test, DisplayName("every command option is described")]
    public void EveryOptionIsDescribed()
    {
        Runic.CommandLine.CommandCatalog catalog = Runic.Application.Tool.Program.CreateCatalog();
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

    [Test, DisplayName("--runtime/--rid, --aot/--no-aot and --self-contained/--no-self-contained are spelled alike in every command")]
    public void RidAndAotOptionsMatchAcrossCommands()
    {
        Runic.CommandLine.CommandCatalog catalog = Runic.Application.Tool.Program.CreateCatalog();
        string[] rid = ["--rid", "--runtime", "-r"];
        int ridCommands = 0;
        foreach (Runic.CommandLine.CommandDescriptor command in catalog.Commands)
        {
            string[] spellings = [.. command.Options.SelectMany(option => option.Aliases.Prepend(option.Name))];
            if (spellings.Intersect(rid).Any())
            {
                ridCommands++;
                True(command.Options.Any(option => option.Name == "--runtime" && option.Aliases.Contains("--rid") && option.Aliases.Contains("-r")),
                    $"{command.Name} names --runtime first, as dotnet publish does, with --rid and -r as aliases.");
                foreach (string spelling in rid) True(spellings.Contains(spelling), $"{command.Name} needs {spelling}.");
            }
            if (spellings.Contains("--aot") || spellings.Contains("--no-aot"))
            {
                True(spellings.Contains("--aot") && spellings.Contains("--no-aot"), $"{command.Name} needs --aot and --no-aot.");
            }
            if (spellings.Contains("--self-contained") || spellings.Contains("--no-self-contained"))
            {
                True(spellings.Contains("--self-contained") && spellings.Contains("--no-self-contained"),
                    $"{command.Name} needs --self-contained and --no-self-contained.");
            }
        }
        Equal(2, ridCommands);

        // The spellings parse: a missing project is reported, not an unknown option.
        string missing = Path.Combine(Path.GetTempPath(), "runic-missing-" + Guid.NewGuid().ToString("N"), "App.csproj");
        foreach (string[] arguments in new[]
        {
            new[] { "size", "--rid", "linux-x64", "--aot" },
            ["size", "--runtime", "linux-x64", "--no-aot"],
            ["doctor", "--runtime", "linux-x64", "--no-aot"],
            ["doctor", "--runtime", "linux-x64", "--self-contained"],
            ["doctor", "--runtime", "linux-x64", "--no-self-contained"],
        })
        {
            (int exitCode, string output) = RunCli([.. arguments, "--project", missing, "--format", "json"]);
            Equal(2, exitCode);
            using JsonDocument document = JsonDocument.Parse(output);
            Equal("RAPPDEV1002", document.RootElement.GetProperty("fault").GetProperty("code").GetString());
        }
        foreach (string command in new[] { "size", "doctor" })
        {
            (int exitCode, string output) = RunCli(command, "-r", "linux-x64", "--aot", "--no-aot", "--project", missing, "--format", "json");
            Equal(2, exitCode);
            using JsonDocument document = JsonDocument.Parse(output);
            DoesNotContain(document.RootElement.GetProperty("fault").GetProperty("code").GetString() ?? "", "RAPPDEV1002");
        }
        {
            (int exitCode, string output) = RunCli("doctor", "-r", "linux-x64", "--self-contained", "--no-self-contained", "--project", missing, "--format", "json");
            Equal(2, exitCode);
            using JsonDocument document = JsonDocument.Parse(output);
            DoesNotContain(document.RootElement.GetProperty("fault").GetProperty("code").GetString() ?? "", "RAPPDEV1002");
        }
        foreach (string option in new[] { "--no-aot", "--self-contained", "--no-self-contained" })
        {
            (int exitCode, string output) = RunCli("doctor", option, "--project", missing, "--format", "json");
            Equal(2, exitCode);
            using JsonDocument withoutRid = JsonDocument.Parse(output);
            Equal("RAPPCLI1011", withoutRid.RootElement.GetProperty("fault").GetProperty("code").GetString());
        }
        DoctorTargetRid linuxX64 = DoctorTargetRid.Parse("linux-x64");
        Contains(string.Join(',', DoctorProjectConfiguration.PublishProperties(linuxX64, false, null)), "PublishAot=false");
        DoesNotContain(string.Join(',', DoctorProjectConfiguration.PublishProperties(linuxX64, null, null)), "SelfContained");
        Contains(string.Join(',', DoctorProjectConfiguration.PublishProperties(linuxX64, null, false)), "SelfContained=false,_CommandLineDefinedSelfContained=true");
    }

    [Test, DisplayName("doctor guides an unrestored project instead of failing")]
    public void DoctorGuidesUnrestoredProject()
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

    [Test, DisplayName("doctor accepts the Runic Desktop Views host")]
    public void DoctorAcceptsDesktopHost()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration project = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority);
        DoctorReport report = DoctorChecks.InspectAsync(
            project, "dotnet", new FakeDoctorRuntime(authority.Toolchain), CancellationToken.None)
            .GetAwaiter().GetResult();
        Equal(DoctorStatus.Pass, report.Checks.Single(check => check.Name == "compatibility-set").Status);
    }

    [Test, DisplayName("doctor checks every Views host the project references")]
    public void DoctorChecksEveryReferencedHost()
    {
        using var workspace = new TestWorkspace();
        // CS-WebUI is the primary host, as DetectHost selects it when a project references both.
        DoctorProjectConfiguration both = CreateDoctorProject(workspace, "Runic.Application.Views.CsWebUi", out var authority)
            with { Hosts = [RunicViewsHost.CsWebUi, RunicViewsHost.Desktop] };
        DoctorReport Inspect(DoctorProjectConfiguration project, FakeDoctorRuntime runtime) =>
            DoctorChecks.InspectAsync(project, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult();

        // A Linux shell without WebKitGTK, but with a Chromium-family browser for CS-WebUI.
        DoctorReport missing = Inspect(both, new FakeDoctorRuntime(authority.Toolchain) { Executables = ["chromium"] });
        DoctorCheck hosts = Check(missing, "views-hosts");
        Equal(DoctorStatus.Pass, hosts.Status);
        Contains(hosts.Message, "Found 2 Runic Views hosts");
        Contains(hosts.Message, "CS-WebUI, checked by browser");
        Contains(hosts.Message, "Runic Desktop with GTK 3 and WebKitGTK 4.1, checked by desktop-runtime and platform-services");
        Equal(DoctorStatus.Pass, Check(missing, "browser").Status);
        DoctorCheck desktop = Check(missing, "desktop-runtime");
        Equal(DoctorStatus.Warning, desktop.Status);
        Contains(desktop.Message, "libgtk-3.so.0 and libwebkit2gtk-4.1.so.0");
        Contains(desktop.Remediation ?? string.Empty, "libwebkit2gtk-4.1-0");
        True(missing.Warnings > 0, "A referenced host with missing native libraries must not leave a clean pass.");
        // Runic Desktop's portal check runs as well, not CS-WebUI's OwnerUnavailable note.
        Contains(Check(missing, "platform-services").Message, "xdg-desktop-portal");

        DoctorCheck partial = Check(Inspect(both, new FakeDoctorRuntime(authority.Toolchain) { Libraries = ["libgtk-3.so.0"] }), "desktop-runtime");
        Equal(DoctorStatus.Warning, partial.Status);
        Contains(partial.Message, "missing libwebkit2gtk-4.1.so.0.");
        Equal(DoctorStatus.Pass, Check(Inspect(both, new FakeDoctorRuntime(authority.Toolchain)
        {
            Libraries = ["libgtk-3.so.0", "libwebkit2gtk-4.1.so.0"],
        }), "desktop-runtime").Status);

        // The GTK 4 profile's libraries are gtk4-profile's; desktop-runtime would repeat them.
        DoctorReport gtk4 = Inspect(both with { UsesGtk4 = true, DirectReferences = ["Runic.Application.Views.Desktop", "Runic.Desktop.Gtk4"] },
            new FakeDoctorRuntime(authority.Toolchain));
        False(gtk4.Checks.Any(static check => check.Name == "desktop-runtime"), "desktop-runtime repeated the GTK 4 profile's check.");
        Contains(Check(gtk4, "views-hosts").Message, "Runic Desktop with the GTK 4 profile, checked by gtk4-profile");
        Equal(DoctorStatus.Warning, Check(gtk4, "gtk4-profile").Status);

        var windows = new DoctorHostPlatform("win", "x64", false, "10.0.26100");
        Equal(DoctorStatus.Warning, Check(Inspect(both, new FakeDoctorRuntime(authority.Toolchain) { Platform = windows }), "desktop-runtime").Status);
        Contains(Check(Inspect(both, new FakeDoctorRuntime(authority.Toolchain) { Platform = windows, WebView2Version = "130.0.2849.80" }),
            "desktop-runtime").Message, "WebView2 Runtime 130.0.2849.80");
        Equal(DoctorStatus.Pass, Check(Inspect(both, new FakeDoctorRuntime(authority.Toolchain) { Platform = new("osx", "arm64", false, "15.0") }),
            "desktop-runtime").Status);

        // With --rid, the target checks follow the primary host. On a machine with the target's operating system,
        // desktop-runtime still probes Runic Desktop, so a missing WebKitGTK is not hidden behind --rid.
        DoctorReport rid = InspectTarget(both, "linux-x64", new FakeDoctorRuntime(authority.Toolchain) { Executables = ["chromium"] });
        Equal(DoctorStatus.Warning, Check(rid, "desktop-runtime").Status);
        Equal(DoctorStatus.Pass, Check(rid, "views-hosts").Status);
        Contains(Check(rid, "views-hosts").Message, "Runic Desktop with GTK 3 and WebKitGTK 4.1, checked by desktop-runtime");
        Contains(Check(rid, "views-hosts").Message, "CS-WebUI, checked by browser and the target checks");
        Contains(Check(rid, "views-hosts").Message, "The target checks follow CS-WebUI.");
        // From another operating system nothing checks Runic Desktop, so views-hosts warns instead of passing.
        DoctorReport remote = InspectTarget(both, "linux-x64", new FakeDoctorRuntime(authority.Toolchain) { Platform = windows, Executables = ["chromium"] });
        False(remote.Checks.Any(static check => check.Name == "desktop-runtime"), "A Windows machine probed Linux libraries.");
        DoctorCheck uncovered = Check(remote, "views-hosts");
        Equal(DoctorStatus.Warning, uncovered.Status);
        Contains(uncovered.Message, "Runic Desktop with GTK 3 and WebKitGTK 4.1, which --runtime linux-x64 cannot check from this Windows machine");
        Contains(uncovered.Remediation ?? string.Empty, "'dotnet runic doctor --runtime linux-x64' on a Linux machine");
        // The GTK 4 profile's libraries are probed the same way.
        DoctorReport gtk4Rid = InspectTarget(both with { UsesGtk4 = true, DirectReferences = ["Runic.Application.Views.Desktop", "Runic.Desktop.Gtk4"] },
            "linux-x64", new FakeDoctorRuntime(authority.Toolchain));
        Contains(Check(gtk4Rid, "gtk4-profile").Message, "libwebkitgtk-6.0.so.4");
    }

    [Test, DisplayName("doctor checks the WPF host and reports a project without a host")]
    public void DoctorChecksWpfHostAndMissingHost()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration wpf = CreateDoctorProject(workspace, null, out var authority) with { Hosts = [RunicViewsHost.Wpf] };
        DoctorReport Inspect(DoctorProjectConfiguration project, FakeDoctorRuntime runtime) =>
            DoctorChecks.InspectAsync(project, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult();
        var windows = new DoctorHostPlatform("win", "x64", false, "10.0.26100");

        DoctorReport linux = Inspect(wpf, new FakeDoctorRuntime(authority.Toolchain));
        Equal(DoctorStatus.Warning, Check(linux, "wpf-runtime").Status);
        Contains(Check(linux, "views-hosts").Message, "WPF, checked by wpf-runtime");
        Contains(Check(linux, "browser").Message, "Not required");
        Equal(DoctorStatus.Warning, Check(Inspect(wpf, new FakeDoctorRuntime(authority.Toolchain) { Platform = windows }), "wpf-runtime").Status);
        Equal(DoctorStatus.Pass, Check(Inspect(wpf, new FakeDoctorRuntime(authority.Toolchain) { Platform = windows, WebView2Version = "130.0.2849.80" }),
            "wpf-runtime").Status);

        DoctorCheck none = Check(Inspect(wpf with { Hosts = [] }, new FakeDoctorRuntime(authority.Toolchain)), "views-hosts");
        Equal(DoctorStatus.Warning, none.Status);
        Contains(none.Message, "references no Runic Views host");
        Contains(none.Remediation ?? string.Empty, "Runic.Application.Views.Wpf");
        // An adapter that arrives through another package or project sets _RunicApplicationHostAdapter.
        DoctorCheck indirect = Check(Inspect(wpf with { Hosts = [], ReferencesHostAdapter = true }, new FakeDoctorRuntime(authority.Toolchain)), "views-hosts");
        Equal(DoctorStatus.Warning, indirect.Status);
        Contains(indirect.Message, "A Runic Views host is referenced indirectly");
        DoesNotContain(indirect.Message, "references no Runic Views host");

        // A WPF-only project's --rid checks follow WPF, which runs on Runic Desktop's Windows layer.
        DoctorProjectConfiguration primary = wpf with { Host = RunicViewsHost.Wpf };
        DoctorReport windowsTarget = InspectTarget(primary, "win-x64", new FakeDoctorRuntime(authority.Toolchain));
        Equal(DoctorStatus.Pass, Check(windowsTarget, "target-rid").Status);
        DoesNotContain(Check(windowsTarget, "target-rid").Message, "assumed");
        Contains(Check(windowsTarget, "target-presentation").Message, "The WPF host on win-x64 needs the Microsoft Edge WebView2 Runtime");
        Contains(Check(windowsTarget, "views-hosts").Message, "WPF, checked by the target checks");
        Equal(DoctorStatus.Warning, Check(InspectTarget(primary, "win-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Platform = windows }), "target-presentation").Status);
        DoctorCheck linuxTarget = Check(InspectTarget(primary, "linux-x64", new FakeDoctorRuntime(authority.Toolchain)), "target-rid");
        Equal(DoctorStatus.Failure, linuxTarget.Status);
        Contains(linuxTarget.Message, "The WPF host runs only on Windows");
        // Next to Runic Desktop, WPF is not the primary host; wpf-runtime reports a target it cannot run on.
        DoctorReport desktopAndWpf = InspectTarget(primary with { Host = RunicViewsHost.Desktop, Hosts = [RunicViewsHost.Desktop, RunicViewsHost.Wpf] },
            "linux-x64", new FakeDoctorRuntime(authority.Toolchain));
        Contains(Check(desktopAndWpf, "wpf-runtime").Message, "cannot run on linux-x64");
        Equal(DoctorStatus.Pass, Check(desktopAndWpf, "views-hosts").Status);

        using JsonDocument evaluation = JsonDocument.Parse("""{"Items":{"PackageReference":[{"Identity":"Runic.Application.Views.Wpf"}]}}""");
        Equal(RunicViewsHost.Wpf, DoctorProjectConfiguration.DetectHost(evaluation.RootElement));
    }

    [Test, DisplayName("doctor host warnings change only the --fail-on warn result")]
    public void DoctorHostWarningsKeepDefaultExit()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration both = CreateDoctorProject(workspace, "Runic.Application.Views.CsWebUi", out var authority)
            with { Hosts = [RunicViewsHost.CsWebUi, RunicViewsHost.Desktop] };
        var path = new Runic.CommandLine.CommandPath(["doctor"]);
        // A headless CI image without WebKitGTK, and a project whose host doctor cannot identify.
        foreach (DoctorProjectConfiguration project in new[] { both, both with { Host = RunicViewsHost.Unknown, Hosts = [] } })
        {
            DoctorReport report = DoctorChecks.InspectAsync(project, "dotnet",
                new FakeDoctorRuntime(authority.Toolchain) { Executables = ["chromium"] }, CancellationToken.None).GetAwaiter().GetResult();
            True(report.IsHealthy, "Host warnings must not fail the default --fail-on fail: " +
                string.Join("; ", report.Checks.Where(check => check.Status == DoctorStatus.Failure).Select(check => check.Name)));
            True(report.Checks.Any(check => check.Status == DoctorStatus.Warning && check.Name is "desktop-runtime" or "views-hosts"),
                "The host warning is reported.");
            var run = new DoctorRun(project, report);
            foreach (Runic.CommandLine.CommandOutputMode mode in new[] { Runic.CommandLine.CommandOutputMode.Human, Runic.CommandLine.CommandOutputMode.Json })
            {
                True(DoctorOutcome.Create(run, mode, DoctorFailOn.Fail, path, "report", "report").Succeeded, $"--fail-on fail ({mode}) succeeds.");
                False(DoctorOutcome.Create(run, mode, DoctorFailOn.Warn, path, "report", "report").Succeeded, $"--fail-on warn ({mode}) fails.");
            }
        }
    }

    [Test, DisplayName("development servers are inferred from the frontend")]
    public void DevelopmentServersAreInferred()
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

        // A SvelteKit frontend has no src/main module; its root route's page is the entry the probe loads.
        string kit = Path.GetDirectoryName(workspace.Write("Kit/vite.config.ts", "export default {};"))!;
        workspace.Write("Kit/src/routes/+layout.ts", "export const ssr = false;");
        workspace.Write("Kit/src/routes/+page.svelte", "<h1>Kit</h1>");
        var sveltekit = DevProjectConfiguration.InferDevelopmentServer(kit, "", false, "", "");
        Equal("vite", sveltekit.Kind);
        Equal("/src/routes/+page.svelte", sveltekit.ViteEntry);
    }

    [Test, DisplayName("a development server owns the frontend build")]
    public void DevelopmentServerOwnsFrontendBuild()
    {
        using var workspace = new TestWorkspace();
        var configuration = CreateConfiguration(workspace, "vite");
        var watching = new DevOptions(null, "Debug", true, true, true, false, []);
        string build = string.Join(" ", DevApplication.CreateBuildArguments(configuration, watching));
        Contains(build, "-property:RunicApplicationFrontendBuildEnabled=false");
        Contains(build, "-property:RunicApplicationFrontendCopyEnabled=false");
        Contains(string.Join(" ", HostProcessController.CreateWatchArguments(configuration, watching)),
            "--property:RunicApplicationFrontendBuildEnabled=false");
        Contains(string.Join(" ", HostProcessController.CreateRestartBuildArguments(configuration, watching)),
            "-property:RunicApplicationFrontendCopyEnabled=false");

        var staticFrontend = new DevOptions(null, "Debug", true, false, true, false, []);
        DoesNotContain(string.Join(" ", DevApplication.CreateBuildArguments(configuration, staticFrontend)),
            "RunicApplicationFrontendBuildEnabled");
    }

    private sealed class FakeDoctorRuntime(CompatibilityToolchain toolchain) : IDoctorRuntime
    {
        internal const string VsWhere = @"C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe";

        public DoctorHostPlatform Platform { get; init; } = new("linux", "x64", false, "6.12");
        public HashSet<string> Executables { get; init; } = [];
        public HashSet<string> Libraries { get; init; } = [];
        public Dictionary<string, string> Environment { get; init; } = [];
        public string? WebView2Version { get; init; }
        public string? Gtk4Version { get; init; }
        public string? NodeVersion { get; init; }
        public HashSet<string> VisualStudioComponents { get; init; } = [];
        // No os-release by default, so remediation does not depend on the machine running the tests.
        public string OsReleasePath { get; init; } = Path.Combine(Path.GetTempPath(), "runic-tool-test-no-os-release");
        public string FallbackOsReleasePath { get; init; } = Path.Combine(Path.GetTempPath(), "runic-tool-test-no-os-release");
        public IReadOnlyList<string> OsReleasePaths => [OsReleasePath, FallbackOsReleasePath];

        // No files by default, so file checks do not depend on the machine running the tests.
        public HashSet<string> Files { get; init; } = [];
        public bool FileExists(string path) => Files.Contains(path);

        public string? GetEnvironmentVariable(string name) => Environment.GetValueOrDefault(name);

        public string? FindExecutable(string name) =>
            name.EndsWith("vswhere.exe", StringComparison.Ordinal) ? (Executables.Contains("vswhere") ? VsWhere : null)
            : name is "dotnet" or "node" or "npm" || Executables.Contains(name) ? name : null;

        public Task<bool> IsNativeLibraryAvailableAsync(string fileName, CancellationToken cancellationToken) =>
            Task.FromResult(Libraries.Contains(fileName));

        public string? GetWebView2RuntimeVersion() => WebView2Version;

        public Task<CommandResult> RunAsync(
            string executable, string workingDirectory, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            if (executable == "pkg-config")
            {
                return Task.FromResult(Gtk4Version is null
                    ? new CommandResult(1, string.Empty, "not found")
                    : new CommandResult(0, Gtk4Version + "\n", string.Empty));
            }
            if (executable == VsWhere)
            {
                string component = arguments[arguments.ToList().IndexOf("-requires") + 1];
                return Task.FromResult(new CommandResult(0,
                    VisualStudioComponents.Contains(component) ? @"C:\VS" : string.Empty, string.Empty));
            }
            string version = executable switch
            {
                "dotnet" => toolchain.DotNetSdk,
                "node" => NodeVersion ?? toolchain.Node,
                "npm" => toolchain.Npm,
                _ => throw new InvalidOperationException($"Unexpected executable {executable}"),
            };
            return Task.FromResult(new CommandResult(0, version, string.Empty));
        }
    }

    private static DoctorReport InspectTarget(
        DoctorProjectConfiguration project, string rid, FakeDoctorRuntime runtime) =>
        DoctorChecks.InspectAsync(project, "dotnet", runtime, CancellationToken.None, DoctorTargetRid.Parse(rid))
            .GetAwaiter().GetResult();

    private static DoctorCheck Check(DoctorReport report, string id) => report.Checks.Single(check => check.Name == id);

    private static string[] TargetCheckIds(DoctorReport report) =>
        [.. report.Checks.Select(check => check.Name).Where(name => name.StartsWith("target-", StringComparison.Ordinal))];

    [Test, DisplayName("doctor --rid accepts only runtime identifier syntax")]
    public void DoctorRidParsesRuntimeIdentifiers()
    {
        DoctorTargetRid linux = DoctorTargetRid.Parse("linux-musl-arm64");
        Equal("linux-musl", linux.Os);
        Equal("arm64", linux.Architecture);
        True(linux.Musl, "linux-musl is a musl target.");
        Equal("linux", linux.Family);
        Equal("osx", DoctorTargetRid.Parse("osx-arm64").Family);
        True(DoctorTargetRid.Parse("ubuntu.22.04-x64") is { IsPortable: false }, "Version-specific RIDs are not portable.");
        foreach (string invalid in new[] { "linux", "Linux-x64", "linux-x64 ", "-x64", "linux_x64", "../x64", "linux--x64" })
        {
            try
            {
                DoctorTargetRid.Parse(invalid);
                throw new InvalidOperationException($"'{invalid}' was accepted as a runtime identifier.");
            }
            catch (DevUsageException error)
            {
                Equal("RAPPCLI1011", error.Code);
            }
        }
    }

    [Test, DisplayName("doctor --rid rejects unsupported targets")]
    public void DoctorRidRejectsUnsupportedTargets()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority);
        var runtime = new FakeDoctorRuntime(authority.Toolchain);
        foreach (string rid in new[] { "android-arm64", "browser-wasm", "win-x86", "linux-arm", "ubuntu.22.04-x64", "win10-x64" })
        {
            DoctorReport report = InspectTarget(desktop, rid, runtime);
            Equal(DoctorStatus.Failure, Check(report, "target-rid").Status);
            SequenceEqual(["target-rid"], TargetCheckIds(report));
        }
        DoctorReport csWebUi = InspectTarget(desktop with { Host = RunicViewsHost.CsWebUi }, "win-arm64", runtime);
        Equal(DoctorStatus.Failure, Check(csWebUi, "target-rid").Status);
        Contains(Check(csWebUi, "target-rid").Message, "CS-WebUI");
        // Runic Desktop on win-arm64 and musl is CI-verified; osx-x64 is packaged but not CI-verified.
        Equal(DoctorStatus.Pass, Check(InspectTarget(desktop, "win-arm64", runtime), "target-rid").Status);
        Equal(DoctorStatus.Warning, Check(InspectTarget(desktop, "osx-x64", runtime), "target-rid").Status);
        foreach (string musl in new[] { "linux-musl-x64", "linux-musl-arm64" })
        {
            DoctorReport cswebuiMusl = InspectTarget(desktop with { Host = RunicViewsHost.CsWebUi }, musl, runtime);
            Equal(DoctorStatus.Failure, Check(cswebuiMusl, "target-rid").Status);
            Contains(Check(cswebuiMusl, "target-rid").Message, "glibc");
            SequenceEqual(["target-rid"], TargetCheckIds(cswebuiMusl));
            Equal(DoctorStatus.Pass, Check(InspectTarget(desktop, musl, runtime), "target-rid").Status);
        }
        Equal(DoctorStatus.Pass, Check(InspectTarget(desktop, "osx-arm64", runtime), "target-rid").Status);
    }

    [Test, DisplayName("doctor --rid follows the embedded support matrix")]
    public void DoctorRidFollowsSupportMatrix()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority);
        var runtime = new FakeDoctorRuntime(authority.Toolchain);
        SupportMatrix support = authority.Support;
        SequenceEqual(["desktop", "cswebui"], support.Hosts.Select(host => host.Id).ToArray());
        True(support.Requirements.Any(requirement => requirement.Id == "glibc" && requirement.Minimum == "2.34"),
            "The support matrix must record the CS-WebUI glibc baseline.");
        foreach (SupportHost host in support.Hosts)
        {
            DoctorProjectConfiguration project = desktop with
            {
                Host = host.Id == "desktop" ? RunicViewsHost.Desktop : RunicViewsHost.CsWebUi,
            };
            foreach (SupportTarget target in host.Targets)
            {
                DoctorCheck check = Check(InspectTarget(project, target.Rid, runtime), "target-rid");
                Equal(target.Status switch
                {
                    SupportStatus.CiVerified => DoctorStatus.Pass,
                    SupportStatus.PackagedUnverified => DoctorStatus.Warning,
                    _ => DoctorStatus.Failure,
                }, check.Status);
                if (target.Status != SupportStatus.CiVerified) Contains(check.Message, target.Reason);
                else Contains(check.Message, support.CiVerifiedMeaning);
            }
        }
        DoctorCheck unknownHost = Check(InspectTarget(desktop with { Host = RunicViewsHost.Unknown }, "osx-x64", runtime), "target-rid");
        Equal(DoctorStatus.Warning, unknownHost.Status);
        Contains(unknownHost.Message, "most permissive host");
    }

    [Test, DisplayName("doctor --rid checks a Linux Native AOT target")]
    public void DoctorRidChecksLinuxAot()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority)
            with { PublishAot = true };
        var ready = new FakeDoctorRuntime(authority.Toolchain)
        {
            Executables = ["clang", "objcopy"],
            Libraries = ["libgtk-3.so.0", "libwebkit2gtk-4.1.so.0"],
        };
        DoctorReport report = InspectTarget(desktop, "linux-x64", ready);
        SequenceEqual(["target-rid", "target-runtime-identifiers", "target-publish", "target-presentation"], TargetCheckIds(report));
        True(report.Checks.Where(check => check.Name.StartsWith("target-", StringComparison.Ordinal)).All(check => check.Status == DoctorStatus.Pass),
            "A Linux machine with clang and WebKitGTK passes a linux-x64 Native AOT target: " +
            string.Join("; ", report.Checks.Select(check => $"{check.Name}={check.Status}")));
        Contains(Check(report, "target-publish").Message, "clang");
        Contains(Check(report, "target-presentation").Message, "WebKitGTK 4.1");

        var bare = new FakeDoctorRuntime(authority.Toolchain) { Libraries = ["libgtk-3.so.0"] };
        DoctorReport missing = InspectTarget(desktop, "linux-x64", bare);
        Equal(DoctorStatus.Failure, Check(missing, "target-publish").Status);
        Equal(DoctorStatus.Warning, Check(missing, "target-presentation").Status);
        Contains(Check(missing, "target-presentation").Message, "libwebkit2gtk-4.1.so.0");
        DoesNotContain(Check(missing, "target-presentation").Message, "libgtk-3.so.0");

        Equal(DoctorStatus.Warning, Check(InspectTarget(desktop, "linux-arm64", ready), "target-publish").Status);
        Equal(DoctorStatus.Warning, Check(InspectTarget(desktop, "linux-musl-x64", ready), "target-publish").Status);
        DoctorCheck noObjcopy = Check(InspectTarget(desktop, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Executables = ["clang"] }), "target-publish");
        Equal(DoctorStatus.Failure, noObjcopy.Status);
        Contains(noObjcopy.Message, "objcopy");
        DoctorCheck unstripped = Check(InspectTarget(desktop with { StripSymbols = false }, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Executables = ["clang"] }), "target-publish");
        Equal(DoctorStatus.Pass, unstripped.Status);
        DoesNotContain(unstripped.Message, "objcopy");
    }

    [Test, DisplayName("doctor --rid refuses cross-OS Native AOT")]
    public void DoctorRidRefusesCrossOsAot()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority)
            with { PublishAot = true };
        var linux = new FakeDoctorRuntime(authority.Toolchain) { Executables = ["clang"] };
        foreach (string rid in new[] { "win-x64", "osx-arm64" })
        {
            DoctorReport report = InspectTarget(desktop, rid, linux);
            DoctorCheck publish = Check(report, "target-publish");
            Equal(DoctorStatus.Failure, publish.Status);
            Contains(publish.Message, "does not cross operating systems");
            Contains(publish.Remediation ?? string.Empty, "PublishAot=false");
            DoctorCheck presentation = Check(report, "target-presentation");
            Equal(DoctorStatus.Pass, presentation.Status);
            Contains(presentation.Message, "cannot inspect");
        }
        var mac = new FakeDoctorRuntime(authority.Toolchain) { Platform = new("osx", "arm64", false, "15.4") };
        DoctorReport noXcode = InspectTarget(desktop, "osx-arm64", mac);
        Equal(DoctorStatus.Failure, Check(noXcode, "target-publish").Status);
        Contains(Check(noXcode, "target-presentation").Message, "15.4");
        var xcode = new FakeDoctorRuntime(authority.Toolchain) { Platform = mac.Platform, Executables = ["xcrun"] };
        Equal(DoctorStatus.Pass, Check(InspectTarget(desktop, "osx-x64", xcode), "target-publish").Status);
    }

    [Test, DisplayName("doctor --rid checks framework-dependent and RuntimeIdentifiers")]
    public void DoctorRidChecksFrameworkDependent()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority);
        var runtime = new FakeDoctorRuntime(authority.Toolchain);
        DoctorCheck dependent = Check(InspectTarget(desktop, "win-x64", runtime), "target-publish");
        Equal(DoctorStatus.Pass, dependent.Status);
        Contains(dependent.Message, ".NET 10 and ASP.NET Core 10 runtimes");
        Contains(Check(InspectTarget(desktop with { SelfContained = true }, "win-x64", runtime), "target-publish").Message,
            "no .NET installation");
        DoesNotContain(Check(InspectTarget(desktop with { Host = RunicViewsHost.CsWebUi }, "win-x64", runtime), "target-publish").Message,
            "ASP.NET Core");
        // A multi-targeted project has no single TargetFramework in its outer evaluation.
        Contains(Check(InspectTarget(desktop with { TargetFramework = string.Empty }, "win-x64", runtime), "target-publish").Message,
            "the .NET and ASP.NET Core runtimes");

        DoctorProjectConfiguration declared = desktop with { RuntimeIdentifiers = "linux-x64; osx-arm64" };
        Equal(DoctorStatus.Pass, Check(InspectTarget(declared, "osx-arm64", runtime), "target-runtime-identifiers").Status);
        DoctorCheck absent = Check(InspectTarget(declared, "win-x64", runtime), "target-runtime-identifiers");
        Equal(DoctorStatus.Warning, absent.Status);
        Contains(absent.Remediation ?? string.Empty, "win-x64");
    }

    [Test, DisplayName("doctor --rid checks GTK 4 and WebKitGTK 6")]
    public void DoctorRidChecksGtk4()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority)
            with { UsesGtk4 = true };
        string[] gtk4 = ["libgtk-4.so.1", "libwebkitgtk-6.0.so.4"];
        DoctorCheck current = Check(InspectTarget(desktop, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. gtk4], Executables = ["pkg-config"], Gtk4Version = "4.22.4" }),
            "target-presentation");
        Equal(DoctorStatus.Pass, current.Status);
        Contains(current.Message, "GTK 4.22.4");
        DoctorCheck old = Check(InspectTarget(desktop, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. gtk4], Executables = ["pkg-config"], Gtk4Version = "4.10.1" }),
            "target-presentation");
        Equal(DoctorStatus.Warning, old.Status);
        Contains(old.Message, "4.12");
        Equal(DoctorStatus.Pass, Check(InspectTarget(desktop, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. gtk4] }), "target-presentation").Status);
        Equal(DoctorStatus.Warning, Check(InspectTarget(desktop, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Libraries = ["libgtk-3.so.0", "libwebkit2gtk-4.1.so.0"] }), "target-presentation").Status);

        using JsonDocument evaluation = JsonDocument.Parse("""{"Items":{"PackageReference":[{"Identity":"Runic.Application.Views.Desktop"},{"Identity":"Runic.Desktop.Gtk4"}]}}""");
        True(DoctorProjectConfiguration.References(evaluation.RootElement, DoctorProjectConfiguration.DesktopGtk4Package),
            "A Runic.Desktop.Gtk4 package reference selects the GTK 4 checks.");
    }

    [Test, DisplayName("doctor lists every missing GTK 4 profile piece")]
    public void DoctorGtk4ProfileListsEveryMissingPiece()
    {
        using var workspace = new TestWorkspace();
        // A custom composition on Runic.Desktop has no native window services: only Runic.Application.Views.Desktop creates
        // them, because the providers keep them internal (W319-003 D33).
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, null, out var authority)
            with { UsesGtk4 = true, DirectReferences = ["Runic.Desktop.Gtk4"] };
        DoctorReport Inspect(DoctorProjectConfiguration project, FakeDoctorRuntime runtime) =>
            DoctorChecks.InspectAsync(project, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult();

        DoctorCheck bare = Check(Inspect(desktop, new FakeDoctorRuntime(authority.Toolchain)), "gtk4-profile");
        Equal(DoctorStatus.Warning, bare.Status);
        foreach (string piece in new[] { "Runic.Application.Views.Desktop", "libgtk-4.so.1", "libwebkitgtk-6.0.so.4" })
        {
            Contains(bare.Message, piece);
        }
        Contains(bare.Message, "3 piece(s)");
        Contains(bare.Remediation ?? string.Empty, "reference Runic.Application.Views.Desktop");
        Contains(bare.Remediation ?? string.Empty, "WithGtk4()");
        DoesNotContain(bare.Remediation ?? string.Empty, "CreatePortalWindowOwner");

        // The provider packages alone do not complete the profile.
        string[] gtk4 = ["libgtk-4.so.1", "libwebkitgtk-6.0.so.4"];
        DoctorProjectConfiguration providers = desktop with { DirectReferences = ["Runic.Desktop.Gtk4", "Runic.Platform.Linux.Gtk4", "Runic.Platform.Linux.Portal"] };
        DoctorCheck old = Check(Inspect(providers, new FakeDoctorRuntime(authority.Toolchain)
        {
            Libraries = [.. gtk4], Executables = ["pkg-config"], Gtk4Version = "4.10.1",
        }), "gtk4-profile");
        Equal(DoctorStatus.Warning, old.Status);
        Contains(old.Message, "2 piece(s)");
        Contains(old.Message, "Runic.Application.Views.Desktop");
        Contains(old.Message, "GTK 4.10.1");

        // Runic.Application.Views.Desktop brings the platform providers and selects the GTK 4 one, even before restore.
        DoctorProjectConfiguration complete = desktop with { DirectReferences = ["Runic.Application.Views.Desktop", "Runic.Desktop.Gtk4"] };
        Equal(DoctorStatus.Pass, Check(Inspect(complete, new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. gtk4] }), "gtk4-profile").Status);

        // Native libraries are not checked on other machines, and --rid reports them under target-presentation.
        DoctorHostPlatform windows = new("win", "x64", false, "10.0.26100");
        Equal(DoctorStatus.Pass, Check(Inspect(complete, new FakeDoctorRuntime(authority.Toolchain) { Platform = windows }), "gtk4-profile").Status);
        DoctorCheck target = Check(InspectTarget(complete, "linux-x64", new FakeDoctorRuntime(authority.Toolchain)), "gtk4-profile");
        Equal(DoctorStatus.Pass, target.Status);

        False(Inspect(desktop with { UsesGtk4 = false }, new FakeDoctorRuntime(authority.Toolchain)).Checks.Any(static check => check.Name == "gtk4-profile"),
            "Projects without Runic.Desktop.Gtk4 get no GTK 4 profile check.");

        using JsonDocument evaluation = JsonDocument.Parse("""{"Items":{"ProjectReference":[{"Identity":"../Runic.Platform.Linux.Gtk4/Runic.Platform.Linux.Gtk4.csproj"}]}}""");
        SequenceEqual(["Runic.Platform.Linux.Gtk4"], DoctorProjectConfiguration.ReferenceIdentities(evaluation.RootElement));

        // Restored transitively, the host counts too.
        Write(desktop.ProjectAssetsFile, JsonSerializer.Serialize(new
        {
            libraries = new Dictionary<string, object> { ["Runic.Application.Views.Desktop/1.0.0"] = new { type = "package" } },
        }));
        Equal(DoctorStatus.Pass, Check(Inspect(desktop, new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. gtk4] }), "gtk4-profile").Status);

        using var hosted = new TestWorkspace();
        DoctorProjectConfiguration application = CreateDoctorProject(hosted, null, out _)
            with { Host = RunicViewsHost.Desktop, UsesGtk4 = true, DirectReferences = ["Runic.Application.Views.Desktop", "Runic.Desktop.Gtk4"] };
        DoctorCheck libraries = Check(Inspect(application, new FakeDoctorRuntime(authority.Toolchain)), "gtk4-profile");
        Equal(DoctorStatus.Warning, libraries.Status);
        Contains(libraries.Message, "2 piece(s)");
        DoesNotContain(libraries.Message, "Runic.Application.Views.Desktop");
        Contains(libraries.Remediation ?? string.Empty, "AddRunicPlatformServices()");
        Equal(DoctorStatus.Pass, Check(Inspect(application, new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. gtk4] }), "gtk4-profile").Status);
    }

    [Test, DisplayName("doctor names GTK 4 packages for the distribution")]
    public void DoctorGtk4ProfileNamesDistributionPackages()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority)
            with { UsesGtk4 = true, DirectReferences = ["Runic.Desktop.Gtk4", "Runic.Platform.Linux.Gtk4", "Runic.Platform.Linux.Portal"] };
        DoctorCheck Inspect(string? osRelease, params string[] libraries)
        {
            string path = osRelease is null ? Path.Combine(workspace.Root, "no-os-release") : workspace.Write("os-release", osRelease);
            var runtime = new FakeDoctorRuntime(authority.Toolchain) { Libraries = [.. libraries], OsReleasePath = path };
            return Check(DoctorChecks.InspectAsync(desktop, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult(), "gtk4-profile");
        }

        foreach ((string osRelease, string packages) in new[]
        {
            ("ID=ubuntu\nID_LIKE=debian\n", "libgtk-4-1 and libwebkitgtk-6.0-4"),
            ("ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", "libgtk-4-1 and libwebkitgtk-6.0-4"),
            ("ID=fedora\n", "gtk4 and webkitgtk6.0"),
            ("ID=arch\n", "gtk4 and webkitgtk-6.0"),
            ("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", "libgtk-4-1 and libwebkitgtk-6_0-4"),
            ("ID=nixos\n", "gtk4 and webkitgtk_6_0 and put their lib directories on the loader path"),
        })
        {
            DoctorCheck check = Inspect(osRelease);
            Equal(DoctorStatus.Warning, check.Status);
            Contains(check.Remediation ?? string.Empty, packages);
        }

        // /usr/lib/os-release is read when /etc/os-release is absent.
        DoctorCheck fallback = Check(DoctorChecks.InspectAsync(desktop, "dotnet", new FakeDoctorRuntime(authority.Toolchain)
        {
            OsReleasePath = Path.Combine(workspace.Root, "no-os-release"),
            FallbackOsReleasePath = workspace.Write("usr-lib-os-release", "ID=fedora\n"),
        }, CancellationToken.None).GetAwaiter().GetResult(), "gtk4-profile");
        Contains(fallback.Remediation ?? string.Empty, "on Fedora: gtk4 and webkitgtk6.0)");

        // Only the missing library's package is named.
        DoctorCheck webKitOnly = Inspect("ID=fedora\n", "libgtk-4.so.1");
        Contains(webKitOnly.Remediation ?? string.Empty, "on Fedora: webkitgtk6.0)");
        DoesNotContain(webKitOnly.Message, "libgtk-4.so.1");

        // An unknown or absent os-release names the sonames instead.
        foreach (string? osRelease in new[] { "ID=plan9\n", null })
        {
            string remediation = Inspect(osRelease).Remediation ?? string.Empty;
            Contains(remediation, "libgtk-4.so.1 and libwebkitgtk-6.0.so.4 or libwebkitgtk-6.0.so.0");
            DoesNotContain(remediation, "(on ");
        }

        // The older WebKitGTK 6.0 soname satisfies the profile, as it does at runtime.
        Equal(DoctorStatus.Pass, Inspect(null, "libgtk-4.so.1", "libwebkitgtk-6.0.so.0").Status);
        Equal(DoctorStatus.Pass, Check(InspectTarget(desktop, "linux-x64", new FakeDoctorRuntime(authority.Toolchain)
        {
            Libraries = ["libgtk-4.so.1", "libwebkitgtk-6.0.so.0"],
        }), "target-presentation").Status);
        Contains(Check(InspectTarget(desktop, "linux-x64", new FakeDoctorRuntime(authority.Toolchain)
        {
            OsReleasePath = workspace.Write("os-release", "ID=debian\n"),
        }), "target-presentation").Remediation ?? string.Empty, "on Debian/Ubuntu: libgtk-4-1 and libwebkitgtk-6.0-4");
    }

    // Runic.Platform.Linux has no public API since W319-003 D33: a GTK 4 project that references it cannot call its GTK 3
    // services, and Runic.Application.Views.Desktop never loads them in a GTK 4 process.
    [Test, DisplayName("doctor does not warn about a Runic.Platform.Linux reference in a GTK 4 project")]
    public void DoctorGtk4ProfileIgnoresGtk3Package()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority)
            with { UsesGtk4 = true, DirectReferences = ["Runic.Application.Views.Desktop", "Runic.Desktop.Gtk4", "Runic.Platform.Linux"] };
        var runtime = new FakeDoctorRuntime(authority.Toolchain) { Libraries = ["libgtk-4.so.1", "libwebkitgtk-6.0.so.4"] };
        DoctorCheck check = Check(DoctorChecks.InspectAsync(desktop, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult(), "gtk4-profile");
        Equal(DoctorStatus.Pass, check.Status);
        DoesNotContain(check.Message, "libgtk-3");
    }

    [Test, DisplayName("doctor names the platform-services provider and checks the portal")]
    public void DoctorReportsPlatformServicesProvider()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority);
        DoctorCheck Inspect(DoctorProjectConfiguration project, FakeDoctorRuntime runtime) =>
            Check(DoctorChecks.InspectAsync(project, "dotnet", runtime, CancellationToken.None).GetAwaiter().GetResult(), "platform-services");

        Contains(Inspect(desktop, new FakeDoctorRuntime(authority.Toolchain) { Platform = new("win", "x64", false, "10.0.26100") }).Message, "Windows provider");
        Contains(Inspect(desktop, new FakeDoctorRuntime(authority.Toolchain) { Platform = new("osx", "arm64", false, "15.0") }).Message, "macOS provider");

        // Linux needs a session bus and an installed xdg-desktop-portal.
        DoctorCheck missing = Inspect(desktop, new FakeDoctorRuntime(authority.Toolchain));
        Equal(DoctorStatus.Warning, missing.Status);
        Contains(missing.Message, "GTK 3");
        Contains(missing.Message, "no D-Bus session bus");
        Contains(missing.Message, "xdg-desktop-portal is not installed");
        Contains(missing.Remediation ?? string.Empty, "xdg-desktop-portal-gnome");
        var session = new FakeDoctorRuntime(authority.Toolchain)
        {
            Environment = { ["XDG_RUNTIME_DIR"] = "/run/user/1000", ["HOME"] = "/home/user" },
            Files = ["/run/user/1000/bus", "/usr/share/" + DoctorChecks.PortalServiceFile],
        };
        DoctorCheck ready = Inspect(desktop with { UsesGtk4 = true }, session);
        Equal(DoctorStatus.Pass, ready.Status);
        Contains(ready.Message, "parented through GTK 4");
        // XDG_DATA_DIRS replaces the default system directories; XDG_DATA_HOME is searched too.
        var custom = new FakeDoctorRuntime(authority.Toolchain)
        {
            Environment = { ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/tmp/bus", ["XDG_DATA_DIRS"] = "/nix/profile/share", ["HOME"] = "/home/user" },
            Files = ["/usr/share/" + DoctorChecks.PortalServiceFile],
        };
        Equal(DoctorStatus.Warning, Inspect(desktop, custom).Status);
        custom.Files.Add("/home/user/.local/share/" + DoctorChecks.PortalServiceFile);
        Equal(DoctorStatus.Pass, Inspect(desktop, custom).Status);

        using var webUi = new TestWorkspace();
        DoctorCheck csWebUi = Inspect(CreateDoctorProject(webUi, "Runic.Application.Views.CsWebUi", out _), new FakeDoctorRuntime(authority.Toolchain));
        Equal(DoctorStatus.Pass, csWebUi.Status);
        Contains(csWebUi.Message, "OwnerUnavailable");
        False(InspectTarget(desktop, "linux-x64", new FakeDoctorRuntime(authority.Toolchain)).Checks.Any(static check => check.Name == "platform-services"),
            "--rid inspected this machine's platform services.");
    }

    [Test, DisplayName("doctor --rid checks Windows WebView2 and C++ tools")]
    public void DoctorRidChecksWindows()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration desktop = CreateDoctorProject(workspace, "Runic.Application.Views.Desktop", out var authority)
            with { PublishAot = true };
        DoctorHostPlatform windows = new("win", "x64", false, "10.0.26100");
        var bare = new FakeDoctorRuntime(authority.Toolchain) { Platform = windows };
        DoctorReport missing = InspectTarget(desktop, "win-x64", bare);
        Equal(DoctorStatus.Failure, Check(missing, "target-publish").Status);
        Equal(DoctorStatus.Warning, Check(missing, "target-presentation").Status);
        Contains(Check(missing, "target-presentation").Remediation ?? string.Empty, "WebView2 Runtime");

        var installed = new FakeDoctorRuntime(authority.Toolchain)
        {
            Platform = windows,
            Environment = new() { ["ProgramFiles(x86)"] = @"C:\Program Files (x86)" },
            Executables = ["vswhere"],
            VisualStudioComponents = ["Microsoft.VisualStudio.Component.VC.Tools.x86.x64"],
            WebView2Version = "141.0.3537.71",
        };
        DoctorReport x64 = InspectTarget(desktop, "win-x64", installed);
        Equal(DoctorStatus.Pass, Check(x64, "target-publish").Status);
        Contains(Check(x64, "target-presentation").Message, "141.0.3537.71");
        DoctorCheck arm64 = Check(InspectTarget(desktop, "win-arm64", installed), "target-publish");
        Equal(DoctorStatus.Failure, arm64.Status);
        Contains(arm64.Remediation ?? string.Empty, "VC.Tools.ARM64");
    }

    [Test, DisplayName("doctor --rid checks CS-WebUI presentation")]
    public void DoctorRidChecksCsWebUi()
    {
        using var workspace = new TestWorkspace();
        DoctorProjectConfiguration csWebUi = CreateDoctorProject(workspace, "Runic.Application.Views.CsWebUi", out var authority);
        DoctorCheck browser = Check(InspectTarget(csWebUi, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Executables = ["chromium"] }), "target-presentation");
        Equal(DoctorStatus.Pass, browser.Status);
        Contains(browser.Message, "Chromium-family browser");
        Equal(DoctorStatus.Pass, Check(InspectTarget(csWebUi, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain) { Libraries = ["libgtk-3.so.0", "libwebkit2gtk-4.1.so.0"] }), "target-presentation").Status);
        DoctorCheck neither = Check(InspectTarget(csWebUi, "linux-x64", new FakeDoctorRuntime(authority.Toolchain)), "target-presentation");
        Equal(DoctorStatus.Warning, neither.Status);
        Contains(neither.Remediation ?? string.Empty, "WebKitGTK 4.1");
        Equal(DoctorStatus.Pass, Check(InspectTarget(csWebUi, "linux-arm64", new FakeDoctorRuntime(authority.Toolchain)), "target-rid").Status);
        Equal(DoctorStatus.Warning, Check(InspectTarget(csWebUi, "osx-x64", new FakeDoctorRuntime(authority.Toolchain)), "target-rid").Status);
        Equal(DoctorStatus.Warning, Check(InspectTarget(csWebUi with { Host = RunicViewsHost.Unknown }, "linux-x64",
            new FakeDoctorRuntime(authority.Toolchain)), "target-presentation").Status);
    }

    [Test, DisplayName("doctor --rid reports its target through the CLI")]
    public void DoctorRidThroughCli()
    {
        using var workspace = new TestWorkspace();
        string project = CreateCliDoctorProject(workspace);
        (int exitCode, string output) = RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "never", "--rid", "osx-arm64");
        Equal(0, exitCode);
        var response = Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(output), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult);
        DoctorCommandResult payload = response.Payload!;
        Equal("osx-arm64", payload.Target);
        Equal("pass", payload.Checks.Single(check => check.Id == "target-rid").Status);
        True(payload.Checks.Any(check => check.Id == "target-presentation"), "The target adds presentation checks.");

        (int invalidExit, string invalidOutput) = RunCli("doctor", "--project", project, "--format", "json", "--rid", "Linux_x64");
        Equal(2, invalidExit);
        using JsonDocument invalid = JsonDocument.Parse(invalidOutput);
        Equal("RAPPCLI1011", invalid.RootElement.GetProperty("fault").GetProperty("code").GetString());
        Equal(0, RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "never", "--runtime", "linux-x64").ExitCode);
        (int aliasExit, string aliasOutput) = RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "never", "-r", "win-x64");
        Equal(0, aliasExit);
        Equal("win-x64", Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(aliasOutput), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult).Payload!.Target);
        (int aotExit, string aotOutput) = RunCli("doctor", "--project", project, "--format", "json", "--aot");
        Equal(2, aotExit);
        using JsonDocument aotWithoutRid = JsonDocument.Parse(aotOutput);
        Equal("RAPPCLI1011", aotWithoutRid.RootElement.GetProperty("fault").GetProperty("code").GetString());
    }

    [Test, DisplayName("doctor --rid evaluates the project as dotnet publish -r does")]
    public void DoctorRidEvaluatesLikePublish()
    {
        using var workspace = new TestWorkspace();
        string project = workspace.Write("App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RunicApplicationFrontendWindowProject>true</RunicApplicationFrontendWindowProject>
              </PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' == 'win-x64'">
                <PublishAot>true</PublishAot>
              </PropertyGroup>
              <PropertyGroup Condition="'$(Configuration)' == 'Release'">
                <PublishSelfContained>true</PublishSelfContained>
                <StripSymbols>false</StripSymbols>
              </PropertyGroup>
            </Project>
            """);
        workspace.Write("Frontend/package.json", """{"packageManager":"npm@10.0.0"}""");
        DoctorProjectConfiguration Evaluate(string configuration, string rid, bool? aot = null, bool? selfContained = null) =>
            DoctorProjectConfiguration.EvaluateAsync("dotnet", project, configuration, CancellationToken.None,
                DoctorProjectConfiguration.PublishProperties(DoctorTargetRid.Parse(rid), aot, selfContained))
                .GetAwaiter().GetResult();

        DoctorProjectConfiguration windows = Evaluate("Release", "win-x64");
        True(windows.PublishAot, "A RID-conditioned PublishAot applies to its RID.");
        True(windows.SelfContained, "PublishSelfContained applies while publishing.");
        False(windows.StripSymbols, "An explicit StripSymbols=false is evaluated.");
        DoctorProjectConfiguration linux = Evaluate("Release", "linux-x64");
        False(linux.PublishAot, "A RID-conditioned PublishAot does not apply to another RID.");
        True(linux.SelfContained, "A Release-conditioned PublishSelfContained applies in Release.");
        DoctorProjectConfiguration debug = Evaluate("Debug", "linux-x64");
        False(debug.SelfContained, "A Release-conditioned PublishSelfContained does not apply in Debug.");
        True(debug.StripSymbols, "StripSymbols defaults to true.");
        DoctorProjectConfiguration switches = Evaluate("Debug", "linux-x64", aot: true, selfContained: true);
        True(switches.PublishAot, "--aot sets PublishAot.");
        True(switches.SelfContained, "--self-contained sets SelfContained.");
        False(Evaluate("Release", "win-x64", aot: false).PublishAot, "--no-aot overrides a project PublishAot.");
        False(Evaluate("Release", "linux-x64", selfContained: false).SelfContained, "--no-self-contained overrides a project PublishSelfContained.");

        // Without -c, --rid evaluates Release like dotnet publish.
        (int exitCode, string output) = RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "never", "--rid", "linux-x64");
        Equal(0, exitCode);
        DoctorCommandResult payload = Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(output), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult).Payload!;
        Contains(payload.Checks.Single(check => check.Id == "target-publish").Message, "self-contained");
        (_, string debugOutput) = RunCli("doctor", "--project", project, "--format", "json", "--fail-on", "never", "--rid", "linux-x64", "-c", "Debug");
        Contains(Runic.CommandLine.CommandJsonEnvelopeReader.Read(
            System.Text.Encoding.UTF8.GetBytes(debugOutput), DoctorCommandResult.PayloadType,
            DoctorCommandJsonContext.Default.DoctorCommandResult).Payload!.Checks.Single(check => check.Id == "target-publish").Message,
            "framework-dependent");
    }

    [Test, DisplayName("compatibility authority includes the public Views packages")]
    public void CompatibilityAuthorityIncludesViews()
    {
        CompatibilitySetAuthority authority = CompatibilitySetAuthority.Current;
        True(authority.NuGetPackages.ContainsKey("Runic.Application.Views.CsWebUi"),
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

    [Test, DisplayName("stopping the host signals dotnet run, not dotnet watch")]
    public void StopSignalsDotNetRunNotWatch()
    {
        // The process tree that dotnet watch --no-hot-reload starts for a Window, as ps reports it.
        ProcessTreeSignal.ProcessEntry[] processes =
        [
            new(100, 1, "dotnet watch --no-hot-reload --non-interactive run --no-launch-profile"),
            new(101, 100, "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/DotnetTools/dotnet-watch/10.0.100/tools/net10.0/any/dotnet-watch.dll --no-hot-reload run"),
            new(102, 101, "/usr/share/dotnet/dotnet run --no-launch-profile"),
            new(103, 102, "/home/user/App/bin/Debug/net10.0/App"),
            new(104, 103, "/usr/lib/webkitgtk-6.0/WebKitWebProcess 7 12"),
            new(105, 101, "/usr/share/dotnet/dotnet /usr/share/dotnet/sdk/10.0.100/MSBuild.dll -nodemode:1"),
        ];
        SequenceEqual<int>([102], ProcessTreeSignal.SelectStopTargets(100, processes));
        // dotnet run --no-build without watch is signalled itself, and so is a tree without dotnet run.
        SequenceEqual<int>([102], ProcessTreeSignal.SelectStopTargets(102, processes));
        SequenceEqual<int>([103], ProcessTreeSignal.SelectStopTargets(103, processes));
    }

    [Test, DisplayName("size names a new report for each run without --report")]
    public void SizeDefaultReportIsUnique()
    {
        using var workspace = new TestWorkspace();
        var now = new DateTimeOffset(2026, 10, 20, 9, 15, 0, TimeSpan.FromHours(2));
        string first = SizeApplication.DefaultReportPath(workspace.Root, "linux-x64", now);
        Equal(Path.Combine(workspace.Root, "runic-size-linux-x64-20261020T071500Z.json"), first);
        File.WriteAllText(first, "{}");
        string second = SizeApplication.DefaultReportPath(workspace.Root, "linux-x64", now);
        Equal(Path.Combine(workspace.Root, "runic-size-linux-x64-20261020T071500Z-2.json"), second);
        File.WriteAllText(second, "{}");
        Equal(Path.Combine(workspace.Root, "runic-size-linux-x64-20261020T071500Z-3.json"), SizeApplication.DefaultReportPath(workspace.Root, "linux-x64", now));
    }

    [Test, DisplayName("the Linux process list comes from /proc, without ps")]
    public void ProcSnapshotWithoutPs()
    {
        // W319-003 L1: without procps (minimal containers, the Nix sandbox) the stop neither waited nor signalled.
        using var workspace = new TestWorkspace();
        string proc = Path.Combine(workspace.Root, "proc");
        void Process(string pid, string? stat, string cmdline)
        {
            Directory.CreateDirectory(Path.Combine(proc, pid));
            if (stat is not null) File.WriteAllText(Path.Combine(proc, pid, "stat"), stat);
            File.WriteAllText(Path.Combine(proc, pid, "cmdline"), cmdline);
        }
        Process("100", "100 (dotnet) S 1 100 100 0 -1 4194560 0 0", "dotnet\0watch\0run\0");
        // The command name may contain spaces and parentheses.
        Process("102", "102 (a (b) c) S 100 100 100 0 -1", "/usr/share/dotnet/dotnet\0run\0--no-launch-profile\0");
        Process("2", "2 (kthreadd) S 0 0 0 0 -1", "");
        Process("103", null, "exited\0");
        Process("104", "104 (truncated", "");
        Directory.CreateDirectory(Path.Combine(proc, "self-not-a-pid"));
        File.WriteAllText(Path.Combine(proc, "uptime"), "1.0 2.0");

        var processes = ProcessTreeSignal.ReadProcSnapshot(proc).OrderBy(process => process.Id).ToArray();
        SequenceEqual(
            [new ProcessTreeSignal.ProcessEntry(2, 0, ""), new(100, 1, "dotnet watch run"), new(102, 100, "/usr/share/dotnet/dotnet run --no-launch-profile")],
            processes);
        SequenceEqual<int>([102], ProcessTreeSignal.SelectStopTargets(100, processes));
        Equal<int?>(7, ProcessTreeSignal.ParseProcStatParent("8 (x) R 7 8 8"));
        Equal<int?>(null, ProcessTreeSignal.ParseProcStatParent("8 (x"));

        Directory.Delete(proc, recursive: true);
        Directory.CreateDirectory(proc);
        // An empty process list is an error, not an empty tree.
        Throws<IOException>(() => ProcessTreeSignal.ReadProcSnapshot(proc));

        if (OperatingSystem.IsLinux())
        {
            var self = ProcessTreeSignal.ReadProcSnapshot("/proc").Single(process => process.Id == Environment.ProcessId);
            True(self.ParentId > 0 && self.CommandLine.Length > 0, self.ToString());
        }
    }

    [Test, DisplayName("stopping the host says so when it cannot list the processes")]
    public async Task StopWarnsWithoutProcessList()
    {
        if (OperatingSystem.IsWindows()) return; // Windows raises Ctrl+C on the console instead.
        // No process has this ID, so it is missing from the list; the root is still asked to stop.
        const int Missing = int.MaxValue;
        var warnings = new List<string>();
        var targets = await ProcessTreeSignal.RequestStopAsync(Missing, CancellationToken.None, warnings.Add);
        SequenceEqual<int>([Missing], targets);
        True(warnings.Count == 1 && warnings[0].Contains($"only process {Missing} is asked to stop", StringComparison.Ordinal), string.Join('\n', warnings));
    }

    [Test, DisplayName("stopping the host lets the application exit before the tree is killed")]
    public void StopLetsApplicationExit()
    {
        using var workspace = new TestWorkspace();
        // Windows raises Ctrl+C for the whole hidden console, so the stand-in for dotnet watch is Unix-only.
        RunningProcess running = StartStopChild(OperatingSystem.IsWindows() ? "run" : "parent", workspace.Root);
        int application = WaitForStopChild(workspace.Root);
        var clock = Stopwatch.StartNew();
        True(running.StopGracefullyAsync(TimeSpan.FromSeconds(20)).Wait(TimeSpan.FromSeconds(40)), "The stop did not finish.");
        True(clock.Elapsed < TimeSpan.FromSeconds(15), $"The stop waited {clock.Elapsed} for an application that exited.");
        True(File.Exists(Path.Combine(workspace.Root, "stopped")), "The application was not asked to stop.");
        if (OperatingSystem.IsWindows()) Equal(StopTestChild.ApplicationExitCode, running.Completion.Result);
        WaitForExit(application);
        running.DisposeAsync().AsTask().Wait();
    }

    [Test, DisplayName("stopping the host kills an application that ignores the signal")]
    public void StopKillsApplicationAfterGrace()
    {
        using var workspace = new TestWorkspace();
        workspace.Write("stuck", "");
        RunningProcess running = StartStopChild(OperatingSystem.IsWindows() ? "run" : "parent", workspace.Root);
        int application = WaitForStopChild(workspace.Root);
        var clock = Stopwatch.StartNew();
        True(running.StopGracefullyAsync(TimeSpan.FromSeconds(1)).Wait(TimeSpan.FromSeconds(30)), "The stop did not finish.");
        True(clock.Elapsed >= TimeSpan.FromSeconds(1), "The stop did not wait for the grace period.");
        False(File.Exists(Path.Combine(workspace.Root, "stopped")), "The stuck application reported a stop.");
        WaitForExit(application);
        running.DisposeAsync().AsTask().Wait();
    }

    private static RunningProcess StartStopChild(string role, string markers)
    {
        (string executable, IReadOnlyList<string> prefix) = ProcessTreeSignal.SelfCommand();
        return RunningProcess.Start("stop-test", executable, markers, [.. prefix, StopTestChild.Argument, role, markers]);
    }

    private static int WaitForStopChild(string markers)
    {
        string ready = Path.Combine(markers, "ready");
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(ready) && deadline.Elapsed < TimeSpan.FromSeconds(30)) Thread.Sleep(50);
        True(File.Exists(ready), "The stand-in application did not start.");
        return int.Parse(File.ReadAllText(ready), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WaitForExit(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (!ProcessTreeSignal.HasExited(pid) && deadline.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(50);
        True(ProcessTreeSignal.HasExited(pid), $"Process {pid} is still running after the stop.");
    }

    // Proves the RunAsync wiring (Command Line's termination signal handling): a SIGTERM
    // while doctor waits for MSBuild cancels the command (RCLI4000, exit code 130) instead
    // of the default termination. A second signal is not sent: it could coalesce with the
    // first or arrive after the process has exited; Runic Command Line's tests cover the
    // forced exit and CommandExecutionContext.ProtectShutdown.
    [Test, DisplayName("SIGTERM cancels a running command through the tool's handler")]
    public void SigtermCancelsRunningCommand()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var workspace = new TestWorkspace();
        workspace.Write("App.csproj", "<Project />");
        string started = Path.Combine(workspace.Root, "started");
        string fakeHost = workspace.Write("fake-dotnet", $"#!/bin/sh\necho $$ > '{started}'\nexec sleep 60\n");
        File.SetUnixFileMode(fakeHost, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } dotnet ? dotnet : "dotnet")
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(typeof(Runic.Application.Tool.Program).Assembly.Location);
        startInfo.ArgumentList.Add("doctor");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add("App.csproj");
        startInfo.Environment["DOTNET_HOST_PATH"] = fakeHost;
        using Process tool = Process.Start(startInfo)!;
        Task<string> output = tool.StandardOutput.ReadToEndAsync();
        Task<string> error = tool.StandardError.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!(File.Exists(started) && File.ReadAllText(started).Length > 0) && deadline.Elapsed < TimeSpan.FromSeconds(30) && !tool.HasExited) Thread.Sleep(50);
            True(File.Exists(started), "doctor did not start the MSBuild evaluation.");
            using (Process kill = Process.Start("kill", ["-TERM", tool.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            {
                kill.WaitForExit();
            }
            True(tool.WaitForExit(TimeSpan.FromSeconds(30)), "doctor did not exit after SIGTERM.");
            string text = output.Result + error.Result;
            Equal(Runic.Application.Tool.Program.Cancelled, tool.ExitCode);
            Contains(text, "RCLI4000");
        }
        finally
        {
            if (!tool.HasExited) tool.Kill(entireProcessTree: true);
            // The tool should have stopped the fake MSBuild; never leave it behind.
            if (File.Exists(started) && int.TryParse(File.ReadAllText(started).Trim(), out int fakePid))
            {
                try { using Process fake = Process.GetProcessById(fakePid); fake.Kill(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
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

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
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
            ToolTests.Write(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
