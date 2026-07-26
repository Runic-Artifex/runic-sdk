using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal static class DevApplication
{
    internal static async Task<int> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (DevOptions.RequestsHelp(arguments))
        {
            WriteHelp();
            return Program.Success;
        }

        DevOptions options = DevOptions.Parse(arguments);
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, options.Project);
        string dotnetHost = ResolveDotNetHost();
        DevProjectConfiguration configuration = await DevProjectConfiguration
            .EvaluateAsync(
                dotnetHost,
                project,
                options.Configuration,
                cancellationToken)
            .ConfigureAwait(false);
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
        string reloadManifest = Path.Combine(
            configuration.FrontendOutputDirectory,
            "webuitoolkit.assets.json");
        if (!File.Exists(reloadManifest))
        {
            throw new DevDevelopmentException(
                "WUTDEV1006",
                $"The frontend build did not emit reload manifest '{reloadManifest}'.");
        }

        var mirror = new AssetMirror(
            configuration.FrontendOutputDirectory,
            configuration.RuntimeWebRoot);
        await using var host = new HostProcessController(dotnetHost, configuration, options);
        await host.StartAsync(cancellationToken).ConfigureAwait(false);
        await using RunningProcess? frontend = options.WatchFrontend
            ? StartFrontendWatcher(dotnetHost, configuration, options.Configuration)
            : null;

        Task assetMonitor = FilePoller.WatchAsync(
            reloadManifest,
            async token =>
            {
                int changed = mirror.Synchronize();
                Console.WriteLine($"[dev] Frontend build completed; synchronized {changed} asset(s).");
                await host.RestartAsync(token).ConfigureAwait(false);
            },
            cancellationToken);
        Task contractMonitor = options.GenerateContracts && configuration.HasContracts
            ? FilePoller.WatchAsync(
                configuration.ContractSource,
                token => GenerateAndVerifyContractsAsync(configuration, token),
                cancellationToken)
            : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        Task cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        var observed = new List<Task> { host.Completion, assetMonitor, contractMonitor, cancellation };
        if (frontend is not null)
        {
            observed.Add(frontend.Completion);
        }

        Task completed = await Task.WhenAny(observed).ConfigureAwait(false);
        if (completed == cancellation)
        {
            await cancellation.ConfigureAwait(false);
            return Program.Success;
        }

        if (completed == assetMonitor || completed == contractMonitor)
        {
            await completed.ConfigureAwait(false);
            return Program.DevelopmentFailure;
        }

        int exitCode = await ((Task<int>)completed).ConfigureAwait(false);
        Program.WriteError(
            "WUTDEV1007",
            completed == host.Completion
                ? $"The CsWebUi host watcher exited unexpectedly with code {exitCode}."
                : $"The frontend watcher exited unexpectedly with code {exitCode}.");
        return Program.DevelopmentFailure;
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
                    "-property:WebUIToolkitFrontendInstall=false",
                ]);
        }

        if (configuration.HasNodeWorkspace)
        {
            return RunningProcess.Start(
                "frontend",
                "npm",
                configuration.WorkspaceRoot,
                ["run", "dev", "--workspace", configuration.Workspace]);
        }

        throw new InvalidOperationException("No frontend watcher is configured.");
    }

    private static async Task BuildAsync(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "build",
            configuration.ProjectPath,
            "--configuration",
            options.Configuration,
            "--nologo",
            "-property:WebUIToolkitFrontendInstall=" + (options.Restore ? "true" : "false"),
        };
        if (!options.Restore)
        {
            arguments.Add("--no-restore");
        }

        Console.WriteLine("[dev] Building the managed host and initial frontend assets.");
        await RequireSuccessAsync(
            dotnetHost,
            configuration.ProjectDirectory,
            arguments,
            "WUTDEV1006",
            "Initial build failed.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task GenerateAndVerifyContractsAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        string[] commonArguments =
        [
            configuration.ContractTool,
            "--source",
            configuration.ContractSource,
            "--csharp",
            configuration.ContractCSharpOutput,
            "--typescript",
            configuration.ContractTypeScriptOutput,
        ];
        Console.WriteLine("[contracts] Generating C# and TypeScript contracts.");
        await RequireSuccessAsync(
            "node",
            configuration.WorkspaceRoot,
            commonArguments,
            "WUTDEV1006",
            "Contract generation failed.",
            cancellationToken).ConfigureAwait(false);

        var verifyArguments = new List<string>(commonArguments) { "--verify" };
        await RequireSuccessAsync(
            "node",
            configuration.WorkspaceRoot,
            verifyArguments,
            "WUTDEV1006",
            "Generated contract verification failed.",
            cancellationToken).ConfigureAwait(false);
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
            configuration.HasFrontendWatchTarget
                ? $"[dev] Frontend: MSBuild target {configuration.FrontendWatchTarget}"
                : $"[dev] Frontend: npm workspace {configuration.Workspace}");
        Console.WriteLine($"[dev] Assets: {configuration.FrontendOutputDirectory}");
        Console.WriteLine($"[dev] CsWebUi root: {configuration.RuntimeWebRoot}");
        if (configuration.HasContracts)
        {
            Console.WriteLine($"[dev] Contract: {configuration.ContractSource}");
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
              dotnet webuitoolkit dev [PROJECT] [options] [-- APPLICATION_ARGUMENTS]

            Options:
              --project PATH          Select a .csproj or a directory containing one.
              --configuration NAME    Build configuration (default: Debug).
              --no-restore            Do not restore NuGet or npm dependencies.
              --no-contracts          Do not generate or watch the configured contract.
              --no-frontend-watch     Build once without starting the frontend watcher.
              --dry-run               Evaluate and print the development configuration.
              -h, --help              Show this help.

            The selected project supplies frontend paths through
            WebUIToolkit.Frontend.Sdk MSBuild properties. The command generates and
            verifies contracts, performs the initial build, starts the native CsWebUi
            host and frontend watcher, mirrors completed frontend builds into the
            runtime web root, and restarts the host for a coordinated reload.
            """);
    }
}
