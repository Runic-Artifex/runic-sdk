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
    private int _hotReloadGeneration;

    internal HostProcessController(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options,
        IReadOnlyDictionary<string, string?>? developmentEnvironment = null)
    {
        _dotnetHost = dotnetHost;
        _configuration = configuration;
        _options = options;
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
            ObserveExit(_host);
        }
        finally
        {
            _gate.Release();
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
                throw new DevUsageException("RAPPDEV1006",
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
            Console.WriteLine("[dev] Reloading the Runic Views Window application.");
            _host = Start();
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
        if (_options.ApplicationArguments.Count != 0)
        {
            arguments.Add("--");
            arguments.AddRange(_options.ApplicationArguments);
        }

        RunningProcess process = RunningProcess.Start(
            _options.WatchHost ? "host" : "app",
            _dotnetHost,
            _configuration.ProjectDirectory,
            arguments,
            CreateDevelopmentEnvironment(_configuration, _developmentEnvironment, environment: _options.Environment),
            _options.WatchHost ? IsExpectedTerminationNotice : null);
        if (_options.WatchHost)
        {
            process.OutputReceived += ObserveOutput;
        }

        return process;
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
    /// <c>--launch-profile</c> selects a profile from <c>Properties/launchSettings.json</c>, whose variables and
    /// arguments dotnet run applies; without it the Window runs with <c>--no-launch-profile</c>, as before 0.11.
    /// </summary>
    private static IReadOnlyList<string> LaunchProfileArguments(DevOptions options) =>
        options.LaunchProfile is { } profile ? ["--launch-profile", profile] : ["--no-launch-profile"];

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
        IReadOnlyList<KeyValuePair<string, string>>? environment = null)
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
        return variables;
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
