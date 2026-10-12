using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed class ViteDevelopmentServer : IFrontendDevelopmentServer
{
    internal const string ServerEnvironmentVariable = "RUNIC_APPLICATION_VITE_DEV_SERVER";
    internal const string EntryEnvironmentVariable = "RUNIC_APPLICATION_VITE_ENTRY";
    internal const string PackageDirectoryEnvironmentVariable =
        "RUNIC_APPLICATION_VITE_PACKAGE_DIRECTORY";
    internal const string DiagnosticsEnvironmentVariable =
        "RUNIC_APPLICATION_FRONTEND_COMPILER_DIAGNOSTICS";
    internal const string HotReloadEnvironmentVariable =
        "RUNIC_APPLICATION_FRONTEND_COMPILER_HOT_RELOAD";
    internal const string ProjectEnvironmentVariable =
        "RUNIC_APPLICATION_DEV_PROJECT";
    private readonly RunningProcess _process;

    private ViteDevelopmentServer(
        RunningProcess process,
        Uri origin,
        string entry,
        string packageDirectory,
        string diagnosticsPath,
        string hotReloadPath,
        string projectPath)
    {
        _process = process;
        Origin = origin;
        HostEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ServerEnvironmentVariable] = origin.AbsoluteUri,
            [EntryEnvironmentVariable] = entry,
            [PackageDirectoryEnvironmentVariable] = packageDirectory,
            [DiagnosticsEnvironmentVariable] = diagnosticsPath,
            [HotReloadEnvironmentVariable] = hotReloadPath,
            [ProjectEnvironmentVariable] = projectPath,
        };
    }

    public Uri Origin { get; }

    public IReadOnlyDictionary<string, string?> HostEnvironment { get; }

    public Task<int> Completion => _process.Completion;

    internal static async Task<ViteDevelopmentServer> StartAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using PhaseTimer phase = PhaseTimer.Start("Starting Vite development server");
        ViteDevelopmentServer started = await LoopbackPort
            .StartAsync(
                "Vite development server",
                port => StartOnPortAsync(configuration, port, cancellationToken))
            .ConfigureAwait(false);
        phase.Complete();
        return started;
    }

    private static async Task<ViteDevelopmentServer> StartOnPortAsync(
        DevProjectConfiguration configuration,
        int port,
        CancellationToken cancellationToken)
    {
        Uri origin = new($"http://127.0.0.1:{port}/", UriKind.Absolute);
        IReadOnlyList<string> arguments = CreateArguments(
            configuration,
            port);
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        var conflict = new PortConflictWatch();
        RunningProcess process = RunningProcess.Start(
            "vite",
            packageManager.Executable,
            configuration.WorkspaceRoot,
            arguments,
            CreateEnvironment(configuration),
            observeLine: conflict.Observe);
        var server = new ViteDevelopmentServer(
            process,
            origin,
            configuration.ViteDevServerEntry,
            configuration.FrontendPackageDirectory,
            configuration.FrontendCompilerDiagnosticsPath,
            configuration.FrontendCompilerHotReloadPath + ".ready",
            configuration.ProjectPath);
        try
        {
            await server.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            if (!configuration.HasFrontendCompiler)
            {
                using var client = new HttpClient();
                foreach (string destination in configuration.DevelopmentServerDocuments)
                {
                    string document = await FrontendDevelopmentDocument.ReadAsync(
                            client, origin, destination, "Vite", cancellationToken)
                        .ConfigureAwait(false);
                    FrontendDevelopmentDocument.Write(
                        configuration,
                        origin,
                        destination,
                        document);
                }
            }
            Console.WriteLine($"[dev] Vite development server ready at {origin}");
            return server;
        }
        catch (DevDevelopmentException) when (server.Completion.IsCompleted)
        {
            bool portInUse = await conflict.ExplainsExitAsync(process).ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
            if (portInUse)
            {
                throw new LoopbackPort.PortInUseException();
            }

            throw;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static IReadOnlyDictionary<string, string?> CreateEnvironment(
        DevProjectConfiguration configuration) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["BROWSER"] = "none",
            // Scope Vite diagnostics to the dev server. Passing DEBUG through
            // MSBuild can make its Exec task parse config dumps as build errors.
            ["DEBUG"] = Environment.GetEnvironmentVariable("RUNIC_APPLICATION_VITE_DEBUG")
                ?? Environment.GetEnvironmentVariable("DEBUG"),
            ["RUNIC_APPLICATION_DEV_PROJECT"] = configuration.ProjectPath,
        };

    internal static IReadOnlyList<string> CreateArguments(
        DevProjectConfiguration configuration,
        int port)
    {
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        return packageManager.RunScriptArguments(
            "dev",
            configuration.Workspace,
            [
                "--host",
                "127.0.0.1",
                "--port",
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--strictPort",
            ]);
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        Console.WriteLine($"[vite] Probing {Origin} for the Vite client and application entry.");
        await WaitUntilReadyAsync(Origin, HostEnvironment[EntryEnvironmentVariable]![1..],
            Completion, client, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WaitUntilReadyAsync(
        Uri origin, string entry, Task<int> completion, HttpClient client,
        TimeSpan deadline, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        Uri clientModule = new(origin, "@vite/client");
        Uri entryModule = new(origin, entry);
        string probe = "Vite client";
        string lastResponse = "no response";
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (completion.IsCompleted)
                {
                    int exitCode = await completion.ConfigureAwait(false);
                    throw new DevDevelopmentException(
                        "RAPPDEV1007",
                        $"The Vite development server exited before readiness with code {exitCode}.");
                }

                try
                {
                    probe = "Vite client";
                    lastResponse = "no response";
                    using HttpResponseMessage clientResponse = await client
                        .GetAsync(clientModule, timeout.Token)
                        .ConfigureAwait(false);
                    lastResponse = $"HTTP {(int)clientResponse.StatusCode}";
                    if (clientResponse.IsSuccessStatusCode)
                    {
                        probe = "application entry";
                        lastResponse = "no response";
                        using HttpResponseMessage entryResponse = await client
                            .GetAsync(entryModule, timeout.Token)
                            .ConfigureAwait(false);
                        lastResponse = $"HTTP {(int)entryResponse.StatusCode}";
                        if (entryResponse.IsSuccessStatusCode)
                        {
                            return;
                        }
                    }
                }
                catch (Exception error) when (LoopbackPort.IsConnectionFailure(error))
                {
                    lastResponse = "connection failed";
                }
                catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
                {
                    lastResponse = "request timed out";
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1007",
                $"Timed out waiting for the Vite development server ({probe}: {lastResponse}). " +
                "Run 'dotnet runic doctor' and verify the configured dev script " +
                "and Vite entry module.");
        }
    }

    public async ValueTask DisposeAsync()
        => await _process.DisposeAsync().ConfigureAwait(false);
}
