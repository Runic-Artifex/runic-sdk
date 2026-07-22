using System;
using Cocona;

FixtureOutput.ApplyCulture();
CoconaApp app = CoconaApp.Create();
app.AddCommand("export", ([Argument] string input, [Option('f')] string? format, [Option('t')] string[]? tag, [Option('v')] bool verbose, [Option] decimal? ratio, [Option] TimeSpan? timeout) =>
    FixtureOutput.Invoke("export", new { input, format, tag, verbose, ratio, timeout }));
app.AddCommand("run", ([Argument] string[] arguments) => FixtureOutput.Invoke("run", arguments));
app.AddCommand("echo", ([Argument] string text) => FixtureOutput.Invoke("echo", text));
app.AddSubCommand("cache", cache =>
    cache.AddCommand("clear", ([Argument] string? target, [Option('q')] bool quiet) => FixtureOutput.Invoke("cache/clear", new { target, quiet })));
await app.RunAsync();
return 0;
