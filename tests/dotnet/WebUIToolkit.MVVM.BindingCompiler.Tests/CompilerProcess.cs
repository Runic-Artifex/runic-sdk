using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WebUIToolkit.MVVM.BindingCompiler.Tests;

internal sealed record CompilerResult(int ExitCode, string StandardOutput, string StandardError);

internal static class CompilerProcess
{
    private const int ProcessTimeoutMilliseconds = 30_000;

    public static CompilerResult Run(string workingDirectory, params string[] arguments) =>
        Run(workingDirectory, (IReadOnlyList<string>)arguments);

    public static CompilerResult Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        string compilerAssembly = Path.Combine(
            AppContext.BaseDirectory,
            "WebUIToolkit.MVVM.BindingCompiler.dll");
        Assert.True(File.Exists(compilerAssembly), $"Compiler assembly was not copied to '{compilerAssembly}'.");

        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveDotNetHost(),
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false, true),
        };
        startInfo.ArgumentList.Add(compilerAssembly);
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the binding compiler process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(ProcessTimeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException(
                $"Binding compiler exceeded the {ProcessTimeoutMilliseconds} ms test timeout.");
        }

        return new CompilerResult(process.ExitCode, standardOutput.GetAwaiter().GetResult(), standardError.GetAwaiter().GetResult());
    }

    private static string ResolveDotNetHost()
    {
        string? configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string? processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return processPath;
        }

        return "dotnet";
    }
}

internal sealed class TestWorkspace : IDisposable
{
    private readonly string _root;

    public TestWorkspace()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "WebUIToolkit.MVVM.BindingCompiler.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string WriteText(string relativePath, string content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    public string WriteBytes(string relativePath, byte[] content)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A failed cleanup must not mask the contract-test result.
        }
        catch (UnauthorizedAccessException)
        {
            // See the IOException case above.
        }
    }
}
