using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Spectre;
using Runic.CommandLine.Examples;

if (args is ["--hosted", .. var hostedArgs])
    return await HostedExample.RunAsync(hostedArgs, CancellationToken.None);

return await ExampleApplication.Create(new SpectreCommandConsole()).RunAsync(args);

internal static class Commands
{
    [Command("greet", Description = "Say hello.", Examples = ["hello greet Ada --count 2"])]
    [DefaultCommand]
    internal static string Greet(
        [Argument(Description = "Person to greet.")] string name = "world",
        [Option("--count", "-n", Description = "Number of greetings.", Minimum = 1, Maximum = 100)] int count = 1) =>
        string.Join('\n', Enumerable.Repeat($"Hello, {name}!", count));

    [Command("config show", Description = "Show the chosen environment.")]
    internal static string Config([Option("--environment", EnvironmentVariable = "HELLO_ENV", Choices = ["local", "production"])] string environment = "local") => environment;

    [Command("work", Description = "Demonstrate progress and cancellation.")]
    internal static Task Work(ICommandConsole console, [FromServices] HostedExample.ApplicationServices services,
        CancellationToken cancellationToken,
        [Option("--verbose", "-v")] bool verbose = false) =>
        new SpectreCommandConsole(console).WithProgressAsync(verbose ? services.Greeting + ": preparing greeting" : "Preparing greeting", async (progress, token) =>
        {
            for (int i = 1; i <= 4; i++)
            {
                await Task.Delay(100, token);
                progress.Report(i * 25);
            }
        }, cancellationToken);
}
