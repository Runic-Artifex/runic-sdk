using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Runic.Application.Bridge.Generators;

namespace Runic.Application.Tool;

internal static class BridgeInspection
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Bridge inspection requires one .NET project path.");
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        return await InspectAsync(Path.GetFullPath(args[0])).ConfigureAwait(false);
    }

    private static async Task<int> InspectAsync(string path)
    {
        var console = new Runic.CommandLine.Spectre.SpectreCommandConsole();
        using var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> {
            ["RunicBridgeInspect"] = "true", ["RunicSkipFrontendBuild"] = "true", ["DesignTimeBuild"] = "true"
        });
        Project project = await workspace.OpenProjectAsync(path).ConfigureAwait(false);
        Compilation compilation = await project.GetCompilationAsync().ConfigureAwait(false) ?? throw new InvalidOperationException("Could not inspect the application compilation.");
        var projects = new HashSet<ProjectId>();
        void Visit(Project p) { if (!projects.Add(p.Id)) return; foreach (ProjectReference reference in p.ProjectReferences) Visit(p.Solution.GetProject(reference.ProjectId)!); }
        Visit(project);
        var lowerer = new MemberBridgeLowerer(compilation);
        string? ir = lowerer.Inspect(projects.Select(id => project.Solution.GetProject(id)!.AssemblyName!));
        if (ir is null)
        {
            foreach (Diagnostic error in lowerer.Errors) await console.WriteErrorAsync((error.ToString() + "\n").AsMemory(), default).ConfigureAwait(false);
            if (lowerer.Errors.Count == 0) await console.WriteErrorAsync("RTKAB2001: The entry project requires one ApplicationBridgeContract root.\n".AsMemory(), default).ConfigureAwait(false);
            return 1;
        }
        Diagnostic[] failures = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
        if (failures.Length > 0)
        {
            foreach (Diagnostic failure in failures) await console.WriteErrorAsync((failure.ToString() + "\n").AsMemory(), default).ConfigureAwait(false);
            return 1;
        }
        string[] dependencies = projects.SelectMany(id => {
            Project p = project.Solution.GetProject(id)!;
            return p.Documents.Select(d => d.FilePath).Concat(p.AdditionalDocuments.Select(d => d.FilePath)).Append(p.FilePath);
        }).Where(p => p is not null && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(p => p!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        using JsonDocument document = JsonDocument.Parse(ir);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("ir"); document.RootElement.WriteTo(writer);
            writer.WriteStartArray("dependencies");
            foreach (string dependency in dependencies) writer.WriteStringValue(dependency);
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        return 0;
    }
}
