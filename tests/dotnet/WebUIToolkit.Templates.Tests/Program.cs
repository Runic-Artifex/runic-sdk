using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
string temporaryRoot = Path.Combine(
    Path.GetTempPath(),
    $"webuitoolkit-template-tests-{Guid.NewGuid():N}");
string packageDirectory = Path.Combine(temporaryRoot, "packages");
string generatedDirectory = Path.Combine(temporaryRoot, "generated");
string templateHive = Path.Combine(temporaryRoot, "template-hive");
string consumerPackages = Path.Combine(temporaryRoot, "consumer-packages");
string packageSourceRoot = Path.Combine(temporaryRoot, "package-source");
string npmPackageDirectory = Path.Combine(temporaryRoot, "npm-packages");
Directory.CreateDirectory(packageDirectory);
Directory.CreateDirectory(generatedDirectory);
Directory.CreateDirectory(npmPackageDirectory);

string templateProject = Path.Combine(
    repositoryRoot,
    "templates",
    "WebUIToolkit.Templates",
    "WebUIToolkit.Templates.csproj");
bool packageInstalled = false;
try
{
    CopyPackageSources(repositoryRoot, packageSourceRoot);
    PackConsumerPackageGraph(packageSourceRoot, packageDirectory);
    PackFrontendPackages(repositoryRoot, npmPackageDirectory);
    Run(repositoryRoot, "dotnet", "pack", templateProject, "-o", packageDirectory);
    string installedPackage = Directory
        .EnumerateFiles(packageDirectory, "WebUIToolkit.Templates.*.nupkg")
        .Single(path => !path.EndsWith(".snupkg", StringComparison.Ordinal));
    Run(
        repositoryRoot,
        "dotnet",
        "new",
        "install",
        installedPackage,
        "--force",
        "--debug:custom-hive",
        templateHive);
    packageInstalled = true;

    string[] shortNames =
    [
        "webuitoolkit-cwhtml",
        "webuitoolkit-react",
        "webuitoolkit-vue",
        "webuitoolkit-svelte",
        "webuitoolkit-angular",
    ];
    foreach (string shortName in shortNames)
    {
        string projectName = "Acceptance." +
            shortName["webuitoolkit-".Length..]
                .Replace('-', '.');
        string output = Path.Combine(generatedDirectory, shortName);
        Run(
            repositoryRoot,
            "dotnet",
            "new",
            shortName,
            "-n",
            projectName,
            "-o",
            output,
            "--debug:custom-hive",
            templateHive);
        ValidateOutput(output, projectName, shortName);
        PrepareFrontendPackages(output, shortName, npmPackageDirectory);
        string project = Directory.EnumerateFiles(output, "*.csproj").Single();
        Run(
            output,
            "dotnet",
            "restore",
            project,
            "--source",
            packageDirectory,
            "--source",
            "https://api.nuget.org/v3/index.json",
            "--packages",
            consumerPackages);
        Run(output, "dotnet", "build", project, "--configuration", "Release", "--no-restore");
        if (shortName != "webuitoolkit-cwhtml")
        {
            Run(
                output,
                "dotnet",
                "run",
                "--project",
                project,
                "--configuration",
                "Release",
                "--no-build",
                "--",
                "--smoke-test");
        }
        Run(
            output,
            "dotnet",
            "publish",
            project,
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            Path.Combine(output, "publish"));
        if (shortName != "webuitoolkit-cwhtml")
        {
            AssertProductionExcludesMock(output, shortName);
            BuildMockFrontend(output, shortName);
        }
    }

    Console.WriteLine(
        "All five WebUIToolkit templates passed isolated package restore, native exercise, production build, publish, and isolated mock-graph acceptance.");
    return 0;
}
finally
{
    if (packageInstalled)
    {
        Run(
            repositoryRoot,
            "dotnet",
            "new",
            "uninstall",
            "WebUIToolkit.Templates",
            "--debug:custom-hive",
            templateHive);
    }

    Directory.Delete(temporaryRoot, recursive: true);
}

static void PackFrontendPackages(string repositoryRoot, string output)
{
    string[] packages =
    [
        "web/packages/mvvm",
        "web/packages/mvvm-react",
        "web/packages/mvvm-vue",
        "web/packages/mvvm-svelte",
        "web/packages/mvvm-angular",
    ];
    foreach (string package in packages)
    {
        Run(
            repositoryRoot,
            "npm",
            "pack",
            Path.Combine(repositoryRoot, package),
            "--pack-destination",
            output);
    }
}

static void PrepareFrontendPackages(
    string output,
    string shortName,
    string packageDirectory)
{
    if (shortName == "webuitoolkit-cwhtml")
    {
        return;
    }

    string framework = shortName["webuitoolkit-".Length..];
    string packagePath = Path.Combine(output, "Frontend", "package.json");
    JsonNode root = JsonNode.Parse(File.ReadAllText(packagePath))
        ?? throw new InvalidOperationException("Frontend package.json is empty.");
    JsonObject dependencies = root["dependencies"]?.AsObject()
        ?? throw new InvalidOperationException("Frontend package.json has no dependencies.");
    dependencies["@webuitoolkit/mvvm"] =
        "file:" + Path.Combine(packageDirectory, "webuitoolkit-mvvm-0.1.0.tgz");
    dependencies[$"@webuitoolkit/mvvm-{framework}"] =
        "file:" + Path.Combine(
            packageDirectory,
            $"webuitoolkit-mvvm-{framework}-0.1.0.tgz");
    File.WriteAllText(
        packagePath,
        root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
        Environment.NewLine);
    Run(output, "npm", "install", "--package-lock-only", "--ignore-scripts");
}

static void CopyPackageSources(string repositoryRoot, string destinationRoot)
{
    CopyDirectory(
        Path.Combine(repositoryRoot, "src"),
        Path.Combine(destinationRoot, "src"));
    foreach (string fileName in new[]
             {
                 "Directory.Build.props",
                 "Directory.Build.targets",
                 "Directory.Packages.props",
                 "NuGet.config",
                 "global.json",
             })
    {
        string source = Path.Combine(repositoryRoot, fileName);
        if (File.Exists(source))
        {
            Directory.CreateDirectory(destinationRoot);
            File.Copy(source, Path.Combine(destinationRoot, fileName));
        }
    }
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source))
    {
        File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    foreach (string directory in Directory.EnumerateDirectories(source))
    {
        string name = Path.GetFileName(directory);
        if (name is "bin" or "obj" or "node_modules" or "dist" or ".angular")
        {
            continue;
        }

        CopyDirectory(directory, Path.Combine(destination, name));
    }
}

static void PackConsumerPackageGraph(string repositoryRoot, string packageDirectory)
{
    Dictionary<string, string> localPackages = Directory
        .EnumerateFiles(
            Path.Combine(repositoryRoot, "src"),
            "*.csproj",
            SearchOption.AllDirectories)
        .Select(path => (Path: path, Document: XDocument.Load(path)))
        .Where(entry => entry.Document
            .Descendants("WebUIToolkitShippingProject")
            .Any(element => StringComparer.OrdinalIgnoreCase.Equals(
                element.Value.Trim(),
                "true")))
        .ToDictionary(
            entry => entry.Document.Descendants("PackageId").FirstOrDefault()?.Value.Trim()
                ?? Path.GetFileNameWithoutExtension(entry.Path),
            entry => entry.Path,
            StringComparer.Ordinal);
    string[] roots =
    [
        "src/WebUIToolkit.Frontend.Sdk/WebUIToolkit.Frontend.Sdk.csproj",
        "src/WebUIToolkit.Hosting.Build/WebUIToolkit.Hosting.Build.csproj",
        "src/WebUIToolkit.Hosting.CsWebUi.App/WebUIToolkit.Hosting.CsWebUi.App.csproj",
        "src/WebUIToolkit.Hosting.CsWebUi.Mvvm/WebUIToolkit.Hosting.CsWebUi.Mvvm.csproj",
        "src/WebUIToolkit.MVVM.Html.Build/WebUIToolkit.MVVM.Html.Build.csproj",
        "src/WebUIToolkit.MVVM.Html.CommunityToolkit/WebUIToolkit.MVVM.Html.CommunityToolkit.csproj",
        "src/WebUIToolkit.MVVM.Html.Htmx.CsWebUi.App/WebUIToolkit.MVVM.Html.Htmx.CsWebUi.App.csproj",
        "src/WebUIToolkit.MVVM.Html.Htmx.Js/WebUIToolkit.MVVM.Html.Htmx.Js.csproj",
        "src/WebUIToolkit.MVVM.CommunityToolkit/WebUIToolkit.MVVM.CommunityToolkit.csproj",
    ];
    var visited = new HashSet<string>(StringComparer.Ordinal);
    foreach (string root in roots)
    {
        PackProjectAndDependencies(
            repositoryRoot,
            Path.GetFullPath(root, repositoryRoot),
            packageDirectory,
            visited,
            localPackages);
    }
}

static void PackProjectAndDependencies(
    string repositoryRoot,
    string project,
    string packageDirectory,
    HashSet<string> visited,
    IReadOnlyDictionary<string, string> localPackages)
{
    if (!visited.Add(project))
    {
        return;
    }

    XDocument document = XDocument.Load(project);
    foreach (XElement reference in document.Descendants("ProjectReference"))
    {
        string include = reference.Attribute("Include")?.Value
            ?? throw new InvalidOperationException(
                $"ProjectReference in '{project}' has no Include.");
        PackProjectAndDependencies(
            repositoryRoot,
            Path.GetFullPath(
                include.Replace('\\', Path.DirectorySeparatorChar),
                Path.GetDirectoryName(project)!),
            packageDirectory,
            visited,
            localPackages);
    }

    foreach (XElement reference in document.Descendants("PackageReference"))
    {
        string? include = reference.Attribute("Include")?.Value;
        if (include is not null &&
            localPackages.TryGetValue(include, out string? dependency))
        {
            PackProjectAndDependencies(
                repositoryRoot,
                dependency,
                packageDirectory,
                visited,
                localPackages);
        }
    }

    string lockDirectory = Path.Combine(packageDirectory, "locks");
    string restorePackages = Path.Combine(packageDirectory, "restore-packages");
    Directory.CreateDirectory(lockDirectory);
    string lockPath = Path.Combine(
        lockDirectory,
        Path.GetFileNameWithoutExtension(project) + ".packages.lock.json");
    Run(
        repositoryRoot,
        "dotnet",
        "pack",
        project,
        "--configuration",
        "Release",
        "--output",
        packageDirectory,
        $"-property:WebUIToolkitLocalPackageSource={packageDirectory}",
        $"-property:NuGetLockFilePath={lockPath}",
        $"-property:RestorePackagesPath={restorePackages}",
        "-property:RestoreLockedMode=false");
}

static void ValidateOutput(string output, string projectName, string shortName)
{
    string project = Directory.EnumerateFiles(output, "*.csproj").Single();
    if (!StringComparer.Ordinal.Equals(
            Path.GetFileNameWithoutExtension(project),
            projectName))
    {
        throw new InvalidOperationException($"{shortName} did not rename its primary project.");
    }

    string[] required =
    [
        project,
        Path.Combine(output, "Program.cs"),
        Path.Combine(output, "README.md"),
        Path.Combine(output, "package.json"),
        Path.Combine(output, "package-lock.json"),
        Path.Combine(output, ".config", "dotnet-tools.json"),
        Path.Combine(output, "Frontend", "package.json"),
    ];
    foreach (string path in required)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"{shortName} is missing generated file '{Path.GetRelativePath(output, path)}'.");
        }
    }

    using (JsonDocument package = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(output, "package.json"))))
    {
        if (package.RootElement.GetProperty("workspaces").GetArrayLength() != 1)
        {
            throw new InvalidOperationException($"{shortName} has an invalid npm workspace.");
        }
    }

    string[] forbidden =
    [
        "/home/",
        "\\home\\",
        "../../src/",
        "../../samples/",
        "<ProjectReference",
        "WebUIToolkitStarter",
    ];
    foreach (string path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
    {
        string relative = Path.GetRelativePath(output, path);
        if (relative.Contains(
                $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            continue;
        }

        string text = File.ReadAllText(path);
        string? violation = forbidden.FirstOrDefault(text.Contains);
        if (violation is not null)
        {
            throw new InvalidOperationException(
                $"{shortName} output '{relative}' contains forbidden marker '{violation}'.");
        }
    }
}

static void AssertProductionExcludesMock(string output, string shortName)
{
    string frontendOutput = Path.Combine(output, "Frontend", "dist");
    foreach (string path in Directory.EnumerateFiles(
                 frontendOutput,
                 "*",
                 SearchOption.AllDirectories)
             .Where(path =>
                 Path.GetExtension(path) is ".js" or ".css" or ".html" or ".json"))
    {
        if (File.ReadAllText(path).Contains("Step must be a whole number", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{shortName} included its development-only mock fixture in production output.");
        }
    }
}

static void BuildMockFrontend(string output, string shortName)
{
    string frontend = Path.Combine(output, "Frontend");
    Run(frontend, "npm", "run", "typecheck");
    if (shortName == "webuitoolkit-angular")
    {
        Run(
            frontend,
            "npm",
            "exec",
            "--",
            "ng",
            "build",
            "frontend",
            "--configuration",
            "mock");
        return;
    }

    Run(frontend, "npm", "run", "build", "--", "--mode", "mock");
}

static string FindRepositoryRoot(string start)
{
    DirectoryInfo? directory = new(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "WebUIToolkit.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the WebUIToolkit repository root.");
}

static void Run(string workingDirectory, string fileName, params string[] arguments)
{
    var start = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    using Process process = Process.Start(start) ??
        throw new InvalidOperationException($"Could not start '{fileName}'.");
    string standardOutput = process.StandardOutput.ReadToEnd();
    string standardError = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException(
            $"Command failed ({process.ExitCode}): {fileName} {string.Join(' ', arguments)}" +
            Environment.NewLine + standardOutput + standardError);
    }
}
