using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

/// <summary>
/// The dev session file, <c>obj/runic/dev-session.json</c>: what an IDE, a task or an extension needs to attach to a
/// running <c>dotnet runic dev</c> (schema <see cref="Schema"/>). <c>dev</c> writes it when it starts, rewrites it when
/// the development server is ready and each time the Window process starts or exits, and marks it
/// <c>"state": "stopped"</c> when it ends. Every write replaces the file whole, so a reader never sees half of it.
/// </summary>
internal sealed partial class DevSession
{
    internal const string Schema = "runic.application.tool.dev-session/1";
    internal const string FileName = "dev-session.json";

    /// <summary>
    /// The frontend marker that the Views build targets read (<c>RunicViewsDetectDevSession</c>): one line, the runtime web
    /// root this session serves. While its write time is less than <see cref="FrontendMarkerLifetime"/> old, a build of
    /// that output skips the production frontend build, and with <see cref="PagesDirectoryName"/> keeps the development
    /// pages in the web root. dev renews it every <see cref="FrontendHeartbeat"/> and deletes it when it ends, so a dev
    /// that was killed stops affecting builds within the lifetime.
    /// </summary>
    internal const string FrontendMarkerName = "dev-frontend";
    internal const string PagesDirectoryName = "dev-pages";
    internal static readonly TimeSpan FrontendHeartbeat = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FrontendMarkerLifetime = TimeSpan.FromSeconds(30);

    /// <summary>The state values: <c>starting</c> until the frontend and Window are usable, then <c>ready</c>, and <c>stopped</c>.</summary>
    internal const string Starting = "starting";
    internal const string ReadyState = "ready";
    internal const string Stopped = "stopped";

    private readonly object _gate = new();
    private DevProjectConfiguration _configuration;
    private readonly DevOptions _options;
    private readonly IReadOnlyList<DebugEndpoint> _debugEndpoints;
    private readonly DateTimeOffset _startedAt;
    private readonly DateTimeOffset _processStartedAt;
    private string _state = Starting;
    private Uri? _origin;
    private bool _confirmsSession;
    private int? _windowProcessId;
    private int _windowStarts;
    private bool _frontendPublished;

    internal DevSession(
        DevProjectConfiguration configuration,
        DevOptions options,
        IReadOnlyList<DebugEndpoint> debugEndpoints,
        DateTimeOffset startedAt,
        DateTimeOffset processStartedAt,
        string? devToolsExtension = null)
    {
        _configuration = configuration;
        _options = options;
        _debugEndpoints = debugEndpoints;
        _startedAt = startedAt;
        _processStartedAt = processStartedAt.ToUniversalTime();
        DevToolsExtension = devToolsExtension;
        Path = System.IO.Path.Combine(configuration.SessionDirectory, FileName);
    }

    /// <summary>The Runic DevTools extension's unpacked directory beside the tool, or null without one.</summary>
    internal string? DevToolsExtension { get; }

    /// <summary>The absolute path of the session file.</summary>
    internal string Path { get; }

    internal string FrontendMarkerPath => System.IO.Path.Combine(_configuration.SessionDirectory, FrontendMarkerName);

    internal string PagesDirectory => System.IO.Path.Combine(_configuration.SessionDirectory, PagesDirectoryName);

    /// <summary>
    /// Marks the frontend as served by this session for the build targets. With <paramref name="pages"/>, the
    /// development pages that dev wrote into the web root are copied beside the marker, so a build that replaces or
    /// cleans the web root puts them back.
    /// </summary>
    internal void PublishFrontend(IReadOnlyList<string> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        string webRoot;
        lock (_gate) webRoot = _configuration.RuntimeWebRoot;
        if (Directory.Exists(PagesDirectory)) Directory.Delete(PagesDirectory, recursive: true);
        foreach (string page in pages)
        {
            string relative = page.Replace('\\', '/').TrimStart('/');
            string source = System.IO.Path.GetFullPath(relative, webRoot);
            if (!File.Exists(source)) continue;
            string destination = System.IO.Path.GetFullPath(relative, PagesDirectory);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        Directory.CreateDirectory(_configuration.SessionDirectory);
        string temporary = FrontendMarkerPath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        File.WriteAllText(temporary, webRoot + "\n");
        File.Move(temporary, FrontendMarkerPath, overwrite: true);
        lock (_gate) _frontendPublished = true;
    }

    /// <summary>Renews the frontend marker until <paramref name="cancellationToken"/> is cancelled.</summary>
    internal async Task RenewFrontendAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(FrontendHeartbeat, timeProvider, cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(FrontendMarkerPath)) File.SetLastWriteTimeUtc(FrontendMarkerPath, timeProvider.GetUtcNow().UtcDateTime);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The next renewal tries again; a marker that ages out only makes builds run the frontend build.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// The session of a configuration, with the debugging endpoints the Window's environment enables. A Window that dev
    /// starts has dev's token, and one that the IDE starts (<c>--no-window</c>) has the IDE's, which runs dev's task, so
    /// dev's own elevation (<see cref="Environment.IsPrivilegedProcess"/>: an elevated token on Windows) stands for the
    /// Window's.
    /// </summary>
    internal static DevSession Create(DevProjectConfiguration configuration, DevOptions options,
        IReadOnlyList<DebugEndpoint>? webKitEndpoints = null)
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        return new(configuration, options,
            [.. FindDebugEndpoints(configuration, options.Environment, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows(),
                Environment.IsPrivilegedProcess), .. webKitEndpoints ?? []],
            DateTimeOffset.UtcNow,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            Tool.DevToolsExtension.Find());
    }

    /// <summary>The debugging endpoints that the Window's environment asks for but cannot open, with the reason.</summary>
    internal IEnumerable<DebugEndpoint> UnavailableDebugEndpoints => _debugEndpoints.Where(static endpoint => endpoint.UnavailableReason is not null);

    internal void Start() => Write();

    /// <summary>Takes the configuration with the pages that dev found in the Vite build inputs.</summary>
    internal void Update(DevProjectConfiguration configuration)
    {
        lock (_gate) _configuration = configuration;
        Write();
    }

    internal void FrontendReady(Uri? origin, bool confirmsSession)
    {
        lock (_gate)
        {
            _origin = origin;
            _confirmsSession = confirmsSession;
        }
        Write();
    }

    internal void WindowStarted(int processId)
    {
        lock (_gate)
        {
            _windowProcessId = processId;
            _windowStarts++;
        }
        Write();
    }

    internal void WindowExited(int processId)
    {
        lock (_gate)
        {
            if (_windowProcessId == processId) _windowProcessId = null;
            // Usable again when the next Window process starts.
            if (_state == ReadyState) _state = Starting;
        }
        Write();
    }

    internal void Ready()
    {
        lock (_gate) _state = ReadyState;
        Write();
    }

    /// <summary>Marks the session stopped. A failure to write is not reported: dev is ending anyway.</summary>
    internal void Stop()
    {
        lock (_gate)
        {
            _state = Stopped;
            _windowProcessId = null;
        }
        try
        {
            if (_frontendPublished)
            {
                // The marker first: a build that starts now runs the frontend build and copy again.
                File.Delete(FrontendMarkerPath);
                if (Directory.Exists(PagesDirectory)) Directory.Delete(PagesDirectory, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            Write();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void Write()
    {
        byte[] content;
        lock (_gate) content = Render();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        lock (_gate)
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, Path, overwrite: true);
        }
    }

    /// <summary>The session file's JSON. Paths are absolute; an absent value is null.</summary>
    internal byte[] Render()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            // A local file for tools, not HTML: keep '+' in times and non-ASCII paths readable.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteString("schema", Schema);
            json.WriteString("state", _state);
            json.WriteNumber("processId", Environment.ProcessId);
            json.WriteString("startedAt", _startedAt.ToString("O", CultureInfo.InvariantCulture));
            // The operating system's start time of the dev process (Process.StartTime), so a reader can tell this dev
            // from a later process that reuses processId.
            json.WriteString("processStartedAt", _processStartedAt.ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("project", _configuration.ProjectPath);
            json.WriteString("configuration", _options.Configuration);

            json.WriteStartObject("frontend");
            bool server = _options.WatchFrontend && _configuration.HasDevelopmentServer;
            WriteStringOrNull(json, "developmentServer", server ? _configuration.DevelopmentServerKind : null);
            WriteStringOrNull(json, "url", _origin?.AbsoluteUri);
            if (_origin is not null) json.WriteNumber("port", _origin.Port);
            else json.WriteNull("port");
            WriteStringOrNull(json, "stateUrl",
                _origin is not null && _confirmsSession ? new Uri(_origin, ViteDevelopmentServer.StatePath).AbsoluteUri : null);
            json.WriteString("directory", _configuration.FrontendPackageDirectory);
            json.WriteString("outputDirectory", _configuration.FrontendOutputDirectory);
            WriteStringOrNull(json, "generatedDirectory",
                _configuration.FrontendGeneratedDirectory.Length == 0 ? null : _configuration.FrontendGeneratedDirectory);
            json.WriteEndObject();

            json.WriteString("webRoot", _configuration.RuntimeWebRoot);
            // The Bridge generator's development source map that dev passes to the development server
            // (RUNIC_APPLICATION_DEV_SOURCE_MAP) for vite-plugin-runic; null when dev passes none. The build writes it.
            WriteStringOrNull(json, "sourceMap",
                server && _configuration.BridgeSourceMapPath.Length > 0 ? _configuration.BridgeSourceMapPath : null);
            json.WriteStartArray("pages");
            foreach (string document in server ? _configuration.DevelopmentServerDocuments : ["index.html"])
            {
                string relative = document.Replace('\\', '/').TrimStart('/');
                json.WriteStartObject();
                json.WriteString("document", relative);
                json.WriteString("file", System.IO.Path.GetFullPath(relative, _configuration.RuntimeWebRoot));
                WriteStringOrNull(json, "url", _origin is null ? null : new Uri(_origin, relative).AbsoluteUri);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartObject("window");
            // "dev" starts the Window; "ide" (--no-window) leaves it to the IDE, which starts it with "environment".
            json.WriteString("owner", _options.StartWindow ? "dev" : "ide");
            json.WriteString("program", _configuration.WindowProgram.Executable);
            if (_windowProcessId is { } pid) json.WriteNumber("processId", pid);
            else json.WriteNull("processId");
            json.WriteNumber("starts", _windowStarts);
            json.WriteStartObject("environment");
            foreach ((string name, string value) in WindowEnvironment(_configuration, _options))
            {
                json.WriteString(name, value);
            }
            json.WriteEndObject();
            json.WriteEndObject();

            json.WriteStartArray("debugEndpoints");
            foreach (DebugEndpoint endpoint in _debugEndpoints.Where(static endpoint => endpoint.UnavailableReason is null))
            {
                json.WriteStartObject();
                json.WriteString("engine", endpoint.Engine);
                json.WriteString("protocol", endpoint.Protocol);
                json.WriteString("url", endpoint.Url);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            // Endpoints the Window's environment asks for but the engine does not open, such as WebView2's in an elevated
            // Window, so readers that only know debugEndpoints never wait on them.
            json.WriteStartArray("unavailableDebugEndpoints");
            foreach (DebugEndpoint endpoint in UnavailableDebugEndpoints)
            {
                json.WriteStartObject();
                json.WriteString("engine", endpoint.Engine);
                json.WriteString("protocol", endpoint.Protocol);
                json.WriteString("url", endpoint.Url);
                json.WriteString("reason", endpoint.UnavailableReason);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            // The unpacked Runic DevTools extension that ships with this tool, for a browser's --load-extension or a
            // WebView2 Window's extension loading; null when the tool has none.
            WriteStringOrNull(json, "devtoolsExtension", DevToolsExtension);
            WriteStringOrNull(json, "devtoolsExtensionId", DevToolsExtension is null ? null : Tool.DevToolsExtension.Id);
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// The variables a Window that the IDE starts needs for this session, as dev would set them: a Development
    /// environment, without a development server the frontend output directory that Runic Desktop serves live, and on
    /// Linux the WebKit inspector address that dev's CDP adapter attaches to (<see cref="DevOptions.DebugEnvironment"/>).
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> WindowEnvironment(DevProjectConfiguration configuration, DevOptions options)
    {
        var variables = new List<KeyValuePair<string, string>> { new("DOTNET_ENVIRONMENT", "Development") };
        if (!(options.WatchFrontend && configuration.HasDevelopmentServer) && !configuration.RunsOnCsWebUiOnly)
        {
            variables.Add(new(HostProcessController.DevelopmentAssetsVariable, System.IO.Path.GetFullPath(configuration.FrontendOutputDirectory)));
        }
        variables.AddRange(options.DebugEnvironment);
        return variables;
    }

    private static void WriteStringOrNull(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null) json.WriteNull(name);
        else json.WriteString(name, value);
    }

    /// <summary>
    /// A debugging endpoint of the page's engine that the Window's environment enables, or, with
    /// <paramref name="UnavailableReason"/>, asks for but cannot open.
    /// </summary>
    internal sealed record DebugEndpoint(string Engine, string Protocol, string Url, string? UnavailableReason = null);

    /// <summary>The reason of a WebView2 endpoint in an elevated Window, which ignores the variable.</summary>
    internal const string ElevatedReason = "elevated";

    internal const string WebView2ArgumentsVariable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

    /// <summary>
    /// The Chrome DevTools Protocol endpoint of WebView2 (Runic Desktop on Windows) when
    /// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c>, from <c>-e</c> or dev's own environment, has
    /// <c>--remote-debugging-port=&lt;port&gt;</c>. dev does not enable remote debugging itself. When the Window runs
    /// <paramref name="elevated"/>, WebView2 (Runtime 150 and later) ignores the variable, so the endpoint is unavailable.
    /// </summary>
    internal static IReadOnlyList<DebugEndpoint> FindDebugEndpoints(
        DevProjectConfiguration configuration,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        Func<string, string?> readEnvironment,
        bool windows,
        bool elevated)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(readEnvironment);
        if (!windows || configuration.RunsOnCsWebUiOnly) return [];
        string? arguments = environment.LastOrDefault(variable =>
            variable.Key.Equals(WebView2ArgumentsVariable, StringComparison.OrdinalIgnoreCase)).Value
            ?? readEnvironment(WebView2ArgumentsVariable);
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        Match port = RemoteDebuggingPort().Match(arguments);
        return port.Success && int.TryParse(port.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value) &&
            value is > 0 and <= 65535
            ? [new DebugEndpoint("webview2", "cdp", $"http://127.0.0.1:{value.ToString(CultureInfo.InvariantCulture)}/",
                elevated ? ElevatedReason : null)]
            : [];
    }

    [GeneratedRegex(@"(?:^|\s)--remote-debugging-port=(?<port>\d+)(?:\s|$)")]
    private static partial Regex RemoteDebuggingPort();
}
