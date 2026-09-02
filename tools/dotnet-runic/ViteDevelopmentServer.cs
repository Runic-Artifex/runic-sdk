using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed class ViteDevelopmentServer : IFrontendDevelopmentServer
{
    internal const string ServerEnvironmentVariable = "RUNIC_TOOLKIT_VITE_DEV_SERVER";
    internal const string EntryEnvironmentVariable = "RUNIC_TOOLKIT_VITE_ENTRY";
    internal const string PackageDirectoryEnvironmentVariable =
        "RUNIC_TOOLKIT_VITE_PACKAGE_DIRECTORY";
    internal const string DiagnosticsEnvironmentVariable =
        "RUNIC_TOOLKIT_FRONTEND_COMPILER_DIAGNOSTICS";
    internal const string HotReloadEnvironmentVariable =
        "RUNIC_TOOLKIT_FRONTEND_COMPILER_HOT_RELOAD";
    internal const string ProjectEnvironmentVariable =
        "RUNIC_TOOLKIT_DEV_PROJECT";

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
        Uri inspectorEndpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(inspectorEndpoint);
        using PhaseTimer phase = PhaseTimer.Start("Starting Vite development server");
        int port = ReserveLoopbackPort();
        Uri origin = new($"http://127.0.0.1:{port}/", UriKind.Absolute);
        RunningProcess process;
        try
        {
            IReadOnlyList<string> arguments = CreateArguments(
                configuration,
                port);
            JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
                configuration.WorkspaceRoot,
                configuration.FrontendPackageDirectory);
            process = RunningProcess.Start(
                "vite",
                packageManager.Executable,
                configuration.WorkspaceRoot,
                arguments,
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["BROWSER"] = "none",
                    ["RUNIC_TOOLKIT_DEVTOOLS_ENDPOINT"] = inspectorEndpoint.AbsoluteUri,
                    ["RUNIC_TOOLKIT_DEV_PROJECT"] = configuration.ProjectPath,
                });
        }
        catch
        {
            throw;
        }
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
                foreach (string destination in configuration.DevelopmentServerDocuments)
                {
                    string document = await server
                        .ReadDevelopmentDocumentAsync(destination, cancellationToken)
                        .ConfigureAwait(false);
                    FrontendDevelopmentDocument.Write(
                        configuration,
                        origin,
                        inspectorEndpoint,
                        destination,
                        document);
                }
            }
            Console.WriteLine($"[dev] Vite development server ready at {origin}");
            phase.Complete();
            return server;
        }
        catch
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> ReadDevelopmentDocumentAsync(
        string document,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync(new Uri(Origin, document), cancellationToken)
            .ConfigureAwait(false);
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
                port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--strictPort",
            ]);
    }

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
                        "RTKDEV1007",
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
                "RTKDEV1007",
                $"Timed out waiting for the Vite development server at {Origin}. " +
                "Run 'dotnet runic doctor' and verify the configured dev script " +
                "and Vite entry module.");
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

    public async ValueTask DisposeAsync()
        => await _process.DisposeAsync().ConfigureAwait(false);
}
