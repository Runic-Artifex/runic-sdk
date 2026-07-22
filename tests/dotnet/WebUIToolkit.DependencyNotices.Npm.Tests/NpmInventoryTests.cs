using System;
using System.IO;
using System.Linq;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Npm.Tests;

internal static class NpmInventoryTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("npm runtime graph is deterministic", RuntimeGraphIsDeterministic);
        tests.Add("npm development profile includes dev dependency", DevelopmentProfileIncludesDevDependency);
        tests.Add("npm scopes and directness are classified", ScopesAndDirectnessAreClassified);
        tests.Add("npm scoped identity is canonical", ScopedIdentityIsCanonical);
        tests.Add("npm evidence candidates are preserved", EvidenceCandidatesArePreserved);
        tests.Add("npm workspace selection is explicit", WorkspaceSelectionIsExplicit);
        tests.Add("npm v2 aliases use resolved package identity", V2AliasUsesResolvedIdentity);
        tests.Add("npm shrinkwrap is supported", ShrinkwrapIsSupported);
        tests.Add("npm unsupported lock version is diagnosed", UnsupportedVersionIsDiagnosed);
        tests.Add("npm missing restore is diagnosed", MissingRestoreIsDiagnosed);
        tests.Add("npm lock drift is diagnosed", LockDriftIsDiagnosed);
    }

    private static void RuntimeGraphIsDeterministic()
    {
        InventoryResult first = Scan("basic", NpmInventoryProfile.Runtime);
        InventoryResult second = Scan("basic", NpmInventoryProfile.Runtime);
        Assert.Equal(5, first.Components.Count);
        Assert.Equal(
            string.Join('\n', first.Components.Select(static component => component.PackageUrl.CanonicalValue)),
            string.Join('\n', second.Components.Select(static component => component.PackageUrl.CanonicalValue)));
        Assert.True(first.Components.Select(static component => component.PackageUrl.CanonicalValue)
            .SequenceEqual(first.Components.Select(static component => component.PackageUrl.CanonicalValue)
                .Order(StringComparer.Ordinal)));
    }

    private static void DevelopmentProfileIncludesDevDependency()
    {
        InventoryResult result = Scan("basic", NpmInventoryProfile.Development);
        InventoryComponent development = Assert.Single(result.Components.Where(static component =>
            component.Name == "dev-package"));
        Assert.Equal(DependencyScope.Development, development.Scope);
        Assert.True(development.IsDirect);
        Assert.Equal(6, result.Components.Count);
    }

    private static void ScopesAndDirectnessAreClassified()
    {
        InventoryResult result = Scan("basic", NpmInventoryProfile.Runtime);
        Assert.Equal(DependencyScope.Runtime, Find(result, "alpha").Scope);
        Assert.Equal(DependencyScope.Optional, Find(result, "optional-package").Scope);
        Assert.Equal(DependencyScope.Peer, Find(result, "peer-package").Scope);
        Assert.Equal(DependencyScope.Bundled, Find(result, "bundled-child").Scope);
        Assert.True(!Find(result, "@scope/transitive").IsDirect);
    }

    private static void ScopedIdentityIsCanonical()
    {
        InventoryComponent component = Find(Scan("basic", NpmInventoryProfile.Runtime), "@scope/transitive");
        Assert.Equal("pkg:npm/%40scope/transitive@2.0.0", component.PackageUrl.CanonicalValue);
        Assert.True(component.Integrity?.StartsWith("sha512-", StringComparison.Ordinal) == true);
    }

    private static void EvidenceCandidatesArePreserved()
    {
        InventoryResult result = Scan("basic", NpmInventoryProfile.Runtime);
        InventoryComponent alpha = Find(result, "alpha");
        Assert.Equal(3, alpha.Evidence.Count);
        Assert.True(alpha.Evidence.All(static evidence => evidence.Sha256.Length == 64));
        Assert.True(result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == NoticeDiagnosticCodes.MultipleEvidenceCandidates &&
            diagnostic.Severity == NoticeDiagnosticSeverity.Warning));
    }

    private static void WorkspaceSelectionIsExplicit()
    {
        InventoryResult result = ScanWorkspace("workspace", "packages/app");
        Assert.Equal(1, result.Components.Count);
        Assert.Equal("workspace-dependency", result.Components[0].Name);

        InventoryResult missing = ScanWorkspace("workspace", "packages/missing");
        Assert.True(missing.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == NoticeDiagnosticCodes.AmbiguousTarget));
    }

    private static void ShrinkwrapIsSupported()
    {
        string temporary = MaterializeFixture("basic");
        try
        {
            File.Move(Path.Combine(temporary, "package-lock.json"), Path.Combine(temporary, "npm-shrinkwrap.json"));
            InventoryResult result = NpmInventoryScanner.Scan(new NpmInventoryOptions(
                temporary, "npm-shrinkwrap.json", ".", NpmInventoryProfile.Runtime));
            Assert.Equal(5, result.Components.Count);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void V2AliasUsesResolvedIdentity()
    {
        InventoryResult result = Scan("alias-v2", NpmInventoryProfile.Runtime);
        InventoryComponent component = Assert.Single(result.Components);
        Assert.Equal("actual-package", component.Name);
        Assert.Equal("pkg:npm/actual-package@7.1.0", component.PackageUrl.CanonicalValue);
        Assert.True(component.IsDirect);
    }

    private static void UnsupportedVersionIsDiagnosed()
    {
        InventoryResult result = Scan("unsupported", NpmInventoryProfile.Runtime);
        Assert.Equal(NoticeDiagnosticCodes.UnsupportedInventoryFormat, Assert.Single(result.Diagnostics).Code);
    }

    private static void MissingRestoreIsDiagnosed()
    {
        InventoryResult result = Scan("missing-node-modules", NpmInventoryProfile.Runtime);
        Assert.True(result.Diagnostics.Any(static diagnostic =>
            diagnostic.Code == NoticeDiagnosticCodes.MissingEvidence));
        Assert.Equal(0, result.Components.Count);
    }

    private static void LockDriftIsDiagnosed()
    {
        InventoryResult result = Scan("drift", NpmInventoryProfile.Runtime);
        Assert.Equal(NoticeDiagnosticCodes.LockFileDrift, Assert.Single(result.Diagnostics).Code);
    }

    internal static InventoryResult Scan(string fixture, NpmInventoryProfile profile)
    {
        string temporary = MaterializeFixture(fixture);
        try
        {
            return NpmInventoryScanner.Scan(new NpmInventoryOptions(
                temporary, "package-lock.json", ".", profile));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    internal static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "dependency-notices.html")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Repository root not found.");
        }

        return Path.Combine(directory.FullName, "spec", "dependency-notices", "fixtures", "npm", name);
    }

    private static InventoryComponent Find(InventoryResult result, string name) =>
        Assert.Single(result.Components.Where(component => component.Name == name));

    private static InventoryResult ScanWorkspace(string fixture, string workspace)
    {
        string temporary = MaterializeFixture(fixture);
        try
        {
            return NpmInventoryScanner.Scan(new NpmInventoryOptions(
                temporary, "package-lock.json", workspace, NpmInventoryProfile.Runtime));
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string MaterializeFixture(string fixture)
    {
        string source = Fixture(fixture);
        string destination = Path.Combine(Path.GetTempPath(), "wut-npm-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(source, destination, "installed");

        string installed = Path.Combine(source, "installed");
        if (Directory.Exists(installed))
        {
            foreach (string file in Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(installed, file);
                string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                for (int index = 0; index < segments.Length; index++)
                {
                    if (segments[index] == "_modules_")
                    {
                        segments[index] = "node_modules";
                    }
                }

                string target = Path.Combine(destination, "node_modules", Path.Combine(segments));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }

        return destination;
    }

    private static void CopyDirectory(string source, string destination, string? excludedTopLevel = null)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            if (!IsExcluded(relative, excludedTopLevel))
            {
                Directory.CreateDirectory(Path.Combine(destination, relative));
            }
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (!IsExcluded(relative, excludedTopLevel))
            {
                string target = Path.Combine(destination, relative);
                File.Copy(file, target);
            }
        }
    }

    private static bool IsExcluded(string relative, string? excludedTopLevel) =>
        IsWithin(relative, "node_modules") ||
        (excludedTopLevel is not null && IsWithin(relative, excludedTopLevel));

    private static bool IsWithin(string relative, string topLevel) =>
        relative.Equals(topLevel, StringComparison.Ordinal) ||
        relative.StartsWith(topLevel + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
