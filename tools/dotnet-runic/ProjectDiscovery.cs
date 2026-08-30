using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Runic.Application.Tool;

internal static class ProjectDiscovery
{
    internal static string Find(string workingDirectory, string? requestedProject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        string root = Path.GetFullPath(workingDirectory);
        if (!string.IsNullOrWhiteSpace(requestedProject))
        {
            string selected = Path.GetFullPath(requestedProject, root);
            if (Directory.Exists(selected))
            {
                return FindSingleProject(selected);
            }

            if (!File.Exists(selected))
            {
                throw new DevUsageException(
                    "RTKDEV1002",
                    $"Project '{selected}' does not exist.");
            }

            if (!StringComparer.OrdinalIgnoreCase.Equals(Path.GetExtension(selected), ".csproj"))
            {
                throw new DevUsageException(
                    "RTKDEV1002",
                    $"Project '{selected}' must be a .csproj file.");
            }

            return selected;
        }

        return FindSingleProject(root);
    }

    private static string FindSingleProject(string directory)
    {
        string[] projects = Directory
            .EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        return projects.Length switch
        {
            1 => projects[0],
            0 => throw new DevUsageException(
                "RTKDEV1002",
                $"No .csproj was found in '{directory}'. Use --project."),
            _ => throw new DevUsageException(
                "RTKDEV1002",
                $"Multiple .csproj files were found in '{directory}'. Use --project."),
        };
    }
}
