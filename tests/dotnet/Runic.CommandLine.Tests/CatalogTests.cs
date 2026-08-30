namespace Runic.CommandLine.Tests;

internal static class CatalogTests
{
    private static readonly string[] MissingRegistrationCodes =
        ["RCLI0010", "RCLI0011", "RCLI0012"];

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("catalog/aliases-resolve-to-canonical-descriptors", AliasesResolve),
        new("catalog/combine-preserves-order-and-rejects-duplicates", CombineCatalogs),
        new("catalog/default-command-receives-unprefixed-tokens", DefaultCommand),
        new("catalog/reserved-command-names-rejected", ReservedCommandNames),
        new("catalog/reserved-option-names-rejected", ReservedOptionNames),
        new("catalog/application-output-and-configured-transport-coexist", ConfiguredTransportOutput),
        new("catalog/duplicate-spellings-reported", DuplicateSpellings),
        new("catalog/contradictory-argument-arity-reported", ContradictoryArgumentArity),
        new("catalog/missing-closed-registration-reported-in-order", MissingRegistration),
        new("catalog/invalid-payload-identities-fail-at-freeze", InvalidPayloadIdentities),
        new("catalog/payload-identity-is-frozen", PayloadIdentityIsFrozen),
        new("catalog/diagnostic-identities-RCLI0002-through-RCLI0017-are-frozen", FrozenDiagnosticIdentities),
        new("catalog/frozen-descriptors-isolate-source-arrays", FrozenDescriptors),
    ];

    private static ValueTask AliasesResolve()
    {
        CommandCatalog catalog = FixtureCatalog.Create();
        AssertEx.True(catalog.TryGetCommand("stat", out CommandDescriptor? status), "Root alias was not resolved.");
        AssertEx.Equal("status", status!.Name);
        AssertEx.True(catalog.TryResolve(["c", "purge"], out CommandDescriptor? clear, out int consumed),
            "Nested aliases were not resolved.");
        AssertEx.Equal("clear", clear!.Name);
        AssertEx.Equal(2, consumed);
        return ValueTask.CompletedTask;
    }

    private static ValueTask CombineCatalogs()
    {
        var firstBuilder = new CommandCatalogBuilder();
        ValidCommand(firstBuilder, "first");
        var secondBuilder = new CommandCatalogBuilder();
        ValidCommand(secondBuilder, "second");
        CommandCatalog combined = CommandCatalog.Combine(firstBuilder.Build(), secondBuilder.Build());
        AssertEx.SequenceEqual(["first", "second"], combined.Commands.Select(static command => command.Name));

        var duplicateBuilder = new CommandCatalogBuilder();
        ValidCommand(duplicateBuilder, "first");
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() =>
            CommandCatalog.Combine(combined, duplicateBuilder.Build()));
        AssertEx.Equal("RCLI0004", exception.Issues[0].Code);

        CommandCatalog firstDefault = firstBuilder.DefaultCommand("first").Build();
        CommandCatalog secondDefault = secondBuilder.DefaultCommand("second").Build();
        exception = AssertEx.Throws<CommandCatalogValidationException>(() => CommandCatalog.Combine(firstDefault, secondDefault));
        AssertEx.Equal("RCLI0019", exception.Issues[0].Code);

        var thirdBuilder = new CommandCatalogBuilder();
        ValidCommand(thirdBuilder, "third");
        CommandCatalog withDefault = CommandCatalog.Combine(firstDefault, thirdBuilder.Build());
        AssertEx.Equal("first", withDefault.DefaultCommand!.Name);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DefaultCommand()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "pack").Argument("path", "path", CommandArity.ExactlyOne);
        CommandCatalog catalog = builder.DefaultCommand("pack").Build();
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["artifact.bin"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        AssertEx.Equal("pack", outcome.Invocation!.Command.Name);
        AssertEx.Equal("artifact.bin", outcome.Invocation.Arguments[0].Values[0]);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ReservedCommandNames()
    {
        foreach (string name in new[] { "help", "version", "output" })
        {
            var builder = new CommandCatalogBuilder();
            ValidCommand(builder, name);
            CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(() =>
                builder.Build());
            AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0002"), name);
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask ReservedOptionNames()
    {
        foreach (string name in new[] { "--help", "-h", "--version" })
        {
            var builder = new CommandCatalogBuilder();
            ValidCommand(builder, "status").Option("reserved", name, CommandArity.Zero);
            CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
            AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0007"), name);
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask ConfiguredTransportOutput()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "status").Option("output", "--output", CommandArity.ExactlyOne);
        CommandCatalog catalog = builder.Build();

        ParseOutcome success = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            ["status", "--output", "application", "--runic-output=json"],
            new ParseSettings(transportOutputOptionName: "--runic-output"));
        AssertEx.Equal(ParseOutcomeKind.Invocation, success.Kind);
        AssertEx.Equal(CommandOutputMode.Json, success.Invocation!.OutputClassification.Mode);
        AssertEx.Equal("application", success.Invocation.Options.Single().Values.Single());

        ParseOutcome collision = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["status"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Error, collision.Kind);
        AssertEx.Equal("RCLI1011", collision.Diagnostics.Single().Code);
        AssertEx.Equal("transport-output-option-collision", collision.Diagnostics.Single().Kind);
        return ValueTask.CompletedTask;
    }

    private static ValueTask DuplicateSpellings()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "one").Alias("shared");
        ValidCommand(builder, "two").Alias("shared");
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0004"));

        builder = new CommandCatalogBuilder();
        ValidCommand(builder, "one")
            .Option("first", "--first", CommandArity.Zero, aliases: "-x")
            .Option("second", "--second", CommandArity.Zero, aliases: "-x");
        exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0008"));
        return ValueTask.CompletedTask;
    }

    private static ValueTask ContradictoryArgumentArity()
    {
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "copy")
            .Argument("optional", "optional", CommandArity.ZeroOrOne)
            .Argument("required", "required", CommandArity.ExactlyOne);
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0015"));

        builder = new CommandCatalogBuilder();
        ValidCommand(builder, "copy")
            .Argument("many", "many", CommandArity.ZeroOrMore)
            .Argument("tail", "tail", CommandArity.ZeroOrOne);
        exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0016"));
        return ValueTask.CompletedTask;
    }

    private static ValueTask MissingRegistration()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("status");
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.SequenceEqual(
            MissingRegistrationCodes,
            exception.Issues.Select(static issue => issue.Code));
        return ValueTask.CompletedTask;
    }

    private static ValueTask InvalidPayloadIdentities()
    {
        foreach (string payloadType in new[]
        {
            string.Empty,
            "Upper/1",
            "name",
            "name/0",
            "name/x",
            "name/",
            "bad name/1",
            "bad_name/1",
            "bad/name/1",
            $"{new string('a', 127)}/1",
            $"a{new string('\u00e9', 63)}/1",
        })
        {
            var builder = new CommandCatalogBuilder();
            builder.Command<TestOptions, TestHandler, TestResult>("status")
                .BindWith(new TestBinder())
                .CreateHandlerWith(new TestHandlerFactory())
                .Produces(new InvalidCodec(payloadType));
            CommandCatalogValidationException exception =
                AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
            AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0017"), payloadType);
        }

        AssertInvalidCodec(new NullTypeInfoCodec());
        AssertInvalidCodec(new ThrowingCodec(throwPayloadType: true));
        AssertInvalidCodec(new ThrowingCodec(throwPayloadType: false));

        return ValueTask.CompletedTask;
    }

    private static async ValueTask PayloadIdentityIsFrozen()
    {
        var initialContext = new TestJsonContext(new System.Text.Json.JsonSerializerOptions());
        var mutatedContext = new TestJsonContext(new System.Text.Json.JsonSerializerOptions());
        var handlerContext = new TestJsonContext(new System.Text.Json.JsonSerializerOptions());
        var codec = new MutableCodec("initial/1", initialContext.TestResult);
        var factory = new TestHandlerFactory(_ => new TestHandler((_, _, _) =>
        {
            codec.PayloadType = "handler-mutated/3";
            codec.TypeInfo = handlerContext.TestResult;
            return ValueTask.FromResult(CommandOutcome.Success(new TestResult(1, "ok")));
        }));
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("status")
            .BindWith(new TestBinder())
            .CreateHandlerWith(factory)
            .Produces(codec);
        CommandCatalog catalog = builder.Build();
        codec.PayloadType = "mutated/2";
        codec.TypeInfo = mutatedContext.TestResult;

        ParsedInvocation invocation = FixtureCatalog.ParseInvocation(catalog, "status");
        var sink = new CapturingSink();
        var request = new CommandExecutionRequest(
            invocation,
            new MemoryCommandConsole(),
            System.Globalization.CultureInfo.InvariantCulture,
            "catalog-freeze");
        await new CommandExecutor(new TrackingScopeFactory()).ExecuteAsync(request, sink);

        AssertEx.Equal("initial/1", sink.PayloadType);
        AssertEx.True(ReferenceEquals(initialContext.TestResult, sink.TypeInfo));
    }

    private static void AssertInvalidCodec(ICommandResultCodec<TestResult> codec)
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("status")
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(codec);
        CommandCatalogValidationException exception =
            AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        AssertEx.True(exception.Issues.Any(static issue => issue.Code == "RCLI0017"));
    }

    private static ValueTask FrozenDescriptors()
    {
        string[] aliases = ["stat"];
        var builder = new CommandCatalogBuilder();
        ValidCommand(builder, "status").Alias(aliases);
        CommandCatalog catalog = builder.Build();
        aliases[0] = "changed";
        AssertEx.True(catalog.TryGetCommand("stat", out _));
        AssertEx.True(!catalog.TryGetCommand("changed", out _));
        AssertEx.Throws<NotSupportedException>(() =>
            ((IList<string>)catalog.Commands[0].Aliases).Add("mutate"));
        return ValueTask.CompletedTask;
    }

    private static ValueTask FrozenDiagnosticIdentities()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Capture(builder => ValidCommand(builder, "Bad"), seen);
        Capture(builder => ValidCommand(builder, "ok").Describe(""), seen);
        Capture(builder => { ValidCommand(builder, "one").Alias("same"); ValidCommand(builder, "two").Alias("same"); }, seen);
        Capture(builder => ValidCommand(builder, "ok").Option("Bad", "--good", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("same", "--one", CommandArity.Zero).Option("same", "--two", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("one", "--same", CommandArity.Zero).Option("two", "--same", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("one", "--help", CommandArity.Zero), seen);
        Capture(builder => ValidCommand(builder, "ok").Option("one", "--one", CommandArity.Zero, (CommandOptionRepeatPolicy)999), seen);
        Capture(builder => builder.Command<TestOptions, TestHandler, TestResult>("ok"), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("Bad", "Bad", CommandArity.ZeroOrOne), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("same", "one", CommandArity.ZeroOrOne).Argument("same", "two", CommandArity.ZeroOrOne), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("optional", "optional", CommandArity.ZeroOrOne).Argument("required", "required", CommandArity.ExactlyOne), seen);
        Capture(builder => ValidCommand(builder, "ok").Argument("many", "many", CommandArity.ZeroOrMore).Argument("tail", "tail", CommandArity.ZeroOrOne), seen);

        var invalidCodecBuilder = new CommandCatalogBuilder();
        invalidCodecBuilder.Command<TestOptions, TestHandler, TestResult>("ok")
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new InvalidCodec(string.Empty));
        Capture(invalidCodecBuilder, seen);

        AssertEx.SequenceEqual(
            Enumerable.Range(2, 16).Select(static number => $"RCLI{number:0000}"),
            seen.OrderBy(static code => code, StringComparer.Ordinal));
        return ValueTask.CompletedTask;
    }

    private static void Capture(
        Action<CommandCatalogBuilder> configure,
        HashSet<string> seen)
    {
        var builder = new CommandCatalogBuilder();
        configure(builder);
        Capture(builder, seen);
    }

    private static void Capture(CommandCatalogBuilder builder, HashSet<string> seen)
    {
        CommandCatalogValidationException exception = AssertEx.Throws<CommandCatalogValidationException>(builder.Build);
        foreach (CommandCatalogIssue issue in exception.Issues)
        {
            seen.Add(issue.Code);
        }
    }

    private sealed class InvalidCodec : ICommandResultCodec<TestResult>
    {
        public InvalidCodec(string payloadType) => PayloadType = payloadType;

        public string PayloadType { get; }

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo => TestJsonContext.Default.TestResult;

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class MutableCodec : ICommandResultCodec<TestResult>
    {
        public MutableCodec(
            string payloadType,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> typeInfo)
        {
            PayloadType = payloadType;
            TypeInfo = typeInfo;
        }

        public string PayloadType { get; set; }

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo { get; set; }

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class NullTypeInfoCodec : ICommandResultCodec<TestResult>
    {
        public string PayloadType => "valid/1";

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo => null!;

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class ThrowingCodec : ICommandResultCodec<TestResult>
    {
        private readonly bool _throwPayloadType;

        public ThrowingCodec(bool throwPayloadType) => _throwPayloadType = throwPayloadType;

        public string PayloadType => _throwPayloadType
            ? throw new InvalidOperationException("payload getter")
            : "valid/1";

        public System.Text.Json.Serialization.Metadata.JsonTypeInfo<TestResult> TypeInfo => !_throwPayloadType
            ? throw new InvalidOperationException("metadata getter")
            : TestJsonContext.Default.TestResult;

        public ValueTask WriteHumanAsync(
            TestResult value,
            ICommandConsole console,
            System.Globalization.CultureInfo culture,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    internal static CommandBuilder<TestOptions, TestHandler, TestResult> ValidCommand(
        CommandCatalogBuilder builder,
        string name,
        TestBinder? binder = null,
        TestHandlerFactory? handlerFactory = null,
        TestCodec? codec = null) =>
        builder.Command<TestOptions, TestHandler, TestResult>(name)
            .BindWith(binder ?? new TestBinder())
            .CreateHandlerWith(handlerFactory ?? new TestHandlerFactory())
            .Produces(codec ?? new TestCodec());
}
