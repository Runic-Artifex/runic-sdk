using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Spectre;

if (args is ["--hosted", .. var hostedArgs])
    return await HostedExample.RunAsync(hostedArgs, CancellationToken.None);

return await new CommandApp(GeneratedCommandCatalog.Create())
{
    ScopeFactory = HostedExample.CreateCommandScopes(),
    Name = "hello",
    Version = "1.0.0",
    HelpPresenter = new SpectreHelpPresenter(),
    Console = new SpectreCommandConsole(),
}.RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello.", Examples = ["hello greet Ada --count 2"])]
    [DefaultCommand]
    internal static string Greet(
        [Argument(Description = "Person to greet.")] string name = "world",
        [Option("--count", "-n", Description = "Number of greetings."), ValidateWith(typeof(CountValidator))] int count = 1) =>
        string.Join('\n', Enumerable.Repeat($"Hello, {name}!", count));

    [Command("config show", Description = "Show the chosen environment.")]
    internal static string Config([Option("--environment", EnvironmentVariable = "HELLO_ENV", Choices = ["local", "production"])] string environment = "local") => environment;

    [Command("work", Description = "Demonstrate progress and cancellation.")]
    internal static Task Work(ICommandConsole console, CancellationToken cancellationToken) =>
        new SpectreCommandConsole(console).WithProgressAsync("Preparing greeting", async (progress, token) =>
        {
            for (int i = 1; i <= 4; i++)
            {
                await Task.Delay(100, token);
                progress.Report(i * 25);
            }
        }, cancellationToken);
}

internal sealed class CountValidator : ICommandValueValidator<int>
{
    public static bool IsValid(int value) => value is > 0 and <= 100;
}
