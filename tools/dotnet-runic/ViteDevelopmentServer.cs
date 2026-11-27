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
    internal const string SourceMapEnvironmentVariable = "RUNIC_APPLICATION_DEV_SOURCE_MAP";
    internal const string StatePath = "__runic/state";
    internal const string RunicVitePluginPackage = "@runic-artifex/vite-plugin-runic";
    private readonly RunningProcess _process;
    private readonly string _viteRoot;
    private readonly RepeatedLineFilter? _repeated;
    private readonly FirstErrorLine? _errors;
    private readonly IReadOnlyList<FrontendDependency> _dependencies;

    private ViteDevelopmentServer(
        RunningProcess process,
        Uri origin,
        string entry,
        string packageDirectory,
        string viteRoot,
        string diagnosticsPath,
        string hotReloadPath,
        string projectPath,
        RepeatedLineFilter? repeated = null,
        FirstErrorLine? errors = null,
        IReadOnlyList<FrontendDependency>? dependencies = null)
    {
        _process = process;
        Origin = origin;
        _viteRoot = viteRoot;
        _repeated = repeated;
        _errors = errors;
        _dependencies = dependencies ?? [];
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

    /// <summary>The first error line of the server's output, for a failure message; null when it printed none.</summary>
    internal string? FirstError => _errors?.Line;

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
        JavaScriptPackageManager packageManager = configuration.PackageManager;
        var conflict = new PortConflictWatch();
        var errors = new FirstErrorLine();
        // F6: Vite logged one unresolved import 1,107 times; each distinct line is printed once, then counted.
        var repeated = new RepeatedLineFilter("vite", Console.WriteLine);
        RunningProcess process;
        try
        {
            process = RunningProcess.Start(
                "vite",
                packageManager.Executable,
                configuration.WorkspaceRoot,
                arguments,
                CreateEnvironment(configuration, session),
                suppressLine: repeated.Suppress,
                observeLine: line =>
                {
                    conflict.Observe(line);
                    errors.Observe(line);
                },
                // The package manager, Node.js below it, and the runic-translations serve that Vite's plugin starts.
                endTreeAtStop: true);
        }
        catch
        {
            repeated.Dispose();
            throw;
        }
        var server = new ViteDevelopmentServer(
            process,
            origin,
            configuration.ViteDevServerEntry,
            configuration.FrontendPackageDirectory,
            configuration.EffectiveViteRoot,
            configuration.FrontendCompilerDiagnosticsPath,
            configuration.FrontendCompilerHotReloadPath + ".ready",
            configuration.ProjectPath,
            repeated,
            errors,
            configuration.FrontendDependencies);
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
                $"The {server} development server exited before readiness with code {completion.Result}.")
            {
                ChildExitCode = completion.Result,
            };
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
            // vite-plugin-runic serves the Bridge generator's development source map from here (#951).
            [SourceMapEnvironmentVariable] = configuration.BridgeSourceMapPath.Length == 0 ? null : configuration.BridgeSourceMapPath,
        };

    internal static IReadOnlyList<string> CreateArguments(
        DevProjectConfiguration configuration,
        int port)
    {
        JavaScriptPackageManager packageManager = configuration.PackageManager;
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
            Completion, client, TimeSpan.FromSeconds(30), cancellationToken, session, _viteRoot,
            () => _errors?.Line, error => FrontendDependencies.Explain(_dependencies, error)).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the server serves the Vite client and the application entry as a JavaScript module, and, with a
    /// <paramref name="session"/>, answers <c>/__runic/state</c> with it. An entry Vite answers with another content
    /// type, such as the <c>index.html</c> fallback for a path that is not below its root, fails at once.
    /// </summary>
    /// <remarks>
    /// devtools-library-dx §3.5, fail fast: an entry that Vite answers with HTTP 500 twice, such as an import it cannot
    /// resolve, fails at once with Vite's own error (the message of its error page, else the first error line of its
    /// output), and <paramref name="explain"/> adds the dependency package it names. A server that exits before
    /// readiness reports the first error line of its output (<paramref name="outputError"/>).
    /// </remarks>
    internal static async Task WaitUntilReadyAsync(
        Uri origin, string entry, Task<int> completion, HttpClient client,
        TimeSpan deadline, CancellationToken cancellationToken, string? session = null, string? viteRoot = null,
        Func<string?>? outputError = null, Func<string, string?>? explain = null)
    {
        int entryFailures = 0;
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
                    string? output = outputError?.Invoke();
                    throw new DevDevelopmentException(
                        "RAPPDEV1007",
                        $"The Vite development server exited before readiness with code {exitCode}" +
                        (output is null ? "." : $": {output}") +
                        (output is not null && explain?.Invoke(output) is { } named ? " " + named : ""))
                    {
                        ChildExitCode = exitCode,
                    };
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
                        if ((int)entryResponse.StatusCode == 500 && ++entryFailures >= 2)
                        {
                            string body = await entryResponse.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                            string error = ReadErrorPage(body) ?? outputError?.Invoke() ?? "Vite printed the error above.";
                            throw new DevDevelopmentException(
                                "RAPPDEV1007",
                                $"The Vite development server cannot serve the application entry '/{entry.TrimStart('/')}' (HTTP 500): {error}" +
                                (explain?.Invoke(error) is { } named ? " " + named : ""));
                        }
                        if (entryResponse.IsSuccessStatusCode)
                        {
                            RequireModule(entryResponse, entry, viteRoot);
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

    /// <summary>
    /// Throws when the entry answer is not a JavaScript module. Vite answers a path it cannot resolve below its root
    /// with its <c>index.html</c> fallback (HTTP 200, <c>text/html</c>), which the window must not load as its entry.
    /// </summary>
    internal static void RequireModule(HttpResponseMessage response, string entry, string? viteRoot)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (IsJavaScript(mediaType))
        {
            return;
        }
        string root = string.IsNullOrEmpty(viteRoot) ? "the frontend package directory" : $"'{viteRoot}'";
        throw new DevDevelopmentException(
            "RAPPDEV1007",
            $"The Vite development server answered the application entry '/{entry.TrimStart('/')}' with " +
            $"{(mediaType is null ? "no content type" : $"'{mediaType}'")}, not a JavaScript module. " +
            $"Vite resolves the entry against its root, {root}: set RunicApplicationFrontendViteDevServerEntry to the " +
            "module's path below that root, starting with '/', and run 'dotnet runic doctor'.");
    }

    /// <summary>
    /// The error of Vite's error page (its error middleware's HTTP 500 answer embeds <c>const error = {…}</c> for its
    /// overlay): the message, with the plugin and file when Vite names them; null for another answer.
    /// </summary>
    internal static string? ReadErrorPage(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(body, @"const error = (?<json>\{.*?\})\s*\r?\n");
        if (!match.Success) return null;
        try
        {
            using JsonDocument error = JsonDocument.Parse(match.Groups["json"].Value);
            string Text(string name) => error.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Trim()
                : string.Empty;
            string message = Text("message");
            if (message.Length == 0) return null;
            message = message.ReplaceLineEndings(" ");
            if (Text("plugin") is { Length: > 0 } plugin && !message.Contains(plugin, StringComparison.Ordinal)) message = $"[{plugin}] {message}";
            if (Text("id") is { Length: > 0 } id && !message.Contains(id, StringComparison.Ordinal)) message += $" (in {id})";
            return message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Whether a media type is one Vite and other servers send for JavaScript modules.</summary>
    internal static bool IsJavaScript(string? mediaType) =>
        mediaType?.Trim().ToLowerInvariant() is "text/javascript" or "application/javascript" or "application/x-javascript"
            or "text/x-javascript" or "text/ecmascript" or "application/ecmascript";

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
    {
        try
        {
            await _process.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _repeated?.Dispose();
        }
    }
}
