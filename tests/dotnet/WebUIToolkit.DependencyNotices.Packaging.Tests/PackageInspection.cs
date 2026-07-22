using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml;
using System.Xml.Linq;

namespace WebUIToolkit.DependencyNotices.Packaging.Tests;

internal sealed record PackageDependency(string Id, string VersionRange);

internal sealed record InspectedPackage(
    string Id,
    string Version,
    string Path,
    IReadOnlyList<PackageDependency> Dependencies,
    bool IsDotnetTool,
    int PublicApiCount);

internal static class PackageInspection
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private const int MaximumEntries = 20_000;
    private const string PackageRoot = "WebUIToolkit.DependencyNotices.";

    public static InspectedPackage Inspect(string packagePath, string expectedId, string expectedVersion)
    {
        FileInfo packageFile = new(packagePath);
        Assert(packageFile.Length is > 0 and <= MaximumPackageBytes,
            $"Package '{packagePath}' has an invalid size.");

        using FileStream stream = packageFile.OpenRead();
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
        Assert(archive.Entries.Count is > 0 and <= MaximumEntries,
            $"Package '{packagePath}' has an invalid entry count.");

        HashSet<string> entryNames = new(StringComparer.OrdinalIgnoreCase);
        long totalLength = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateEntryPath(entry.FullName);
            Assert(entryNames.Add(entry.FullName),
                $"Package '{packagePath}' contains a duplicate path '{entry.FullName}'.");
            Assert(entry.Length <= MaximumEntryBytes,
                $"Package entry '{entry.FullName}' exceeds the size limit.");
            totalLength = checked(totalLength + entry.Length);
            Assert(totalLength <= MaximumPackageBytes,
                $"Expanded package '{packagePath}' exceeds the size limit.");
        }

        ZipArchiveEntry[] nuspecEntries = archive.Entries
            .Where(static entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert(nuspecEntries.Length == 1, $"Package '{packagePath}' must contain exactly one nuspec.");

        XDocument nuspec = LoadXml(nuspecEntries[0]);
        XElement metadata = nuspec.Root?.Elements().FirstOrDefault(static element => element.Name.LocalName == "metadata")
            ?? throw new InvalidDataException($"Package '{packagePath}' has no nuspec metadata element.");
        string id = RequiredElement(metadata, "id");
        string version = RequiredElement(metadata, "version");
        Assert(string.Equals(id, expectedId, StringComparison.Ordinal),
            $"Expected package ID '{expectedId}', found '{id}'.");
        Assert(string.Equals(version, expectedVersion, StringComparison.Ordinal),
            $"Package '{id}' has version '{version}', expected '{expectedVersion}'.");

        bool isTool = metadata.Descendants().Any(static element =>
            element.Name.LocalName == "packageType"
            && string.Equals((string?)element.Attribute("name"), "DotnetTool", StringComparison.Ordinal));
        bool expectedTool = expectedId.EndsWith(".Tool", StringComparison.Ordinal);
        bool expectedBuild = expectedId.EndsWith(".Build", StringComparison.Ordinal);
        Assert(isTool == expectedTool,
            expectedTool ? $"Package '{id}' is not marked as a DotnetTool." : $"Library package '{id}' is incorrectly marked as a DotnetTool.");

        int publicApiCount;
        if (expectedBuild)
        {
            InspectBuildPackageAssets(archive, id);
            publicApiCount = 0;
        }
        else
        {
            string assemblyEntryName = expectedTool
                ? $"tools/net10.0/any/{expectedId}.dll"
                : $"lib/net10.0/{expectedId}.dll";
            ZipArchiveEntry assemblyEntry = archive.GetEntry(assemblyEntryName)
                ?? throw new InvalidDataException($"Package '{id}' is missing '{assemblyEntryName}'.");
            if (expectedTool)
            {
                Assert(archive.GetEntry("tools/net10.0/any/DotnetToolSettings.xml") is not null,
                    $"Tool package '{id}' is missing DotnetToolSettings.xml.");
            }

            publicApiCount = InspectManagedAssembly(assemblyEntry, expectedId);
            Assert(publicApiCount > 0, $"Package '{id}' exposes no public managed API.");
        }

        Assert(!archive.Entries.Any(static entry => IsForbiddenBuildArtifact(entry.FullName)),
            $"Package '{id}' contains a source/build-state artifact.");

        PackageDependency[] dependencies = metadata.Descendants()
            .Where(static element => element.Name.LocalName == "dependency")
            .Select(static element => new PackageDependency(
                (string?)element.Attribute("id") ?? string.Empty,
                (string?)element.Attribute("version") ?? string.Empty))
            .ToArray();
        Assert(dependencies.All(static dependency => dependency.Id.Length > 0 && dependency.VersionRange.Length > 0),
            $"Package '{id}' contains an incomplete dependency declaration.");
        foreach (PackageDependency dependency in dependencies.Where(static dependency => dependency.Id.StartsWith(PackageRoot, StringComparison.Ordinal)))
        {
            Assert(IsPinnedToRelease(dependency.VersionRange, expectedVersion),
                $"Package '{id}' dependency '{dependency.Id}' is not pinned to release '{expectedVersion}'.");
        }

        return new InspectedPackage(id, version, packagePath, dependencies, isTool, publicApiCount);
    }

    public static string FindPackage(string feed, string expectedId, string expectedVersion)
    {
        List<string> matches = [];
        foreach (string path in Directory.EnumerateFiles(feed, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            if (path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(path);
                ZipArchiveEntry? nuspecEntry = archive.Entries.SingleOrDefault(static entry =>
                    entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                if (nuspecEntry is null)
                {
                    continue;
                }

                XDocument nuspec = LoadXml(nuspecEntry);
                XElement? metadata = nuspec.Root?.Elements().FirstOrDefault(static element => element.Name.LocalName == "metadata");
                if (metadata is not null
                    && string.Equals(OptionalElement(metadata, "id"), expectedId, StringComparison.Ordinal)
                    && string.Equals(OptionalElement(metadata, "version"), expectedVersion, StringComparison.Ordinal))
                {
                    matches.Add(path);
                }
            }
            catch (InvalidDataException)
            {
                // The detailed inspection reports malformed expected packages; unrelated feed files are ignored here.
            }
        }

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new FileNotFoundException($"Feed '{feed}' has no '{expectedId}' package at version '{expectedVersion}'."),
            _ => throw new InvalidDataException($"Feed '{feed}' contains more than one '{expectedId}' package at version '{expectedVersion}'."),
        };
    }

    public static void AssertRequiredDependency(InspectedPackage package, string dependencyId)
    {
        Assert(package.Dependencies.Any(dependency => string.Equals(dependency.Id, dependencyId, StringComparison.Ordinal)),
            $"Package '{package.Id}' does not declare required dependency '{dependencyId}'.");
    }

    public static void AssertBundledToolAssembly(InspectedPackage package, string assemblyName)
    {
        Assert(package.IsDotnetTool, $"Package '{package.Id}' is not a dotnet tool.");
        using ZipArchive archive = ZipFile.OpenRead(package.Path);
        string entryName = $"tools/net10.0/any/{assemblyName}.dll";
        Assert(archive.GetEntry(entryName) is not null,
            $"Tool package '{package.Id}' does not bundle required assembly '{assemblyName}'.");
    }

    private static void InspectBuildPackageAssets(ZipArchive archive, string packageId)
    {
        string assetRoot = $"buildTransitive/{packageId}";
        ZipArchiveEntry props = archive.GetEntry(assetRoot + ".props")
            ?? throw new InvalidDataException($"Build package '{packageId}' is missing its buildTransitive props.");
        ZipArchiveEntry targets = archive.GetEntry(assetRoot + ".targets")
            ?? throw new InvalidDataException($"Build package '{packageId}' is missing its buildTransitive targets.");
        Assert(!archive.Entries.Any(static entry =>
                entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.StartsWith("tools/", StringComparison.OrdinalIgnoreCase)),
            $"Build package '{packageId}' must not ship a task assembly or tool executable.");

        XDocument propsDocument = LoadXml(props);
        XDocument targetsDocument = LoadXml(targets);
        Assert(propsDocument.Root?.Name.LocalName == "Project" && targetsDocument.Root?.Name.LocalName == "Project",
            $"Build package '{packageId}' contains malformed MSBuild assets.");
        Assert(!targetsDocument.Descendants().Any(static element => element.Name.LocalName == "UsingTask"),
            $"Build package '{packageId}' must not load an MSBuild task assembly.");

        XElement[] targetElements = targetsDocument.Descendants()
            .Where(static element => element.Name.LocalName == "Target")
            .ToArray();
        string[] expectedTargets =
        [
            "RunDependencyNoticesTool",
            "GenerateDependencyNotices",
            "VerifyDependencyNotices",
            "CollectDependencyNoticeOutputs",
        ];
        foreach (string targetName in expectedTargets)
        {
            Assert(targetElements.Any(element => string.Equals((string?)element.Attribute("Name"), targetName, StringComparison.Ordinal)),
                $"Build package '{packageId}' is missing target '{targetName}'.");
        }

        XElement exec = targetsDocument.Descendants()
            .SingleOrDefault(static element => element.Name.LocalName == "Exec")
            ?? throw new InvalidDataException($"Build package '{packageId}' must contain exactly one CLI invocation.");
        string command = (string?)exec.Attribute("Command") ?? string.Empty;
        Assert(command.Contains("$(DependencyNoticesToolPath)", StringComparison.Ordinal),
            $"Build package '{packageId}' does not invoke the explicitly supplied tool path.");
        string targetsText = targetsDocument.ToString(SaveOptions.DisableFormatting);
        string[] requiredArguments = ["--root", "--config", "--output", "--artifact-name"];
        foreach (string argument in requiredArguments)
        {
            Assert(targetsText.Contains(argument, StringComparison.Ordinal),
                $"Build package '{packageId}' does not pass required CLI argument '{argument}'.");
        }
        Assert(!command.Contains("acquire", StringComparison.OrdinalIgnoreCase)
               && !targetsText.Contains("--allow-network", StringComparison.OrdinalIgnoreCase),
            $"Build package '{packageId}' must not expose acquisition through MSBuild.");
    }

    private static int InspectManagedAssembly(ZipArchiveEntry entry, string expectedAssemblyName)
    {
        using Stream entryStream = entry.Open();
        using MemoryStream buffer = new((int)entry.Length);
        entryStream.CopyTo(buffer);
        buffer.Position = 0;
        using PEReader peReader = new(buffer, PEStreamOptions.LeaveOpen);
        Assert(peReader.HasMetadata, $"'{entry.FullName}' is not a managed assembly.");
        MetadataReader reader = peReader.GetMetadataReader();
        string assemblyName = reader.GetString(reader.GetAssemblyDefinition().Name);
        Assert(string.Equals(assemblyName, expectedAssemblyName, StringComparison.Ordinal),
            $"Assembly name '{assemblyName}' does not match package '{expectedAssemblyName}'.");

        int count = 0;
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic))
            {
                continue;
            }

            string typeNamespace = reader.GetString(type.Namespace);
            Assert(typeNamespace.StartsWith("WebUIToolkit.DependencyNotices", StringComparison.Ordinal),
                $"Public type in '{expectedAssemblyName}' escapes the WebUIToolkit.DependencyNotices namespace.");
            count++;
        }

        return count;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = 4 * 1024 * 1024,
            XmlResolver = null,
        };
        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static string RequiredElement(XElement parent, string localName) =>
        OptionalElement(parent, localName) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Nuspec metadata is missing '{localName}'.");

    private static string? OptionalElement(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName)?.Value;

    private static void ValidateEntryPath(string path)
    {
        Assert(path.Length > 0 && !path.Contains('\\'),
            $"Package entry path '{path}' is invalid.");
        Assert(!path.StartsWith('/') && !Path.IsPathRooted(path),
            $"Package entry path '{path}' is rooted.");
        Assert(!path.Split('/').Any(static segment => segment is "." or ".."),
            $"Package entry path '{path}' contains traversal.");
    }

    private static bool IsForbiddenBuildArtifact(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("packages.lock.json", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("project.assets.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsPinnedToRelease(string range, string version)
    {
        if (range.Contains('*'))
        {
            return false;
        }

        string lowerBound = range.Trim();
        if (lowerBound.StartsWith('[') || lowerBound.StartsWith('('))
        {
            lowerBound = lowerBound[1..];
        }

        int comma = lowerBound.IndexOf(',');
        if (comma >= 0)
        {
            lowerBound = lowerBound[..comma];
        }

        lowerBound = lowerBound.TrimEnd(']', ')').Trim();
        return string.Equals(lowerBound, version, StringComparison.Ordinal);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
