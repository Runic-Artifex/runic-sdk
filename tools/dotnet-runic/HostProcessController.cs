using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed class HostProcessController : IAsyncDisposable
{
    private readonly string _dotnetHost;
    private readonly DevProjectConfiguration _configuration;
    private readonly DevOptions _options;
    private readonly IReadOnlyDictionary<string, string?> _developmentEnvironment;
    private readonly Action<string>? _serveOnlyLine;
    private readonly Action<string>? _debugEndpointLine;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource<int> _unexpectedExit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>How long the application has to close its windows and release its work before it is killed.</summary>
    internal static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The internal tool-to-host contract that RunicDesktopHost (Runic.Application.Views.Desktop) reads in a Development
    /// environment: the frontend output directory to serve live.
    /// </summary>
    internal const string DevelopmentAssetsVariable = "RUNIC_APPLICATION_DEVELOPMENT_ASSETS";

    /// <summary>Makes dotnet watch give the application the same grace when a C# edit restarts it.</summary>
    internal const string WatchCleanupTimeoutVariable = "DOTNET_WATCH_PROCESS_CLEANUP_TIMEOUT_MS";

    private RunningProcess? _host;
    private int _hostProcessId;
    private int _hotReloadGeneration;

    /// <summary>How often <see cref="MonitorWindowAsync"/> looks for the Window process; <c>ps</c> on macOS less often.</summary>
    internal static TimeSpan WindowPollInterval => OperatingSystem.IsMacOS() ? TimeSpan.FromSeconds(1) : TimeSpan.FromMilliseconds(250);

    internal HostProcessController(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
        IReadOnlyDictionary<string, string?>? developmentEnvironment = null,
        Action<string>? serveOnlyLine = null,
        Action<string>? debugEndpointLine = null)
    {
        _dotnetHost = dotnetHost;
        _configuration = configuration;
        _options = options;
        _serveOnlyLine = serveOnlyLine;
        _debugEndpointLine = debugEndpointLine;
        _developmentEnvironment = developmentEnvironment
            ?? new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    internal Task<int> Completion => _unexpectedExit.Task;

    internal int HotReloadGeneration => Volatile.Read(ref _hotReloadGeneration);

    internal async Task<int> WaitForHotReloadAsync(
        int afterGeneration,
        CancellationToken cancellationToken)
    {
        if (HotReloadGeneration > afterGeneration)
        {
            return HotReloadGeneration;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (HotReloadGeneration <= afterGeneration)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token)
                .ConfigureAwait(false);
        }

        return HotReloadGeneration;
    }

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _host = Start();
            Volatile.Write(ref _hostProcessId, _host.Id);
            ObserveExit(_host);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reports each Window process that starts below the current <c>dotnet watch</c> or <c>dotnet run</c> process, and
    /// each one that exits, until <paramref name="cancellationToken"/> is cancelled. <c>dotnet watch</c> restarts the
    /// Window after C# edits without dev knowing, so the process tree is looked at every <see cref="WindowPollInterval"/>.
    /// </summary>
    internal Task MonitorWindowAsync(
        Action<int> started,
        Action<int> exited,
        CancellationToken cancellationToken) =>
        MonitorWindowAsync(
            () => Volatile.Read(ref _hostProcessId),
            root => WindowProcessLocator.FindAsync(root, _configuration.WindowProgram, cancellationToken),
            ProcessTreeSignal.HasExited,
            started,
            exited,
            WindowPollInterval,
            TimeProvider.System,
            cancellationToken);

    /// <summary>The monitor loop, with the process tree, exit test and clock that tests replace.</summary>
    internal static async Task MonitorWindowAsync(
        Func<int> root,
        Func<int, Task<int?>> find,
        Func<int, bool> hasExited,
        Action<int> started,
        Action<int> exited,
        TimeSpan interval,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        int? current = null;
        try
        {
            while (true)
            {
                int host = root();
                int? found = host == 0 ? null : await find(host).ConfigureAwait(false);
                // A list that missed a running Window is not an exit; a different process or an exited one is.
                if (current is { } previous && (found is { } other && other != previous || hasExited(previous)))
                {
                    current = null;
                    exited(previous);
                }
                if (found is { } window && window != current && !hasExited(window))
                {
                    current = window;
                    started(window);
                }
                await Task.Delay(interval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CommandResult build = await CommandRunner.RunAsync(_dotnetHost, _configuration.ProjectDirectory,
                CreateRestartBuildArguments(_configuration, _options), cancellationToken).ConfigureAwait(false);
            if (build.ExitCode != 0)
            {
                Console.Error.Write(build.StandardError);
                Console.Error.Write(build.StandardOutput);
                throw new DevDevelopmentException("RAPPDEV1006",
                    "The Window rebuild failed: " +
                    CommandRunner.DescribeFailure(_dotnetHost, CreateRestartBuildArguments(_configuration, _options), build.ExitCode) +
                    " The running application has been retained.",
                    CommandRunner.LocalDetail(_configuration.ProjectDirectory));
            }
            if (_host is not null)
            {
                RunningProcess previous = _host;
                _host = null;
                await previous.StopGracefullyAsync(StopGracePeriod).ConfigureAwait(false);
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine(DevApplication.ReloadingLine);
            _host = Start();
            Volatile.Write(ref _hostProcessId, _host.Id);
            ObserveExit(_host);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RunningProcess Start()
    {
        var arguments = _options.WatchHost
            ? new List<string>(CreateWatchArguments(_configuration, _options))
            : new List<string>(CreateRunArguments(_configuration, _options));
        arguments.AddRange(ApplicationArguments(_options));

        RunningProcess process = RunningProcess.Start(
            _options.WatchHost ? "host" : "app",
            _dotnetHost,
            _configuration.ProjectDirectory,
            arguments,
            CreateDevelopmentEnvironment(_configuration, _developmentEnvironment, environment: _options.Environment,
                debugEnvironment: _options.DebugEnvironment, browser: _options.Browser, browserOrigin: _options.BrowserOrigin,
                devToolsExtension: _options.WindowDevToolsExtension),
            SuppressLine,
            _options.Browser || _debugEndpointLine is not null ? ObserveProtocolLine : null,
            holdInput: _options.Browser);
        if (_options.WatchHost)
        {
            process.OutputReceived += ObserveOutput;
        }

        return process;
    }

    /// <summary>
    /// dotnet watch's notice of an expected stop, with <c>--browser</c> the serve-only lines, which carry the surface's
    /// credential: dev prints the entry address instead (<see cref="BrowserEntry"/>), and the debugging endpoint lines,
    /// which dev prints as a <c>[dev]</c> hint (<see cref="CsWebUiDebugging"/>).
    /// </summary>
    private bool SuppressLine(string line) =>
        (_options.WatchHost && IsExpectedTerminationNotice(line)) || (_options.Browser && BrowserEntry.IsServeOnlyLine(line)) ||
        (_debugEndpointLine is not null && CsWebUiDebugging.IsProtocolLine(line));

    private void ObserveProtocolLine(string line)
    {
        if (BrowserEntry.IsServeOnlyLine(line)) _serveOnlyLine?.Invoke(line);
        else if (CsWebUiDebugging.IsProtocolLine(line)) _debugEndpointLine?.Invoke(line);
    }

    internal static IReadOnlyList<string> CreateWatchArguments(
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        var arguments = new List<string> { "watch" };
        // The Views MSBuild owner regenerates typed clients before compilation.
        // Restart the Window process so it cannot keep running a stale View shape.
        if (configuration.IsViewsWindowProject) arguments.Add("--no-hot-reload");
        arguments.AddRange([
            "--project",
            configuration.ProjectPath,
            "--configuration",
            options.Configuration,
            "--property:DebugType=portable",
            "--property:DebugSymbols=true",
            "--property:Optimize=false",
        ]);
        arguments.AddRange(DevApplication.CreateBuildProperties(configuration, options, "--property:"));
        if (options.Verbosity is not null)
        {
            arguments.AddRange(["--verbosity", options.Verbosity]);
        }
        arguments.AddRange([
            "--no-restore",
            "--non-interactive",
            "run",
        ]);
        arguments.AddRange(LaunchProfileArguments(options));
        return arguments;
    }

    internal static IReadOnlyList<string> CreateRunArguments(
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        var arguments = new List<string>
        {
            "run",
            "--project",
            configuration.ProjectPath,
            "--configuration",
            options.Configuration,
            "--no-restore",
            "--no-build",
        };
        arguments.AddRange(LaunchProfileArguments(options));
        return arguments;
    }

    /// <summary>
    /// The Window's arguments after <c>--</c>: the profile's when dev passes them itself (D084.2), then the command line's.
    /// dotnet run puts them after the project's <c>RunArguments</c>.
    /// </summary>
    internal static IReadOnlyList<string> ApplicationArguments(DevOptions options) =>
        options.ProfileArguments.Count == 0 && options.ApplicationArguments.Count == 0
            ? []
            : ["--", .. options.ProfileArguments, .. options.ApplicationArguments];

    /// <summary>
    /// <c>--launch-profile</c> selects a profile from <c>Properties/launchSettings.json</c>, whose variables and
    /// arguments dotnet run applies. <see cref="DevLaunch.Resolve"/> names the first <c>Project</c> profile by default
    /// (0.24); without one, or with <c>--no-launch-profile</c>, the Window runs with <c>--no-launch-profile</c>.
    /// </summary>
    private static IReadOnlyList<string> LaunchProfileArguments(DevOptions options) =>
        options.LaunchProfile is { } profile
            ? ["--launch-profile", profile, .. options.RunEnvironment.SelectMany(static variable => new[] { "-e", $"{variable.Key}={variable.Value}" })]
            : ["--no-launch-profile"];

    internal static IReadOnlyList<string> CreateRestartBuildArguments(
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        var arguments = new List<string>
        {
            "build",
            configuration.ProjectPath,
            "--configuration",
            options.Configuration,
            "--no-restore",
        };
        arguments.AddRange(DevApplication.CreateBuildProperties(configuration, options, "-property:"));
        if (options.Verbosity is not null)
        {
            arguments.AddRange(["--verbosity", options.Verbosity]);
        }
        return arguments;
    }

    internal static IReadOnlyDictionary<string, string?> CreateDevelopmentEnvironment(
        DevProjectConfiguration configuration,
        IReadOnlyDictionary<string, string?> developmentEnvironment,
        Func<string, string?>? readEnvironment = null,
        IReadOnlyList<KeyValuePair<string, string>>? environment = null,
        IReadOnlyList<KeyValuePair<string, string>>? debugEnvironment = null,
        bool browser = false,
        string? devToolsExtension = null,
        string? browserOrigin = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Without a development server (its environment is empty), the Runic Desktop host serves the frontend
            // output directory live and reloads its windows on change. With one, the variable is cleared.
            [DevelopmentAssetsVariable] = developmentEnvironment.Count == 0
                ? System.IO.Path.GetFullPath(configuration.FrontendOutputDirectory)
                : null,
            ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1",
            [ViteDevelopmentServer.ServerEnvironmentVariable] = null,
            [ViteDevelopmentServer.EntryEnvironmentVariable] = null,
            [ViteDevelopmentServer.PackageDirectoryEnvironmentVariable] = null,
            [ViteDevelopmentServer.DiagnosticsEnvironmentVariable] = null,
            [ViteDevelopmentServer.HotReloadEnvironmentVariable] = null,
            [ViteDevelopmentServer.ProjectEnvironmentVariable] = null,
            [AngularDevelopmentServer.ServerEnvironmentVariable] = null,
            [AngularDevelopmentServer.KindEnvironmentVariable] = null,
        };
        // The app runs as a development host, so Bridge failures carry local
        // detail (IncludeFailureDetail). An environment the developer chose wins.
        // A variable given with -e counts as chosen too.
        bool Chosen(string name) => !string.IsNullOrEmpty(readEnvironment(name)) ||
            (environment ?? []).Any(variable => variable.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!Chosen("DOTNET_ENVIRONMENT") && !Chosen("ASPNETCORE_ENVIRONMENT"))
        {
            variables["DOTNET_ENVIRONMENT"] = "Development";
        }
        // dotnet watch sends SIGTERM (Ctrl+C on Windows) before a restart, but with hot reload off it kills the
        // application at once unless this is set.
        if (string.IsNullOrEmpty(readEnvironment(WatchCleanupTimeoutVariable)))
        {
            variables[WatchCleanupTimeoutVariable] =
                ((int)StopGracePeriod.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // The debugging variables (WEBKIT_INSPECTOR_SERVER); dev leaves them out when -e or its own environment sets one.
        foreach ((string key, string value) in debugEnvironment ?? [])
        {
            variables[key] = value;
        }
        // -e values win over dev's defaults, as dotnet run -e does; they cannot name the variables below
        // (ToolOptionValues.IsReservedEnvironmentVariable).
        foreach ((string key, string value) in environment ?? [])
        {
            variables[key] = value;
        }
        foreach ((string key, string? value) in developmentEnvironment)
        {
            variables[key] = value;
        }
        // Windows: the Runic DevTools extension joins the folders the developer listed (-e or dev's own environment), in
        // Development only; the host loads none outside Development either.
        if (devToolsExtension is not null &&
            DevToolsExtension.AppendTo(devToolsExtension, name => Lookup(variables, name, readEnvironment)) is { } extensions)
        {
            variables[DevToolsExtension.WindowVariable] = extensions;
        }
        // --browser: the Window serves its pages without presenting them, whatever -e or dev's environment say.
        if (browser)
        {
            variables[BrowserEntry.ServeOnlyVariable] = "1";
            // The Window admits --browser's address as an origin of its pages, and a Runic Desktop page's Bridge connects
            // through it (D077.14).
            variables[BrowserEntry.ServeOriginVariable] = browserOrigin;
        }
        return variables;
    }

    // A variable of the Window's environment as composed so far, else dev's own; a null value is one dev clears.
    private static string? Lookup(Dictionary<string, string?> variables, string name, Func<string, string?> readEnvironment)
    {
        foreach ((string key, string? value) in variables)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return readEnvironment(name);
    }

    /// <summary>Exit codes of an application that stopped on SIGTERM or Ctrl+C: 128 + 15, 128 + 2, and Windows' STATUS_CONTROL_C_EXIT.</summary>
    private static readonly string[] ExpectedTerminationCodes = ["143", "130", "-1073741510"];

    /// <summary>
    /// dotnet watch stops the application with SIGTERM (Ctrl+C on Windows) before each restart, and dev does the
    /// same when it stops. An application without its own signal handling then exits with 143, and dotnet watch
    /// reports "Exited with error code 143" although nothing failed. Other exit codes are still shown.
    /// </summary>
    internal static bool IsExpectedTerminationNotice(string line)
    {
        const string notice = "Exited with error code ";
        int index = line.LastIndexOf(notice, StringComparison.Ordinal);
        if (index < 0) return false;
        // The code can be followed by a period or a colour reset.
        string rest = line[(index + notice.Length)..];
        int length = 0;
        while (length < rest.Length && (char.IsAsciiDigit(rest[length]) || (length == 0 && rest[0] == '-'))) length++;
        string code = rest[..length];
        return Array.IndexOf(ExpectedTerminationCodes, code) >= 0;
    }

    private void ObserveOutput(string line)
    {
        if (line.Contains("Hot reload of changes succeeded", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Hot reload succeeded", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("C# and Razor changes applied", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _hotReloadGeneration);
        }

    }

    private async void ObserveExit(RunningProcess process)
    {
        try
        {
            int exitCode = await process.Completion.ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_host, process))
                {
                    _unexpectedExit.TrySetResult(exitCode);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host is not null)
            {
                RunningProcess previous = _host;
                _host = null;
                await previous.StopGracefullyAsync(StopGracePeriod).ConfigureAwait(false);
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
