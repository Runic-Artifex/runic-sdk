namespace RunicCommandLine.Tests;

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
}
