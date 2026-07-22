using WebUIToolkit.MVVM.Build.Generation;

namespace WebUIToolkit.MVVM.Build.Tests;

internal static class IncrementalContractTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("generation is stable for an unchanged semantic input", UnchangedInputIsStable);
        runner.Add("generation changes when a semantic member changes", SemanticChangeInvalidatesArtifacts);
    }

    private static void UnchangedInputIsStable()
    {
        BindingGenerationInput input = Input("save");
        GeneratedBindingArtifacts first = DeterministicBindingGenerator.Generate(input);
        GeneratedBindingArtifacts second = DeterministicBindingGenerator.Generate(input);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Source, second.Source);
        Assert.Equal(first.Manifest, second.Manifest);
    }

    private static void SemanticChangeInvalidatesArtifacts()
    {
        GeneratedBindingArtifacts before = DeterministicBindingGenerator.Generate(Input("save"));
        GeneratedBindingArtifacts after = DeterministicBindingGenerator.Generate(Input("saveAs"));

        Assert.False(before.Fingerprint == after.Fingerprint,
            "A semantic binding-name change must invalidate the generated artifact fingerprint.");
        Assert.False(before.Source == after.Source,
            "A semantic binding-name change must invalidate generated C# source.");
        Assert.False(before.Manifest == after.Manifest,
            "A semantic binding-name change must invalidate the generated manifest.");
    }

    private static BindingGenerationInput Input(string commandName) => new(
        "settings",
        "Example.Generated",
        "SettingsBindings",
        [
            new BindingGenerationMember(1, "serverName", BindingGenerationMemberKind.Property),
            new BindingGenerationMember(2, commandName, BindingGenerationMemberKind.Command),
        ]);
}
