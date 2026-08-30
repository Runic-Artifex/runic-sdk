using System.Text.Json;

namespace Runic.CommandLine.Tests;

internal static class OutputClassificationCorpusTests
{
    private static readonly OutputClassificationCorpus Corpus = ReadCorpus();
    private static readonly CommandCatalog Catalog = FixtureCatalog.Create();

    public static IReadOnlyList<TestCase> All { get; } = CreateTests();

    private static TestCase[] CreateTests()
    {
        AssertEx.Equal(18, Corpus.Cases.Length, "The frozen output-classification corpus row count changed.");
        AssertEx.Equal(CommandOutputClassifier.EnvironmentVariableName, Corpus.EnvironmentVariable);
        return Corpus.Cases
            .Select(test => new TestCase($"output-classification/{test.Id}", () => RunCase(test)))
            .ToArray();
    }

    private static ValueTask RunCase(OutputClassificationCase test)
    {
        test.Environment.TryGetValue(CommandOutputClassifier.EnvironmentVariableName, out string? environmentValue);
        ParseOutcome actual = PortableCommandSyntaxAdapter.Instance.Parse(
            Catalog,
            test.Args,
            new ParseSettings(environmentValue));

        if (test.Expected.Kind == "output-mode")
        {
            AssertEx.Equal(ParseOutcomeKind.Invocation, actual.Kind, test.Id);
            CommandOutputClassification classification = actual.Invocation!.OutputClassification;
            AssertEx.True(classification.IsValid, test.Id);
            AssertEx.Equal(ParseMode(test.Expected.Mode!), classification.Mode, test.Id);
            AssertEx.Equal(ParseSource(test.Expected.Source!), classification.Source, test.Id);
            AssertEx.Equal(0, actual.Diagnostics.Count, test.Id);
        }
        else
        {
            AssertEx.Equal(ParseOutcomeKind.Error, actual.Kind, test.Id);
            CorpusDiagnostic[] expected = test.Expected.Diagnostics ?? [];
            AssertEx.Equal(expected.Length, actual.Diagnostics.Count, test.Id);
            for (int index = 0; index < expected.Length; index++)
            {
                AssertEx.Equal(expected[index].Code, actual.Diagnostics[index].Code, test.Id);
                AssertEx.Equal(expected[index].Kind, actual.Diagnostics[index].Kind, test.Id);
                AssertEx.Equal(expected[index].TokenIndex, actual.Diagnostics[index].TokenIndex, test.Id);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static OutputClassificationCorpus ReadCorpus()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Corpus", "output-classification-corpus.json");
        return JsonSerializer.Deserialize(
            File.ReadAllBytes(path),
            CorpusJsonContext.Default.OutputClassificationCorpus)
            ?? throw new InvalidOperationException("The frozen output-classification corpus could not be read.");
    }

    private static CommandOutputMode ParseMode(string value) => value switch
    {
        "human" => CommandOutputMode.Human,
        "json" => CommandOutputMode.Json,
        _ => throw new InvalidOperationException($"Unknown output mode '{value}'."),
    };

    private static CommandOutputModeSource ParseSource(string value) => value switch
    {
        "default" => CommandOutputModeSource.Default,
        "environment" => CommandOutputModeSource.Environment,
        "explicit-argument" => CommandOutputModeSource.ExplicitArgument,
        _ => throw new InvalidOperationException($"Unknown output source '{value}'."),
    };
}
