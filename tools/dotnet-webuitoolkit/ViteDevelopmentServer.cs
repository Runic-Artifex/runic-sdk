using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed class ViteDevelopmentServer : IAsyncDisposable
{
    internal const string ServerEnvironmentVariable = "WEBUITOOLKIT_VITE_DEV_SERVER";
    internal const string EntryEnvironmentVariable = "WEBUITOOLKIT_VITE_ENTRY";
    internal const string PackageDirectoryEnvironmentVariable =
        "WEBUITOOLKIT_VITE_PACKAGE_DIRECTORY";

    private readonly RunningProcess _process;

    private ViteDevelopmentServer(
        RunningProcess process,
        Uri origin,
        string entry,
        string packageDirectory)
    {
        _process = process;
        Origin = origin;
        HostEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ServerEnvironmentVariable] = origin.AbsoluteUri,
            [EntryEnvironmentVariable] = entry,
            [PackageDirectoryEnvironmentVariable] = packageDirectory,
        };
    }

    internal Uri Origin { get; }

    internal IReadOnlyDictionary<string, string?> HostEnvironment { get; }

    internal Task<int> Completion => _process.Completion;

    internal static async Task<ViteDevelopmentServer> StartAsync(
        DevProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        int port = ReserveLoopbackPort();
        Uri origin = new($"http://127.0.0.1:{port}/", UriKind.Absolute);
        IReadOnlyList<string> arguments = CreateArguments(configuration, port);
        RunningProcess process = RunningProcess.Start(
            "vite",
            "npm",
            configuration.WorkspaceRoot,
            arguments,
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["BROWSER"] = "none",
            });
        var server = new ViteDevelopmentServer(
            process,
            origin,
            configuration.ViteDevServerEntry,
            configuration.FrontendPackageDirectory);
        try
        {
            await server.WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"[dev] Vite development server ready at {origin}");
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
            port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--strictPort",
        ];

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        Uri clientModule = new(Origin, "@vite/client");
        Uri entryModule = new(Origin, HostEnvironment[EntryEnvironmentVariable]![1..]);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (Completion.IsCompleted)
                {
                    int exitCode = await Completion.ConfigureAwait(false);
                    throw new DevDevelopmentException(
                        "WUTDEV1007",
                        $"The Vite development server exited before readiness with code {exitCode}.");
                }

                try
                {
                    using HttpResponseMessage clientResponse = await client
                        .GetAsync(clientModule, timeout.Token)
                        .ConfigureAwait(false);
                    if (clientResponse.IsSuccessStatusCode)
                    {
                        using HttpResponseMessage entryResponse = await client
                            .GetAsync(entryModule, timeout.Token)
                            .ConfigureAwait(false);
                        if (entryResponse.IsSuccessStatusCode)
                        {
                            return;
                        }
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!timeout.IsCancellationRequested)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DevDevelopmentException(
                "WUTDEV1007",
                $"Timed out waiting for the Vite development server at {Origin}.");
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
