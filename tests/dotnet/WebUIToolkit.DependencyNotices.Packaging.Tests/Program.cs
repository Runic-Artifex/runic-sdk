using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace WebUIToolkit.DependencyNotices.Packaging.Tests;

internal static class Program
{
    private static readonly string[] ExpectedPackageIds =
    [
        "WebUIToolkit.DependencyNotices.Core",
        "WebUIToolkit.DependencyNotices.Engine",
        "WebUIToolkit.DependencyNotices.Rendering",
        "WebUIToolkit.DependencyNotices.Runtime",
        "WebUIToolkit.DependencyNotices.Tool",
        "WebUIToolkit.DependencyNotices.Build",
    ];

    public static int Main(string[] args)
    {
        try
        {
            CliOptions options = CliOptions.Parse(args);
            Run(options);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static void Run(CliOptions options)
    {
        string sourceSample = Path.Combine(options.RepositoryRoot, "samples", "DependencyNotices.PackageConsumer");
        string testRoot = Path.Combine(options.RepositoryRoot, "tests", "WebUIToolkit.DependencyNotices.Packaging.Tests");
        AssertContained(options.RepositoryRoot, sourceSample);
        AssertContained(options.RepositoryRoot, testRoot);
        ValidateNuGetConfiguration(Path.Combine(sourceSample, "NuGet.config"));

        Dictionary<string, InspectedPackage> packages = new(StringComparer.Ordinal);
        foreach (string id in ExpectedPackageIds)
        {
            string path = PackageInspection.FindPackage(options.Feed, id, options.Version);
            packages.Add(id, PackageInspection.Inspect(path, id, options.Version));
        }

        PackageInspection.AssertRequiredDependency(
            packages["WebUIToolkit.DependencyNotices.Engine"],
            "WebUIToolkit.DependencyNotices.Core");
        PackageInspection.AssertRequiredDependency(
            packages["WebUIToolkit.DependencyNotices.Rendering"],
            "WebUIToolkit.DependencyNotices.Core");
        string[] bundledToolAssemblies =
        [
            "WebUIToolkit.DependencyNotices.Core",
            "WebUIToolkit.DependencyNotices.Engine",
            "WebUIToolkit.DependencyNotices.Acquisition",
            "WebUIToolkit.DependencyNotices.Npm",
            "WebUIToolkit.DependencyNotices.NuGet",
            "WebUIToolkit.DependencyNotices.Policy",
            "WebUIToolkit.DependencyNotices.Rendering",
            "WebUIToolkit.DependencyNotices.Sbom",
        ];
        foreach (string assemblyName in bundledToolAssemblies)
        {
            PackageInspection.AssertBundledToolAssembly(
                packages["WebUIToolkit.DependencyNotices.Tool"],
                assemblyName);
        }

        string temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        string workingRoot = Path.Combine(temporaryRoot, $"wut-dn-package-consumer-{Environment.ProcessId}");
        AssertContained(temporaryRoot, workingRoot);
        if (Directory.Exists(workingRoot))
        {
            Directory.Delete(workingRoot, recursive: true);
        }

        Directory.CreateDirectory(workingRoot);
        CopySample(sourceSample, workingRoot);
        string packageFeed = Path.Combine(workingRoot, ".packages");
        Directory.CreateDirectory(packageFeed);
        foreach (string packagePath in Directory.EnumerateFiles(options.Feed, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            File.Copy(packagePath, Path.Combine(packageFeed, Path.GetFileName(packagePath)), overwrite: false);
        }

        string aotFeed = Path.Combine(workingRoot, ".aot-packages");
        Directory.CreateDirectory(aotFeed);
        if (options.AotSupportFeed is not null)
        {
            foreach (string packagePath in Directory.EnumerateFiles(options.AotSupportFeed, "*.nupkg", SearchOption.TopDirectoryOnly))
            {
                File.Copy(packagePath, Path.Combine(aotFeed, Path.GetFileName(packagePath)), overwrite: false);
            }
        }

        IReadOnlyDictionary<string, string> environment = CreateOfflineEnvironment(workingRoot);
        string project = Path.Combine(workingRoot, "PackageConsumer.csproj");
        string config = Path.Combine(workingRoot, "NuGet.config");
        string packagesPath = Path.Combine(workingRoot, "obj", "packages");

        ProcessRunner.Run("dotnet",
        [
            "restore", project,
            "--configfile", config,
            "--packages", packagesPath,
            "--no-http-cache",
            "--force-evaluate",
            $"-p:DependencyNoticesPackageVersion={options.Version}",
        ], workingRoot, environment);
        ProcessRunner.Run("dotnet",
        [
            "build", project,
            "--configuration", "Release",
            "--no-restore",
            $"-p:DependencyNoticesPackageVersion={options.Version}",
        ], workingRoot, environment);
        string consumerOutput = ProcessRunner.Run("dotnet",
        [
            "run", "--project", project,
            "--configuration", "Release",
            "--no-build", "--no-restore",
            $"-p:DependencyNoticesPackageVersion={options.Version}",
        ], workingRoot, environment);
        Assert(consumerOutput.Contains("packed-package consumer passed", StringComparison.Ordinal),
            "Packed library consumer did not report success.");

        string toolPath = Path.Combine(workingRoot, "obj", "tools");
        ProcessRunner.Run("dotnet",
        [
            "tool", "install", "WebUIToolkit.DependencyNotices.Tool",
            "--tool-path", toolPath,
            "--version", options.Version,
            "--configfile", config,
            "--no-cache",
        ], workingRoot, environment);
        string toolExecutable = Path.Combine(toolPath, OperatingSystem.IsWindows() ? "dependency-notices.exe" : "dependency-notices");
        string toolOutput = ProcessRunner.Run(toolExecutable, ["--help"], workingRoot, environment);
        Assert(toolOutput.Contains("dependency-notices", StringComparison.OrdinalIgnoreCase),
            "Installed dotnet tool did not produce its help output.");
        string buildOutputDirectory = Path.Combine(workingRoot, "obj", "build-notices");
        string[] buildTargetProperties =
        [
            "-property:DependencyNoticesEnabled=true",
            $"-property:DependencyNoticesToolPath={toolExecutable}",
            $"-property:DependencyNoticesRoot={workingRoot}",
            "-property:DependencyNoticesConfig=dependency-notices.input.json",
            $"-property:DependencyNoticesOutputDirectory={buildOutputDirectory}",
            "-property:DependencyNoticesArtifactName=DependencyNotices.PackageConsumer.Build",
            "-property:DependencyNoticesArtifactVersion=1.0.0-consumer",
            $"-property:DependencyNoticesPackageVersion={options.Version}",
        ];
        string generateTargetOutput = ProcessRunner.Run("dotnet",
            ["msbuild", project, "-target:GenerateDependencyNotices", "-property:DependencyNoticesMode=Generate", .. buildTargetProperties],
            workingRoot,
            environment);
        Assert(generateTargetOutput.Contains("generated", StringComparison.OrdinalIgnoreCase),
            "Packed Build target did not generate dependency notice outputs.");
        Assert(Directory.Exists(buildOutputDirectory) && Directory.EnumerateFiles(buildOutputDirectory).Count() == 4,
            "Packed Build target did not produce the complete four-file output set.");
        string generatedNotice = File.ReadAllText(Path.Combine(buildOutputDirectory, "dependency-notices.json"));
        Assert(generatedNotice.Contains("pkg:generic/webuitoolkit-text-resources-pack@1.0.0?format=json", StringComparison.Ordinal)
               && generatedNotice.Contains("1368e999508621b0430f54a376bc9a19f9ff940591b60527c54786e94cd23f24", StringComparison.Ordinal),
            "Packed Build target did not preserve the explicit external-pack attribution evidence.");
        string verifyTargetOutput = ProcessRunner.Run("dotnet",
            ["msbuild", project, "-target:VerifyDependencyNotices", "-property:DependencyNoticesMode=Verify", .. buildTargetProperties],
            workingRoot,
            environment);
        Assert(verifyTargetOutput.Contains("verified", StringComparison.OrdinalIgnoreCase),
            "Packed Build target did not verify its generated dependency notice outputs.");

        if (options.AotRid is not null)
        {
            RunAotConsumer(options, workingRoot, project, config, environment);
        }

        Assert(!File.Exists(Path.Combine(sourceSample, "packages.lock.json")),
            "The source sample must not commit a RID-sensitive packages.lock.json.");
        Console.WriteLine(
            $"PASS: {packages.Count} packages inspected; offline library restore/run, tool install/run, and Build Generate/Verify targets succeeded"
            + (options.AotRid is null ? "; Native AOT not requested." : $"; Native AOT {options.AotRid} publish/run succeeded."));
    }

    private static void RunAotConsumer(
        CliOptions options,
        string workingRoot,
        string project,
        string config,
        IReadOnlyDictionary<string, string> environment)
    {
        string publishDirectory = Path.Combine(workingRoot, "obj", "aot-publish");
        string aotPackages = Path.Combine(workingRoot, "obj", "aot-packages");
        string aotLock = Path.Combine(workingRoot, "obj", "aot.packages.lock.json");
        ProcessRunner.Run("dotnet",
        [
            "restore", project,
            "--runtime", options.AotRid!,
            "--configfile", config,
            "--packages", aotPackages,
            "--no-http-cache",
            $"-p:DependencyNoticesPackageVersion={options.Version}",
            "-p:PublishAot=true",
            "-p:RestorePackagesWithLockFile=true",
            "-p:RestoreLockedMode=false",
            $"-p:NuGetLockFilePath={aotLock}",
        ], workingRoot, environment);
        ProcessRunner.Run("dotnet",
        [
            "publish", project,
            "--configuration", "Release",
            "--runtime", options.AotRid!,
            "--no-restore",
            "--output", publishDirectory,
            $"-p:DependencyNoticesPackageVersion={options.Version}",
            "-p:PublishAot=true",
            "-p:StripSymbols=true",
            "-p:IlcTreatWarningsAsErrors=true",
            "-p:TreatWarningsAsErrors=true",
            "-p:RestorePackagesWithLockFile=true",
            "-p:RestoreLockedMode=false",
            $"-p:NuGetLockFilePath={aotLock}",
        ], workingRoot, environment);

        string executable = Path.Combine(
            publishDirectory,
            OperatingSystem.IsWindows() ? "PackageConsumer.exe" : "PackageConsumer");
        Assert(File.Exists(executable), $"Native-AOT executable '{executable}' was not produced.");
        string output = ProcessRunner.Run(executable, [], workingRoot, environment);
        Assert(output.Contains("packed-package consumer passed", StringComparison.Ordinal),
            "Native-AOT package consumer did not report success.");
        Assert(File.Exists(aotLock), "Native-AOT restore did not write its isolated obj/aot.packages.lock.json.");
    }

    private static Dictionary<string, string> CreateOfflineEnvironment(string root)
    {
        string dotnetHome = Path.Combine(root, "obj", "dotnet-home");
        string httpCache = Path.Combine(root, "obj", "http-cache");
        string globalPackages = Path.Combine(root, "obj", "global-packages");
        Directory.CreateDirectory(dotnetHome);
        Directory.CreateDirectory(httpCache);
        Directory.CreateDirectory(globalPackages);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_HOME"] = dotnetHome,
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["NUGET_HTTP_CACHE_PATH"] = httpCache,
            ["NUGET_PACKAGES"] = globalPackages,
            ["NUGET_CERT_REVOCATION_MODE"] = "offline",
        };
    }

    private static void ValidateNuGetConfiguration(string path)
    {
        XDocument document = XDocument.Load(path, LoadOptions.None);
        XElement packageSources = document.Root?.Element("packageSources")
            ?? throw new InvalidDataException("NuGet.config has no packageSources element.");
        Assert(packageSources.Elements("clear").Any(), "NuGet.config must clear inherited sources.");
        XElement[] sources = packageSources.Elements("add").ToArray();
        Assert(sources.Length == 2, "NuGet.config must contain only the package and AOT support local feeds.");
        foreach (XElement source in sources)
        {
            string value = (string?)source.Attribute("value") ?? string.Empty;
            Assert(!Uri.TryCreate(value, UriKind.Absolute, out _),
                $"NuGet.config source '{value}' must be a local relative path.");
            Assert(value is ".packages" or ".aot-packages",
                $"NuGet.config contains unexpected source '{value}'.");
        }
    }

    private static void CopySample(string source, string destination)
    {
        string[] files = ["NuGet.config", "PackageConsumer.csproj", "Program.cs", "dependency-notices.json", "dependency-notices.input.json"];
        foreach (string file in files)
        {
            File.Copy(Path.Combine(source, file), Path.Combine(destination, file), overwrite: false);
        }

        string evidenceSource = Path.Combine(source, "dependency-notices.assets");
        foreach (string evidence in Directory.EnumerateFiles(evidenceSource, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(evidenceSource, evidence);
            string target = Path.Combine(destination, "dependency-notices.assets", relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(evidence, target, overwrite: false);
        }
    }

    private static void AssertContained(string parent, string child)
    {
        string parentPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string childPath = Path.GetFullPath(child);
        string prefix = parentPath + Path.DirectorySeparatorChar;
        Assert(childPath.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            $"Path '{childPath}' escapes '{parentPath}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
