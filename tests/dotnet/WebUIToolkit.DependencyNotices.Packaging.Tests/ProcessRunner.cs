using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WebUIToolkit.DependencyNotices.Packaging.Tests;

internal static class ProcessRunner
{
    public static string Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ProcessStartInfo startInfo = new(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        StringBuilder standardOutput = new();
        StringBuilder standardError = new();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) standardOutput.AppendLine(eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) standardError.AppendLine(eventArgs.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"Command failed ({process.ExitCode}): {fileName} {string.Join(' ', arguments)}{Environment.NewLine}"
                + standardOutput
                + standardError);
        }

        return standardOutput.ToString();
    }
}
