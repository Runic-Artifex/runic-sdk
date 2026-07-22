using System;
using System.Threading;
using Spectre.Console.Cli;

FixtureOutput.ApplyCulture();
CommandApp app = new();
app.Configure(config =>
{
    config.SetApplicationName("wut-cli-evaluation");
    config.SetApplicationVersion("1.0.0-evaluation");
    config.AddCommand<ExportCommand>("export").WithAlias("x");
    config.AddCommand<RunCommand>("run");
    config.AddCommand<EchoCommand>("echo");
    config.AddBranch("cache", branch => branch.AddCommand<ClearCommand>("clear").WithAlias("purge"));
});
return await app.RunAsync(args);

internal sealed class ExportSettings : CommandSettings
{
    [CommandArgument(0, "<input>")] public string Input { get; init; } = "";
    [CommandOption("-f|--format <FORMAT>")] public string? Format { get; init; }
    [CommandOption("-t|--tag <TAG>")] public string[] Tags { get; init; } = Array.Empty<string>();
    [CommandOption("-v|--verbose")] public bool Verbose { get; init; }
    [CommandOption("--ratio <RATIO>")] public decimal? Ratio { get; init; }
    [CommandOption("--timeout <TIMEOUT>")] public TimeSpan? Timeout { get; init; }
}

internal sealed class ExportCommand : Command<ExportSettings>
{
    protected override int Execute(CommandContext context, ExportSettings settings, CancellationToken cancellationToken) => FixtureOutput.Invoke("export", settings);
}

internal sealed class ClearSettings : CommandSettings
{
    [CommandArgument(0, "[target]")] public string? Target { get; init; }
    [CommandOption("-q|--quiet")] public bool Quiet { get; init; }
}

internal sealed class ClearCommand : Command<ClearSettings>
{
    protected override int Execute(CommandContext context, ClearSettings settings, CancellationToken cancellationToken) => FixtureOutput.Invoke("cache/clear", settings);
}

internal sealed class RunSettings : CommandSettings
{
    [CommandArgument(0, "[arguments]")] public string[] Arguments { get; init; } = Array.Empty<string>();
}

internal sealed class RunCommand : Command<RunSettings>
{
    protected override int Execute(CommandContext context, RunSettings settings, CancellationToken cancellationToken) => FixtureOutput.Invoke("run", settings.Arguments);
}

internal sealed class EchoSettings : CommandSettings
{
    [CommandArgument(0, "<text>")] public string Text { get; init; } = "";
}

internal sealed class EchoCommand : Command<EchoSettings>
{
    protected override int Execute(CommandContext context, EchoSettings settings, CancellationToken cancellationToken) => FixtureOutput.Invoke("echo", settings.Text);
}
