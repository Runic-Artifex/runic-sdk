using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool.Packaging;

/// <summary>
/// Finds and runs the external tools a target needs. Their output goes to the command's output as it arrives (standard
/// error with <c>--format json</c>); a missing tool is <c>RAPPPKG1002</c> and a failing one <c>RAPPPKG1005</c>.
/// </summary>
internal static class PackageTools
{
    /// <summary>Finds an executable on PATH.</summary>
    internal static Func<string, string?> Find { get; set; } = SystemDoctorRuntime.Instance.FindExecutable;

    /// <summary>The tool on PATH, or <c>RAPPPKG1002</c> naming what provides it.</summary>
    internal static string Require(string name, string provider) =>
        Find(name) ?? throw Missing(name, provider);

    internal static DevDevelopmentException Missing(string name, string provider) =>
        new(PackageCodes.ToolMissing, $"{name} was not found on PATH. Install {provider}.");

    /// <summary>
    /// makeappx and signtool come with the Windows SDK, which puts them in a versioned folder rather than on PATH: the
    /// tool on PATH, otherwise the newest Windows Kits 10 folder for the host's architecture.
    /// </summary>
    internal static string RequireWindowsKitTool(string name)
    {
        if (Find(name) is { } onPath) return onPath;
        string programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        string root = Path.Combine(programFiles, "Windows Kits", "10", "bin");
        if (Directory.Exists(root))
        {
            string architecture = PackageOptions.CurrentArchitecture == "arm64" ? "arm64" : "x64";
            foreach (string version in Directory.GetDirectories(root)
                .Select(directory => Path.GetFileName(directory))
                .Where(folder => folder.StartsWith("10.", StringComparison.Ordinal) && System.Version.TryParse(folder, out _))
                .OrderByDescending(System.Version.Parse))
            {
                string tool = Path.Combine(root, version, architecture, name);
                if (File.Exists(tool)) return tool;
            }
        }
        throw Missing(name, "the Windows SDK (it provides makeappx and signtool)");
    }

    /// <summary>
    /// Runs a tool and fails with <paramref name="failureCode"/> when it exits with an error. The arguments at the
    /// <paramref name="secret"/> indexes are not printed. With <paramref name="capture"/> the standard output is returned
    /// instead of printed.
    /// </summary>
    internal static async Task<string> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        string failureCode = PackageCodes.ToolFailed,
        bool capture = false,
        IReadOnlySet<int>? secret = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Console.Out.WriteLine("> " + Describe(tool, arguments, secret));
        ProcessStartInfo startInfo = CommandRunner.CreateStartInfo(tool, workingDirectory, arguments);
        if (environment is not null)
        {
            foreach ((string name, string value) in environment) startInfo.Environment[name] = value;
        }
        using var process = new Process { StartInfo = startInfo };
        var captured = new StringBuilder();
        object gate = new();
        process.OutputDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (gate)
            {
                if (capture) captured.AppendLine(line.Data);
                else Console.Out.WriteLine(line.Data);
            }
        };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null) return;
            lock (gate) Console.Error.WriteLine(line.Data);
        };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new DevDevelopmentException(PackageCodes.ToolMissing,
                $"{Path.GetFileName(tool)} could not be started. Check that it is installed.", $"Executable: {tool}\n");
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CommandRunner.TryTerminate(process);
            throw;
        }
        // WaitForExitAsync returns once the redirected streams reach their end, so every line was handled.
        if (process.ExitCode != 0)
        {
            string program = CommandRunner.DescribeProgram(tool, arguments);
            throw new DevDevelopmentException(failureCode,
                $"'{program}' exited with code {process.ExitCode}.",
                CommandRunner.LocalDetail(workingDirectory) + (capture ? captured.ToString() : ""));
        }
        return captured.ToString();
    }

    /// <summary>The command line as the output prints it, with secret arguments replaced.</summary>
    internal static string Describe(string tool, IReadOnlyList<string> arguments, IReadOnlySet<int>? secret = null) =>
        string.Join(' ', arguments.Select((argument, index) => secret?.Contains(index) == true ? "***" : Quote(argument))
            .Prepend(Quote(tool)));

    private static string Quote(string argument) =>
        argument.Length != 0 && !argument.Any(character => char.IsWhiteSpace(character) || character is '"' or '\'')
            ? argument
            : "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Copies a directory tree, replacing files that exist.</summary>
    internal static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            CopyFile(file, target);
        }
    }

    /// <summary>Copies a file with its Unix mode, replacing one that exists.</summary>
    internal static void CopyFile(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    /// <summary>Deletes a file or directory tree when it exists.</summary>
    internal static void Delete(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else if (File.Exists(path)) File.Delete(path);
    }
}
