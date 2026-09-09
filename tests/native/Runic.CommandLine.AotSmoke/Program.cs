using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Hosting;
using Runic.CommandLine.Spectre;
using global::Spectre.Console;

namespace Runic.CommandLine.AotSmoke;

internal static class Program
{
    private static async Task<int> Main()
    {
        CommandCatalog catalog = GeneratedCommandCatalog.Create();
        ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            ["--amount", "2.5", "--tag", "one", "two", "--tag", "three", "--runic-output=json", "world", "--", "--app-flag", "-3"],
            new ParseSettings(transportOutputOptionName: "--runic-output"));
        if (parse.Invocation is null) return 10;

        var console = new BufferCommandConsole();
        CommandExecutionResult execution = await new CommandExecutor(EmptyScopeFactory.Instance).ExecuteAsync(
            new CommandExecutionRequest(parse.Invocation, console, CultureInfo.InvariantCulture, "aot-smoke-1"),
            new CommandOutputDispatcher()).ConfigureAwait(false);
        if (!execution.IsSuccess || execution.ExitCode != CommandExitCodes.Success || !ValidateEnvelope(console.StandardOutput)) return 20;

        var human = new BufferCommandConsole();
        var presenter = new Runic.CommandLine.Spectre.SpectreCommandConsole(human, width: 40, color: false);
        await presenter.WriteAsync(new global::Spectre.Console.Table().AddColumn("Status").AddRow("Ready")).ConfigureAwait(false);
        if (!human.StandardOutput.Contains("Ready", StringComparison.Ordinal)) return 30;
        await presenter.WithProgressAsync("Checking", static (_, _) => Task.CompletedTask).ConfigureAwait(false);
        var runnerOutput = new BufferCommandConsole();
        if (await new CommandApp(catalog) { Console = runnerOutput, HandleCancelKeyPress = false, ParseSettings = ParseSettings.Default }.RunAsync(["ping", "--output=json"]).ConfigureAwait(false) != 0) return 40;
        using (JsonDocument ping = JsonDocument.Parse(runnerOutput.StandardOutput))
            if (ping.RootElement.GetProperty("payload").GetString() != "pong") return 50;
        var hosted = new CommandLineHostingAdapter(catalog, new CommandExecutor(EmptyScopeFactory.Instance))
        {
            Presentation = new() { Name = "smoke", HelpPresenter = new SpectreHelpPresenter() },
        };
        var hostedHelp = new BufferCommandConsole();
        if (await hosted.PresentAsync(hosted.Classify(new(["help", "ping"])), new SpectreCommandConsole(hostedHelp),
            CultureInfo.InvariantCulture, "hosted-help").ConfigureAwait(false) != 0 || !hostedHelp.StandardOutput.Contains("ping", StringComparison.Ordinal)) return 60;
        var hostedOutput = new BufferCommandConsole();
        if (!(await hosted.ExecuteAsync(new(hosted.Classify(new(["ping", "--output=json"])), hostedOutput,
            CultureInfo.InvariantCulture, "hosted-ping", new CommandOutputDispatcher())).ConfigureAwait(false)).IsSuccess) return 70;
        using (JsonDocument ping = JsonDocument.Parse(hostedOutput.StandardOutput))
            if (ping.RootElement.GetProperty("payload").GetString() != "pong") return 80;
        Console.Out.Write(console.StandardOutput);
        return 0;
    }

    [Command("ping")]
    internal static string Ping() => "pong";

    [Command("smoke")]
    [DefaultCommand]
    [CommandResult("runic.commandline.smoke/1", typeof(SmokeJsonContext))]
    internal static SmokeResult Smoke(
        [Option("--amount", Required = true)] decimal amount,
        [Option("--tag", AllowMultipleValues = true)] IReadOnlyList<string> tags,
        [Argument] string name,
        [Argument("app-args", AllowMultipleValues = true)] IReadOnlyList<string> appArgs,
        CancellationToken cancellationToken,
        [Option("--count", "-c")] int count = 2,
        [Option("--note")] string? note = null,
        [Option("--ratio")] double ratio = double.NaN,
        [Option("--label")] string label = null!)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = note;
        _ = ratio;
        _ = label;
        return new SmokeResult($"{name}:{count}:{amount.ToString(CultureInfo.InvariantCulture)}:{tags.Count}:{string.Join('|', appArgs)}");
    }

    private static bool ValidateEnvelope(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("protocol").GetString() == CliProtocol.Identity &&
            document.RootElement.GetProperty("payloadType").GetString() == "runic.commandline.smoke/1" &&
            document.RootElement.GetProperty("payload").GetProperty("Message").GetString() == "world:2:2.5:3:--app-flag|-3";
    }
}

internal sealed record SmokeResult(string Message);

[JsonSerializable(typeof(SmokeResult))]
internal sealed partial class SmokeJsonContext : JsonSerializerContext;

internal sealed class BufferCommandConsole : ICommandConsole
{
    private readonly StringBuilder _standardOutput = new();
    public string StandardOutput => _standardOutput.ToString();
    public bool IsInteractive => false;
    public bool IsInputRedirected => true;
    public bool IsOutputRedirected => true;
    public bool IsErrorRedirected => true;
    public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);
    public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) { _standardOutput.Append(value.Span); return ValueTask.CompletedTask; }
    public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken) { _standardOutput.Append(Encoding.UTF8.GetString(value.Span)); return ValueTask.CompletedTask; }
    public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal sealed class EmptyScopeFactory : ICommandExecutionScopeFactory
{
    public static EmptyScopeFactory Instance { get; } = new();
    public ICommandExecutionScope CreateScope() => EmptyScope.Instance;
    private sealed class EmptyScope : ICommandExecutionScope
    {
        public static EmptyScope Instance { get; } = new();
        public IServiceProvider Services { get; } = new EmptyServices();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
