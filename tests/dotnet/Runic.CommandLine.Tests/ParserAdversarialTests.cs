namespace Runic.CommandLine.Tests;

internal static class ParserAdversarialTests
{
    private static readonly CommandCatalog Catalog = FixtureCatalog.Create();

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("parser/missing-value-does-not-consume-unknown-option", () => MissingValue("--bogus", "input.db")),
        new("parser/missing-value-does-not-consume-help", () => MissingValue("--help")),
        new("parser/missing-value-does-not-consume-terminator", () => MissingValue("--", "input.db")),
        new("parser/output-missing-value-does-not-consume-unknown-option", () => MissingOutputValue("--bogus")),
        new("parser/output-missing-value-does-not-consume-help", () => MissingOutputValue("--help")),
        new("parser/output-missing-value-does-not-consume-terminator", () => MissingOutputValue("--")),
        new("parser/repeated-variadic-option-preserves-encounter-order", VariadicRepeatedOption),
        new("parser/variadic-option-can-reject-a-second-occurrence", VariadicSingleOccurrence),
        new("parser/transport-output-option-cannot-shadow-built-ins", TransportOutputOptionCannotShadowBuiltIns),
        new("parser/trailing-positional-values-preserve-terminator-data", TrailingPositionalValues),
        new("parser/required-options-are-parser-owned", RequiredOptions),
        new("parser/safe-diagnostic-arguments-identify-parse-shape", ParseDiagnosticArguments),
        new("parser/parse-errors-preserve-transport-output-classification", ParseErrorsPreserveTransportOutputClassification),
    ];

    private static ValueTask MissingValue(params string[] tail)
    {
        string[] args = ["export", "--format", .. tail];
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(Catalog, args, ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Error, outcome.Kind);
        AssertEx.Equal(1, outcome.Diagnostics.Count);
        AssertEx.Equal("RCLI1003", outcome.Diagnostics[0].Code);
        AssertEx.Equal("missing-option-value", outcome.Diagnostics[0].Kind);
        AssertEx.Equal(2, outcome.Diagnostics[0].TokenIndex);
        return ValueTask.CompletedTask;
    }

    private static ValueTask MissingOutputValue(string boundary)
    {
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--output", boundary],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Error, outcome.Kind);
        AssertEx.Equal(1, outcome.Diagnostics.Count);
        AssertEx.Equal("RCLI1003", outcome.Diagnostics[0].Code);
        AssertEx.Equal("missing-option-value", outcome.Diagnostics[0].Kind);
        AssertEx.Equal(2, outcome.Diagnostics[0].TokenIndex);
        return ValueTask.CompletedTask;
    }

    private static ValueTask VariadicRepeatedOption()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("pack", command => command
            .Option("documents", "--documents", CommandArity.OneOrMore, CommandOptionRepeatPolicy.Append)
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(
            builder.Build(),
            ["pack", "--documents", "a", "b", "--documents", "c", "--output=json"],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        AssertEx.SequenceEqual(["a", "b", "c"], outcome.Invocation!.Options.Single().Values);
        AssertEx.Equal(CommandOutputMode.Json, outcome.Invocation.OutputClassification.Mode);
        return ValueTask.CompletedTask;
    }

    private static ValueTask VariadicSingleOccurrence()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("pack", command => command
            .Option("documents", "--documents", CommandArity.OneOrMore, CommandOptionRepeatPolicy.Error)
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        CommandCatalog catalog = builder.Build();
        AssertEx.True(!catalog.Commands.Single().Options.Single().AllowsMultipleOccurrences);

        ParseOutcome oneOccurrence = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            ["pack", "--documents", "a", "b"],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, oneOccurrence.Kind);
        AssertEx.SequenceEqual(["a", "b"], oneOccurrence.Invocation!.Options.Single().Values);

        ParseOutcome repeated = PortableCommandSyntaxAdapter.Instance.Parse(
            catalog,
            ["pack", "--documents", "a", "--documents", "b"],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Error, repeated.Kind);
        AssertEx.Equal("RCLI1007", repeated.Diagnostics.Single().Code);
        AssertEx.SequenceEqual(["--documents"], repeated.Diagnostics.Single().Arguments);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TransportOutputOptionCannotShadowBuiltIns()
    {
        foreach (string spelling in new[] { "--help", "--version" })
        {
            AssertEx.Throws<ArgumentException>(() => new ParseSettings(transportOutputOptionName: spelling));
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask TrailingPositionalValues()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("dev", command => command
            .Argument("project", "project", CommandArity.ExactlyOne)
            .Argument("app-args", "app-args", CommandArity.ZeroOrMore)
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(
            builder.Build(),
            ["dev", "app.csproj", "--", "--application-option", "-3", "literal"],
            ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, outcome.Kind);
        AssertEx.Equal("app.csproj", outcome.Invocation!.Arguments[0].Values.Single());
        AssertEx.SequenceEqual(["--application-option", "-3", "literal"], outcome.Invocation.Arguments[1].Values);
        return ValueTask.CompletedTask;
    }

    private static ValueTask RequiredOptions()
    {
        CommandCatalog catalog = CreateRequiredCatalog();
        ParseOutcome missingValue = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["check"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Error, missingValue.Kind);
        AssertEx.Equal("RCLI1012", missingValue.Diagnostics.Single().Code);
        AssertEx.Equal("missing-required-option", missingValue.Diagnostics.Single().Kind);
        AssertEx.SequenceEqual(["--value", "check"], missingValue.Diagnostics.Single().Arguments);

        ParseOutcome missingFlag = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["check", "--value", "one"], ParseSettings.Default);
        AssertEx.Equal("RCLI1012", missingFlag.Diagnostics.Single().Code);
        AssertEx.SequenceEqual(["--force", "check"], missingFlag.Diagnostics.Single().Arguments);

        ParseOutcome invocation = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["check", "--value", "one", "--force", "--documents", "a", "--documents", "b"], ParseSettings.Default);
        AssertEx.Equal(ParseOutcomeKind.Invocation, invocation.Kind);
        AssertEx.SequenceEqual(["a", "b"], invocation.Invocation!.Options.Single(binding => binding.Id == "documents").Values);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ParseDiagnosticArguments()
    {
        CommandCatalog catalog = CreateRequiredCatalog();
        ParseOutcome unknownCommand = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["missing=command"], ParseSettings.Default);
        AssertEx.SequenceEqual(["missing=command"], unknownCommand.Diagnostics.Single().Arguments);

        ParseOutcome unknownOption = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["check", "--unknown=secret"], ParseSettings.Default);
        AssertEx.SequenceEqual(["--unknown"], unknownOption.Diagnostics.Single().Arguments);

        ParseOutcome missingValue = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["check", "--value"], ParseSettings.Default);
        AssertEx.SequenceEqual(["--value"], missingValue.Diagnostics.Single().Arguments);

        ParseOutcome duplicate = PortableCommandSyntaxAdapter.Instance.Parse(catalog, ["check", "--value", "one", "--value", "two"], ParseSettings.Default);
        AssertEx.SequenceEqual(["--value"], duplicate.Diagnostics.Single().Arguments);
        return ValueTask.CompletedTask;
    }

    private static ValueTask ParseErrorsPreserveTransportOutputClassification()
    {
        ParseSettings settings = new(transportOutputOptionName: "--runic-output");
        ParseOutcome knownErrorAfterTransport = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--runic-output=JSON", "--unknown=secret"],
            settings);
        AssertErrorOutput(knownErrorAfterTransport, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        AssertEx.SequenceEqual(["--unknown"], knownErrorAfterTransport.Diagnostics.Single().Arguments);

        ParseOutcome knownErrorBeforeTransport = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--unknown=secret", "--runic-output=JSON"],
            settings);
        AssertErrorOutput(knownErrorBeforeTransport, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        AssertEx.SequenceEqual(["--unknown"], knownErrorBeforeTransport.Diagnostics.Single().Arguments);

        ParseOutcome unknownCommandAfterTransport = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["missing", "--runic-output=JSON"],
            settings);
        AssertErrorOutput(unknownCommandAfterTransport, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);

        ParseOutcome unknownCommandBeforeTransport = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["--runic-output=JSON", "missing"],
            settings);
        AssertErrorOutput(unknownCommandBeforeTransport, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        AssertEx.SequenceEqual(["--runic-output"], unknownCommandBeforeTransport.Diagnostics.Single().Arguments);

        ParseOutcome transportOnly = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["--runic-output=JSON"],
            settings);
        AssertErrorOutput(transportOnly, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        AssertEx.SequenceEqual(["--runic-output"], transportOnly.Diagnostics.Single().Arguments);

        CommandCatalog outputProbeCatalog = CreateOutputProbeCatalog();
        ParseOutcome defaultCommand = PortableCommandSyntaxAdapter.Instance.Parse(
            outputProbeCatalog,
            ["application-argument", "--runic-output=json"],
            settings);
        AssertErrorOutput(defaultCommand, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);

        ParseOutcome nestedCommand = PortableCommandSyntaxAdapter.Instance.Parse(
            outputProbeCatalog,
            ["root", "nested", "--unknown=secret", "--runic-output=JSON"],
            settings);
        AssertErrorOutput(nestedCommand, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);

        ParseOutcome afterSentinel = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--unknown=secret", "--", "--runic-output=JSON"],
            settings);
        AssertErrorOutput(afterSentinel, CommandOutputMode.Human, CommandOutputModeSource.Default);

        AssertSpecialOutput(Catalog, ["--help", "--runic-output=JSON"], settings, ParseOutcomeKind.Help, "");
        AssertSpecialOutput(Catalog, ["--runic-output=JSON", "--help"], settings, ParseOutcomeKind.Help, "");
        AssertSpecialOutput(Catalog, ["help", "--runic-output=JSON"], settings, ParseOutcomeKind.Help, "");
        AssertSpecialOutput(Catalog, ["--runic-output=JSON", "help"], settings, ParseOutcomeKind.Help, "");
        AssertSpecialOutput(Catalog, ["--version", "--runic-output=JSON"], settings, ParseOutcomeKind.Version, "");
        AssertSpecialOutput(Catalog, ["--runic-output=JSON", "--version"], settings, ParseOutcomeKind.Version, "");
        AssertSpecialOutput(Catalog, ["status", "--help", "--runic-output=JSON"], settings, ParseOutcomeKind.Help, "status");
        AssertSpecialOutput(Catalog, ["status", "--runic-output=JSON", "--help"], settings, ParseOutcomeKind.Help, "status");
        AssertSpecialOutput(outputProbeCatalog, ["application-argument", "--help", "--runic-output=JSON"], settings, ParseOutcomeKind.Help, "fallback");
        AssertSpecialOutput(outputProbeCatalog, ["application-argument", "--runic-output=JSON", "--help"], settings, ParseOutcomeKind.Help, "fallback");
        AssertSpecialOutput(outputProbeCatalog, ["root", "nested", "--help", "--runic-output=JSON"], settings, ParseOutcomeKind.Help, "root nested");
        AssertSpecialOutput(outputProbeCatalog, ["root", "nested", "--runic-output=JSON", "--help"], settings, ParseOutcomeKind.Help, "root nested");
        AssertSpecialOutput(Catalog, ["--help", "--", "--runic-output=JSON"], settings, ParseOutcomeKind.Help, "", CommandOutputMode.Human);
        ParseOutcome rootHelpExtra = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["help", "--unknown=secret", "--runic-output=JSON"],
            settings);
        AssertErrorOutput(rootHelpExtra, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        AssertEx.Equal("RCLI1013", rootHelpExtra.Diagnostics.Single().Code);
        AssertEx.Equal(0, rootHelpExtra.Diagnostics.Single().Arguments.Count);
        AssertNoSecret(rootHelpExtra);

        ParseOutcome rootHelpTerminator = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["help", "--", "--runic-output=JSON"],
            settings);
        AssertErrorOutput(rootHelpTerminator, CommandOutputMode.Human, CommandOutputModeSource.Default);
        AssertEx.Equal("RCLI1013", rootHelpTerminator.Diagnostics.Single().Code);
        AssertEx.Equal(ParseOutcomeKind.Error, PortableCommandSyntaxAdapter.Instance.Parse(outputProbeCatalog, ["root", "help"], settings).Kind);
        AssertEx.Equal(ParseOutcomeKind.Error, PortableCommandSyntaxAdapter.Instance.Parse(outputProbeCatalog, ["application-argument", "help"], settings).Kind);
        AssertEx.Equal(ParseOutcomeKind.Error, PortableCommandSyntaxAdapter.Instance.Parse(outputProbeCatalog, ["root", "nested", "help"], settings).Kind);

        CommandCatalog boundaryCatalog = CreateTransportBoundaryCatalog();
        AssertTransportBoundary(boundaryCatalog, ["root", "--runic-output", "/x"], settings);
        AssertTransportBoundary(boundaryCatalog, ["root", "nested", "--runic-output", "/x"], settings);
        AssertTransportBoundary(boundaryCatalog, ["application-argument", "--runic-output", "/x"], settings);

        ParseOutcome duplicate = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--runic-output", "jSoN", "--runic-output", "human"],
            settings);
        AssertErrorOutput(duplicate, CommandOutputMode.Json, CommandOutputModeSource.ExplicitArgument);
        AssertEx.Equal("duplicate-option", duplicate.Diagnostics.Single().Kind);

        ParseOutcome missing = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--runic-output", "--unknown=secret"],
            settings);
        AssertErrorOutput(missing, CommandOutputMode.Human, CommandOutputModeSource.Default);
        AssertEx.SequenceEqual(["--runic-output"], missing.Diagnostics.Single().Arguments);

        ParseOutcome malformed = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--runic-output=xml"],
            settings);
        AssertErrorOutput(malformed, CommandOutputMode.Human, CommandOutputModeSource.Default);
        AssertEx.Equal("invalid-output-mode", malformed.Diagnostics.Single().Kind);

        ParseOutcome hostileRoot = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["--runic-output=TOPSECRET"],
            settings);
        AssertErrorOutput(hostileRoot, CommandOutputMode.Human, CommandOutputModeSource.Default);
        AssertEx.Equal("invalid-output-mode", hostileRoot.Diagnostics.Single().Kind);
        AssertEx.Equal(0, hostileRoot.Diagnostics.Single().Arguments.Count);
        AssertNoSecret(hostileRoot);

        ParseOutcome hostileAfterSyntaxError = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            ["status", "--unknown=secret", "--runic-output=TOPSECRET"],
            settings);
        AssertErrorOutput(hostileAfterSyntaxError, CommandOutputMode.Human, CommandOutputModeSource.Default);
        AssertEx.Equal("invalid-output-mode", hostileAfterSyntaxError.Diagnostics.Single().Kind);
        AssertEx.Equal(0, hostileAfterSyntaxError.Diagnostics.Single().Arguments.Count);
        AssertNoSecret(hostileAfterSyntaxError);
        return ValueTask.CompletedTask;
    }

    private static CommandCatalog CreateOutputProbeCatalog()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("root", command => command
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec())
            .Subcommand<TestOptions, TestHandler, TestResult>("nested", nested => nested
                .BindWith(new TestBinder())
                .CreateHandlerWith(new TestHandlerFactory())
                .Produces(new TestCodec())));
        builder.Command<TestOptions, TestHandler, TestResult>("fallback", command => command
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        return builder.DefaultCommand("fallback").Build();
    }

    private static void AssertNoSecret(ParseOutcome outcome)
    {
        foreach (CommandDiagnostic diagnostic in outcome.Diagnostics)
        {
            AssertEx.True(!diagnostic.Message.Contains("TOPSECRET", StringComparison.Ordinal));
            AssertEx.True(diagnostic.Arguments.All(static argument =>
                !argument.Contains("TOPSECRET", StringComparison.Ordinal)));
        }
    }

    private static void AssertSpecialOutput(
        CommandCatalog catalog,
        string[] args,
        ParseSettings settings,
        ParseOutcomeKind kind,
        string path,
        CommandOutputMode mode = CommandOutputMode.Json)
    {
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, settings);
        AssertEx.Equal(kind, outcome.Kind);
        AssertEx.Equal(mode, outcome.OutputClassification!.Value.Mode);
        if (kind == ParseOutcomeKind.Help)
        {
            AssertEx.Equal(path, outcome.HelpRequest!.Path.ToString());
        }
    }

    private static void AssertTransportBoundary(
        CommandCatalog catalog,
        string[] args,
        ParseSettings settings)
    {
        ParseOutcome outcome = PortableCommandSyntaxAdapter.Instance.Parse(catalog, args, settings);
        AssertErrorOutput(outcome, CommandOutputMode.Human, CommandOutputModeSource.Default);
        AssertEx.Equal("RCLI1003", outcome.Diagnostics.Single().Code);
        AssertEx.SequenceEqual(["--runic-output"], outcome.Diagnostics.Single().Arguments);
    }

    private static CommandCatalog CreateTransportBoundaryCatalog()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("root", command => command
            .Option("value", "--value", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Error, isRequired: false, aliases: ["/x"])
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec())
            .Subcommand<TestOptions, TestHandler, TestResult>("nested", nested => nested
                .Option("value", "--value", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Error, isRequired: false, aliases: ["/x"])
                .BindWith(new TestBinder())
                .CreateHandlerWith(new TestHandlerFactory())
                .Produces(new TestCodec())));
        builder.Command<TestOptions, TestHandler, TestResult>("fallback", command => command
            .Option("value", "--value", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Error, isRequired: false, aliases: ["/x"])
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        return builder.DefaultCommand("fallback").Build();
    }

    private static void AssertErrorOutput(
        ParseOutcome outcome,
        CommandOutputMode mode,
        CommandOutputModeSource source)
    {
        AssertEx.Equal(ParseOutcomeKind.Error, outcome.Kind);
        AssertEx.True(outcome.OutputClassification is { IsValid: true });
        AssertEx.Equal(mode, outcome.OutputClassification!.Value.Mode);
        AssertEx.Equal(source, outcome.OutputClassification.Value.Source);
    }

    private static CommandCatalog CreateRequiredCatalog()
    {
        var builder = new CommandCatalogBuilder();
        builder.Command<TestOptions, TestHandler, TestResult>("check", command => command
            .Option("value", "--value", CommandArity.ExactlyOne, CommandOptionRepeatPolicy.Error, isRequired: true)
            .Option("force", "--force", CommandArity.Zero, CommandOptionRepeatPolicy.Error, isRequired: true)
            .Option("documents", "--documents", CommandArity.OneOrMore, CommandOptionRepeatPolicy.Append, isRequired: true)
            .BindWith(new TestBinder())
            .CreateHandlerWith(new TestHandlerFactory())
            .Produces(new TestCodec()));
        return builder.Build();
    }
}
