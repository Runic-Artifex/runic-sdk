using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

internal sealed class AngularDevelopmentServer : IFrontendDevelopmentServer
{
    internal const string ServerEnvironmentVariable =
        "RUNIC_TOOLKIT_FRONTEND_DEV_SERVER";
    internal const string KindEnvironmentVariable =
        "RUNIC_TOOLKIT_FRONTEND_DEV_SERVER_KIND";

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
        Uri inspectorEndpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inspectorEndpoint);
        using PhaseTimer phase = PhaseTimer.Start("Starting Angular development server");
        int port = ReserveLoopbackPort();
        Uri origin = new($"http://127.0.0.1:{port}/", UriKind.Absolute);
        RunningProcess process = RunningProcess.Start(
            "angular",
            "npm",
            configuration.WorkspaceRoot,
            CreateArguments(configuration, port),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["BROWSER"] = "none",
                ["NG_CLI_ANALYTICS"] = "false",
            });
        var server = new AngularDevelopmentServer(process, origin);
        try
        {
            await server.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            using var client = new HttpClient();
            foreach (string destination in configuration.DevelopmentServerDocuments)
            {
                string document = await client
                    .GetStringAsync(new Uri(origin, destination), cancellationToken)
                    .ConfigureAwait(false);
                FrontendDevelopmentDocument.Write(
                    configuration,
                    origin,
                    inspectorEndpoint,
                    destination,
                    document);
            }
            Console.WriteLine($"[dev] Angular development server ready at {origin}");
            phase.Complete();
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static IReadOnlyList<string> CreateArguments(
        DevProjectConfiguration configuration,
        int port) =>
        [
            "run",
            "dev",
            "--workspace",
            configuration.Workspace,
            "--",
            "--host",
            "127.0.0.1",
            "--port",
            port.ToString(CultureInfo.InvariantCulture),
            "--hmr",
            "--live-reload",
        ];

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
                        "RTKDEV1007",
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
                catch (HttpRequestException)
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
                "RTKDEV1007",
                $"Timed out waiting for the Angular development server at {Origin}. " +
                "Run 'dotnet runic-toolkit doctor' and verify the Angular serve target.");
        }
    }

    private static int ReserveLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();

}
