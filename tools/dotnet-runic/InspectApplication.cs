using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RunicToolkit.DotNet.RunicToolkit;

internal static class InspectApplication
{
    private static readonly string[] Artifacts =
        ["manifest", "diagnostics", "hot-reload", "generated"];

    internal static async Task<int> RunAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Skip(1).Any(static argument => argument is "--help" or "-h" or "help"))
        {
            WriteHelp();
            return Program.Success;
        }

        (string? requestedProject, string configuration, string artifact) = Parse(arguments);
        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, requestedProject);
        string directory = Path.GetDirectoryName(project)!;
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : "dotnet";
        string[] properties =
        [
            "RunicToolkitFrontendCompilerEnabled",
            "RunicToolkitFrontendCompilerManifestPath",
            "RunicToolkitFrontendCompilerDiagnosticsPath",
            "RunicToolkitFrontendCompilerHotReloadPath",
            "RunicToolkitFrontendCompilerGeneratedFilesPath",
            "RunicToolkitFrontendCompilerGeneratedPattern",
        ];
        CommandResult evaluation = await CommandRunner.RunAsync(
            dotnet,
            directory,
            [
                "msbuild",
                project,
                "-nologo",
                $"-property:Configuration={configuration}",
                $"-getProperty:{string.Join(',', properties)}",
            ],
            cancellationToken).ConfigureAwait(false);
        if (evaluation.ExitCode != 0)
        {
            throw new DevUsageException(
                "RTKDEV1010",
                $"Could not evaluate frontend compiler artifacts for '{project}'.");
        }

        using JsonDocument response = JsonDocument.Parse(evaluation.StandardOutput);
        JsonElement values = response.RootElement.GetProperty("Properties");
        string Value(string name) => values.GetProperty(name).GetString() ?? string.Empty;
        bool active = bool.TryParse(
            Value("RunicToolkitFrontendCompilerEnabled"),
            out bool parsedActive) && parsedActive;
        if (!active)
        {
            throw new DevUsageException(
                "RTKDEV1010",
                "The selected project has no active frontend compiler integration.");
        }

        if (artifact == "generated")
        {
            string configuredGeneratedRoot = Value("RunicToolkitFrontendCompilerGeneratedFilesPath");
            if (string.IsNullOrWhiteSpace(configuredGeneratedRoot))
            {
                throw new DevUsageException(
                    "RTKDEV1010",
                    "The compiler integration does not expose RunicToolkitFrontendCompilerGeneratedFilesPath.");
            }
            string generatedRoot = Path.GetFullPath(configuredGeneratedRoot, directory);
            string pattern = Value("RunicToolkitFrontendCompilerGeneratedPattern") is { Length: > 0 } configuredPattern
                ? configuredPattern
                : "*.g.cs";
            string[] files = Directory.Exists(generatedRoot)
                ? Directory.EnumerateFiles(
                        generatedRoot,
                        pattern,
                        SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal)
                    .ToArray()
                : [];
            if (files.Length == 0)
            {
                throw MissingArtifact("generated C#", generatedRoot);
            }

            foreach (string file in files)
            {
                Console.WriteLine($"// {Path.GetRelativePath(generatedRoot, file).Replace('\\', '/')}");
                Console.Write(File.ReadAllText(file));
                if (!File.ReadAllText(file).EndsWith('\n'))
                {
                    Console.WriteLine();
                }
            }

            return Program.Success;
        }

        string property = artifact switch
        {
            "manifest" => "RunicToolkitFrontendCompilerManifestPath",
            "diagnostics" => "RunicToolkitFrontendCompilerDiagnosticsPath",
            "hot-reload" => "RunicToolkitFrontendCompilerHotReloadPath",
            _ => throw new InvalidOperationException(),
        };
        string configuredPath = Value(property);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new DevUsageException(
                "RTKDEV1010",
                $"The compiler integration does not expose {property}.");
        }

        string path = Path.GetFullPath(configuredPath, directory);
        if (!File.Exists(path))
        {
            throw MissingArtifact(artifact, path);
        }

        using JsonDocument artifactDocument = JsonDocument.Parse(File.ReadAllBytes(path));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            artifactDocument.RootElement.WriteTo(writer);
        }
        Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
        return Program.Success;
    }

    private static (string? Project, string Configuration, string Artifact) Parse(
        string[] arguments)
    {
        string? project = null;
        string configuration = "Debug";
        string artifact = "manifest";
        for (int index = 1; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (argument is "--configuration" or "--artifact")
            {
                if (++index == arguments.Length)
                {
                    throw new DevUsageException("RTKDEV1001", $"{argument} requires a value.");
                }

                if (argument == "--configuration")
                {
                    configuration = arguments[index];
                }
                else
                {
                    artifact = arguments[index];
                }
            }
            else if (argument.StartsWith('-'))
            {
                throw new DevUsageException("RTKDEV1001", $"Unknown option '{argument}'.");
            }
            else if (project is null)
            {
                project = argument;
            }
            else
            {
                throw new DevUsageException("RTKDEV1001", "Specify at most one project.");
            }
        }

        if (!Artifacts.Contains(artifact, StringComparer.Ordinal))
        {
            throw new DevUsageException(
                "RTKDEV1001",
                $"Unknown inspection artifact '{artifact}'.");
        }

        return (project, configuration, artifact);
    }

    private static DevDevelopmentException MissingArtifact(string name, string path) =>
        new(
            "RTKDEV1011",
            $"The frontend compiler {name} artifact '{path}' does not exist. Build the project first.");

    private static void WriteHelp() => Console.WriteLine(
        """
        Usage:
          dotnet runic-toolkit inspect [PROJECT] [options]

        Options:
          --configuration NAME    Build configuration (default: Debug).
          --artifact NAME         manifest, diagnostics, hot-reload, or generated.
          -h, --help              Show this help.
        """);
}
