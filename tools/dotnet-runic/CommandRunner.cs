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
                "RAPPDEV1004",
                $"Could not start '{DescribeProgram(executable, arguments)}'. Ensure it is installed and available on PATH.",
                $"Executable: {executable}\nWorking directory: {workingDirectory}\n");
        }

        // Captured commands are non-interactive: a prompt (npx install, git credentials) must see
        // end-of-input instead of waiting on a pipe nobody writes.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
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

    /// <summary>Names a program by executable and verb, for example <c>dotnet build</c>.</summary>
    internal static string DescribeProgram(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string program = Path.GetFileNameWithoutExtension(executable);
        if (program.Length == 0) program = executable;
        return arguments.Count > 0 && arguments[0].Length > 0 && arguments[0][0] != '-'
            ? $"{program} {arguments[0]}"
            : program;
    }

    /// <summary>Describes a failed child process without paths, for faults.</summary>
    internal static string DescribeFailure(
        string executable,
        IReadOnlyList<string> arguments,
        int exitCode) =>
        $"'{DescribeProgram(executable, arguments)}' exited with code {exitCode}.";

    /// <summary>Local-only detail: the working directory, then an optional hint.</summary>
    internal static string LocalDetail(string workingDirectory, string? hint = null) =>
        $"Working directory: {workingDirectory}\n" + (hint is null ? string.Empty : hint + "\n");

    /// <summary>Points to doctor for the selected project.</summary>
    internal static string DoctorHint(string projectPath) =>
        $"Run 'dotnet runic doctor --project \"{projectPath}\"' to check prerequisites.";

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
            // Tool output is diagnostic text; invalid bytes become U+FFFD instead of faulting the drain.
            StandardOutputEncoding = new UTF8Encoding(false, false),
            StandardErrorEncoding = new UTF8Encoding(false, false),
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
        bool truncated = false;
        while (true)
        {
            int count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return output.ToString() + (truncated ? "\n[output truncated after 4 Mi characters]\n" : "");
            }

            int retained = Math.Min(count, maximumCharacters - output.Length);
            truncated |= retained != count;
            if (retained > 0)
            {
                output.Append(buffer, 0, retained);
            }
        }
    }
}
