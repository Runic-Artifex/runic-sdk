using System;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Examples.GreetingCommandLine;

internal static class GreetingCommand
{
    internal static CommandCatalog CreateCatalog() => new CommandCatalogBuilder()
        .Command<GreetingOptions, GreetingHandler, GreetingResult>(
            "greet",
            command => command
                .Describe("Greet a person")
                .Argument("name", "name", CommandArity.ExactlyOne)
                .Option("times", "--times", CommandArity.ExactlyOne, aliases: "-n")
                .BindWith(GreetingOptionsBinder.Instance)
                .CreateHandlerWith(GreetingHandlerFactory.Instance)
                .Produces(GreetingResultCodec.Instance))
        .Build();
}

internal sealed record GreetingOptions(string Name, int Times);

internal sealed record GreetingResult(string Name, int Times);

internal sealed class GreetingOptionsBinder : ICommandOptionsBinder<GreetingOptions>
{
    internal static GreetingOptionsBinder Instance { get; } = new();

    private GreetingOptionsBinder()
    {
    }

    public ValueTask<CommandOutcome<GreetingOptions>> BindAsync(
        ParsedInvocation invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = invocation.Arguments[0].Values[0];
        int times = 1;

        foreach (CommandValueBinding option in invocation.Options)
        {
            if (option.Id == "times" &&
                (!int.TryParse(option.Values[0], NumberStyles.None, CultureInfo.InvariantCulture, out times) ||
                 times is < 1 or > 5))
            {
                return ValueTask.FromResult(CommandOutcome.Failure<GreetingOptions>(
                    CommandExitCategory.Validation,
                    new CommandFault("SAMPLE_TIMES_INVALID", "--times must be a number from 1 to 5.")));
            }
        }

        return ValueTask.FromResult(CommandOutcome.Success(new GreetingOptions(name, times)));
    }
}

internal sealed class GreetingHandlerFactory : ICommandHandlerFactory<GreetingHandler>
{
    internal static GreetingHandlerFactory Instance { get; } = new();

    private GreetingHandlerFactory()
    {
    }

    public GreetingHandler Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return new GreetingHandler();
    }
}

internal sealed class GreetingHandler : ICommandHandler<GreetingOptions, GreetingResult>
{
    public ValueTask<CommandOutcome<GreetingResult>> ExecuteAsync(
        GreetingOptions options,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommandOutcome.Success(
            new GreetingResult(options.Name, options.Times)));
    }
}

internal sealed class GreetingResultCodec : ICommandResultCodec<GreetingResult>
{
    internal static GreetingResultCodec Instance { get; } = new();

    private GreetingResultCodec()
    {
    }

    public string PayloadType => "sample.greeting/1";

    public JsonTypeInfo<GreetingResult> TypeInfo => GreetingJsonContext.Default.GreetingResult;

    public async ValueTask WriteHumanAsync(
        GreetingResult value,
        ICommandConsole console,
        CultureInfo culture,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < value.Times; index++)
        {
            await console.WriteOutAsync(
                string.Create(culture, $"Hello, {value.Name}!\n").AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GreetingResult))]
internal sealed partial class GreetingJsonContext : JsonSerializerContext;
