using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Application.Views;

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
            // RUNICBRIDGE038: the contract check comes before the first Bridge registration.
            $"global::Runic.Application.Views.Generated.BridgeGeneratedCode.RequireContract({BridgeGeneratedCode.ContractVersion}, typeof(RunicViewsComposition).Assembly);",
        })
            Require(composition.Contains(text, StringComparison.Ordinal), $"The composition lacks '{text}':\n{composition}");
        var version = System.Text.RegularExpressions.Regex.Match(composition, "GeneratedCode\\(\"Runic\\.Application\\.Views\\.Codegen\", \"([^\"]+)\"\\)").Groups[1].Value;
        Require(version.Length > 0 && !version.Contains('+', StringComparison.Ordinal), $"The generated-code version is '{version}'.");
        var compiled = Compile("CompositionConsumer", Preamble + CompositionModel)
            .AddSyntaxTrees(Directory.GetFiles(output, "*.g.cs").Select(path =>
                CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)))
            .GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Require(compiled.Length == 0, $"The generated composition did not compile:\n{string.Join('\n', compiled)}");
        var check = composition.IndexOf("RequireContract(", StringComparison.Ordinal);
        Require(check > 0 && check < composition.IndexOf("services.AddScoped<", StringComparison.Ordinal),
            $"The contract check does not precede the Bridge registrations:\n{composition}");
        AssertStaleCompositionReportsRebuild(output, composition);

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

        // Q7 (D031.2): without a Runic ReactiveUI adapter (the build passes "none"), a ReactiveUI command or
        // interaction is RUNICBRIDGE025, not a C# error in the generated code; so is an interaction of the other
        // flavor than the adapter's. A model without ReactiveUI members builds with "none".
        const string ReactiveCommand = """
            public sealed class GoViewModel : FixtureModel { public ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> GoCommand { get; } = null!; }
            public sealed partial class GoWindow : RunicWindow<GoViewModel>;
            """;
        const string ReactiveInteraction = """
            public sealed class AskViewModel : FixtureModel { public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; } = new(); }
            public sealed partial class AskWindow : RunicWindow<AskViewModel>;
            """;
        foreach (var (name, source, flavor, expected) in new[]
        {
            ("NoAdapterCommand", ReactiveCommand, "none", "GoViewModel.GoCommand is a ReactiveUI command, but the project references no Runic ReactiveUI adapter"),
            ("NoAdapterInteraction", ReactiveInteraction, "none", "AskViewModel.Confirm is a ReactiveUI interaction, but the project references no Runic ReactiveUI adapter"),
            ("MixedInteraction", ReactiveInteraction, "reactive", "AskViewModel.Confirm is a ReactiveUI.Primitives ReactiveUI interaction, but the project references Runic.Application.Views.ReactiveUI.Reactive"),
        })
        {
            var (adapterExit, adapterOutput, adapterDirectory) = await RunGeneratorAsync(generator, temporaryRoot, name, Preamble + source,
                "--reactiveui-flavor", flavor).ConfigureAwait(false);
            var adapterError = adapterOutput.Split('\n').SingleOrDefault(line => line.Contains("error RUNICBRIDGE025: ", StringComparison.Ordinal));
            Require(adapterExit != 0 && adapterError is not null && adapterError.Contains(expected, StringComparison.Ordinal)
                    && adapterError.StartsWith($"{Path.Combine(adapterDirectory, "Fixture.cs")}(", StringComparison.Ordinal)
                    && adapterError.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE025")}", StringComparison.Ordinal),
                $"{name}: expected a located RUNICBRIDGE025 '{expected}':\n{adapterOutput}");
        }
        _ = await GenerateValidAsync(generator, temporaryRoot, "NoAdapterPlain", Preamble + CompositionModel, "--reactiveui-flavor", "none")
            .ConfigureAwait(false);
        _ = await GenerateValidAsync(generator, temporaryRoot, "PrimitivesInteraction", Preamble + ReactiveInteraction,
            "--reactiveui-flavor", "primitives").ConfigureAwait(false);
    }

    // RUNICBRIDGE038: a composition that another major release generated, simulated by an older contract version, fails
    // in AddRunicBridges with the rebuild message, before it registers or loads a Bridge.
    private static void AssertStaleCompositionReportsRebuild(string output, string composition)
    {
        var current = $"RequireContract({BridgeGeneratedCode.ContractVersion},";
        var stale = composition.Replace(current, $"RequireContract({BridgeGeneratedCode.ContractVersion - 1},", StringComparison.Ordinal);
        Require(stale != composition, $"The composition has no '{current}' call to make stale.");
        var trees = Directory.GetFiles(output, "*.g.cs").Where(path => Path.GetFileName(path) != "RunicBridgeComposition.g.cs")
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path))
            .Append(CSharpSyntaxTree.ParseText(stale, new CSharpParseOptions(LanguageVersion.Latest), "RunicBridgeComposition.g.cs"));
        using var image = new MemoryStream();
        var emitted = Compile("StaleComposition", Preamble + CompositionModel).AddSyntaxTrees(trees).Emit(image);
        Require(emitted.Success, $"The stale composition did not compile:\n{string.Join('\n', emitted.Diagnostics)}");
        var context = new System.Runtime.Loader.AssemblyLoadContext("StaleComposition", isCollectible: true);
        try
        {
            image.Position = 0;
            var assembly = context.LoadFromStream(image);
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            var addBridges = assembly.GetType("Fixture.RunicViewsComposition", throwOnError: true)!.GetMethod("AddRunicBridges")!;
            RunicBridgeContractException? error = null;
            try { addBridges.Invoke(null, [services]); }
            catch (System.Reflection.TargetInvocationException invocation) { error = invocation.InnerException as RunicBridgeContractException; }
            Require(error is not null, "A stale generated-code contract did not throw RunicBridgeContractException.");
            var name = assembly.GetName().Name!;
            Require(error!.AssemblyName == name
                && error.Message.StartsWith("RUNICBRIDGE038: the generated Runic Bridges in " + name + " were built against an older Runic release", StringComparison.Ordinal)
                && error.Message.Contains($"Rebuild this library against Runic {RunicBridgeContractException.RuntimeVersion}.", StringComparison.Ordinal)
                && error.Message.EndsWith("/docs/diagnostics.md#runicbridge038", StringComparison.Ordinal),
                $"The stale composition's message is not the rebuild error:\n{error.Message}");
            Require(services.Count == 0, $"The stale composition registered {services.Count} services before failing.");
        }
        finally { context.Unload(); }
    }
}
