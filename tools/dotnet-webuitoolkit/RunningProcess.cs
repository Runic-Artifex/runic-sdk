using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed class RunningProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopping;

    private RunningProcess(Process process)
    {
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _completion.TrySetResult(_process.ExitCode);
    }

    internal Task<int> Completion => _completion.Task;

    internal static RunningProcess Start(
        string label,
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        ProcessStartInfo startInfo = CommandRunner.CreateStartInfo(
            executable,
            workingDirectory,
            arguments);
        startInfo.RedirectStandardInput = false;
        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(key);
                }
                else
                {
                    startInfo.Environment[key] = value;
                }
            }
        }

        var process = new Process { StartInfo = startInfo };
        var running = new RunningProcess(process);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Out.WriteLine($"[{label}] {eventArgs.Data}");
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                Console.Error.WriteLine($"[{label}] {eventArgs.Data}");
            }
        };

        try
        {
            if (!process.Start())
            {
                throw new IOException($"Could not start '{executable}'.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return running;
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            process.Dispose();
            throw new DevUsageException(
                "WUTDEV1004",
                $"Could not start '{executable}'. Ensure it is installed and available on PATH.");
        }
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        CommandRunner.TryTerminate(_process);
        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new IOException($"Process '{_process.StartInfo.FileName}' did not stop.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _process.Dispose();
    }
}
