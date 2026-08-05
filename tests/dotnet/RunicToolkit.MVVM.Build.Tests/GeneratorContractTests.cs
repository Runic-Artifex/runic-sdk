using System;
using System.Globalization;
using System.Text;
using RunicToolkit.MVVM.Build.Generation;

namespace RunicToolkit.MVVM.Build.Tests;

internal static class GeneratorContractTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("generator canonicalizes member order and checkout-independent output", CanonicalizesMemberOrder);
        runner.Add("generator output is culture independent", OutputIsCultureIndependent);
        runner.Add("generator emits a closed dispatch contract", EmitsClosedDispatchContract);
        runner.Add("generator keys shared IDs by descriptor kind", KeysSharedIdsByKind);
        runner.Add("generator artifacts use stable portable names and encoding", UsesStableNamesAndEncoding);
    }

    private static void CanonicalizesMemberOrder()
    {
        BindingGenerationInput ordered = Input(
            new BindingGenerationMember(1, "serverName", BindingGenerationMemberKind.Property),
            new BindingGenerationMember(2, "items", BindingGenerationMemberKind.Collection),
            new BindingGenerationMember(3, "save", BindingGenerationMemberKind.Command),
            new BindingGenerationMember(4, "serverNameErrors", BindingGenerationMemberKind.Validation));
        BindingGenerationInput reversed = Input(
            new BindingGenerationMember(4, "serverNameErrors", BindingGenerationMemberKind.Validation),
            new BindingGenerationMember(3, "save", BindingGenerationMemberKind.Command),
            new BindingGenerationMember(2, "items", BindingGenerationMemberKind.Collection),
            new BindingGenerationMember(1, "serverName", BindingGenerationMemberKind.Property));

        GeneratedBindingArtifacts first = DeterministicBindingGenerator.Generate(ordered);
        GeneratedBindingArtifacts second = DeterministicBindingGenerator.Generate(reversed);

        Assert.Equal(first.Source, second.Source);
        Assert.Equal(first.Manifest, second.Manifest);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    private static void OutputIsCultureIndependent()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("tr-TR");
            GeneratedBindingArtifacts turkish = DeterministicBindingGenerator.Generate(Input(
                new BindingGenerationMember(9, "identifier", BindingGenerationMemberKind.Property)));

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            GeneratedBindingArtifacts german = DeterministicBindingGenerator.Generate(Input(
                new BindingGenerationMember(9, "identifier", BindingGenerationMemberKind.Property)));

            Assert.Equal(turkish.Source, german.Source);
            Assert.Equal(turkish.Manifest, german.Manifest);
            Assert.Equal(turkish.Fingerprint, german.Fingerprint);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static void EmitsClosedDispatchContract()
    {
        GeneratedBindingArtifacts artifacts = DeterministicBindingGenerator.Generate(Input(
            new BindingGenerationMember(1, "serverName", BindingGenerationMemberKind.Property),
            new BindingGenerationMember(3, "save", BindingGenerationMemberKind.Command)));

        Assert.Contains("TryGetMemberId", artifacts.Source);
        Assert.Contains("TryGetMemberName", artifacts.Source);
        Assert.Contains("AcceptsMutation", artifacts.Source);
        Assert.Contains("DispatchAsync", artifacts.Source);
        Assert.Contains("member.unknown", artifacts.Source);
        Assert.Contains("request.invalid", artifacts.Source);
        Assert.False(artifacts.Source.Contains("System.Reflection", StringComparison.Ordinal),
            "Generated dispatch must not rely on reflection.");
        Assert.False(artifacts.Source.Contains("dynamic", StringComparison.Ordinal),
            "Generated dispatch must not rely on the dynamic runtime binder.");
    }

    private static void UsesStableNamesAndEncoding()
    {
        GeneratedBindingArtifacts artifacts = DeterministicBindingGenerator.Generate(Input(
            new BindingGenerationMember(1, "serverName", BindingGenerationMemberKind.Property)));

        Assert.Equal(64, artifacts.Fingerprint.Length);
        Assert.Equal(artifacts.Fingerprint, artifacts.Fingerprint.ToLowerInvariant());
        Assert.True(IsLowerHex(artifacts.Fingerprint), "Fingerprint must be lowercase hexadecimal SHA-256.");
        Assert.True(artifacts.SourceHintName.EndsWith(".g.cs", StringComparison.Ordinal),
            "C# source hint names must use the .g.cs suffix.");
        Assert.True(artifacts.ManifestFileName.EndsWith(".json", StringComparison.Ordinal),
            "Manifest artifacts must use the .json suffix.");
        Assert.False(artifacts.Source.Contains('\r'), "Generated source must use LF line endings.");
        Assert.False(artifacts.Manifest.Contains('\r'), "Generated manifests must use LF line endings.");
        Assert.False(HasUtf8Bom(artifacts.Source), "Generated source must be UTF-8 without a BOM.");
        Assert.False(HasUtf8Bom(artifacts.Manifest), "Generated manifests must be UTF-8 without a BOM.");
    }

    private static void KeysSharedIdsByKind()
    {
        GeneratedBindingArtifacts artifacts = DeterministicBindingGenerator.Generate(Input(
            new BindingGenerationMember(1, "serverName", BindingGenerationMemberKind.Property, canWrite: true),
            new BindingGenerationMember(1, "serverNameErrors", BindingGenerationMemberKind.Validation)));

        Assert.Contains("switch ((memberId, memberKind))", artifacts.Source);
        Assert.Contains("case (1, \"property\")", artifacts.Source);
        Assert.Contains("case (1, \"validation\")", artifacts.Source);
        Assert.Contains("\"id\":1,\"name\":\"serverName\",\"kind\":\"property\"", artifacts.Manifest);
        Assert.Contains("\"id\":1,\"name\":\"serverNameErrors\",\"kind\":\"validation\"", artifacts.Manifest);
    }

    private static BindingGenerationInput Input(params BindingGenerationMember[] members) =>
        new("settings", "Example.Generated", "SettingsBindings", members);

    private static bool IsLowerHex(string value)
    {
        foreach (char character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUtf8Bom(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        return bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;
    }
}
