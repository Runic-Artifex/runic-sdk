using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Runic.Application.Testing.Tests;

internal sealed partial class CodegenDiagnosticsTests
{
    private const string CompositionModel = """
        public sealed class CompositionViewModel : FixtureModel { public string Title { get; set; } = ""; }
        public sealed partial class CompositionWindow : RunicWindow<CompositionViewModel>;
        """;

    private static async Task RunCompositionAndConfigurationDiagnosticsAsync(string generator, string temporaryRoot)
    {
        // ViewsCodegen-07: the generated composition carries the standard generated-code markers and documentation.
        var output = await GenerateValidAsync(generator, temporaryRoot, "Composition", Preamble + CompositionModel,
            "--di-composition", "Fixture.RunicViewsComposition").ConfigureAwait(false);
        var composition = File.ReadAllText(Path.Combine(output, "RunicBridgeComposition.g.cs"));
        foreach (var text in new[]
        {
            "namespace Fixture;",
            "/// <summary>Registers this project's generated Runic Views for dependency injection.</summary>",
            "[global::System.CodeDom.Compiler.GeneratedCode(\"Runic.Application.Views.Codegen\", \"",
            "[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]",
            "public static class RunicViewsComposition",
            "/// <summary>Registers only the generated Bridges",
            "/// <summary>Registers the generated Bridges, every non-Window View as transient",
        })
            Require(composition.Contains(text, StringComparison.Ordinal), $"The composition lacks '{text}':\n{composition}");
        var version = System.Text.RegularExpressions.Regex.Match(composition, "GeneratedCode\\(\"Runic\\.Application\\.Views\\.Codegen\", \"([^\"]+)\"\\)").Groups[1].Value;
        Require(version.Length > 0 && !version.Contains('+', StringComparison.Ordinal), $"The generated-code version is '{version}'.");
        var compiled = Compile("CompositionConsumer", Preamble + CompositionModel)
            .AddSyntaxTrees(Directory.GetFiles(output, "*.g.cs").Select(path =>
                CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)))
            .GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Require(compiled.Length == 0, $"The generated composition did not compile:\n{string.Join('\n', compiled)}");

        // ViewsCodegen-08: RUNICBRIDGE001 for invalid configuration, such as a composition name that is no C# name.
        var (exit, invalid, _) = await RunGeneratorAsync(generator, temporaryRoot, "CompositionInvalid", Preamble + CompositionModel,
            "--di-composition", "2048Game.RunicViewsComposition").ConfigureAwait(false);
        Require(exit != 0 && invalid.Contains("RUNICBRIDGE001", StringComparison.Ordinal)
            && invalid.Contains("fully qualified C# class name", StringComparison.Ordinal),
            $"An invalid composition name was not RUNICBRIDGE001:\n{invalid}");

        // ViewsCodegen-08: RUNICBRIDGE002 for an ObservableValidator ViewModel in a Native AOT publish.
        const string Validator = """
            public sealed partial class ValidatedViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableValidator
            {
                [System.ComponentModel.DataAnnotations.Required] public string Name { get; set; } = "";
            }
            public sealed partial class ValidatedWindow : RunicWindow<ValidatedViewModel>;
            """;
        var (aotExit, aot, _) = await RunGeneratorAsync(generator, temporaryRoot, "ValidatorAot", Preamble + Validator, "--aot")
            .ConfigureAwait(false);
        Require(aotExit != 0 && aot.Contains("RUNICBRIDGE002", StringComparison.Ordinal),
            $"An ObservableValidator ViewModel in an AOT publish was not RUNICBRIDGE002:\n{aot}");
        _ = await GenerateValidAsync(generator, temporaryRoot, "ValidatorJit", Preamble + Validator).ConfigureAwait(false);

        // I-002: an exception that no diagnostic covers is RUNICBRIDGE024, not RUNICBRIDGE001, names the ViewModel
        // and the exception type, and is followed by its stack trace. The other ViewModels are still generated.
        const string Internal = """
            public sealed class BrokenViewModel : FixtureModel { public int Count { get; } }
            public sealed partial class BrokenWindow : RunicWindow<BrokenViewModel>;
            public sealed class HealthyViewModel : FixtureModel { public int Count { get; } }
            public sealed partial class HealthyWindow : RunicWindow<HealthyViewModel>;
            """;
        Environment.SetEnvironmentVariable("RUNIC_BRIDGE_CODEGEN_TEST_INTERNAL_ERROR", "Fixture.BrokenViewModel");
        int internalExit;
        string internalOutput;
        try
        {
            (internalExit, internalOutput, _) = await RunGeneratorAsync(generator, temporaryRoot, "InternalError", Preamble + Internal)
                .ConfigureAwait(false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUNIC_BRIDGE_CODEGEN_TEST_INTERNAL_ERROR", null);
        }
        var internalLines = internalOutput.Split('\n');
        var internalError = internalLines.SingleOrDefault(line => line.Contains("error RUNICBRIDGE024: ", StringComparison.Ordinal));
        Require(internalExit != 0 && internalError is not null
            && internalError.Contains("Fixture.BrokenViewModel: internal generator error: System.InvalidOperationException: Injected internal error.", StringComparison.Ordinal)
            && internalError.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE024")}", StringComparison.Ordinal)
            && !internalOutput.Contains("RUNICBRIDGE001", StringComparison.Ordinal)
            && !internalOutput.Contains("error RUNICBRIDGE024: Fixture.HealthyViewModel", StringComparison.Ordinal)
            && internalLines.Any(line => line.TrimStart().StartsWith("at Diagnostics.InjectInternalErrorForTests", StringComparison.Ordinal)),
            $"An internal generator error was not RUNICBRIDGE024 with its stack trace:\n{internalOutput}");
    }
}
