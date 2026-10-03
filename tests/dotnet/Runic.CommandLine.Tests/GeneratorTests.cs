using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.CommandLine.Generators;

namespace Runic.CommandLine.Tests;

internal static class GeneratorTests
{
    private const string Commands = """
        using Runic.CommandLine;

        namespace Probe;

        internal static class Commands
        {
            [Command("greet")]
            public static string Greet([Argument("name")] string name, [Option("--loud")] bool loud) => loud ? name.ToUpperInvariant() : name;

            [Command("list")]
            public static string List([Argument("items", AllowMultipleValues = true)] string[] items) => string.Join(",", items);
        }
        """;

    public static IReadOnlyList<TestCase> All { get; } =
    [
        new("generator/unchanged-commands-are-cached", UnchangedCommandsAreCached),
        new("generator/parameter-errors-use-distinct-diagnostics", ParameterErrorsUseDistinctDiagnostics),
    ];

    private static ValueTask UnchangedCommandsAreCached()
    {
        CSharpCompilation compilation = CreateCompilation(Commands);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new CommandLineGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));
        driver = driver.RunGenerators(compilation);
        GeneratorRunResult first = driver.GetRunResult().Results.Single();
        AssertEx.Equal(0, first.Diagnostics.Length, string.Join("\n", first.Diagnostics));
        AssertEx.Equal(1, first.GeneratedSources.Length);

        driver = driver.RunGenerators(compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("internal static class Unrelated { }", (CSharpParseOptions)compilation.SyntaxTrees[0].Options)));
        GeneratorRunResult second = driver.GetRunResult().Results.Single();
        foreach (string step in new[] { "CommandModels", "CommandCatalog" })
        {
            ImmutableArray<IncrementalGeneratorRunStep> runs = second.TrackedSteps[step];
            AssertEx.True(runs.Length > 0, step + " was not tracked.");
            AssertEx.True(
                runs.SelectMany(static run => run.Outputs).All(static output => output.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged),
                step + " recomputed after an unrelated edit.");
        }

        return ValueTask.CompletedTask;
    }

    private static ValueTask ParameterErrorsUseDistinctDiagnostics()
    {
        CSharpCompilation compilation = CreateCompilation("""
            using Runic.CommandLine;

            internal static class Commands
            {
                [Command("by-ref")]
                public static string ByRef([Argument("value")] ref string value) => value;

                [Command("flag")]
                public static string Flag([Option("--on")] bool on = true) => "";

                [Command("required")]
                public static string Required([Option("--name", Required = true)] string name = "x") => name;

                [Command("first")]
                [DefaultCommand]
                public static string First() => "";

                [Command("second")]
                [DefaultCommand]
                public static string Second() => "";
            }
            """);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CommandLineGenerator()).RunGenerators(compilation);
        ImmutableArray<Diagnostic> diagnostics = driver.GetRunResult().Results.Single().Diagnostics;
        AssertEx.SequenceEqual(
            ["RCLI9021", "RCLI9024", "RCLI9023", "RCLI9029", "RCLI9029"],
            diagnostics.Select(static diagnostic => diagnostic.Id).ToArray(),
            string.Join("\n", diagnostics));
        AssertEx.True(
            diagnostics.All(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line > 0),
            "Generator diagnostics lost their source locations.");
        return ValueTask.CompletedTask;
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        string[] platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        IEnumerable<MetadataReference> references = platform
            .Where(static path => Path.GetFileName(path).StartsWith("System.", StringComparison.Ordinal) || Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll")
            .Append(typeof(CommandAttribute).Assembly.Location)
            .Select(static path => MetadataReference.CreateFromFile(path));
        return CSharpCompilation.Create(
            "GeneratorProbe",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }
}
