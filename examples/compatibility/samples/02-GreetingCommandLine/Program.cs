using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Examples.GreetingCommandLine;

internal static class Program
{
    private const string Usage = """
        Usage: GreetingCommandLine greet <name> [--times <1-5>] [--output human|json]
               GreetingCommandLine --help

        Examples:
          dotnet run --project samples/02-GreetingCommandLine -- greet Ada
          dotnet run --project samples/02-GreetingCommandLine -- greet Ada --times 2 --output json
        """;

    public static async Task<int> Main(string[] args)
    {
        // Make a plain `dotnet run` useful while still accepting normal CLI arguments.
        string[] input = args.Length == 0 ? ["greet", "World"] : args;
        CommandCatalog catalog = GreetingCommand.CreateCatalog();
        ParseOutcome parse = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            input,
            new ParseSettings(Environment.GetEnvironmentVariable(
                CommandOutputClassifier.EnvironmentVariableName)));

        if (parse.Kind == ParseOutcomeKind.Help)
        {
            await Console.Out.WriteLineAsync(Usage).ConfigureAwait(false);
            return CommandExitCodes.Success;
        }

        if (parse.Kind != ParseOutcomeKind.Invocation || parse.Invocation is null)
        {
            foreach (CommandDiagnostic diagnostic in parse.Diagnostics)
            {
                await Console.Error.WriteLineAsync(
                    string.Create(CultureInfo.InvariantCulture, $"{diagnostic.Code}: {diagnostic.Message}"))
                    .ConfigureAwait(false);
            }

            await Console.Error.WriteLineAsync(Usage).ConfigureAwait(false);
            return CommandExitCodes.Usage;
        }

        var request = new CommandExecutionRequest(
            parse.Invocation,
            new SystemCommandConsole(),
            CultureInfo.CurrentCulture,
            string.Create(CultureInfo.InvariantCulture, $"greeting-{Environment.ProcessId}"));
        CommandExecutionResult result = await new CommandExecutor(EmptyScopeFactory.Instance)
            .ExecuteAsync(request, new CommandOutputDispatcher())
            .ConfigureAwait(false);
        return result.ExitCode;
    }
}
