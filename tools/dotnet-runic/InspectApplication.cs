using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Application.Tool;

internal static class InspectApplication
{
    internal static async Task<int> RunAsync(
        string? requestedProject,
        string configuration,
        string artifact,
        CancellationToken cancellationToken)
    {
        if (!StringComparer.Ordinal.Equals(artifact, "manifest"))
        {
            throw new DevUsageException("RTKDEV1001", "Runic.Application inspect currently exposes only the manifest artifact.");
        }
        if (string.IsNullOrWhiteSpace(configuration))
        {
            throw new DevUsageException("RTKDEV1001", "Configuration cannot be empty.");
        }

        string project = ProjectDiscovery.Find(Environment.CurrentDirectory, requestedProject);
        string directory = Path.GetDirectoryName(project)!;
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        string inspectionRoot = Path.Combine(Path.GetTempPath(), "runic-application-inspect", Path.GetRandomFileName());
        string generatedRoot = Path.Combine(inspectionRoot, "generated");
        string intermediateRoot = Path.Combine(inspectionRoot, "obj") + Path.DirectorySeparatorChar;
        string outputRoot = Path.Combine(inspectionRoot, "bin") + Path.DirectorySeparatorChar;
        string projectAssetsFile = Path.Combine(directory, "obj", "project.assets.json");
        try
        {
            Directory.CreateDirectory(inspectionRoot);
            CommandResult build = await CommandRunner.RunAsync(
                dotnet,
                directory,
                [
                    "build", project, "--nologo", "--no-restore", $"--configuration={configuration}", "-t:Rebuild",
                    "-p:EmitCompilerGeneratedFiles=true",
                    $"-p:ProjectAssetsFile={projectAssetsFile}",
                    $"-p:CompilerGeneratedFilesOutputPath={generatedRoot}",
                    $"-p:IntermediateOutputPath={intermediateRoot}",
                    $"-p:BaseOutputPath={outputRoot}",
                ], cancellationToken).ConfigureAwait(false);
            if (build.ExitCode != 0)
            {
                throw new DevUsageException("RTKDEV1010", $"Could not generate the Runic.Application manifest for '{project}'.");
            }
            string[] sources = Directory.Exists(generatedRoot)
                ? Directory.EnumerateFiles(generatedRoot, "Runic.Application.GeneratedManifest.g.cs", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal).ToArray()
                : [];
            if (sources.Length != 1) throw new DevDevelopmentException("RTKDEV1011", "The generated runic.application manifest was not produced deterministically. Declare RunicApplicationManifest on the application assembly.");
            string marker = File.ReadLines(sources[0]).FirstOrDefault(static line => line.StartsWith("// runic.application/1: ", StringComparison.Ordinal)) ?? string.Empty;
            if (marker.Length == 0) throw new DevDevelopmentException("RTKDEV1011", "The generated runic.application manifest is malformed.");
            using JsonDocument manifest = JsonDocument.Parse(marker["// runic.application/1: ".Length..]);
            Console.WriteLine(manifest.RootElement.GetRawText());
            return Program.Success;
        }
        finally
        {
            try
            {
                if (Directory.Exists(inspectionRoot)) Directory.Delete(inspectionRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
