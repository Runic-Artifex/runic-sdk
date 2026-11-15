using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    /// <summary>The state values: <c>starting</c> until the frontend and Window are usable, then <c>ready</c>, and <c>stopped</c>.</summary>
    internal const string Starting = "starting";
    internal const string ReadyState = "ready";
    internal const string Stopped = "stopped";

    private readonly object _gate = new();
    private DevProjectConfiguration _configuration;
    private readonly DevOptions _options;
    private readonly IReadOnlyList<DebugEndpoint> _debugEndpoints;
    private readonly DateTimeOffset _startedAt;
    private string _state = Starting;
    private Uri? _origin;
    private bool _confirmsSession;
    private int? _windowProcessId;
    private int _windowStarts;

    internal DevSession(
        DevProjectConfiguration configuration,
        DevOptions options,
        IReadOnlyList<DebugEndpoint> debugEndpoints,
        DateTimeOffset startedAt)
    {
        _configuration = configuration;
        _options = options;
        _debugEndpoints = debugEndpoints;
        _startedAt = startedAt;
        Path = System.IO.Path.Combine(configuration.SessionDirectory, FileName);
    }

    /// <summary>The absolute path of the session file.</summary>
    internal string Path { get; }

    /// <summary>The session of a configuration, with the debugging endpoints the Window's environment enables.</summary>
    internal static DevSession Create(DevProjectConfiguration configuration, DevOptions options) =>
        new(configuration, options,
            FindDebugEndpoints(configuration, options.Environment, Environment.GetEnvironmentVariable, OperatingSystem.IsWindows()),
            DateTimeOffset.UtcNow);

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
            json.WriteString("program", _configuration.WindowProgram.Executable);
            if (_windowProcessId is { } pid) json.WriteNumber("processId", pid);
            else json.WriteNull("processId");
            json.WriteNumber("starts", _windowStarts);
            json.WriteEndObject();

            json.WriteStartArray("debugEndpoints");
            foreach (DebugEndpoint endpoint in _debugEndpoints)
            {
                json.WriteStartObject();
                json.WriteString("engine", endpoint.Engine);
                json.WriteString("protocol", endpoint.Protocol);
                json.WriteString("url", endpoint.Url);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteStringOrNull(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null) json.WriteNull(name);
        else json.WriteString(name, value);
    }

    /// <summary>A debugging endpoint of the page's engine that the Window's environment enables.</summary>
    internal sealed record DebugEndpoint(string Engine, string Protocol, string Url);

    internal const string WebView2ArgumentsVariable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";

    /// <summary>
    /// The Chrome DevTools Protocol endpoint of WebView2 (Runic Desktop on Windows) when
    /// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS</c>, from <c>-e</c> or dev's own environment, has
    /// <c>--remote-debugging-port=&lt;port&gt;</c>. dev does not enable remote debugging itself.
    /// </summary>
    internal static IReadOnlyList<DebugEndpoint> FindDebugEndpoints(
        DevProjectConfiguration configuration,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        Func<string, string?> readEnvironment,
        bool windows)
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
            ? [new DebugEndpoint("webview2", "cdp", $"http://127.0.0.1:{value.ToString(CultureInfo.InvariantCulture)}/")]
            : [];
    }

    [GeneratedRegex(@"(?:^|\s)--remote-debugging-port=(?<port>\d+)(?:\s|$)")]
    private static partial Regex RemoteDebuggingPort();
}
