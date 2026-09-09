using System.IO;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Testing;

namespace Runic.CommandLine.Tests;

internal static partial class ApplicationTests
{
    private static async ValueTask HiddenItems()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await App(console).RunAsync(["--help"]));
        AssertEx.True(!console.StandardOutput.Contains("maintenance", StringComparison.Ordinal));
        console = new();
        AssertEx.Equal(0, await App(console).RunAsync(["maintenance", "--secret-switch"]));
        AssertEx.Equal("True\n", console.StandardOutput);
        console = new();
        AssertEx.Equal(0, await App(console).RunAsync(["maintenance", "--help"]));
        AssertEx.True(!console.StandardOutput.Contains("secret-switch", StringComparison.Ordinal));
        string script = CommandCompletion.Generate(GeneratedCommandCatalog.Create(), "sample", "bash");
        AssertEx.True(!script.Contains("maintenance", StringComparison.Ordinal));
        AssertEx.True(!script.Contains("secret-switch", StringComparison.Ordinal));
    }
    private static async ValueTask Suggestions()
    {
        var console = new TestCommandConsole();
        AssertEx.Equal(2, await App(console).RunAsync(["config", "shwo"]));
        AssertEx.True(console.StandardError.Contains("Did you mean 'show'", StringComparison.Ordinal));
        console = new();
        AssertEx.Equal(2, await App(console).RunAsync(["values", "--coutn=private-secret"]));
        AssertEx.True(console.StandardError.Contains("Did you mean '--count'", StringComparison.Ordinal));
        AssertEx.True(!console.StandardError.Contains("private-secret", StringComparison.Ordinal));
        console = new();
        AssertEx.Equal(2, await App(console).RunAsync(["maintenance", "--secret-swtich"]));
        AssertEx.True(!console.StandardError.Contains("Did you mean", StringComparison.Ordinal));
    }
    private static async ValueTask InputConstraints()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-path-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "source with spaces.txt");
        await File.WriteAllTextAsync(source, "data");
        try
        {
            var console = new TestCommandConsole();
            AssertEx.Equal(0, await App(console).RunAsync(["inspect-path", "--source", source, "--target", directory, "--count", "3"]));
            AssertEx.True(console.StandardOutput.Contains("source with spaces.txt:3", StringComparison.Ordinal));
            foreach (string[] args in new string[][] {
                ["inspect-path", "--source", source, "--target", directory, "--count", "99"],
                ["inspect-path", "--source", directory, "--target", directory],
                ["inspect-path", "--source", source, "--target", source],
                ["relations", "--apply"], ["relations", "--apply", "--confirm", "--dry-run"] })
            {
                console = new();
                AssertEx.Equal(2, await App(console).RunAsync(args));
                AssertEx.True(console.StandardError.Contains("requires", StringComparison.Ordinal) || console.StandardError.Contains("cannot", StringComparison.Ordinal));
                AssertEx.True(!console.StandardError.Contains(directory, StringComparison.Ordinal));
            }
            console = new();
            AssertEx.Equal(0, await App(console).RunAsync(["relations", "--apply", "--confirm"]));
            var catalog = GeneratedCommandCatalog.Create();
            catalog.TryGetCommand("inspect-path", out var command);
            AssertEx.Equal(CommandPathKind.File, command!.Options[0].Help.PathKind);
            foreach (string shell in new[] { "bash", "zsh", "fish", "powershell" })
            {
                string script = CommandCompletion.Generate(catalog, "sample", shell);
                AssertEx.True(script.Contains("--source", StringComparison.Ordinal));
                AssertEx.True(script.Contains("--target", StringComparison.Ordinal));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static async ValueTask HumanPresenter()
    {
        int calls = 0;
        var catalog = GeneratedCommandCatalog.Create(builder => builder.Present<SampleReport>("report", (value, console, _, token) =>
        {
            calls++;
            return console.WriteOutAsync(("Custom " + value.Status + "\n").AsMemory(), token);
        }));
        var console = new TestCommandConsole();
        AssertEx.Equal(0, await new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false }.RunAsync(["report"]));
        AssertEx.True(console.StandardOutput.StartsWith("Custom ", StringComparison.Ordinal));
        console = new();
        AssertEx.Equal(0, await new CommandApp(catalog) { Console = console, HandleCancelKeyPress = false }.RunAsync(["report", "--output=json"]));
        using var frame = CommandTestEnvelope.Parse(console.StandardOutput);
        AssertEx.Equal("sample.report/1", frame.RootElement.GetProperty("payloadType").GetString());
        AssertEx.Equal(1, calls);
    }
}

internal static class RoadmapCommands
{
    [Command("maintenance", Hidden = true)]
    internal static string Maintenance([Option("--secret-switch", Hidden = true)] bool secret = false) => secret.ToString();

    [Command("inspect-path")]
    internal static string Inspect(
        [Option("--source", MustExist = true)] FileInfo source,
        [Option("--target", MustExist = true)] DirectoryInfo target,
        [Option("--count", Minimum = 1, Maximum = 5)] int count = 1) => source.Name + ":" + count;

    [Command("relations")]
    internal static string Relations(
        [Option("--apply", Requires = ["confirm"], ConflictsWith = ["dry-run"])] bool apply = false,
        [Option("--confirm")] bool confirm = false,
        [Option("--dry-run")] bool dryRun = false) => "valid";
}
