using System;
using System.CommandLine;

FixtureOutput.ApplyCulture();
RootCommand root = new("Runic.CommandLine parser evaluation fixture");

Command status = new("status");
Option<bool> statusVerbose = new("--verbose", "-v");
status.Options.Add(statusVerbose);
status.SetAction(result => FixtureOutput.Invoke("status", new { verbose = result.GetValue(statusVerbose) }));
root.Subcommands.Add(status);

Command cache = new("cache");
Command clear = new("clear");
clear.Aliases.Add("purge");
Option<bool> quiet = new("--quiet", "-q");
quiet.Aliases.Add("/quiet");
Argument<string?> target = new("target") { Arity = ArgumentArity.ZeroOrOne };
clear.Options.Add(quiet);
clear.Arguments.Add(target);
clear.SetAction(result => FixtureOutput.Invoke("cache/clear", new { quiet = result.GetValue(quiet), target = result.GetValue(target) }));
cache.Subcommands.Add(clear);
root.Subcommands.Add(cache);

Command export = new("export");
export.Aliases.Add("x");
Argument<string> input = new("input");
Option<ExportFormat?> format = new("--format", "-f");
Option<string[]> tags = new("--tag", "-t") { AllowMultipleArgumentsPerToken = false };
Option<bool> verbose = new("--verbose", "-v");
Option<decimal?> ratio = new("--ratio");
Option<TimeSpan?> timeout = new("--timeout");
export.Arguments.Add(input);
export.Options.Add(format);
export.Options.Add(tags);
export.Options.Add(verbose);
export.Options.Add(ratio);
export.Options.Add(timeout);
export.SetAction(result => FixtureOutput.Invoke("export", new
{
    input = result.GetValue(input),
    format = result.GetValue(format),
    tags = result.GetValue(tags),
    verbose = result.GetValue(verbose),
    ratio = result.GetValue(ratio),
    timeout = result.GetValue(timeout)
}));
root.Subcommands.Add(export);

Command run = new("run");
Argument<string[]> runArguments = new("arguments") { Arity = ArgumentArity.ZeroOrMore };
run.Arguments.Add(runArguments);
run.SetAction(result => FixtureOutput.Invoke("run", result.GetValue(runArguments)));
root.Subcommands.Add(run);

Command echo = new("echo");
Argument<string> text = new("text");
echo.Arguments.Add(text);
echo.SetAction(result => FixtureOutput.Invoke("echo", result.GetValue(text)));
root.Subcommands.Add(echo);

return root.Parse(args).Invoke();

internal enum ExportFormat
{
    Text,
    Json
}
