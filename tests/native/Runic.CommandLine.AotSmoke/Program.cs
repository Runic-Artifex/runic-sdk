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

        Console.Out.Write(console.StandardOutput);
        return 0;
    }

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
