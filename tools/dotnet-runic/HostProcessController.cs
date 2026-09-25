using System;
using System.Collections.Generic;
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
                throw new DevUsageException("RAPPDEV1006", "Window rebuild failed; the running application has been retained.");
            }
            if (_host is not null)
            {
                RunningProcess previous = _host;
                _host = null;
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
            CreateDevelopmentEnvironment(_configuration, _developmentEnvironment));
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
            "--no-restore",
            "--non-interactive",
            "run",
            "--no-launch-profile",
        ]);
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
            "--no-launch-profile",
        };
        return arguments;
    }

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
        return arguments;
    }

    internal static IReadOnlyDictionary<string, string?> CreateDevelopmentEnvironment(
        DevProjectConfiguration configuration,
        IReadOnlyDictionary<string, string?> developmentEnvironment)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["RUNIC_APPLICATION_DEVELOPMENT_DOCUMENT"] = developmentEnvironment.Count == 0 ? null :
                System.IO.Path.GetFullPath(configuration.DevelopmentServerDocuments[0], configuration.RuntimeWebRoot),
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
        foreach ((string key, string? value) in developmentEnvironment)
        {
            environment[key] = value;
        }
        return environment;
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
