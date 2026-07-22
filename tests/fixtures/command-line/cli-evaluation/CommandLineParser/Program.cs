using System;
using System.Collections.Generic;
using CommandLine;

FixtureOutput.ApplyCulture();
return Parser.Default.ParseArguments<ExportOptions, CacheOptions, RunOptions, EchoOptions>(args)
    .MapResult(
        (ExportOptions o) => FixtureOutput.Invoke("export", o),
        (CacheOptions o) => o.Action == "clear" || o.Action == "purge" ? FixtureOutput.Invoke("cache/clear", o) : 2,
        (RunOptions o) => FixtureOutput.Invoke("run", o.Arguments),
        (EchoOptions o) => FixtureOutput.Invoke("echo", o.Text),
        _ => 2);

[Verb("export", HelpText = "Export fixture")]
internal sealed class ExportOptions
{
    [Value(0, Required = true)] public string Input { get; init; } = "";
    [Option('f', "format")] public string? Format { get; init; }
    [Option('t', "tag")] public IEnumerable<string> Tags { get; init; } = Array.Empty<string>();
    [Option('v', "verbose")] public bool Verbose { get; init; }
    [Option("ratio")] public decimal? Ratio { get; init; }
    [Option("timeout")] public TimeSpan? Timeout { get; init; }
}

[Verb("cache", HelpText = "Emulated one-level cache verb; proves the nested-command mismatch")]
internal sealed class CacheOptions
{
    [Value(0, Required = true)] public string Action { get; init; } = "";
    [Value(1)] public string? Target { get; init; }
    [Option('q', "quiet")] public bool Quiet { get; init; }
}

[Verb("run")]
internal sealed class RunOptions
{
    [Value(0)] public IEnumerable<string> Arguments { get; init; } = Array.Empty<string>();
}

[Verb("echo")]
internal sealed class EchoOptions
{
    [Value(0, Required = true)] public string Text { get; init; } = "";
}
