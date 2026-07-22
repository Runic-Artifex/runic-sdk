using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace WebUIToolkit.MVVM.Build.Tests;

internal static class BuildIntegrationTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("build package carries transitive props and targets", CarriesTransitiveAssets);
        runner.Add("build targets discover binding files as additional files", TargetsDiscoverBindings);
        runner.Add("build targets reconcile and compile validated generated source", TargetsReconcileGeneratedSource);
        runner.Add("build integration keeps generated state under intermediate output", GeneratedStateIsIntermediate);
    }

    private static void CarriesTransitiveAssets()
    {
        XDocument project = Load("WebUIToolkit.MVVM.Build.csproj");
        XElement[] packedItems = project.Descendants("None")
            .Where(static element => string.Equals((string?)element.Attribute("Pack"), "true", StringComparison.Ordinal))
            .ToArray();

        Assert.True(packedItems.Any(static element =>
                ((string?)element.Attribute("PackagePath"))?.StartsWith("buildTransitive", StringComparison.Ordinal) == true),
            "The NuGet package must carry buildTransitive assets.");
        Assert.True(packedItems.Any(static element =>
                ((string?)element.Attribute("PackagePath"))?.StartsWith("tools", StringComparison.Ordinal) == true),
            "The NuGet package must carry the compiler host under tools.");
    }

    private static void TargetsDiscoverBindings()
    {
        XDocument props = Load("buildTransitive", "WebUIToolkit.MVVM.Build.props");
        Assert.Equal(".wutmvvm", Property(props, "WebUIToolkitMvvmBindingExtension"));

        XDocument targets = Load("buildTransitive", "WebUIToolkit.MVVM.Build.targets");
        XElement item = targets.Descendants("WebUIToolkitMvvmBinding").Single();
        Assert.Equal(
            "@(None->WithMetadataValue('Extension', '$(WebUIToolkitMvvmBindingExtension)'))",
            RequiredAttribute(item, "Include"));
        Assert.Equal(null, (string?)item.Attribute("Condition"),
            "Binding discovery must filter item metadata before inclusion instead of adding broad items conditionally.");

        XElement additionalFiles = targets.Descendants("AdditionalFiles").Single();
        Assert.Equal("@(WebUIToolkitMvvmBinding)", RequiredAttribute(additionalFiles, "Include"));
    }

    private static void TargetsReconcileGeneratedSource()
    {
        XDocument targets = Load("buildTransitive", "WebUIToolkit.MVVM.Build.targets");
        XElement compile = Target(targets, "WebUIToolkitMvvmCompileBindings");
        Assert.Equal("CoreCompile", RequiredAttribute(compile, "BeforeTargets"));
        Assert.Equal(null, (string?)compile.Attribute("Inputs"),
            "The host must revalidate and reconcile every real compilation instead of trusting stale timestamps.");
        Assert.Equal(null, (string?)compile.Attribute("Outputs"),
            "Write-if-different host output, not MSBuild target skipping, preserves downstream incrementality.");
        Assert.Contains("$(DesignTimeBuild)", RequiredAttribute(compile, "Condition"));

        XElement host = compile.Descendants("Exec").Single();
        Assert.Contains("--intermediate-directory", RequiredAttribute(host, "Command"));
        Assert.Contains("--input-list", RequiredAttribute(host, "Command"));

        XElement writeInputs = targets.Descendants("WriteLinesToFile").Single();
        Assert.Equal("true", RequiredAttribute(writeInputs, "WriteOnlyWhenDifferent"));

        XElement collect = Target(targets, "WebUIToolkitMvvmCollectGeneratedBindings");
        XElement inventoryRead = collect.Descendants("ReadLinesFromFile").First(element =>
            RequiredAttribute(element, "File").Contains("GeneratedFiles", StringComparison.Ordinal));
        Assert.Contains("GeneratedFiles", RequiredAttribute(inventoryRead, "File"));
        XElement[] inventoryGuards = collect.Descendants("Error")
            .Where(static element => string.Equals((string?)element.Attribute("Code"), "WUTMVVM0903", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, inventoryGuards.Length,
            "Source and artifact inventories must each be validated before their names become paths.");
        Assert.True(inventoryGuards.Any(static element =>
                RequiredAttribute(element, "Condition").Contains("\\.g\\.cs$", StringComparison.Ordinal)),
            "Generated C# inventory names must use the closed hash-qualified suffix.");
        Assert.True(inventoryGuards.Any(static element =>
                RequiredAttribute(element, "Condition").Contains("\\.contract\\.json", StringComparison.Ordinal)),
            "Generated contract inventory names must use the closed hash-qualified suffix.");
        XElement generatedCompile = collect.Descendants("Compile").Single();
        Assert.Contains("@(_WebUIToolkitMvvmGeneratedCompile", RequiredAttribute(generatedCompile, "Include"));
        Assert.False(RequiredAttribute(generatedCompile, "Include").Contains('*', StringComparison.Ordinal),
            "Generated compilation must consume the compiler's exact inventory instead of a directory glob.");
    }

    private static void GeneratedStateIsIntermediate()
    {
        XDocument targets = Load("buildTransitive", "WebUIToolkit.MVVM.Build.targets");
        string directory = Property(targets, "WebUIToolkitMvvmBindingGeneratedDirectory");
        Assert.Equal("$(IntermediateOutputPath)WebUIToolkit.MVVM.Bindings", directory,
            "Generated binding artifacts must remain under the project's intermediate output path.");

        XElement pathValidation = Target(targets, "WebUIToolkitMvvmValidateBuildPaths");
        XElement containmentError = pathValidation.Descendants("Error").Single();
        Assert.Equal("WUTMVVM0902", RequiredAttribute(containmentError, "Code"));
        Assert.Contains("GetRelativePath", pathValidation.ToString(SaveOptions.DisableFormatting));

        Assert.True(targets.Descendants("FileWrites").Any(),
            "Generated artifacts must be registered as FileWrites for build cleanup.");
        Assert.True(targets.Descendants("ReadLinesFromFile").Any(),
            "Only compiler-inventoried generated artifacts may be collected or cleaned.");

        XElement clean = Target(targets, "WebUIToolkitMvvmCleanRemovedBindings");
        Assert.Contains("@(WebUIToolkitMvvmBinding)' == ''", RequiredAttribute(clean, "Condition"));
        Assert.Contains("WebUIToolkitMvvmValidateBuildPaths", RequiredAttribute(clean, "DependsOnTargets"));
        Assert.Contains("--clean", RequiredAttribute(clean.Descendants("Exec").Single(), "Command"));
        Assert.False(clean.Descendants("RemoveDir").Any(),
            "Last-input cleanup must use the validated compiler inventory, never delete the generated directory broadly.");
    }

    private static XDocument Load(params string[] segments)
    {
        string[] pathSegments = new string[segments.Length + 2];
        pathSegments[0] = "src";
        pathSegments[1] = "WebUIToolkit.MVVM.Build";
        Array.Copy(segments, 0, pathSegments, 2, segments.Length);
        return XDocument.Load(RepositoryPaths.Resolve(pathSegments), LoadOptions.PreserveWhitespace);
    }

    private static string Property(XDocument document, string name) =>
        (document.Root ?? throw new InvalidOperationException("Expected an MSBuild Project root."))
            .Elements("PropertyGroup")
            .Elements(name)
            .Single()
            .Value;

    private static XElement Target(XDocument document, string name) =>
        document.Descendants("Target").Single(element =>
            string.Equals((string?)element.Attribute("Name"), name, StringComparison.Ordinal));

    private static string RequiredAttribute(XElement element, string name) =>
        (string?)element.Attribute(name) ?? throw new InvalidOperationException(
            $"Expected attribute '{name}' on '{element.Name}'.");
}
