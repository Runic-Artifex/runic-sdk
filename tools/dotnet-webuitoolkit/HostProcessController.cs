using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed class HostProcessController : IAsyncDisposable
{
    private readonly string _dotnetHost;
    private readonly DevProjectConfiguration _configuration;
    private readonly DevOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource<int> _unexpectedExit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RunningProcess? _host;

    internal HostProcessController(
        string dotnetHost,
        DevProjectConfiguration configuration,
        DevOptions options)
    {
        _dotnetHost = dotnetHost;
        _configuration = configuration;
        _options = options;
    }

    internal Task<int> Completion => _unexpectedExit.Task;

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
            if (_host is not null)
            {
                RunningProcess previous = _host;
                _host = null;
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine("[dev] Reloading the native CsWebUi host.");
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
        var arguments = new List<string>
        {
            "watch",
            "--project",
            _configuration.ProjectPath,
            "--configuration",
            _options.Configuration,
            "--no-restore",
            "--non-interactive",
            "run",
            "--no-launch-profile",
        };
        if (_options.ApplicationArguments.Count != 0)
        {
            arguments.Add("--");
            arguments.AddRange(_options.ApplicationArguments);
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["WebUIToolkitFrontendEnabled"] = "false",
            ["WebUIToolkitFrontendInstall"] = "false",
            ["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1",
        };
        return RunningProcess.Start(
            "host",
            _dotnetHost,
            _configuration.ProjectDirectory,
            arguments,
            environment);
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
