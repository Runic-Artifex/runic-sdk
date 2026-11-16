using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool.WebKit;
using Runic.CommandLine;

namespace Runic.Application.Tool;

internal static class DevApplication
{
    /// <summary>The line dev prints for each Window process that starts: <c>[dev] Window started: process &lt;pid&gt;</c>.</summary>
    internal static string WindowStartedLine(int processId) =>
        $"[dev] Window started: process {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The line dev prints when that Window process exits: <c>[dev] Window exited: process &lt;pid&gt;</c>.</summary>
    internal static string WindowExitedLine(int processId) =>
        $"[dev] Window exited: process {processId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>The line dev prints once the frontend and a Window process can be used.</summary>
    internal const string ReadyLine = "[dev] Ready";

    /// <summary>
    /// The phase dev starts with; <see cref="PhaseTimer"/> prints <c>[dev] Evaluating Views Window project...</c>. The
    /// README's IDE contract names its prefix, <c>[dev] Evaluating</c>, which the template's tasks.json waits on.
    /// </summary>
    internal const string EvaluatingPhase = "Evaluating Views Window project";

    /// <summary>
    /// The line dev prints when it restarts the Window process itself. The README's IDE contract names its prefix,
    /// <c>[dev] Reloading</c>, which the template's tasks.json waits on.
    /// </summary>
    internal const string ReloadingLine = "[dev] Reloading the Runic Views Window application.";

    /// <summary>
    /// A failure as one line in MSBuild's canonical form, <c>&lt;project&gt; : error RAPPDEV1007: &lt;message&gt;</c>, which VS Code's
    /// <c>$msCompile</c> problem matcher and the regex in the README match like the build's own errors. The origin is
    /// the project file, or <c>dotnet-runic</c> before dev found one.
    /// </summary>
    internal static string DiagnosticLine(string? project, string code, string message) =>
        $"{(string.IsNullOrEmpty(project) ? "dotnet-runic" : project)} : error {code}: {message.ReplaceLineEndings(" ")}";

    /// <summary>
    /// The hint for a debugging endpoint the Window cannot open. WebView2 ignores WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS
    /// in an elevated process (Runtime 150 and later), so the session file leaves the endpoint out of debugEndpoints.
    /// </summary>
    internal static string UnavailableEndpointHint(DevSession.DebugEndpoint endpoint) =>
        $"[dev] Debugging: WebView2 ignores {DevSession.WebView2ArgumentsVariable} in an elevated process, so the Window " +
        $"opens no DevTools Protocol endpoint at {endpoint.Url}. Run dotnet runic dev (and your IDE) without elevation; see " +
        $"{SessionFileHelpUri}";

    /// <summary>The session file's section of the dotnet-runic README at this release.</summary>
    internal static string SessionFileHelpUri => RunicReleaseLinks.SourceUrl + "tools/dotnet-runic/README.md#session-file";

    internal static async Task<int> RunAsync(
        CommandExecutionContext context,
        DevOptions options,
        CancellationToken cancellationToken,
        Action<DevProjectConfiguration>? evaluated = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        string? project = null;
        try
        {
            ToolOptionValues.RequireWindowOptions(options);
            project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
            return await RunProjectAsync(context, options, project, evaluated, cancellationToken).ConfigureAwait(false);
        }
        catch (DevDevelopmentException error)
        {
            Console.Error.WriteLine(DiagnosticLine(project, error.Code, error.Message));
            throw;
        }
        catch (DevUsageException error)
        {
            Console.Error.WriteLine(DiagnosticLine(project, error.Code, error.Message));
            throw;
        }
    }

    private static async Task<int> RunProjectAsync(
        CommandExecutionContext context,
        DevOptions options,
        string project,
        Action<DevProjectConfiguration>? evaluated,
        CancellationToken cancellationToken)
    {
        string dotnetHost = ResolveDotNetHost();
        using PhaseTimer evaluation = PhaseTimer.Start(EvaluatingPhase);
        DevProjectConfiguration configuration = await DevProjectConfiguration
            .EvaluateAsync(dotnetHost, project, options.Configuration, cancellationToken)
            .ConfigureAwait(false);
        evaluation.Complete();
        WriteConfiguration(configuration, options);
        evaluated?.Invoke(configuration);
        RequireWindowFrontend(configuration, options);
        if (options.DryRun)
        {
            return Program.Success;
        }
        // Linux: WebKit's remote inspector for the Window and Runic's CDP adapter for JavaScript debuggers (D066.39).
        WebKitDebugging.Plan debugging = WebKitDebugging.Decide(configuration, options, Environment.GetEnvironmentVariable,
            OperatingSystem.IsLinux(), LoopbackPort.Reserve, WebKitDebugging.ReadInstalledVersion);
        options = options with { DebugEnvironment = debugging.WindowEnvironment };
        // On Linux the adapter accepts only connections of this user, and talks only to an inspector port of this user.
        LoopbackSockets? sockets = LoopbackSockets.Current;
        await using WebKitDebugEndpoint? adapter = debugging.StartAdapter
            ? new WebKitDebugEndpoint(debugging.InspectorHost!, debugging.InspectorPort, sockets, Console.Error.WriteLine)
            : null;
        var webKitEndpoints = new List<DevSession.DebugEndpoint>(debugging.Endpoints);
        var debuggingHints = new List<string>(debugging.Hints);
        if (adapter is not null)
        {
            // A fixed inspector port (--no-window, or the developer's) that another user holds already: say so now.
            if (sockets is not null && IPAddress.TryParse(debugging.InspectorHost, out IPAddress? inspectorAddress) &&
                sockets.ForeignListener(inspectorAddress, debugging.InspectorPort) is { } inspectorOwner)
            {
                debuggingHints.Add(WebKitDebugging.ForeignInspectorHint(debugging.InspectorHost!, debugging.InspectorPort, inspectorOwner));
            }
            // 9222 like WebView2's endpoint, so one attach configuration serves Windows and Linux; else any free port, but
            // not under --no-window, whose IDE attach configuration expects 9222 (#1065).
            if (adapter.Start(WebKitDebugging.DefaultCdpPort) || (options.StartWindow && adapter.Start(0)))
            {
                webKitEndpoints.Insert(0, WebKitDebugging.CdpEndpoint(adapter.Port!.Value));
                uint? defaultPortOwner = adapter.Port.Value == WebKitDebugging.DefaultCdpPort
                    ? null
                    : sockets?.ForeignListener(IPAddress.Loopback, WebKitDebugging.DefaultCdpPort);
                debuggingHints.AddRange(WebKitDebugging.CdpHints(adapter.Port.Value, debugging, defaultPortOwner));
            }
            else if (!options.StartWindow)
            {
                throw new DevDevelopmentException(WebKitDebugging.NoWindowCdpPortTakenCode, WebKitDebugging.NoWindowCdpPortTakenMessage(
                    sockets?.ForeignListener(IPAddress.Loopback, WebKitDebugging.DefaultCdpPort)));
            }
            else
            {
                debuggingHints.Add(WebKitDebugging.CdpUnavailableHint);
            }
        }
        // The session file says where the running session is, for IDEs; it is marked stopped when dev ends.
        var session = DevSession.Create(configuration, options, webKitEndpoints);
        foreach (DevSession.DebugEndpoint endpoint in session.UnavailableDebugEndpoints) Console.WriteLine(UnavailableEndpointHint(endpoint));
        foreach (string hint in debuggingHints) Console.WriteLine(hint);
        if (configuration.HasDevelopmentServer && options.WatchFrontend && session.DevToolsExtension is { } extension)
        {
            Console.WriteLine(DevToolsExtension.Hint(extension));
        }
        WriteSession(session.Start);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Stop the frontend and native host process trees on Ctrl+C and also when
        // an IDE or terminal ends only this process. A repeated signal must not
        // exit before those process trees are stopped. These handlers also stop on SIGHUP, which Command Line does
        // not handle.
        using IDisposable protectShutdown = context.ProtectShutdown();
        Action<PosixSignalContext> stopHandler = context =>
        {
            context.Cancel = true;
            stop.Cancel();
        };
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, stopHandler);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, stopHandler);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stopHandler);
        using var hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, stopHandler);
        // The .NET 11 SDK's dotnet CLI does not forward SIGTERM to dotnet runic, so its exit stops dev too.
        Task launcher = LauncherWatch.Start(() =>
        {
            Console.Error.WriteLine("[dev] The dotnet command that started dev exited; stopping the application.");
            stop.Cancel();
        }, stop.Token);
        try
        {
            if (!options.Restore && configuration.NodeEnabled)
            {
                RequireInstalledFrontendPackages(configuration);
            }
            // --no-build implies no .NET restore, as for dotnet run; the frontend packages still follow --no-restore.
            if (options.Restore && options.Build)
            {
                using var phase = PhaseTimer.Start("Restoring Views Window dependencies");
                await RequireSuccessAsync(dotnetHost, configuration.ProjectDirectory,
                    CreateRestoreArguments(configuration, options.Configuration, options.Verbosity),
                    "RAPPDEV1006", "The .NET restore failed:", CommandRunner.DoctorHint(configuration.ProjectPath), stop.Token).ConfigureAwait(false);
                phase.Complete();
            }
            if (configuration.NodeEnabled)
            {
                await InstallFrontendAsync(configuration, options.Restore, stop.Token).ConfigureAwait(false);
            }
            // Before the build, so a missing window entry fails without waiting for it.
            if (options.WatchFrontend && configuration.DevelopmentServerKind == "vite" &&
                !configuration.HasFrontendCompiler)
            {
                configuration = await ViteBuildInputs.ApplyAsync(configuration, stop.Token)
                    .ConfigureAwait(false);
                DevProjectConfiguration withPages = configuration;
                WriteSession(() => session.Update(withPages));
            }
            if (options.Build)
            {
                await BuildAsync(dotnetHost, configuration, options, stop.Token)
                    .ConfigureAwait(false);
            }
            if (!options.StartWindow)
            {
                return await RunFrontendOnlyAsync(dotnetHost, configuration, options, session, stop.Token).ConfigureAwait(false);
            }
            await RequireApplicationRuntimeAsync(dotnetHost, configuration, stop.Token).ConfigureAwait(false);
            return await RunDevelopmentLoopAsync(
                dotnetHost,
                configuration,
                options,
                session,
                stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopping dev with Ctrl+C is how it ends normally, but it is still a cancellation: 130, as every Runic
            // tool reports it, after the frontend and application process trees have stopped.
            return Program.Cancelled;
        }
        finally
        {
            stop.Cancel();
            // The watch ends on the cancellation, so stop is not used after it is disposed.
            await launcher.ConfigureAwait(false);
            session.Stop();
        }
    }

    private static async Task<int> RunDevelopmentLoopAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
        DevSession session,
        CancellationToken cancellationToken)
    {
        bool useDevelopmentServer =
            options.WatchFrontend && configuration.HasDevelopmentServer;
        await using IFrontendDevelopmentServer? developmentServer =
            useDevelopmentServer
                ? await StartDevelopmentServerAsync(
                    configuration,
                    cancellationToken).ConfigureAwait(false)
                : null;
        WriteSession(() => session.FrontendReady(
            developmentServer?.Origin,
            developmentServer is not null && ViteDevelopmentServer.ConfirmsSession(configuration)));
        if (developmentServer is not null)
        {
            WriteSession(() => session.PublishFrontend(configuration.DevelopmentServerDocuments));
        }
        await using var host = new HostProcessController(
            dotnetHost,
            configuration,
            options,
            developmentServer?.HostEnvironment);
        using (PhaseTimer phase = PhaseTimer.Start("Starting native application host"))
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            phase.Complete();
        }
        bool coordinateCompilerReload =
            configuration.DevelopmentServerKind == "vite" && options.WatchHost;
        FrontendCompilerHotReloadCoordinator? compilerReload =
            coordinateCompilerReload &&
            configuration.FrontendCompilerEnabled &&
            !string.IsNullOrWhiteSpace(configuration.FrontendCompilerHotReloadPath) &&
            File.Exists(configuration.FrontendCompilerHotReloadPath)
                ? FrontendCompilerHotReloadCoordinator.Create(configuration.FrontendCompilerHotReloadPath, host)
                : null;
        await using RunningProcess? frontend =
            options.WatchFrontend && !useDevelopmentServer && configuration.HasFrontendWatcher
                ? StartFrontendWatcher(dotnetHost, configuration, options.Configuration)
                : null;
        if (frontend is not null)
        {
            WriteSession(() => session.PublishFrontend([]));
        }

        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken monitorToken = monitorStop.Token;
        Task frontendRenewal = session.RenewFrontendAsync(TimeProvider.System, monitorToken);
        // The IDE contract: a line for each Window process, then Ready once the frontend and the Window can be used.
        var contract = new ContractReporter(session, Console.WriteLine, WriteSession);
        Task windowMonitor = host.MonitorWindowAsync(
            started: contract.WindowStarted,
            exited: contract.WindowExited,
            monitorToken);
        // Runic Views owns source contract generation; the CLI coordinates the frontend and native Window process
        // only. Without a development server, the Runic Desktop host serves the frontend output directory live.
        var compilerReloadMonitors = new List<Task>();
        if (compilerReload is not null)
        {
            compilerReloadMonitors.Add(compilerReload.WatchAsync(monitorToken));
        }
        Task frontendCompilerMonitor = options.WatchHost &&
            configuration.FrontendCompilerEnabled &&
            !string.IsNullOrWhiteSpace(configuration.FrontendCompilerWatchPattern) &&
            !string.IsNullOrWhiteSpace(configuration.FrontendCompilerHotReloadTarget)
            ? FilePoller.WatchTreeAsync(
                configuration.ProjectDirectory,
                configuration.FrontendCompilerWatchPattern,
                compilerReload is null
                    ? async token =>
                    {
                        await CompileFrontendAsync(
                            dotnetHost,
                            configuration,
                            options.Configuration,
                            token).ConfigureAwait(false);
                        await host.RestartAsync(token).ConfigureAwait(false);
                    }
                    : token => CompileFrontendAsync(
                        dotnetHost,
                        configuration,
                        options.Configuration,
                        token),
                monitorToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, monitorToken);
        Task cancellation = Task.Delay(Timeout.InfiniteTimeSpan, monitorToken);

        var observed = new List<Task>
        {
            host.Completion,
            frontendCompilerMonitor,
            cancellation,
        };
        observed.AddRange(compilerReloadMonitors);
        if (frontend is not null)
        {
            observed.Add(frontend.Completion);
        }
        if (developmentServer is not null)
        {
            observed.Add(developmentServer.Completion);
        }

        try
        {
            Task completed = await Task.WhenAny(observed).ConfigureAwait(false);
            // A terminal Ctrl+C reaches the child processes too, so one of
            // them can exit with 130 or 143 before the cancellation is seen.
            if (completed == cancellation || cancellationToken.IsCancellationRequested)
            {
                if (completed != cancellation) return Program.Cancelled;
                await cancellation.ConfigureAwait(false);
                return Program.Cancelled;
            }

            if (compilerReloadMonitors.Contains(completed) ||
                completed == frontendCompilerMonitor)
            {
                await completed.ConfigureAwait(false);
                return Program.DevelopmentFailure;
            }

            int exitCode = await ((Task<int>)completed).ConfigureAwait(false);
            if (completed == host.Completion &&
                (!options.WatchHost || exitCode == Program.Success))
            {
                return exitCode;
            }

            throw new DevDevelopmentException(
                "RAPPDEV1007",
                completed == host.Completion
                    ? $"The native host process ('{(options.WatchHost ? "dotnet watch" : "dotnet run")}') exited unexpectedly with code {exitCode}."
                    : completed == developmentServer?.Completion
                        ? $"The {configuration.DevelopmentServerKind} development server " +
                          $"exited unexpectedly with code {exitCode}."
                        : $"The frontend watcher exited unexpectedly with code {exitCode}.",
                completed == host.Completion
                    ? CommandRunner.LocalDetail(configuration.ProjectDirectory, CommandRunner.DoctorHint(configuration.ProjectPath))
                    : null);
        }
        finally
        {
            monitorStop.Cancel();
            var monitors = new List<Task>
            {
                frontendCompilerMonitor,
                cancellation,
                windowMonitor,
                frontendRenewal,
            };
            monitors.AddRange(compilerReloadMonitors);
            try
            {
                await Task.WhenAll(monitors).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // The selected completion already controls the result. Drain
                // every other callback before disposing the child processes.
            }
        }
    }

    /// <summary><c>--no-window</c> needs a frontend that dev serves or watches; otherwise dev would have nothing to run.</summary>
    internal static void RequireWindowFrontend(DevProjectConfiguration configuration, DevOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.StartWindow && !configuration.HasDevelopmentServer && !configuration.HasFrontendWatcher)
        {
            throw new DevDevelopmentException(ToolOptionValues.NoWindowFrontendCode,
                "--no-window needs a frontend that dev serves or watches: a Vite or Angular development server, a frontend " +
                "dev script or RunicApplicationFrontendDevWatchTarget. Start the Window with dotnet runic dev instead.");
        }
    }

    /// <summary>
    /// <c>--no-window</c>: the development server or frontend watcher, without the Window, until Ctrl+C or the frontend
    /// exits. The session file and the frontend marker tell the IDE's build and launch what the Window needs.
    /// </summary>
    private static async Task<int> RunFrontendOnlyAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
        DevSession session,
        CancellationToken cancellationToken)
    {
        await using IFrontendDevelopmentServer? developmentServer = configuration.HasDevelopmentServer
            ? await StartDevelopmentServerAsync(configuration, cancellationToken).ConfigureAwait(false)
            : null;
        await using RunningProcess? frontend = developmentServer is null
            ? StartFrontendWatcher(dotnetHost, configuration, options.Configuration)
            : null;
        WriteSession(() => session.FrontendReady(
            developmentServer?.Origin,
            developmentServer is not null && ViteDevelopmentServer.ConfirmsSession(configuration)));
        WriteSession(() => session.PublishFrontend(developmentServer is null ? [] : configuration.DevelopmentServerDocuments));
        Console.WriteLine("[dev] Window: not started (--no-window). Start it from your IDE with DOTNET_ENVIRONMENT=Development;");
        Console.WriteLine($"[dev] its builds keep the development pages while dev runs. Session: {session.Path}");
        new ContractReporter(session, Console.WriteLine, WriteSession).Ready();

        using var renewalStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewal = session.RenewFrontendAsync(TimeProvider.System, renewalStop.Token);
        Task cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task<int> served = developmentServer?.Completion ?? frontend!.Completion;
        try
        {
            Task completed = await Task.WhenAny(served, cancellation).ConfigureAwait(false);
            if (completed == cancellation || cancellationToken.IsCancellationRequested)
            {
                return Program.Cancelled;
            }
            int exitCode = await served.ConfigureAwait(false);
            throw new DevDevelopmentException(
                "RAPPDEV1007",
                developmentServer is not null
                    ? $"The {configuration.DevelopmentServerKind} development server exited unexpectedly with code {exitCode}."
                    : $"The frontend watcher exited unexpectedly with code {exitCode}.");
        }
        finally
        {
            renewalStop.Cancel();
            await renewal.ConfigureAwait(false);
        }
    }

    /// <summary>Writes the session file; a failure is reported once per write but does not stop dev.</summary>
    private static void WriteSession(Action write)
    {
        try
        {
            write();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[dev] Could not write the dev session file: {error.Message}");
        }
    }

    private static async Task InstallFrontendAsync(
        DevProjectConfiguration configuration,
        bool installDependencies,
        CancellationToken cancellationToken)
    {
        using PhaseTimer phase = PhaseTimer.Start("Installing Views Window frontend dependencies");
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        if (installDependencies)
        {
            await RequireSuccessAsync(
                packageManager.Executable,
                configuration.FrontendPackageDirectory,
                packageManager.InstallArguments(),
                "RAPPDEV1006",
                $"The frontend dependency install with {packageManager.Name} failed:",
                $"Run 'dotnet runic doctor --project \"{configuration.ProjectPath}\"' to verify the committed lock file and package train.",
                cancellationToken).ConfigureAwait(false);
        }
        phase.Complete();
    }

    /// <summary>
    /// With --no-restore nothing installs the frontend packages, so a missing
    /// node_modules would only surface later as a confusing build failure.
    /// </summary>
    internal static void RequireInstalledFrontendPackages(DevProjectConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (Directory.Exists(Path.Combine(configuration.FrontendPackageDirectory, "node_modules")) ||
            (!string.IsNullOrWhiteSpace(configuration.WorkspaceRoot) &&
             Directory.Exists(Path.Combine(configuration.WorkspaceRoot, "node_modules"))) ||
            !DeclaresDependencies(Path.Combine(configuration.FrontendPackageDirectory, "package.json")))
        {
            return;
        }
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        throw new DevDevelopmentException(
            "RAPPDEV1008",
            $"--no-restore needs installed frontend packages; run '{packageManager.Name} install' in the frontend directory or omit --no-restore.",
            $"Frontend directory: {configuration.FrontendPackageDirectory}\n", category: CommandExitCategory.Unavailable);
    }

    /// <summary>
    /// A package.json without dependencies installs no node_modules, so there
    /// is nothing to require. An unreadable file is treated as declaring them.
    /// </summary>
    internal static bool DeclaresDependencies(string packageJsonPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return true;
            foreach (string section in (string[])["dependencies", "devDependencies", "optionalDependencies"])
            {
                if (document.RootElement.TryGetProperty(section, out JsonElement packages) &&
                    (packages.ValueKind != JsonValueKind.Object || packages.EnumerateObject().Any()))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private static async Task<IFrontendDevelopmentServer> StartDevelopmentServerAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken) =>
        configuration.DevelopmentServerKind switch
        {
            "vite" => await ViteDevelopmentServer
                .StartAsync(
                    configuration,
                    cancellationToken)
                .ConfigureAwait(false),
            "angular" => await AngularDevelopmentServer
                .StartAsync(configuration, cancellationToken)
                .ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"Unsupported frontend development server '{configuration.DevelopmentServerKind}'."),
        };

    private static async Task CompileFrontendAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        string buildConfiguration,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("[frontend-compiler] Compiling changed sources for managed Hot Reload.");
        await RequireSuccessAsync(
            dotnetHost,
            configuration.ProjectDirectory,
            CreateFrontendCompilerArguments(configuration, buildConfiguration),
            "RAPPDEV1006",
            "Frontend compiler integration failed:",
            CommandRunner.DoctorHint(configuration.ProjectPath),
            cancellationToken).ConfigureAwait(false);
    }

    private static RunningProcess StartFrontendWatcher(
        string dotnetHost,
        DevProjectConfiguration configuration,
        string buildConfiguration)
    {
        if (configuration.HasFrontendWatchTarget)
        {
            return RunningProcess.Start(
                "frontend",
                dotnetHost,
                configuration.ProjectDirectory,
                CreateFrontendWatcherArguments(configuration, buildConfiguration));
        }

        if (configuration.HasNodeWorkspace)
        {
            JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
                configuration.WorkspaceRoot,
                configuration.FrontendPackageDirectory);
            return RunningProcess.Start(
                "frontend",
                packageManager.Executable,
                configuration.WorkspaceRoot,
                packageManager.RunScriptArguments("dev", configuration.Workspace));
        }

        throw new InvalidOperationException("No frontend watcher is configured.");
    }

    internal static IReadOnlyList<string> CreateFrontendCompilerArguments(
        DevProjectConfiguration configuration,
        string buildConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var arguments = new List<string>
        {
            "msbuild",
            configuration.ProjectPath,
            "-nologo",
            $"-target:{configuration.FrontendCompilerHotReloadTarget}",
            $"-property:Configuration={buildConfiguration}",
        };
        return arguments;
    }

    internal static IReadOnlyList<string> CreateFrontendWatcherArguments(
        DevProjectConfiguration configuration,
        string buildConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var arguments = new List<string>
        {
            "msbuild",
            configuration.ProjectPath,
            "-nologo",
            $"-target:{configuration.FrontendWatchTarget}",
            $"-property:Configuration={buildConfiguration}",
        };
        return arguments;
    }

    /// <summary>
    /// Stops before the application starts when a runtime it needs is missing, which happens with a newer-major SDK,
    /// such as SDK 11 building a net10.0 application without the .NET 10 runtime installed: the build succeeds and
    /// the application would exit with code 150 (D059.17).
    /// </summary>
    private static async Task RequireApplicationRuntimeAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (DotNetRuntimes.ReadRuntimeConfig(configuration.RuntimeConfigPath) is not { } required) return;
        CommandResult result = await CommandRunner.RunAsync(dotnetHost, configuration.ProjectDirectory,
            DotNetRuntimes.ListArguments, cancellationToken).ConfigureAwait(false);
        // Without a runtime list, starting the application reports the problem itself.
        if (result.ExitCode != 0) return;
        if (FindMissingRuntime(required.Frameworks, required.RollForward, result.StandardOutput,
                Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD")) is { } message)
        {
            throw new DevDevelopmentException("RAPPDEV1013", message);
        }
    }

    /// <summary>The RAPPDEV1013 message for the first framework that no installed runtime satisfies, or null.</summary>
    internal static string? FindMissingRuntime(
        IReadOnlyList<RequiredRuntime> frameworks,
        string? rollForward,
        string listRuntimesOutput,
        string? environmentRollForward)
    {
        IReadOnlyList<InstalledRuntime> installed = DotNetRuntimes.Parse(listRuntimesOutput);
        string? policy = string.IsNullOrWhiteSpace(environmentRollForward) ? rollForward : environmentRollForward;
        RequiredRuntime? missing = frameworks.FirstOrDefault(framework => !DotNetRuntimes.IsSatisfied(installed, framework, policy));
        if (missing is null) return null;
        string major = missing.Version.Split('.')[0];
        string runtime = StringComparer.OrdinalIgnoreCase.Equals(missing.Name, DotNetRuntimes.AspNetCoreApp)
            ? $"ASP.NET Core {major} runtime"
            : $".NET {major} runtime";
        return $"The application needs the {runtime} ({missing.Name} {missing.Version}), and " +
            $"{DotNetRuntimes.Describe(installed, missing.Name)}. The .NET SDK builds the application but cannot start it. " +
            $"Install the {runtime} next to the SDK; 'dotnet runic doctor' checks it.";
    }

    private static async Task BuildAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> arguments = CreateBuildArguments(configuration, options);

        using PhaseTimer phase = PhaseTimer.Start(
            configuration.HasDevelopmentServer && options.WatchFrontend
                ? "Building managed host and development bootstrap"
                : "Building managed host and frontend assets");
        await RequireSuccessAsync(
            dotnetHost,
            configuration.ProjectDirectory,
            arguments,
            "RAPPDEV1006",
            "The initial build failed:",
            CommandRunner.DoctorHint(configuration.ProjectPath),
            cancellationToken).ConfigureAwait(false);
        phase.Complete();
    }

    internal static IReadOnlyList<string> CreateBuildArguments(
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var arguments = new List<string>
        {
            "build",
            configuration.ProjectPath,
            "--configuration",
            options.Configuration,
            "--nologo",
            "-property:DebugType=portable",
            "-property:DebugSymbols=true",
            "-property:Optimize=false",
        };
        arguments.AddRange(CreateBuildProperties(configuration, options, "-property:"));
        if (!options.Restore)
        {
            arguments.Add("--no-restore");
        }
        if (options.Verbosity is not null)
        {
            arguments.AddRange(["--verbosity", options.Verbosity]);
        }

        return arguments;
    }

    /// <summary>
    /// While a development server serves the frontend, MSBuild must neither run
    /// the production frontend build nor copy dist/ over the development
    /// document in the runtime web root. Generated TypeScript is still written.
    /// </summary>
    internal static IReadOnlyList<string> CreateDevelopmentServerProperties(
        DevProjectConfiguration configuration,
        DevOptions options,
        string prefix) =>
        options.WatchFrontend && configuration.HasDevelopmentServer
            ? [$"{prefix}RunicApplicationFrontendBuildEnabled=false", $"{prefix}RunicApplicationFrontendCopyEnabled=false"]
            : [];

    /// <summary>
    /// Development server properties plus <c>RunicApplicationFrontendInstallEnabled=false</c>:
    /// dev installs the frontend packages itself before the build, and with
    /// <c>--no-restore</c> nothing installs them behind the caller's back.
    /// <c>RunicApplicationFrontendDelivery=Files</c>: dev serves the frontend from its development server or its
    /// output directory, beside the host scripts in <c>www/</c>, never from an embedded archive.
    /// </summary>
    internal static IReadOnlyList<string> CreateBuildProperties(
        DevProjectConfiguration configuration,
        DevOptions options,
        string prefix)
    {
        var properties = new List<string>(CreateDevelopmentServerProperties(configuration, options, prefix))
        {
            $"{prefix}RunicApplicationFrontendDelivery=Files",
        };
        if (!options.Restore || configuration.NodeEnabled)
        {
            properties.Add($"{prefix}RunicApplicationFrontendInstallEnabled=false");
        }
        return properties;
    }

    internal static IReadOnlyList<string> CreateRestoreArguments(
        DevProjectConfiguration configuration,
        string buildConfiguration,
        string? verbosity = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var arguments = new List<string>
        {
            "restore",
            configuration.ProjectPath,
            $"-p:Configuration={buildConfiguration}",
        };
        if (verbosity is not null)
        {
            arguments.AddRange(["--verbosity", verbosity]);
        }
        return arguments;
    }

    private static async Task RequireSuccessAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        string code,
        string message,
        string remediation,
        CancellationToken cancellationToken)
    {
        CommandResult result = await CommandRunner
            .RunAsync(executable, workingDirectory, arguments, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            Console.Write(result.StandardOutput);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            Console.Error.Write(result.StandardError);
        }

        if (result.ExitCode != 0)
        {
            throw new DevDevelopmentException(
                code,
                $"{message} {CommandRunner.DescribeFailure(executable, arguments, result.ExitCode)}",
                $"Failed program: {CommandRunner.DescribeProgram(executable, arguments)} (exit code {result.ExitCode})\n" +
                CommandRunner.LocalDetail(workingDirectory, remediation));
        }
    }

    internal static string ResolveDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

    /// <summary>
    /// The assets line: with a development server, the directory it builds into; without one, Runic Desktop serves the
    /// directory live (RUNIC_APPLICATION_DEVELOPMENT_ASSETS), and CS-WebUI keeps the initial build.
    /// </summary>
    internal static string? DescribeAssets(DevProjectConfiguration configuration, DevOptions options)
    {
        if (string.IsNullOrWhiteSpace(configuration.FrontendOutputDirectory)) return null;
        if (options.WatchFrontend && configuration.HasDevelopmentServer)
            return $"[dev] Assets: {configuration.FrontendOutputDirectory}";
        return configuration.RunsOnCsWebUiOnly
            ? "[dev] Assets: the CS-WebUI host shows the initial build; restart to see frontend changes"
            : $"[dev] Assets: {configuration.FrontendOutputDirectory} (served live; windows reload on change)";
    }

    private static void WriteConfiguration(
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        Console.WriteLine($"[dev] Project: {configuration.ProjectPath}");
        Console.WriteLine("[dev] Model: Runic Views Window project");
        Console.WriteLine(
            configuration.HasDevelopmentServer
                ? $"[dev] Frontend: {configuration.DevelopmentServerKind} dev server " +
                  $"for {configuration.Workspace}"
                : configuration.HasFrontendWatchTarget
                ? $"[dev] Frontend: MSBuild target {configuration.FrontendWatchTarget}"
                : configuration.HasNodeWorkspace
                ? $"[dev] Frontend: JavaScript workspace {configuration.Workspace}"
                : "[dev] Frontend: external compiler/static assets");
        if (DescribeAssets(configuration, options) is { } assets)
        {
            Console.WriteLine(assets);
        }
        Console.WriteLine($"[dev] Runtime web root: {configuration.RuntimeWebRoot}");
        if (configuration.HasFrontendCompiler)
        {
            Console.WriteLine(
                "[dev] Compiler: external integration with diagnostics and compatible fragment refresh");
        }

        if (options.LaunchProfile is not null)
        {
            Console.WriteLine($"[dev] Launch profile: {options.LaunchProfile}");
        }
        if (options.Environment.Count != 0)
        {
            // Only the names: a value can be a secret.
            Console.WriteLine($"[dev] Environment: {string.Join(", ", options.Environment.Select(static variable => variable.Key).Distinct(StringComparer.Ordinal))}");
        }
        if (options.DryRun)
        {
            Console.WriteLine("[dev] Dry run complete; no files or processes were changed.");
        }
    }

    /// <summary>
    /// The IDE contract's lines with the session file. Each line follows the session file write it announces, so a
    /// reader that reacts to <c>[dev] Ready</c> or a Window line, as the template's VS Code tasks and IDE extensions do,
    /// finds that state in the file (#1026). Each write replaces the file whole.
    /// </summary>
    internal sealed class ContractReporter(DevSession session, Action<string> writeLine, Action<Action> writeSession)
    {
        /// <summary>A Window process started: the file names it, then its line, then Ready.</summary>
        internal void WindowStarted(int processId)
        {
            writeSession(() => session.WindowStarted(processId));
            writeLine(WindowStartedLine(processId));
            Ready();
        }

        /// <summary>The Window process exited: the file drops it and says starting, then its line.</summary>
        internal void WindowExited(int processId)
        {
            writeSession(() => session.WindowExited(processId));
            writeLine(WindowExitedLine(processId));
        }

        /// <summary>The frontend and the Window can be used: the file says ready, then <see cref="ReadyLine"/>.</summary>
        internal void Ready()
        {
            writeSession(session.Ready);
            writeLine(ReadyLine);
        }
    }
}
