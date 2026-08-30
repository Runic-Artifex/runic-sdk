using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    internal string CombinedOutput => StandardOutput + StandardError;
}

internal static class CommandRunner
{
    private const int MaximumCapturedCharacters = 4 * 1024 * 1024;

    internal static async Task<CommandResult> RunAsync(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateStartInfo(executable, workingDirectory, arguments);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new IOException($"Could not start '{executable}'.");
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            throw new DevUsageException(
                "RTKDEV1004",
                $"Could not start '{executable}'. Ensure it is installed and available on PATH.");
        }

        Task<string> standardOutput = ReadBoundedAsync(
            process.StandardOutput,
            MaximumCapturedCharacters,
            cancellationToken);
        Task<string> standardError = ReadBoundedAsync(
            process.StandardError,
            MaximumCapturedCharacters,
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }

        return new(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        string workingDirectory,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    internal static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToString();
            }

            int retained = Math.Min(count, maximumCharacters - output.Length);
            if (retained > 0)
            {
                output.Append(buffer, 0, retained);
            }
        }
    }
}
