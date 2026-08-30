using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Runic.Application.Tool;

namespace Runic.Application.Tool;

internal static class MigrationApplication
{
    private const string ApplicationPackageVersion = "0.2.0";
    private const string AssetsPackageVersion = "0.1.0";
    private static readonly (string Legacy, string Current)[] Replacements =
    {
        ("RunicToolkit.Hosting.Build", "Runic.Assets"),
        ("RunicToolkit.Hosting.WebUi", "Runic.Application"),
        ("RunicToolkit.Hosting.CsWebUi", "Runic.Application.Desktop"),
        ("RunicToolkit.Hosting.CsWebUi.App", "Runic.Application.Desktop"),
        ("RunicToolkit.Hosting.CsWebUi.ApplicationBridge", "Runic.Application.Desktop"),
        ("RunicToolkit.Hosting.GenericHost", "Runic.Application.Hosting"),
        ("RunicToolkit.Hosting.Generators", "Runic.Application"),
        ("RunicToolkit.Hosting.Abstractions", "Runic.Application"),
        ("RunicToolkit.Hosting", "Runic.Application"),
        ("RunicToolkit.Desktop", "Runic.Application"),
        ("RunicToolkit.ApplicationBridge.Generators", "Runic.Application.Bridge"),
        ("RunicToolkit.ApplicationBridge", "Runic.Application.Bridge"),
        ("RunicToolkitFrontendOutputDirectory", "RunicAssetsDist"),
        ("RunicToolkitFrontendEntryPoint", "RunicAssetsEntryPoint"),
        ("RunicToolkitFrontendEmbeddedResourceName", "RunicAssetsEmbeddedResourceName"),
    };

    internal static MigrationResult Execute(string? requestedProject, bool apply, bool dryRun, bool check)
    {
        if ((apply ? 1 : 0) + (dryRun ? 1 : 0) + (check ? 1 : 0) != 1)
            throw new DevUsageException("RAPPMIG003", "Choose exactly one of --check, --dry-run, or --apply.");
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, requestedProject);
        string source = File.ReadAllText(project);
        XDocument document;
        try { document = XDocument.Parse(source, LoadOptions.PreserveWhitespace); }
        catch (System.Xml.XmlException) { throw new DevUsageException("RAPPMIG004", "The selected project is not valid XML."); }
        var output = new StringBuilder();
        foreach (XElement reference in document.Descendants().Where(static element => element.Name.LocalName == "PackageReference"))
        {
            XAttribute? identity = reference.Attribute("Include") ?? reference.Attribute("Update");
            if (identity is null) continue;
            foreach ((string legacy, string current) in Replacements)
            {
                if (!StringComparer.Ordinal.Equals(identity.Value, legacy)) continue;
                output.Append("RAPPMIG001: ").Append(legacy).Append(" -> ").Append(current).AppendLine();
                identity.Value = current;
                UpdatePackageVersion(reference, current, output);
                break;
            }
        }
        foreach (string legacyProperty in new[]
        {
            "RunicToolkitFrontendEnabled", "RunicToolkitFrontendAssetMode", "RunicToolkitFrontendManifestPath",
            "RunicToolkitFrontendNodeEnabled", "RunicToolkitFrontendWorkspaceRoot", "RunicToolkitFrontendWorkspace",
            "RunicToolkitFrontendPackageDirectory", "RunicToolkitFrontendWebRoot", "RunicToolkitFrontendContractSource",
            "RunicToolkitFrontendContractCSharpOutput", "RunicToolkitFrontendContractTypeScriptOutput",
            "RunicToolkitFrontendContractTool", "RunicToolkitFrontendDevWatchTarget",
            "RunicToolkitFrontendViteDevServerEnabled", "RunicToolkitFrontendViteDevServerEntry",
            "RunicToolkitFrontendViteConfiguration", "RunicToolkitFrontendDevServerKind",
            "RunicToolkitFrontendDevServerDocument", "RunicToolkitFrontendCompilerEnabled",
            "RunicToolkitFrontendCompilerDiagnosticsPath", "RunicToolkitFrontendCompilerHotReloadPath",
            "RunicToolkitFrontendCompilerWatchPattern", "RunicToolkitFrontendCompilerHotReloadTarget",
        })
        {
            foreach (XElement property in document.Descendants().Where(element => StringComparer.Ordinal.Equals(element.Name.LocalName, legacyProperty)).ToArray())
            {
                output.Append("RAPPMIG001: remove legacy MSBuild property ").Append(legacyProperty)
                    .Append("; use Runic.Assets and the generated Runic.Application manifest.\n");
                property.Remove();
            }
        }
        foreach (XElement property in document.Descendants().Where(static element => element.Name.LocalName is "RunicToolkitFrontendOutputDirectory" or "RunicToolkitFrontendEntryPoint" or "RunicToolkitFrontendEmbeddedResourceName"))
        {
            foreach ((string legacy, string current) in Replacements)
            {
                if (StringComparer.Ordinal.Equals(property.Name.LocalName, legacy))
                {
                    output.Append("RAPPMIG001: ").Append(legacy).Append(" -> ").Append(current).AppendLine();
                    property.Name = property.Name.Namespace + current;
                    break;
                }
            }
        }
        if (output.Length == 0)
        {
            return new MigrationResult(
                "RAPPMIG000: no legacy application references or properties were found.",
                HasChanges: false);
        }
        if (apply)
        {
            string temporary = project + ".runic-migrate.tmp";
            try
            {
                using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false))) document.Save(writer, SaveOptions.DisableFormatting);
                File.Move(temporary, project, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            output.AppendLine("RAPPMIG002: applied package-reference migration; add RunicApplicationManifest and Runic Assets properties if not already present.");
        }
        else output.AppendLine(check ? "RAPPMIG002: check found migration work." : "RAPPMIG002: dry-run only; pass --apply to update package references.");
        return new MigrationResult(output.ToString().TrimEnd(), HasChanges: true);
    }
    private static void UpdatePackageVersion(XElement reference, string packageId, StringBuilder output)
    {
        XAttribute? version = reference.Attribute("Version");
        XElement? childVersion = reference.Elements().FirstOrDefault(static element => element.Name.LocalName == "Version");
        if (version is null && childVersion is null) return;
        string expected = packageId.StartsWith("Runic.Assets", StringComparison.Ordinal)
            ? AssetsPackageVersion
            : ApplicationPackageVersion;
        if (version is not null) version.Value = expected;
        if (childVersion is not null) childVersion.Value = expected;
        output.Append("RAPPMIG001: package version -> ").Append(expected).AppendLine();
    }
}

internal sealed record MigrationResult(string Output, bool HasChanges);
