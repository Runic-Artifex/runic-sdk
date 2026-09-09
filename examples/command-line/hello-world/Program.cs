using Runic.CommandLine;
using Runic.CommandLine.Generated;

return await new CommandApp(GeneratedCommandCatalog.Create()) { Name = "hello" }.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello."), DefaultCommand]
    internal static string Greet([Argument] string name = "world",
        [Option("--count", "-n", Minimum = 1, Maximum = 100)] int count = 1) =>
        string.Join('\n', Enumerable.Repeat($"Hello, {name}!", count));
}
