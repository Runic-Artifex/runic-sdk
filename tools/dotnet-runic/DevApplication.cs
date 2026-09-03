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
        DevProjectConfiguration configuration;
        using (PhaseTimer phase = PhaseTimer.Start("Evaluating project"))
        {
            configuration = await DevProjectConfiguration
                .EvaluateAsync(
                    dotnetHost,
                    project,
                    options.Configuration,
                    cancellationToken)
                .ConfigureAwait(false);
            phase.Complete();
        }
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
            if (configuration.NodeEnabled)
            {
                await BuildCanonicalFrontendAsync(configuration, options.Restore, stop.Token).ConfigureAwait(false);
            }

            if (options.GenerateContracts && configuration.HasContracts)
            {
                await GenerateAndVerifyContractsAsync(configuration, stop.Token)
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
            return Program.Success;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
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
        await using DevelopmentInspectorServer? inspectorServer =
            useDevelopmentServer
                ? DevelopmentInspectorServer.Start(configuration.ProjectDirectory)
                : null;
        await using IFrontendDevelopmentServer? developmentServer =
            useDevelopmentServer
                ? await StartDevelopmentServerAsync(
                    configuration,
                    inspectorServer!,
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

        // Runic Assets owns archive refresh. Phase 1 has no parallel legacy
        // manifest/mirror watcher.
        Task assetMonitor = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task contractMonitor = options.GenerateContracts && configuration.HasContracts
            ? FilePoller.WatchAsync(
                configuration.DevelopmentServerKind == "vite"
                    ? configuration.BridgeIr
                    : configuration.BridgeSource,
                async token =>
                {
                    if (configuration.DevelopmentServerKind != "vite")
                    {
                        await GenerateAndVerifyContractsAsync(configuration, token).ConfigureAwait(false);
                    }
                    await host.RestartAsync(token).ConfigureAwait(false);
                },
                cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var compilerReloadMonitors = new List<Task>();
        if (compilerReload is not null)
        {
            compilerReloadMonitors.Add(compilerReload.WatchAsync(cancellationToken));
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
                cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        var observed = new List<Task>
        {
            host.Completion,
            assetMonitor,
            contractMonitor,
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

        Task completed = await Task.WhenAny(observed).ConfigureAwait(false);
        if (completed == cancellation)
        {
            await cancellation.ConfigureAwait(false);
            return Program.Success;
        }

        if (completed == assetMonitor ||
            completed == contractMonitor ||
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
            "RTKDEV1007",
            completed == host.Completion
                ? $"The Runic Desktop host watcher exited unexpectedly with code {exitCode}."
                : completed == developmentServer?.Completion
                    ? $"The {configuration.DevelopmentServerKind} development server " +
                      $"exited unexpectedly with code {exitCode}."
                    : $"The frontend watcher exited unexpectedly with code {exitCode}.");
    }

    private static async Task BuildCanonicalFrontendAsync(
        DevProjectConfiguration configuration,
        bool installDependencies,
        CancellationToken cancellationToken)
    {
        using PhaseTimer phase = PhaseTimer.Start("Installing and building Runic Assets frontend");
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        if (installDependencies)
        {
            await RequireSuccessAsync(
                packageManager.Executable,
                configuration.FrontendPackageDirectory,
                packageManager.InstallArguments(),
                "RTKDEV1006",
                $"The Runic Assets frontend dependency restore with {packageManager.Name} failed. Run 'dotnet runic doctor' to verify the committed lock file and package train.",
                cancellationToken).ConfigureAwait(false);
        }
        await RequireSuccessAsync(
            packageManager.Executable,
            configuration.FrontendPackageDirectory,
            packageManager.RunScriptArguments("build", "."),
            "RTKDEV1006",
            "The Runic Assets frontend build failed.",
            cancellationToken).ConfigureAwait(false);
        phase.Complete();
    }

    private static async Task<IFrontendDevelopmentServer> StartDevelopmentServerAsync(
        DevProjectConfiguration configuration,
        DevelopmentInspectorServer inspectorServer,
        CancellationToken cancellationToken) =>
        configuration.DevelopmentServerKind switch
        {
            "vite" => await ViteDevelopmentServer
                .StartAsync(
                    configuration,
                    inspectorServer.Endpoint,
                    cancellationToken)
                .ConfigureAwait(false),
            "angular" => await AngularDevelopmentServer
                .StartAsync(configuration, inspectorServer.Endpoint, cancellationToken)
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
            [
                "msbuild",
                configuration.ProjectPath,
                "-nologo",
                $"-target:{configuration.FrontendCompilerHotReloadTarget}",
                $"-property:Configuration={buildConfiguration}",
                "-property:RunicToolkitFrontendCompilerDevelopmentHotReload=true",
                "-property:RunicToolkitFrontendEnabled=false",
                "-property:RunicToolkitFrontendInstall=false",
            ],
            "RTKDEV1006",
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
                [
                    "msbuild",
                    configuration.ProjectPath,
                    "-nologo",
                    $"-target:{configuration.FrontendWatchTarget}",
                    $"-property:Configuration={buildConfiguration}",
                    "-property:RunicToolkitFrontendInstall=false",
                ]);
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
            "RTKDEV1006",
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
            "-property:RunicToolkitFrontendCompilerDevelopmentHotReload=true",
            "-property:RunicToolkitFrontendInstall=" + (options.Restore ? "true" : "false"),
            "-property:RunicToolkitFrontendBuild="
                + (options.WatchFrontend && configuration.HasDevelopmentServer
                    ? "false"
                    : "true"),
        };
        if (!options.Restore)
        {
            arguments.Add("--no-restore");
        }

        return arguments;
    }

    private static async Task GenerateAndVerifyContractsAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        using PhaseTimer phase = PhaseTimer.Start("Generating and verifying contracts");
        await RequireSuccessAsync(
            packageManager.Executable,
            configuration.FrontendPackageDirectory,
            packageManager.RunScriptArguments("contract:generate", "."),
            "RTKDEV1006",
            $"Bridge IR generation failed. Run 'dotnet runic doctor \"{configuration.ProjectPath}\"' to inspect the configured toolchain.",
            cancellationToken).ConfigureAwait(false);

        await RequireSuccessAsync(
            packageManager.Executable,
            configuration.FrontendPackageDirectory,
            packageManager.RunScriptArguments("contract:check", "."),
            "RTKDEV1006",
            $"Bridge IR verification failed. Run 'dotnet runic doctor \"{configuration.ProjectPath}\"' to inspect stale outputs.",
            cancellationToken).ConfigureAwait(false);
        phase.Complete();
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

    private static string ResolveDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";

    private static void WriteConfiguration(
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        Console.WriteLine($"[dev] Project: {configuration.ProjectPath}");
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
        if (configuration.HasContracts)
        {
            Console.WriteLine($"[dev] Contract: {configuration.BridgeSource}");
            Console.WriteLine($"[dev] Bridge IR: {configuration.BridgeIr}");
        }

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

    private static void WriteHelp()
    {
        Console.WriteLine(
            """
            Usage:
              dotnet runic dev [PROJECT] [options] [-- APPLICATION_ARGUMENTS]
              dotnet runic doctor [PROJECT]
              dotnet runic inspect [PROJECT] --artifact manifest

            Options:
              --project PATH          Select a .csproj or a directory containing one.
              --configuration NAME    Build configuration (default: Debug).
              --no-restore            Do not restore NuGet or frontend package dependencies.
              --no-contracts          Do not generate or watch the configured contract.
              --no-frontend-watch     Build once without starting the frontend watcher.
              --no-dotnet-watch       Run the managed application once (useful for gates).
              --dry-run               Evaluate and print the development configuration.
              -h, --help              Show this help.

            The selected project supplies frontend paths through
            optional RunicToolkit frontend-development MSBuild properties. The command generates and
            verifies contracts, performs the initial build, starts the native Runic Desktop
            host and frontend tooling. Projects that opt into Vite development-server
            mode receive native-window CSS/JavaScript HMR without restarting .NET;
            their private application-bridge bindings remain the transport.
            """);
    }
}
