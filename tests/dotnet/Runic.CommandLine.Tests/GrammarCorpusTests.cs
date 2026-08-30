namespace Runic.CommandLine.Tests;

internal static class GrammarCorpusTests
{
    private static readonly GrammarCorpus Corpus = FixtureCatalog.ReadCorpus();
    private static readonly CommandCatalog Catalog = FixtureCatalog.Create();

    public static IReadOnlyList<TestCase> All { get; } = CreateTests();

    private static TestCase[] CreateTests()
    {
        AssertEx.Equal(42, Corpus.Cases.Length, "The frozen grammar corpus row count changed.");
        return Corpus.Cases
            .Select(test => new TestCase($"grammar/{test.Id}", () => RunCase(test)))
            .ToArray();
    }

    private static ValueTask RunCase(CorpusCase test)
    {
        ParseOutcome actual = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            test.Args,
            ParseSettings.Default);

        switch (test.Expected.Kind)
        {
            case "invocation":
                AssertEx.Equal(ParseOutcomeKind.Invocation, actual.Kind, test.Id);
                AssertEx.SequenceEqual(test.Expected.CommandPath!, actual.Invocation!.Path.Segments, test.Id);
                AssertBindings(test.Expected.Options ?? [], actual.Invocation.Options, test.Id);
                AssertBindings(test.Expected.Arguments ?? [], actual.Invocation.Arguments, test.Id);
                AssertEx.Equal(0, actual.Diagnostics.Count, test.Id);
                break;
            case "help":
                AssertEx.Equal(ParseOutcomeKind.Help, actual.Kind, test.Id);
                AssertEx.SequenceEqual(test.Expected.CommandPath!, actual.HelpRequest!.Path.Segments, test.Id);
                AssertEx.Equal(0, actual.Diagnostics.Count, test.Id);
                break;
            case "version":
                AssertEx.Equal(ParseOutcomeKind.Version, actual.Kind, test.Id);
                AssertEx.True(actual.Invocation is null && actual.HelpRequest is null, test.Id);
                AssertEx.Equal(0, actual.Diagnostics.Count, test.Id);
                break;
            case "error":
                AssertEx.Equal(ParseOutcomeKind.Error, actual.Kind, test.Id);
                CorpusDiagnostic[] expected = test.Expected.Diagnostics ?? [];
                AssertEx.Equal(expected.Length, actual.Diagnostics.Count, test.Id);
                for (int index = 0; index < expected.Length; index++)
                {
                    AssertEx.Equal(expected[index].Code, actual.Diagnostics[index].Code, test.Id);
                    AssertEx.Equal(expected[index].Kind, actual.Diagnostics[index].Kind, test.Id);
                    AssertEx.Equal(expected[index].TokenIndex, actual.Diagnostics[index].TokenIndex, test.Id);
                }
                break;
            default:
                throw new InvalidOperationException($"Unknown corpus result kind '{test.Expected.Kind}'.");
        }

        return ValueTask.CompletedTask;
    }

    private static void AssertBindings(
        CorpusValue[] expected,
        IReadOnlyList<CommandValueBinding> actual,
        string caseId)
    {
        AssertEx.Equal(expected.Length, actual.Count, caseId);
        for (int index = 0; index < expected.Length; index++)
        {
            AssertEx.Equal(expected[index].Id, actual[index].Id, caseId);
            AssertEx.SequenceEqual(expected[index].Values, actual[index].Values, caseId);
        }
    }
}
