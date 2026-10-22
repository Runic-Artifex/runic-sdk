using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
        // A new value for each attempt: a server from an earlier attempt cannot answer for this one.
        string session = ViteDevelopmentServer.CreateSession();
        bool confirmSession = ConfirmsSession(configuration);
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        var conflict = new PortConflictWatch();
        RunningProcess process = RunningProcess.Start(
            "angular",
            packageManager.Executable,
            configuration.WorkspaceRoot,
            CreateArguments(configuration, port),
            CreateEnvironment(session),
            observeLine: conflict.Observe);
        var server = new AngularDevelopmentServer(process, origin);
        try
        {
            await server.WaitUntilReadyAsync(confirmSession ? session : null, cancellationToken).ConfigureAwait(false);
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
            if (confirmSession)
            {
                Console.WriteLine("[dev] The Angular development server confirmed this dev session.");
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

    /// <summary>
    /// Whether readiness requires the session confirmation: the Angular frontend declares vite-plugin-runic, whose
    /// <c>runicDevSessionProxy()</c> the serve target's proxy configuration answers <c>/__runic/state</c> with, as the
    /// runic-app template sets up. The Angular CLI loads no Vite plugins, so without it nothing can confirm the session,
    /// and dev relies on the server exiting when the port is taken.
    /// </summary>
    internal static bool ConfirmsSession(DevProjectConfiguration configuration) =>
        ViteDevelopmentServer.DeclaresPackage(
            Path.Combine(configuration.FrontendPackageDirectory, "package.json"),
            ViteDevelopmentServer.RunicVitePluginPackage);

    internal static IReadOnlyDictionary<string, string?> CreateEnvironment(string? session = null) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ViteDevelopmentServer.SessionEnvironmentVariable] = session,
            ["BROWSER"] = "none",
            ["NG_CLI_ANALYTICS"] = "false",
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
                port.ToString(CultureInfo.InvariantCulture),
                "--hmr",
                "--live-reload",
            ]);
    }

    private async Task WaitUntilReadyAsync(string? session, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        await WaitUntilReadyAsync(Origin, Completion, client, TimeSpan.FromSeconds(45), cancellationToken, session)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the server serves the application, and, with a <paramref name="session"/>, answers
    /// <c>/__runic/state</c> with it.
    /// </summary>
    internal static async Task WaitUntilReadyAsync(
        Uri origin, Task<int> completion, HttpClient client, TimeSpan deadline,
        CancellationToken cancellationToken, string? session = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(deadline);
        bool served = false;
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
                        $"The Angular development server exited before readiness with code {exitCode}.");
                }

                try
                {
                    served = false;
                    lastResponse = "no response";
                    using HttpResponseMessage response = await client
                        .GetAsync(origin, timeout.Token)
                        .ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        if (session is null)
                        {
                            return;
                        }
                        served = true;
                        using HttpResponseMessage stateResponse = await client
                            .GetAsync(new Uri(origin, ViteDevelopmentServer.StatePath), timeout.Token)
                            .ConfigureAwait(false);
                        lastResponse = $"HTTP {(int)stateResponse.StatusCode}";
                        if (stateResponse.IsSuccessStatusCode)
                        {
                            if (ViteDevelopmentServer.ConfirmsSession(stateResponse, session))
                            {
                                return;
                            }
                            lastResponse = $"no {ViteDevelopmentServer.SessionHeader} for this session";
                        }
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
                served
                    ? $"Timed out waiting for the Angular development server to confirm this dev session (session confirmation: {lastResponse}). " +
                      "The frontend declares @runic-artifex/vite-plugin-runic, so the serve target's proxyConfig must include runicDevSessionProxy() from it. " +
                      "Otherwise another process may have taken the port, or the plugin is older than dotnet-runic: " +
                      "run 'dotnet runic doctor' and use one release for both."
                    : $"Timed out waiting for the Angular development server at {origin}. " +
                      "Run 'dotnet runic doctor' and verify the Angular serve target.");
        }
    }

    public ValueTask DisposeAsync() => _process.DisposeAsync();

}
