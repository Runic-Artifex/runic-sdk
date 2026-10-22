using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using TUnit.Core;

namespace Runic.Assets.Tests;

// W319-003 D11, D12 and D13 for the packer: --format human|json selects the output, with --output as a hidden
// alias until 2.0; the runic.assets.pack-result/1 payload is camelCase; the exit codes follow the Runic tool table
// (0 success, 1 the archive could not be packed, 2 usage or validation, 3 internal failure, 130 cancelled).
// The cases run the packer this checkout built, as a separate process.
internal sealed class PackerContractTests
{
    [Test, DisplayName("packer --format json writes the camelCase pack-result payload")]
    public async Task FormatJsonWritesThePayload()
    {
        using var directory = new TemporaryDirectory();
        string site = Path.GetDirectoryName(directory.Write("site/index.html", "<p>x</p>"))!;
        string archive = Path.Combine(directory.Path, "out.runic-assets");

        (int exitCode, string output, string error) = await RunPackerAsync(site, archive, "--format", "json").ConfigureAwait(false);
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Equal("pack", root.GetProperty("command").GetString());
        Equal("runic.assets.pack-result/1", root.GetProperty("payloadType").GetString());
        Equal($"{{\"archiveLength\":{new FileInfo(archive).Length}}}", root.GetProperty("payload").GetRawText());
    }

    [Test, DisplayName("packer --output json remains a hidden alias of --format json")]
    public async Task OutputIsAHiddenAlias()
    {
        using var directory = new TemporaryDirectory();
        string site = Path.GetDirectoryName(directory.Write("site/index.html", "<p>x</p>"))!;
        string archive = Path.Combine(directory.Path, "out.runic-assets");

        (int exitCode, string output, string error) = await RunPackerAsync(site, archive, "--output", "json").ConfigureAwait(false);
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        using (JsonDocument document = JsonDocument.Parse(output))
            Equal("runic.assets.pack-result/1", document.RootElement.GetProperty("payloadType").GetString());

        (exitCode, output, error) = await RunPackerAsync(site, archive, "--output=json").ConfigureAwait(false);
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        using (JsonDocument document = JsonDocument.Parse(output))
            Equal("runic.assets.pack-result/1", document.RootElement.GetProperty("payloadType").GetString());

        (exitCode, string help, _) = await RunPackerAsync("--help").ConfigureAwait(false);
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        True(help.Contains("--format", StringComparison.Ordinal), help);
        True(!help.Contains("--output", StringComparison.Ordinal), help);
    }

    [Test, DisplayName("packer exit codes follow the Runic tool table")]
    public async Task ExitCodesFollowTheToolTable()
    {
        using var directory = new TemporaryDirectory();
        string site = Path.GetDirectoryName(directory.Write("site/index.html", "<p>x</p>"))!;
        string archive = Path.Combine(directory.Path, "out.runic-assets");

        // Usage: the destination argument is missing.
        Equal("2", await ExitCodeAsync(site).ConfigureAwait(false));
        // Validation: the source directory does not exist (RAS1001).
        Equal("2", await ExitCodeAsync(Path.Combine(directory.Path, "missing"), archive, "--format", "json").ConfigureAwait(false));
        // Unavailable: the entry point does not exist (RAS1002).
        Equal("1", await ExitCodeAsync(site, archive, "--entry-point", "missing.html", "--trusted-generated-output").ConfigureAwait(false));

        // Creating a symbolic link on Windows needs Developer Mode or elevation.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Command failure: the directory holds a symbolic link (RAS1003).
        File.CreateSymbolicLink(Path.Combine(site, "linked.txt"), Path.Combine(site, "index.html"));
        Equal("1", await ExitCodeAsync(site, archive, "--trusted-generated-output").ConfigureAwait(false));
    }

    // The packer once built ParseSettings without an environment reader, so RUNIC_COMMANDLINE_NO_INPUT and option
    // environment fallbacks never reached it. Without settings of its own, CommandApp reads RUNIC_COMMANDLINE_OUTPUT,
    // RUNIC_COMMANDLINE_NO_INPUT and the fallbacks from the process environment for each run; settings of its own
    // must read the same variables. (CommandApp passes prompt state only to the handler's context, and the packer has
    // no prompts, so the reader is what the test can observe.)
    [Test, DisplayName("RUNIC_COMMANDLINE_NO_INPUT reaches the packer")]
    public void PackerReadsNoInputFromTheEnvironment()
    {
        const string noInput = "RUNIC_COMMANDLINE_NO_INPUT";
        string? previous = Environment.GetEnvironmentVariable(noInput);
        try
        {
            Environment.SetEnvironmentVariable(noInput, "1");
            Runic.CommandLine.ParseSettings? settings = PackerApplication.CreateApp(new Runic.CommandLine.SystemCommandConsole()).ParseSettings;
            True(settings is null || settings.GetEnvironmentVariable?.Invoke(noInput) == "1",
                "The packer's parse settings do not read RUNIC_COMMANDLINE_NO_INPUT from the process environment.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(noInput, previous);
        }
    }

    private static async Task<string> ExitCodeAsync(params string[] arguments)
    {
        (int exitCode, _, _) = await RunPackerAsync(arguments).ConfigureAwait(false);
        return exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunPackerAsync(params string[] arguments)
    {
        // The test runs from <repository>/tests/dotnet/Runic.Assets.Tests/bin/<Configuration>/net10.0/.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        string configuration = output.Parent!.Name;
        string repository = Path.GetFullPath(Path.Combine(output.FullName, "../../../../../.."));
        string packer = Path.Combine(repository, "tools/Runic.Assets.Packer/bin", configuration, "net10.0/Runic.Assets.Packer.dll");
        if (!File.Exists(packer))
        {
            throw new InvalidOperationException(
                $"The packer was not found at '{packer}'. Build tools/Runic.Assets.Packer first ({configuration}).");
        }

        var start = new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(packer);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return (process.ExitCode, await standardOutput.ConfigureAwait(false), await standardError.ConfigureAwait(false));
    }

    private static void Equal(string? expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' but found '{actual}'.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() =>
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "runic-packer-" + Guid.NewGuid().ToString("N"));

        public string Path { get; }

        public string Write(string relativePath, string content)
        {
            string fullPath = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
            return fullPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
