using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

    /// <summary>
    /// The stamp that dev touches when it ends (<see cref="Stop"/>). Visual Studio's fast up-to-date check compares it
    /// with the build's <c>runic-www.stamp</c> (the Views targets' <c>RunicDevSession</c> set), so the next F5 builds
    /// and copies the frontend over the development pages that dev left in the web root.
    /// </summary>
    internal const string StoppedStampName = "dev-stopped";
    internal static readonly TimeSpan FrontendHeartbeat = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan FrontendMarkerLifetime = TimeSpan.FromSeconds(30);

    /// <summary>The state values: <c>starting</c> until the frontend and Window are usable, then <c>ready</c>, and <c>stopped</c>.</summary>
    internal const string Starting = "starting";
    internal const string ReadyState = "ready";
    internal const string Stopped = "stopped";

    private readonly object _gate = new();
    private DevProjectConfiguration _configuration;
    private readonly DevOptions _options;
    private readonly List<DebugEndpoint> _debugEndpoints;
    private readonly DateTimeOffset _startedAt;
    private readonly DateTimeOffset _processStartedAt;
    private string _state = Starting;
    private Uri? _origin;
    private bool _confirmsSession;
    private int? _windowProcessId;
    private int _windowStarts;
    private bool _frontendPublished;
    private bool _writePending;
    private bool _markerPending;
    private string? _browserUrl;
    private string? _browserCodeFile;
    private IReadOnlyList<BrowserEntry.ServedWindow> _browserWindows = [];
    private readonly List<DebugEndpoint> _windowEndpoints = [];
    private readonly List<(FrontendDependency Dependency, DependencyState State)> _dependencies = [];

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
        _debugEndpoints = [.. debugEndpoints];
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

    internal string StoppedStampPath => System.IO.Path.Combine(_configuration.SessionDirectory, StoppedStampName);

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
        lock (_gate)
        {
            // Before the replace: a marker that stays unwritten is written again by the heartbeat, and removed at the stop.
            _frontendPublished = true;
            _markerPending = true;
        }
        WriteFrontendMarker();
    }

    private void WriteFrontendMarker()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_configuration.SessionDirectory);
            string temporary = FrontendMarkerPath + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(temporary, _configuration.RuntimeWebRoot + "\n");
            Replace(temporary, FrontendMarkerPath);
            _markerPending = false;
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="temporary"/> (<see cref="FileReplace"/>). A replace that still
    /// fails leaves no temporary file and is reported as a <see cref="DevSessionFileException"/> naming the path.
    /// </summary>
    private static void Replace(string temporary, string path)
    {
        try
        {
            FileReplace.Move(temporary, path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            throw new DevSessionFileException(path, error);
        }
    }

    /// <summary>
    /// Writes what an earlier write could not (#1102): the session file, so it does not keep saying <c>starting</c> for a
    /// session that is ready, and the frontend marker. A failure here was reported already and waits for the next try.
    /// </summary>
    internal void RetryPendingWrites()
    {
        bool session, marker;
        lock (_gate)
        {
            session = _writePending;
            marker = _markerPending && _state != Stopped;
        }
        try
        {
            if (marker) WriteFrontendMarker();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            if (session) Write();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Renews the frontend marker until <paramref name="cancellationToken"/> is cancelled.</summary>
    internal async Task RenewFrontendAsync(TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(FrontendHeartbeat, timeProvider, cancellationToken).ConfigureAwait(false);
                RetryPendingWrites();
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
        // --browser: the Window opens no WebView2, so WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS opens nothing either.
        return new(configuration, options,
            options.Browser ? [] : [.. FindDebugEndpoints(configuration, options.Environment, Environment.GetEnvironmentVariable,
                OperatingSystem.IsWindows(), Environment.IsPrivilegedProcess), .. webKitEndpoints ?? []],
            DateTimeOffset.UtcNow,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            Tool.DevToolsExtension.Find());
    }

    /// <summary>The debugging endpoints that the Window's environment asks for but cannot open, with the reason.</summary>
    internal IEnumerable<DebugEndpoint> UnavailableDebugEndpoints
    {
        get
        {
            lock (_gate) return [.. _debugEndpoints.Where(static endpoint => endpoint.UnavailableReason is not null)];
        }
    }

    /// <summary>The endpoints that the session file lists in <c>debugEndpoints</c> now.</summary>
    internal IReadOnlyList<DebugEndpoint> ListedDebugEndpoints
    {
        get
        {
            lock (_gate) return [.. _debugEndpoints.Where(static endpoint => endpoint.UnavailableReason is null).Concat(_windowEndpoints)];
        }
    }

    /// <summary>
    /// Moves a listed endpoint to <c>unavailableDebugEndpoints</c> with <paramref name="reason"/>, such as a fixed port that
    /// another user took while dev runs (<see cref="CsWebUiDebugging.WatchNoWindowPortAsync"/>).
    /// </summary>
    internal void MarkUnavailable(string url, string reason)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(reason);
        lock (_gate)
        {
            int index = _debugEndpoints.FindIndex(endpoint => endpoint.Url == url && endpoint.UnavailableReason is null);
            if (index < 0) return;
            _debugEndpoints[index] = _debugEndpoints[index] with { UnavailableReason = reason };
        }
        Write();
    }

    internal void Start() => Write();

    /// <summary>Takes the configuration with the pages that dev found in the Vite build inputs.</summary>
    internal void Update(DevProjectConfiguration configuration)
    {
        lock (_gate) _configuration = configuration;
        Write();
    }

    /// <summary>
    /// The frontend packages that the page depends on, with their state (devtools-library-dx §4.1, written as
    /// <c>dependencies</c> since 0.24): dev lists them once it read them after the build.
    /// </summary>
    internal void Dependencies(IReadOnlyList<FrontendDependency> dependencies, Func<FrontendDependency, DependencyState> state)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            _dependencies.Clear();
            foreach (FrontendDependency dependency in dependencies) _dependencies.Add((dependency, state(dependency)));
        }
        Write();
    }

    /// <summary>A dependency package's state changed: it builds, built, or its build failed.</summary>
    internal void DependencyChanged(FrontendDependency dependency, DependencyState state)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            int index = _dependencies.FindIndex(entry => FrontendDependencies.PathComparer.Equals(entry.Dependency.Directory, dependency.Directory));
            if (index < 0) _dependencies.Add((dependency, state));
            else if (_dependencies[index].State == state) return;
            else _dependencies[index] = (dependency, state);
        }
        Write();
    }

    /// <summary>The dependency packages and their state, as the file lists them.</summary>
    internal IReadOnlyList<(FrontendDependency Dependency, DependencyState State)> DependencyStates
    {
        get
        {
            lock (_gate) return [.. _dependencies];
        }
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
            // Its browsers ended with it.
            _windowEndpoints.Clear();
            // Usable again when the next Window process starts.
            if (_state == ReadyState) _state = Starting;
        }
        Write();
    }

    /// <summary>
    /// <c>--browser</c>: the entry address that sends a browser to the Window (<see cref="BrowserEntry"/>), written as
    /// <c>browser.url</c>, and the page that opens it with the current one-time code, as <c>browser.codeFile</c> (the code
    /// itself stays out of this file, which other users may be able to read); without <c>--browser</c>, <c>browser</c> is null.
    /// </summary>
    internal void BrowserStarted(string url, string? codeFile = null)
    {
        lock (_gate)
        {
            _browserUrl = url;
            _browserCodeFile = codeFile;
        }
        Write();
    }

    /// <summary>The Windows the serve-only Window process serves now, as <c>browser.windows</c>.</summary>
    internal void BrowserWindows(IReadOnlyList<BrowserEntry.ServedWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        lock (_gate) _browserWindows = windows;
        Write();
    }

    /// <summary>
    /// A debugging endpoint that the running Window opened (a CS-WebUI browser's, <see cref="CsWebUiDebugging"/>): listed
    /// in <c>debugEndpoints</c> until <see cref="WindowEndpointClosed"/> or the Window exits.
    /// </summary>
    internal void WindowEndpointOpened(DebugEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        lock (_gate)
        {
            _windowEndpoints.RemoveAll(existing => existing.Url == endpoint.Url);
            _windowEndpoints.Add(endpoint);
        }
        Write();
    }

    /// <summary>The Window's endpoint at <paramref name="url"/> closed, with its browser.</summary>
    internal void WindowEndpointClosed(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        lock (_gate) _windowEndpoints.RemoveAll(existing => existing.Url == url);
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
            _markerPending = false;
            _windowProcessId = null;
            _browserWindows = [];
            _windowEndpoints.Clear();
        }
        try
        {
            if (_frontendPublished)
            {
                // The marker first: a build that starts now runs the frontend build and copy again.
                File.Delete(FrontendMarkerPath);
                RetireDevelopmentPages();
                if (Directory.Exists(PagesDirectory)) Directory.Delete(PagesDirectory, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            // After the marker: a build that this stamp starts copies the frontend over the development pages.
            Directory.CreateDirectory(_configuration.SessionDirectory);
            File.WriteAllBytes(StoppedStampPath, []);
            // Rewriting an empty file need not change its time.
            File.SetLastWriteTimeUtc(StoppedStampPath, DateTime.UtcNow);
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

    /// <summary>
    /// The page that takes a development page's place in the web root when dev stops (DEV-SEC-48, #1178). A development
    /// page loads its script from the development server's loopback port, which another local process can bind once dev
    /// has stopped and then serve the application script. This page loads nothing: its content security policy refuses
    /// every script and every request. The next build copies the frontend's pages over it (<see cref="StoppedStampName"/>).
    /// </summary>
    internal const string StoppedPage = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
        <title>dotnet runic dev has stopped</title>
        </head>
        <body>
        <p>This page was written by <code>dotnet runic dev</code>, which served the frontend and has stopped.
        Build the project (<code>dotnet build</code>) to put the application's pages here, or start <code>dotnet runic dev</code> again.</p>
        </body>
        </html>

        """;

    /// <summary>
    /// Replaces each development page that dev wrote into the web root, and that is still there unchanged, with
    /// <see cref="StoppedPage"/>. A page that a build replaced since is left as it is. The copies beside the marker
    /// (<see cref="PagesDirectory"/>) tell which pages dev wrote and what they hold.
    /// </summary>
    private void RetireDevelopmentPages()
    {
        if (!Directory.Exists(PagesDirectory)) return;
        string webRoot;
        lock (_gate) webRoot = _configuration.RuntimeWebRoot;
        foreach (string copy in Directory.EnumerateFiles(PagesDirectory, "*", SearchOption.AllDirectories))
        {
            string page = System.IO.Path.GetFullPath(System.IO.Path.GetRelativePath(PagesDirectory, copy), webRoot);
            try
            {
                if (File.Exists(page) && File.ReadAllBytes(page).AsSpan().SequenceEqual(File.ReadAllBytes(copy)))
                    File.WriteAllText(page, StoppedPage, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Writes the session file from the current state. A write that fails keeps the state and marks the file pending, so
    /// the next write, or the heartbeat's <see cref="RetryPendingWrites"/>, writes it.
    /// </summary>
    private void Write()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
        lock (_gate)
        {
            _writePending = true;
            File.WriteAllBytes(temporary, Render());
            Replace(temporary, Path);
            _writePending = false;
        }
    }

    /// <summary>Whether the session file or the frontend marker waits for a write that failed.</summary>
    internal bool HasPendingWrites
    {
        get
        {
            lock (_gate) return _writePending || _markerPending;
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

            // --browser: the Window serves without a native window; a browser opens it through the entry address. The
            // Windows' own addresses carry the surface's credential, so only the entry's are written.
            if (_options.Browser)
            {
                json.WriteStartObject("browser");
                WriteStringOrNull(json, "url", _browserUrl);
                WriteStringOrNull(json, "codeFile", _browserCodeFile);
                json.WriteStartArray("windows");
                foreach (BrowserEntry.ServedWindow served in _browserWindows)
                {
                    json.WriteStartObject();
                    json.WriteString("url", served.EntryUrl);
                    WriteStringOrNull(json, "type", served.Type);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("browser");
            }

            json.WriteStartArray("debugEndpoints");
            foreach (DebugEndpoint endpoint in _debugEndpoints.Where(static endpoint => endpoint.UnavailableReason is null)
                .Concat(_windowEndpoints))
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
            // Since 0.24 (devtools-library-dx S5): the frontend packages that the page depends on, dependencies first,
            // and what dev does with each. Empty until dev read them after the build, and for a page without any.
            json.WriteStartArray("dependencies");
            foreach ((FrontendDependency dependency, DependencyState state) in _dependencies)
            {
                json.WriteStartObject();
                WriteStringOrNull(json, "package", dependency.PackageName.Length == 0 ? null : dependency.PackageName);
                json.WriteString("directory", dependency.Directory);
                json.WriteString("kind", dependency.Kind);
                WriteStringOrNull(json, "owner", dependency.Owner);
                WriteStringOrNull(json, "ownerName", dependency.OwnerName);
                WriteStringOrNull(json, "specifier", dependency.Specifier);
                WriteStringOrNull(json, "buildCommand", dependency.IsBuilt ? dependency.BuildCommand : null);
                WriteStringOrNull(json, "watchCommand", dependency.IsBuilt && dependency.WatchCommand.Length != 0 ? dependency.WatchCommand : null);
                json.WriteStartArray("exports");
                foreach (string export in dependency.Exports) json.WriteStringValue(System.IO.Path.GetFullPath(export, dependency.Directory));
                json.WriteEndArray();
                json.WriteString("state", state.State);
                json.WriteString("watch", state.Watch);
                WriteStringOrNull(json, "message", state.Message);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// The variables a Window that the IDE starts needs for this session, as dev would set them: a Development
    /// environment, without a development server the frontend output directory that Runic Desktop serves live, and on
    /// Linux the WebKit inspector address that dev's CDP adapter attaches to (<see cref="DevOptions.DebugEnvironment"/>), and on
    /// Windows the Runic DevTools extension for WebView2 (<see cref="DevOptions.WindowDevToolsExtension"/>).
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, string>> WindowEnvironment(DevProjectConfiguration configuration, DevOptions options)
    {
        var variables = new List<KeyValuePair<string, string>> { new("DOTNET_ENVIRONMENT", "Development") };
        if (!(options.WatchFrontend && configuration.HasDevelopmentServer) && !configuration.RunsOnCsWebUiOnly)
        {
            variables.Add(new(HostProcessController.DevelopmentAssetsVariable, System.IO.Path.GetFullPath(configuration.FrontendOutputDirectory)));
        }
        variables.AddRange(options.DebugEnvironment);
        if (options.WindowDevToolsExtension is { } extension &&
            Tool.DevToolsExtension.AppendTo(extension, name => name == "DOTNET_ENVIRONMENT" ? "Development" : System.Environment.GetEnvironmentVariable(name)) is { } extensions)
        {
            variables.Add(new(Tool.DevToolsExtension.WindowVariable, extensions));
        }
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
