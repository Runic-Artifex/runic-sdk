using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal static class DevApplication
{
    internal static async Task<int> RunAsync(
        DevOptions options,
        CancellationToken cancellationToken)
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
        if (options.DryRun)
        {
            return Program.Success;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            if (options.Restore)
            {
                using var phase = PhaseTimer.Start("Restoring Views Window dependencies");
                await RequireSuccessAsync(dotnetHost, configuration.ProjectDirectory,
                    CreateRestoreArguments(configuration, options.Configuration),
                    "RAPPDEV1006", "Selected host restore failed.", stop.Token).ConfigureAwait(false);
                phase.Complete();
            }
            if (configuration.NodeEnabled)
            {
                await InstallFrontendAsync(configuration, options.Restore, stop.Token).ConfigureAwait(false);
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
            return Program.Success;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
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
        // Runic Views owns source contract generation; the CLI coordinates
        // the frontend and native Window process only.
        Task assetMonitor = Task.Delay(Timeout.InfiniteTimeSpan, monitorToken);
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
            assetMonitor,
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
            if (completed == cancellation)
            {
                await cancellation.ConfigureAwait(false);
                return Program.Success;
            }

            if (completed == assetMonitor ||
                compilerReloadMonitors.Contains(completed) ||
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
                    ? $"The Runic Desktop host watcher exited unexpectedly with code {exitCode}."
                    : completed == developmentServer?.Completion
                        ? $"The {configuration.DevelopmentServerKind} development server " +
                          $"exited unexpectedly with code {exitCode}."
                        : $"The frontend watcher exited unexpectedly with code {exitCode}.");
        }
        finally
        {
            monitorStop.Cancel();
            var monitors = new List<Task>
            {
                assetMonitor,
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
                $"The Runic Assets frontend dependency restore with {packageManager.Name} failed. Run 'dotnet runic doctor' to verify the committed lock file and package train.",
                cancellationToken).ConfigureAwait(false);
        }
        phase.Complete();
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
            "Frontend compiler integration failed.",
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
            $"Initial build failed. Run 'dotnet runic doctor \"{configuration.ProjectPath}\"' to inspect prerequisites.",
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
        if (!options.Restore)
        {
            arguments.Add("--no-restore");
        }

        return arguments;
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
                $"{message} Child process exited with code {result.ExitCode}.");
        }
    }

    internal static string ResolveDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

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
        if (!string.IsNullOrWhiteSpace(configuration.FrontendOutputDirectory))
        {
            Console.WriteLine($"[dev] Assets: {configuration.FrontendOutputDirectory}");
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
