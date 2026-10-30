using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Application.Tool;

internal sealed class RunningProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _label;
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopping;

    private RunningProcess(Process process, string label)
    {
        _process = process;
        _label = label;
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => _completion.TrySetResult(_process.ExitCode);
    }

    internal Task<int> Completion => _completion.Task;

    /// <summary>
    /// Waits, for at most <paramref name="timeout"/>, until the output of an exited process has been delivered. A
    /// descendant that still holds the pipes open must not stall the caller.
    /// </summary>
    internal async Task DrainOutputAsync(TimeSpan timeout)
    {
        try
        {
            await _process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

    internal event Action<string>? OutputReceived;

    internal static RunningProcess Start(
        string label,
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        Func<string, bool>? suppressLine = null,
        Action<string>? observeLine = null)
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
        var running = new RunningProcess(process, label);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                observeLine?.Invoke(eventArgs.Data);
                running.OutputReceived?.Invoke(eventArgs.Data);
                if (suppressLine?.Invoke(eventArgs.Data) == true) return;
                Console.Out.WriteLine($"[{label}] {eventArgs.Data}");
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                observeLine?.Invoke(eventArgs.Data);
                running.OutputReceived?.Invoke(eventArgs.Data);
                if (suppressLine?.Invoke(eventArgs.Data) == true) return;
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
            throw new DevDevelopmentException(
                "RAPPDEV1004",
                $"Could not start '{CommandRunner.DescribeProgram(executable, arguments)}'. Ensure it is installed and available on PATH.",
                $"Executable: {executable}\n" + CommandRunner.LocalDetail(workingDirectory, "Run 'dotnet runic doctor' to check prerequisites."), category: CommandExitCategory.Unavailable);
        }
    }

    /// <summary>
    /// Asks the application to stop as Ctrl+C in a terminal would, waits up to <paramref name="grace"/> for it to
    /// exit, then kills whatever remains of the process tree.
    /// </summary>
    internal async Task StopGracefullyAsync(TimeSpan grace)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        using var timeout = new CancellationTokenSource(grace);
        try
        {
            if (!Completion.IsCompleted)
            {
                IReadOnlyList<int> targets = await ProcessTreeSignal
                    .RequestStopAsync(_process.Id, timeout.Token, message => Console.Error.WriteLine($"[{_label}] {message}"))
                    .ConfigureAwait(false);
                while (!Completion.IsCompleted && !targets.All(ProcessTreeSignal.HasExited))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[{_label}] The application did not stop within {grace.TotalSeconds:0.#} seconds; ending it.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or DevUsageException)
        {
            Console.Error.WriteLine($"[{_label}] Could not ask the application to stop: {exception.Message}");
        }

        await TerminateAsync().ConfigureAwait(false);
    }

    internal async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            await Completion.ConfigureAwait(false);
            return;
        }

        await TerminateAsync().ConfigureAwait(false);
    }

    private async Task TerminateAsync()
    {
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
