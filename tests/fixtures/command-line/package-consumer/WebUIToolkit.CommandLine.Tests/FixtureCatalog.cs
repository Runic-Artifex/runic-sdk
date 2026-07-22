using System.Text.Json;

namespace WebUIToolkit.CommandLine.Tests;

internal static class FixtureCatalog
{
    private static readonly TestBinder Binder = new();
    private static readonly TestHandlerFactory HandlerFactory = new();
    private static readonly TestCodec Codec = new();

    public static GrammarCorpus ReadCorpus()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Corpus", "grammar-corpus.json");
        return JsonSerializer.Deserialize(File.ReadAllBytes(path), CorpusJsonContext.Default.GrammarCorpus)
            ?? throw new InvalidOperationException("The frozen grammar corpus could not be read.");
    }

    public static CommandCatalog Create() => Create(Binder, HandlerFactory, Codec);

    public static CommandCatalog Create(
        ICommandOptionsBinder<TestOptions> binder,
        ICommandHandlerFactory<TestHandler> handlerFactory,
        ICommandResultCodec<TestResult> codec)
    {
        GrammarCorpus corpus = ReadCorpus();
        var builder = new CommandCatalogBuilder();
        foreach (CorpusCommand root in corpus.Catalog.Commands.Where(static command => command.Path.Length == 1))
        {
            builder.Command<TestOptions, TestHandler, TestResult>(root.Path[0], command =>
            {
                Configure(command, root, binder, handlerFactory, codec);
                foreach (CorpusCommand child in corpus.Catalog.Commands.Where(
                    candidate => candidate.Path.Length == 2 && candidate.Path[0] == root.Path[0]))
                {
                    command.Subcommand<TestOptions, TestHandler, TestResult>(child.Path[1], childBuilder =>
                        Configure(childBuilder, child, binder, handlerFactory, codec));
                }
            });
        }

        return builder.Build();
    }

    public static ParsedInvocation ParseInvocation(
        CommandCatalog catalog,
        params string[] args)
    {
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        return outcome.Invocation!;
    }

    private static void Configure(
        CommandBuilder<TestOptions, TestHandler, TestResult> builder,
        CorpusCommand definition,
        ICommandOptionsBinder<TestOptions> binder,
        ICommandHandlerFactory<TestHandler> handlerFactory,
        ICommandResultCodec<TestResult> codec)
    {
        builder.Alias(definition.Aliases)
            .BindWith(binder)
            .CreateHandlerWith(handlerFactory)
            .Produces(codec);

        foreach (CorpusOption option in definition.Options)
        {
            builder.Option(
                option.Id,
                option.LongName,
                ReadArity(option.Arity),
                option.Repeat == "append" ? CommandOptionRepeatPolicy.Append : CommandOptionRepeatPolicy.Error,
                false,
                null,
                option.Aliases);
        }

        foreach (CorpusArgument argument in definition.Arguments)
        {
            builder.Argument(argument.Id, argument.Id, ReadArity(argument.Arity));
        }
    }

    private static CommandArity ReadArity(CorpusArity arity) => new(
        arity.Minimum.GetInt32(),
        arity.Maximum.ValueKind == JsonValueKind.String ? null : arity.Maximum.GetInt32());
}
