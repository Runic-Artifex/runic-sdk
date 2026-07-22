using System;
using System.Collections.Generic;
using System.IO;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Rendering;

public static class NoticeOutputWriter
{
    public static IReadOnlyList<NoticeDiagnostic> Write(
        string outputRoot,
        IReadOnlyList<RenderedNoticeOutput> outputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        ArgumentNullException.ThrowIfNull(outputs);

        try
        {
            return WriteCore(Path.GetFullPath(outputRoot), outputs);
        }
        catch (IOException)
        {
            return new[] { Unsafe("Output could not be written safely.") };
        }
        catch (UnauthorizedAccessException)
        {
            return new[] { Unsafe("Output destination is not writable.") };
        }
        catch (ArgumentException)
        {
            return new[] { Unsafe("Output destination is invalid.") };
        }
        catch (NotSupportedException)
        {
            return new[] { Unsafe("Output destination is invalid.") };
        }
    }

    private static NoticeDiagnostic[] WriteCore(
        string root,
        IReadOnlyList<RenderedNoticeOutput> outputs)
    {
        if (HasUnsafeExistingAncestor(root))
        {
            return new[] { Unsafe("An existing output-root ancestor is a link or reparse point.") };
        }

        Directory.CreateDirectory(root);
        if (HasUnsafeExistingAncestor(root))
        {
            return new[] { Unsafe("An output-root ancestor is a link or reparse point.") };
        }

        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        List<(RenderedNoticeOutput Output, string Destination)> destinations = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        HashSet<string> resolvedDestinations = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (RenderedNoticeOutput output in outputs)
        {
            if (!RenderingUtilities.IsPortableRelativeName(output.FileName) || output.FileName.Contains('/') || output.FileName.Contains('\\'))
            {
                return new[] { Unsafe("Output file name is not a safe leaf name.") };
            }

            if (!names.Add(output.FileName))
            {
                return new[] { Unsafe("Output file name is duplicated.") };
            }

            string destination = Path.GetFullPath(Path.Combine(root, output.FileName));
            if (!destination.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                return new[] { Unsafe("Output file resolves outside the declared output root.") };
            }

            if (!resolvedDestinations.Add(destination))
            {
                return new[] { Unsafe("Output files resolve to the same destination.") };
            }

            if (IsReparsePoint(destination))
            {
                return new[] { Unsafe("Existing output file is a link or reparse point.") };
            }

            destinations.Add((output, destination));
        }

        foreach ((RenderedNoticeOutput output, string destination) in destinations)
        {
            string temporary = Path.Combine(root, ".dependency-notices-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllBytes(temporary, output.Content);
                if (HasUnsafeExistingAncestor(root) || IsReparsePoint(destination))
                {
                    return new[] { Unsafe("Output path safety changed before commit.") };
                }

                File.Move(temporary, destination, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        return Array.Empty<NoticeDiagnostic>();
    }

    private static bool HasUnsafeExistingAncestor(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static NoticeDiagnostic Unsafe(string message) => new(
        NoticeDiagnosticCodes.UnsafeOutputDestination,
        NoticeDiagnosticSeverity.Error,
        message,
        Remediation: "Choose a dedicated non-linked output directory inside the build root.");
}
