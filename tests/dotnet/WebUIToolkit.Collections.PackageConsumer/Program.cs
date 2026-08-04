using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace WebUIToolkit.Collections.PackageConsumer;

internal static class Program
{
    private const string PackageId = "WebUIToolkit.Collections";
    private const string PackageVersion = "1.0.0";

    public static int Main(string[] args)
    {
        bool runAot = args.Contains("--aot", StringComparer.Ordinal);
        bool keepTemp = args.Contains("--keep-temp", StringComparer.Ordinal);
        string? rootArgument = GetOption(args, "--repo-root");
        string temporaryRoot = Path.Combine(Path.GetTempPath(), $"webuitoolkit-collections-consumer-{Guid.NewGuid():N}");

        try
        {
            string repositoryRoot = rootArgument is null
                ? FindRepositoryRoot()
                : Path.GetFullPath(rootArgument);
            string shippingProject = Path.Combine(
                repositoryRoot,
                "src",
                "WebUIToolkit.Collections",
                "WebUIToolkit.Collections.csproj");

            Require(File.Exists(shippingProject), $"Shipping project was not found at '{shippingProject}'.");
            Directory.CreateDirectory(temporaryRoot);

            string feed = Directory.CreateDirectory(Path.Combine(temporaryRoot, "feed")).FullName;
            string consumer = Directory.CreateDirectory(Path.Combine(temporaryRoot, "consumer")).FullName;
            string packages = Directory.CreateDirectory(Path.Combine(temporaryRoot, "packages")).FullName;
            string nugetConfig = WriteNuGetConfig(temporaryRoot, feed, packages);
            string aotNugetConfig = WriteAotNuGetConfig(temporaryRoot, feed, packages);

            RunDotNet(repositoryRoot, null, "restore", shippingProject);
            RunDotNet(repositoryRoot, null, "build", shippingProject, "-c", "Release", "--no-restore");
            RunDotNet(
                repositoryRoot,
                null,
                "pack",
                shippingProject,
                "--configuration",
                "Release",
                "--output",
                feed,
                "--no-build",
                "--no-restore",
                $"-p:PackageVersion={PackageVersion}",
                "-p:ContinuousIntegrationBuild=true");

            string packagePath = Path.Combine(feed, $"{PackageId}.{PackageVersion}.nupkg");
            ValidatePackage(packagePath);
            WriteConsumerProject(consumer);
            WriteConsumerProgram(consumer);

            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NUGET_PACKAGES"] = packages,
                ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            };

            string consumerProject = Path.Combine(consumer, "PackageConsumer.csproj");
            RunDotNet(
                consumer,
                environment,
                "restore",
                consumerProject,
                "--configfile",
                nugetConfig);
            RunDotNet(consumer, environment, "build", consumerProject, "-c", "Release", "--no-restore");
            CommandResult managed = RunDotNet(
                consumer,
                environment,
                "run",
                "--project",
                consumerProject,
                "-c",
                "Release",
                "--no-build");
            Require(
                managed.Output.Contains("Package consumer API compatibility: PASS", StringComparison.Ordinal),
                "Managed package consumer did not emit its success marker.");

            if (runAot)
            {
                RunNativeAot(consumer, consumerProject, aotNugetConfig, environment);
            }

            Console.WriteLine(
                runAot
                    ? "WebUIToolkit.Collections packed managed/native consumer: PASS"
                    : "WebUIToolkit.Collections packed managed consumer: PASS");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"WebUIToolkit.Collections packed consumer: FAIL: {exception.Message}");
            return 1;
        }
        finally
        {
            if (keepTemp)
            {
                Console.WriteLine($"Temporary consumer retained at: {temporaryRoot}");
            }
            else
            {
                DeleteTemporaryDirectory(temporaryRoot);
            }
        }
    }

    private static void RunNativeAot(
        string consumer,
        string consumerProject,
        string nugetConfig,
        IReadOnlyDictionary<string, string> environment)
    {
        string runtimeIdentifier = RuntimeInformation.RuntimeIdentifier;
        string publishDirectory = Path.Combine(consumer, "native");
        RunDotNet(
            consumer,
            environment,
            "publish",
            consumerProject,
            "-c",
            "Release",
            "-r",
            runtimeIdentifier,
            "--configfile",
            nugetConfig,
            "-p:PublishAot=true",
            "-p:PublishTrimmed=true",
            "-p:InvariantGlobalization=true",
            "--output",
            publishDirectory);

        string executable = Path.Combine(
            publishDirectory,
            OperatingSystem.IsWindows() ? "PackageConsumer.exe" : "PackageConsumer");
        Require(File.Exists(executable), $"Native executable was not found at '{executable}'.");
        CommandResult native = RunProcess(executable, [], consumer, environment);
        Require(
            native.Output.Contains("Package consumer API compatibility: PASS", StringComparison.Ordinal),
            "Native package consumer did not emit its success marker.");
    }

    private static string FindRepositoryRoot()
    {
        foreach (string startingDirectory in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var current = new DirectoryInfo(startingDirectory);
            while (current is not null)
            {
                string candidate = Path.Combine(
                    current.FullName,
                    "src",
                    "WebUIToolkit.Collections",
                    "WebUIToolkit.Collections.csproj");
                if (File.Exists(candidate))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. Pass it explicitly with --repo-root <path>.");
    }

    private static string WriteNuGetConfig(string temporaryRoot, string feed, string packages)
    {
        string path = Path.Combine(temporaryRoot, "NuGet.config");
        var configuration = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement(
                        "add",
                        new XAttribute("key", "temporary-local-feed"),
                        new XAttribute("value", feed))),
                new XElement(
                    "config",
                    new XElement(
                        "add",
                        new XAttribute("key", "globalPackagesFolder"),
                        new XAttribute("value", packages)))));
        configuration.Save(path);
        return path;
    }

    private static string WriteAotNuGetConfig(string temporaryRoot, string feed, string packages)
    {
        string path = Path.Combine(temporaryRoot, "NuGet.Aot.config");
        var configuration = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    new XElement(
                        "add",
                        new XAttribute("key", "temporary-local-feed"),
                        new XAttribute("value", feed)),
                    new XElement(
                        "add",
                        new XAttribute("key", "nuget.org"),
                        new XAttribute("value", "https://api.nuget.org/v3/index.json"),
                        new XAttribute("protocolVersion", "3"))),
                new XElement(
                    "packageSourceMapping",
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "temporary-local-feed"),
                        new XElement("package", new XAttribute("pattern", PackageId))),
                    new XElement(
                        "packageSource",
                        new XAttribute("key", "nuget.org"),
                        new XElement("package", new XAttribute("pattern", "Microsoft.*")),
                        new XElement("package", new XAttribute("pattern", "runtime.*")))),
                new XElement(
                    "config",
                    new XElement(
                        "add",
                        new XAttribute("key", "globalPackagesFolder"),
                        new XAttribute("value", packages)))));
        configuration.Save(path);
        return path;
    }

    private static void ValidatePackage(string packagePath)
    {
        Require(File.Exists(packagePath), $"Packed artifact was not found at '{packagePath}'.");
        using ZipArchive package = ZipFile.OpenRead(packagePath);
        var entries = package.Entries.Select(static entry => entry.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Require(entries.Contains("lib/net10.0/WebUIToolkit.Collections.dll"), "Package is missing its net10.0 assembly.");
        Require(entries.Contains("lib/net10.0/WebUIToolkit.Collections.xml"), "Package is missing its XML documentation.");
        Require(entries.Contains("README.md"), "Package is missing its declared readme.");
        Require(
            !entries.Any(static name => name.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)),
            "BCL-only package unexpectedly contains RID-specific assets.");

        ZipArchiveEntry nuspecEntry = package.Entries.Single(
            static entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using Stream nuspecStream = nuspecEntry.Open();
        XDocument nuspec = XDocument.Load(nuspecStream);
        XElement metadata = nuspec.Descendants().Single(static element => element.Name.LocalName == "metadata");

        Require(ReadMetadata(metadata, "id") == PackageId, "Package id is incorrect.");
        Require(ReadMetadata(metadata, "version") == PackageVersion, "Package version is incorrect.");
        Require(ReadMetadata(metadata, "readme") == "README.md", "Package readme metadata is incorrect.");
        Require(
            ReadMetadata(metadata, "description").Contains("observable collection", StringComparison.OrdinalIgnoreCase),
            "Package description does not identify its observable-collection purpose.");

        XElement? dependencies = metadata.Elements().SingleOrDefault(
            static element => element.Name.LocalName == "dependencies");
        Require(
            dependencies is null || !dependencies.Descendants().Any(static element => element.Name.LocalName == "dependency"),
            "BCL-only package unexpectedly declares a package dependency.");
    }

    private static string ReadMetadata(XElement metadata, string name) =>
        metadata.Elements().Single(element => element.Name.LocalName == name).Value;

    private static void WriteConsumerProject(string consumer)
    {
        const string project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <AssemblyName>PackageConsumer</AssemblyName>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="WebUIToolkit.Collections" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """;
        File.WriteAllText(Path.Combine(consumer, "PackageConsumer.csproj"), project, Encoding.UTF8);
    }

    private static void WriteConsumerProgram(string consumer)
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Collections.Specialized;
            using System.ComponentModel;
            using System.Linq;
            using WebUIToolkit.Collections;

            internal static class Program
            {
                public static int Main()
                {
                    try
                    {
                        ExerciseConstructorsAndOptions();
                        ExerciseRangesAndNotifications();
                        ExerciseReconciliation();
                        Console.WriteLine("Package consumer API compatibility: PASS");
                        return 0;
                    }
                    catch (Exception exception)
                    {
                        Console.Error.WriteLine($"Package consumer API compatibility: FAIL: {exception.Message}");
                        return 1;
                    }
                }

                private static void ExerciseConstructorsAndOptions()
                {
                    var empty = new ObservableRangeCollection<int>();
                    var items = new ObservableRangeCollection<int>(new[] { 1, 2 });
                    var options = new ObservableRangeCollectionOptions
                    {
                        RangeNotifications = RangeNotificationMode.Reset,
                    };
                    var configured = new ObservableRangeCollection<int>(options);
                    var configuredItems = new ObservableRangeCollection<int>(new[] { 3, 4 }, options);

                    Require(empty.Count == 0 && configured.Count == 0, "Empty constructors failed.");
                    Require(items.SequenceEqual(new[] { 1, 2 }), "Items constructor failed.");
                    Require(configuredItems.SequenceEqual(new[] { 3, 4 }), "Items/options constructor failed.");
                    Require(new ObservableRangeCollectionOptions().RangeNotifications == RangeNotificationMode.Range, "Range default changed.");
                    Require((int)RangeNotificationMode.Range == 0 && (int)RangeNotificationMode.Reset == 1, "Range enum changed.");
                    Require((int)UpdateNotificationMode.Auto == 0, "Auto enum changed.");
                    Require((int)UpdateNotificationMode.Granular == 1, "Granular enum changed.");
                    Require((int)UpdateNotificationMode.Reset == 2, "Reset enum changed.");

                    var updateOptions = new CollectionUpdateOptions
                    {
                        Notifications = UpdateNotificationMode.Granular,
                        MaxGranularEvents = 7,
                        ResetRatioMinimumCount = 8,
                        ResetChangeRatio = 0.25,
                    };
                    Require(updateOptions.Notifications == UpdateNotificationMode.Granular, "Notifications property failed.");
                    Require(updateOptions.MaxGranularEvents == 7, "MaxGranularEvents property failed.");
                    Require(updateOptions.ResetRatioMinimumCount == 8, "ResetRatioMinimumCount property failed.");
                    Require(updateOptions.ResetChangeRatio == 0.25, "ResetChangeRatio property failed.");
                }

                private static void ExerciseRangesAndNotifications()
                {
                    var collection = new ObservableRangeCollection<int>();
                    var trace = new List<string>();
                    ((INotifyPropertyChanged)collection).PropertyChanged += (_, args) => trace.Add($"P:{args.PropertyName}");
                    collection.CollectionChanged += (_, args) => trace.Add($"C:{args.Action}");

                    collection.AddRange(new[] { 1, 2, 3 });
                    Require(trace.SequenceEqual(new[] { "P:Count", "P:Item[]", "C:Add" }), "AddRange event order changed.");
                    trace.Clear();

                    collection.InsertRange(1, new[] { 8, 9 });
                    collection.RemoveRange(1, 2);
                    collection.ReplaceRange(1, 1, new[] { 4 });
                    collection.ReplaceRange(1, 1, new[] { 5, 6 });
                    collection.MoveRange(1, 2, 2);
                    Require(collection.SequenceEqual(new[] { 1, 3, 5, 6 }), "Range mutation result changed.");

                    int[] snapshot = collection.ToSnapshot();
                    collection.Clear();
                    Require(snapshot.SequenceEqual(new[] { 1, 3, 5, 6 }), "Snapshot was not isolated.");
                }

                private static void ExerciseReconciliation()
                {
                    var first = new Item(1, "old-one");
                    var second = new Item(2, "old-two");
                    var collection = new ObservableRangeCollection<Item>(new[] { first, second });
                    CollectionUpdateResult keyed = collection.UpdateTo(
                        new[] { new Item(2, "new-two"), new Item(3, "new-three"), new Item(1, "new-one") },
                        static item => item.Key,
                        keyComparer: EqualityComparer<int>.Default,
                        resolveMatch: static (existing, _) => existing,
                        options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Granular });

                    Require(ReferenceEquals(second, collection[0]), "Keyed identity was not preserved.");
                    Require(collection[1].Key == 3, "Keyed add failed.");
                    Require(ReferenceEquals(first, collection[2]), "Keyed moved identity was not preserved.");
                    Require(keyed.Changed && !keyed.UsedReset, "Keyed result flags changed.");
                    Require(keyed.Added == 1 && keyed.Removed == 0, "Keyed result counts changed.");
                    Require(keyed.Moved >= 1 && keyed.Replaced == 0 && keyed.NotificationCount >= 1, "Keyed operation counts changed.");

                    CollectionUpdateResult comparer = collection.UpdateTo(
                        new[] { new Item(1, "incoming-one"), new Item(2, "incoming-two") },
                        new ItemComparer(),
                        resolveMatch: static (existing, _) => existing,
                        options: new CollectionUpdateOptions { Notifications = UpdateNotificationMode.Reset });
                    Require(comparer.Changed && comparer.UsedReset && comparer.NotificationCount == 1, "Comparer reset result changed.");
                    Require(collection.Count == 2 && ReferenceEquals(first, collection[0]), "Comparer identity was not preserved.");

                    var positional = new CollectionUpdateResult(1, 2, 3, 4, 5, true);
                    positional.Deconstruct(out int added, out int removed, out int moved, out int replaced, out int notifications, out bool reset);
                    Require(
                        positional.Changed && added == 1 && removed == 2 && moved == 3 && replaced == 4 && notifications == 5 && reset,
                        "CollectionUpdateResult positional API changed.");
                }

                private static void Require(bool condition, string message)
                {
                    if (!condition)
                    {
                        throw new InvalidOperationException(message);
                    }
                }

                private sealed class Item(int key, string value)
                {
                    public int Key { get; } = key;
                    public string Value { get; } = value;
                }

                private sealed class ItemComparer : IEqualityComparer<Item>
                {
                    public bool Equals(Item? x, Item? y) => x?.Key == y?.Key;
                    public int GetHashCode(Item obj) => obj.Key;
                }
            }
            """;
        File.WriteAllText(Path.Combine(consumer, "Program.cs"), source, Encoding.UTF8);
    }

    private static CommandResult RunDotNet(
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        params string[] arguments) =>
        RunProcess(GetDotNetHost(), arguments, workingDirectory, environment);

    private static CommandResult RunProcess(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{executable}'.");
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) => AppendLine(standardOutput, eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => AppendLine(standardError, eventArgs.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        string output = standardOutput.ToString();
        string error = standardError.ToString();
        Console.Write(output);
        Console.Error.Write(error);
        if (process.ExitCode != 0)
        {
            string renderedArguments = string.Join(" ", arguments.Select(QuoteArgument));
            throw new InvalidOperationException(
                $"'{executable} {renderedArguments}' exited with code {process.ExitCode}.{Environment.NewLine}{error}");
        }

        return new CommandResult(output, error);
    }

    private static void AppendLine(StringBuilder builder, string? value)
    {
        if (value is not null)
        {
            builder.AppendLine(value);
        }
    }

    private static string GetDotNetHost() =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    private static string? GetOption(string[] args, string option)
    {
        int index = Array.IndexOf(args, option);
        if (index < 0)
        {
            return null;
        }

        Require(index + 1 < args.Length, $"{option} requires a value.");
        return args[index + 1];
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception exception) when (
                attempt < 3 && exception is (IOException or UnauthorizedAccessException))
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }

        throw new IOException($"Temporary consumer directory '{path}' could not be removed.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed record CommandResult(string Output, string Error);
}
