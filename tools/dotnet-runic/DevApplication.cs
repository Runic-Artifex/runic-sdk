using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal static class DevApplication
{
    internal static async Task<int> RunAsync(
        DevOptions options,
        CancellationToken cancellationToken,
        Action<DevProjectConfiguration>? evaluated = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
        string dotnetHost = ResolveDotNetHost();
        using PhaseTimer evaluation = PhaseTimer.Start("Evaluating Views Window project");
        DevProjectConfiguration configuration = await DevProjectConfiguration
            .EvaluateAsync(dotnetHost, project, options.Configuration, cancellationToken)
            .ConfigureAwait(false);
        evaluation.Complete();
        WriteConfiguration(configuration, options);
        evaluated?.Invoke(configuration);
        if (options.DryRun)
        {
            return Program.Success;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Stop the frontend and native host process trees on Ctrl+C and also when
        // an IDE or terminal ends only this process. A repeated signal must not
        // exit before those process trees are stopped.
        using IDisposable protectShutdown = ToolSignalCancellation.ProtectShutdown();
        Action<PosixSignalContext> stopHandler = context =>
        {
            context.Cancel = true;
            stop.Cancel();
        };
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, stopHandler);
        using var quit = PosixSignalRegistration.Create(PosixSignal.SIGQUIT, stopHandler);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, stopHandler);
        using var hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, stopHandler);
        try
        {
            if (!options.Restore && configuration.NodeEnabled)
            {
                RequireInstalledFrontendPackages(configuration);
            }
            if (options.Restore)
            {
                using var phase = PhaseTimer.Start("Restoring Views Window dependencies");
                await RequireSuccessAsync(dotnetHost, configuration.ProjectDirectory,
                    CreateRestoreArguments(configuration, options.Configuration),
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
            }
            await BuildAsync(dotnetHost, configuration, options, stop.Token)
                .ConfigureAwait(false);
            return await RunDevelopmentLoopAsync(
                dotnetHost,
                configuration,
                options,
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
        }
    }

    private static async Task<int> RunDevelopmentLoopAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
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

        using var monitorStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken monitorToken = monitorStop.Token;
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
        throw new DevUsageException(
            "RAPPDEV1008",
            $"--no-restore needs installed frontend packages; run '{packageManager.Name} install' in the frontend directory or omit --no-restore.",
            $"Frontend directory: {configuration.FrontendPackageDirectory}\n");
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
    /// </summary>
    internal static IReadOnlyList<string> CreateBuildProperties(
        DevProjectConfiguration configuration,
        DevOptions options,
        string prefix)
    {
        var properties = new List<string>(CreateDevelopmentServerProperties(configuration, options, prefix));
        if (!options.Restore || configuration.NodeEnabled)
        {
            properties.Add($"{prefix}RunicApplicationFrontendInstallEnabled=false");
        }
        return properties;
    }

    internal static IReadOnlyList<string> CreateRestoreArguments(
        DevProjectConfiguration configuration,
        string buildConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var arguments = new List<string>
        {
            "restore",
            configuration.ProjectPath,
            $"-p:Configuration={buildConfiguration}",
        };
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

        if (options.DryRun)
        {
            Console.WriteLine("[dev] Dry run complete; no files or processes were changed.");
        }
    }

}
