using Runic.CommandLine;
using Runic.CommandLine.Generated;

// --format selects the output, as in the Runic tools; --output stays a hidden alias that completions do not list.
return await new CommandApp(GeneratedCommandCatalog.Create(builder => builder.OutputSelector("--format", "--output")))
{
    Name = "hello",
    Version = "1.0.0",
}.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello.")]
    [DefaultCommand]
    internal static string Greet([Argument] string name = "world") => $"Hello, {name}!";
}
