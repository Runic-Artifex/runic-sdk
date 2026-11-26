using System.Diagnostics;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Runs the Views TypeScript emitter (R7 G6) on the contract IR that the generator wrote into a C# output directory, as
/// the build's <c>RunicViewsGenerateClients</c> does for a project without a frontend: Bun runs
/// <c>runic-views-codegen.mjs</c> with the bundled emitter, which the generator's build bundles beside it
/// (<c>tools/Runic.Application.Views.Codegen/bin/&lt;configuration&gt;/net10.0/codegen/index.js</c>). The generator tests that run <c>BridgeCodegen --generate</c> themselves
/// run it afterwards, so they see the clients and the emitter's diagnostics as a build does.
/// </summary>
internal static class TypeScriptEmitterProcess
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Writes the clients of <paramref name="csharpDirectory"/>'s IR into <paramref name="typescriptDirectory"/>.
    /// Without an IR (the generator failed or found no Views) it does nothing and returns exit code 0.
    /// </summary>
    internal static async Task<(int ExitCode, string Output)> RunAsync(string csharpDirectory, string typescriptDirectory)
    {
        var ir = Path.Combine(csharpDirectory, "contract.ir.json");
        if (!File.Exists(ir)) return (0, "");
        var root = WorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(TypeScriptEmitterProcess).Assembly)?.Configuration ?? "Release";
        var emitter = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0", "codegen", "index.js");
        if (!File.Exists(emitter))
            throw new InvalidOperationException($"{emitter} does not exist: build tools/Runic.Application.Views.Codegen, which bundles the emitter, first.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("bun")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                ArgumentList =
                {
                    Path.Combine(root, "packages", "dotnet", "Runic.Application.Views", "build", "runic-views-codegen.mjs"),
                    "--ir", ir, "--out", typescriptDirectory,
                    "--outputs", Path.Combine(csharpDirectory, "typescript.outputs"),
                    "--bundled", emitter, "--help-link-base", HelpLinkBase(),
                },
            },
        };
        var symbols = Path.Combine(csharpDirectory, "client-symbols.json");
        if (File.Exists(symbols)) { process.StartInfo.ArgumentList.Add("--symbols"); process.StartInfo.ArgumentList.Add(symbols); }
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = TestTimeouts.Cancellation(Timeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"The TypeScript emitter did not exit within {Timeout.TotalSeconds} seconds for {ir}.");
        }
        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }

    /// <summary>The generator's run followed by the emitter's, as a build runs them: the emitter runs after a success.</summary>
    internal static async Task<(int ExitCode, string Output)> AfterGeneratorAsync((int ExitCode, string Output) generator,
        string csharpDirectory, string typescriptDirectory)
    {
        if (generator.ExitCode != 0) return generator;
        var (exitCode, output) = await RunAsync(csharpDirectory, typescriptDirectory).ConfigureAwait(false);
        return (exitCode, generator.Output + output);
    }

    // The generator and this test assembly build with the same version, so they link the same catalog.
    private static string HelpLinkBase()
    {
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(TypeScriptEmitterProcess).Assembly)!.InformationalVersion.Split('+')[0];
        return $"https://github.com/Runic-Artifex/runic-sdk/blob/v{version}/docs/diagnostics.md#";
    }

    private static string WorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }
}
