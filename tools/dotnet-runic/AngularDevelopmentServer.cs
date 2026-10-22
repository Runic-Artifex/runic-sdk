using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed class AngularDevelopmentServer : IFrontendDevelopmentServer
{
    internal const string ServerEnvironmentVariable =
        "RUNIC_APPLICATION_FRONTEND_DEV_SERVER";
    internal const string KindEnvironmentVariable =
        "RUNIC_APPLICATION_FRONTEND_DEV_SERVER_KIND";

    private readonly RunningProcess _process;

    private AngularDevelopmentServer(
        RunningProcess process,
        Uri origin)
    {
        _process = process;
        Origin = origin;
        HostEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ServerEnvironmentVariable] = origin.AbsoluteUri,
            [KindEnvironmentVariable] = "angular",
        };
    }

    public Uri Origin { get; }

    public IReadOnlyDictionary<string, string?> HostEnvironment { get; }

    public Task<int> Completion => _process.Completion;

    internal static async Task<AngularDevelopmentServer> StartAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        using PhaseTimer phase = PhaseTimer.Start("Starting Angular development server");
        AngularDevelopmentServer server = await LoopbackPort
            .StartAsync(
                "Angular development server",
                port => StartOnPortAsync(configuration, port, cancellationToken))
            .ConfigureAwait(false);
        phase.Complete();
        return server;
    }

    private static async Task<AngularDevelopmentServer> StartOnPortAsync(
        DevProjectConfiguration configuration,
        int port,
        CancellationToken cancellationToken)
    {
        Uri origin = new($"http://127.0.0.1:{port}/", UriKind.Absolute);
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        var conflict = new PortConflictWatch();
        RunningProcess process = RunningProcess.Start(
            "angular",
            packageManager.Executable,
            configuration.WorkspaceRoot,
            CreateArguments(configuration, port),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["BROWSER"] = "none",
                ["NG_CLI_ANALYTICS"] = "false",
            },
            observeLine: conflict.Observe);
        var server = new AngularDevelopmentServer(process, origin);
        try
        {
            await server.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            using var client = new HttpClient();
            var documents = new List<(string Destination, string Document)>();
            foreach (string destination in configuration.DevelopmentServerDocuments)
            {
                documents.Add((destination, await FrontendDevelopmentDocument.ReadAsync(
                        client, origin, destination, "Angular", cancellationToken)
                    .ConfigureAwait(false)));
            }
            // The Angular server exits when another process holds the port; write nothing that such a process served.
            ViteDevelopmentServer.ThrowIfExited(server.Completion, "Angular");
            foreach ((string destination, string document) in documents)
            {
                FrontendDevelopmentDocument.Write(
                    configuration,
                    origin,
                    destination,
                    document);
            }
            Console.WriteLine($"[dev] Angular development server ready at {origin}");
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
                port.ToString(CultureInfo.InvariantCulture),
                "--hmr",
                "--live-reload",
            ]);
    }

    private async Task WaitUntilReadyAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (Completion.IsCompleted)
                {
                    int exitCode = await Completion.ConfigureAwait(false);
                    throw new DevDevelopmentException(
                        "RAPPDEV1007",
                        $"The Angular development server exited before readiness with code {exitCode}.");
                }

                try
                {
                    using HttpResponseMessage response = await client
                        .GetAsync(Origin, timeout.Token)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception error) when (LoopbackPort.IsConnectionFailure(error))
                {
                }
                catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(75), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1007",
                $"Timed out waiting for the Angular development server at {Origin}. " +
                "Run 'dotnet runic doctor' and verify the Angular serve target.");
        }
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();

}
