using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// A random value for each start of the development server process. vite-plugin-runic answers <c>/__runic/state</c>
    /// with it in <see cref="SessionHeader"/>: <c>runic()</c> on a Vite server, <c>runicDevSessionProxy()</c> in an
    /// Angular proxy configuration. dev thus does not take another process that bound the port first for its server.
    /// </summary>
    internal const string SessionEnvironmentVariable = "RUNIC_APPLICATION_DEV_SESSION";
    internal const string SessionHeader = "X-Runic-Dev-Session";
    internal const string StatePath = "__runic/state";
    internal const string RunicVitePluginPackage = "@runic-artifex/vite-plugin-runic";
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
        // A new value for each attempt: a server from an earlier attempt cannot answer for this one.
        string session = CreateSession();
        bool confirmSession = ConfirmsSession(configuration);
        JavaScriptPackageManager packageManager = JavaScriptPackageManager.Resolve(
            configuration.WorkspaceRoot,
            configuration.FrontendPackageDirectory);
        var conflict = new PortConflictWatch();
        RunningProcess process = RunningProcess.Start(
            "vite",
            packageManager.Executable,
            configuration.WorkspaceRoot,
            arguments,
            CreateEnvironment(configuration, session),
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
            await server.WaitUntilReadyAsync(confirmSession ? session : null, cancellationToken).ConfigureAwait(false);
            if (!configuration.HasFrontendCompiler)
            {
                using var client = new HttpClient();
                var documents = new List<(string Destination, string Document)>();
                foreach (string destination in configuration.DevelopmentServerDocuments)
                {
                    documents.Add((destination, await FrontendDevelopmentDocument.ReadAsync(
                            client, origin, destination, "Vite", cancellationToken)
                        .ConfigureAwait(false)));
                }
                // Vite exits at once when another process holds the port (--strictPort). Write nothing that such a
                // process served.
                ThrowIfExited(server.Completion);
                foreach ((string destination, string document) in documents)
                {
                    FrontendDevelopmentDocument.Write(
                        configuration,
                        origin,
                        destination,
                        document);
                }
            }
            ThrowIfExited(server.Completion);
            if (confirmSession)
            {
                Console.WriteLine("[dev] The Vite development server confirmed this dev session.");
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

    /// <summary>
    /// Throws the readiness failure when the development server process has exited, so that a port conflict is
    /// retried on another port and nothing another process served is used.
    /// </summary>
    internal static void ThrowIfExited(Task<int> completion, string server = "Vite")
    {
        if (completion.IsCompleted)
        {
            throw new DevDevelopmentException(
                "RAPPDEV1007",
                $"The {server} development server exited before readiness with code {completion.Result}.");
        }
    }

    /// <summary>32 random bytes as 64 lowercase hex digits, the shape vite-plugin-runic accepts.</summary>
    internal static string CreateSession() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Whether readiness requires the session confirmation: the resolved Vite configuration has <c>runic()</c>, or,
    /// when dev did not resolve it (an external frontend compiler), the frontend declares vite-plugin-runic. Without
    /// the plugin nothing can confirm the session, and dev relies on Vite exiting when the port is taken.
    /// </summary>
    internal static bool ConfirmsSession(DevProjectConfiguration configuration) =>
        configuration.UsesRunicVitePlugin ?? DeclaresPackage(Path.Combine(configuration.FrontendPackageDirectory, "package.json"), RunicVitePluginPackage);

    internal static bool DeclaresPackage(string packageJsonPath, string package)
    {
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(packageJsonPath));
            if (manifest.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (string section in (string[])["dependencies", "devDependencies", "optionalDependencies"])
            {
                if (manifest.RootElement.TryGetProperty(section, out JsonElement packages) &&
                    packages.ValueKind == JsonValueKind.Object && packages.TryGetProperty(package, out _))
                {
                    return true;
                }
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, string?> CreateEnvironment(
        DevProjectConfiguration configuration, string? session = null) =>
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [SessionEnvironmentVariable] = session,
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

    private async Task WaitUntilReadyAsync(string? session, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(1),
        };
        Console.WriteLine($"[vite] Probing {Origin} for the Vite client and application entry.");
        await WaitUntilReadyAsync(Origin, HostEnvironment[EntryEnvironmentVariable]![1..],
            Completion, client, TimeSpan.FromSeconds(30), cancellationToken, session).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the server serves the Vite client and the application entry, and, with a <paramref name="session"/>,
    /// answers <c>/__runic/state</c> with it.
    /// </summary>
    internal static async Task WaitUntilReadyAsync(
        Uri origin, string entry, Task<int> completion, HttpClient client,
        TimeSpan deadline, CancellationToken cancellationToken, string? session = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        Uri clientModule = new(origin, "@vite/client");
        Uri entryModule = new(origin, entry);
        string probe = "Vite client";
        string lastResponse = "no response";
        // The last attempt that finished. The deadline can cut off the next attempt before its first answer; reporting
        // that one would say "Vite client: no response" about a server that answered everything but the session.
        (string Probe, string Response)? finished = null;
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
                            if (session is null)
                            {
                                return;
                            }
                            probe = "session confirmation";
                            lastResponse = "no response";
                            using HttpResponseMessage stateResponse = await client
                                .GetAsync(new Uri(origin, StatePath), timeout.Token)
                                .ConfigureAwait(false);
                            lastResponse = $"HTTP {(int)stateResponse.StatusCode}";
                            if (stateResponse.IsSuccessStatusCode)
                            {
                                if (ConfirmsSession(stateResponse, session))
                                {
                                    return;
                                }
                                lastResponse = $"no {SessionHeader} for this session";
                            }
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

                finished = (probe, lastResponse);
                await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (finished is { } last && lastResponse == "no response" && Stage(last.Probe) >= Stage(probe))
            {
                (probe, lastResponse) = last;
            }
            throw new DevDevelopmentException(
                "RAPPDEV1007",
                probe == "session confirmation"
                    ? $"Timed out waiting for the Vite development server to confirm this dev session ({probe}: {lastResponse}). " +
                      "Another process may have taken the port, or the frontend's @runic-artifex/vite-plugin-runic is older than dotnet-runic: " +
                      "run 'dotnet runic doctor' and use one release for both."
                    : $"Timed out waiting for the Vite development server ({probe}: {lastResponse}). " +
                      "Run 'dotnet runic doctor' and verify the configured dev script " +
                      "and Vite entry module.");
        }
    }

    private static int Stage(string probe) => probe switch
    {
        "Vite client" => 0,
        "application entry" => 1,
        _ => 2,
    };

    /// <summary>Whether the response carries exactly this session's value in <see cref="SessionHeader"/>.</summary>
    internal static bool ConfirmsSession(HttpResponseMessage response, string session) =>
        response.Headers.TryGetValues(SessionHeader, out IEnumerable<string>? values) &&
        System.Linq.Enumerable.ToArray(values) is [string answer] &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(answer), Encoding.ASCII.GetBytes(session));

    public async ValueTask DisposeAsync()
        => await _process.DisposeAsync().ConfigureAwait(false);
}
