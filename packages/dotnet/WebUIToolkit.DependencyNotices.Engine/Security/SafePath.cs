using System;
using System.IO;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Engine;

public static class SafePath
{
    public static string ResolveContainedPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        string fullRoot = Path.GetFullPath(root);
        if (Path.IsPathFullyQualified(relativePath) || relativePath.StartsWith('\\') || relativePath.StartsWith('/'))
        {
            throw Unsafe(relativePath);
        }

        string normalizedSeparators = relativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        foreach (string segment in normalizedSeparators.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or ".." ||
                segment.Contains(':', StringComparison.Ordinal) ||
                segment.Contains('\0', StringComparison.Ordinal) ||
                IsReservedDeviceName(segment))
            {
                throw Unsafe(relativePath);
            }
        }

        string candidate = Path.GetFullPath(Path.Combine(fullRoot, normalizedSeparators));
        EnsureContained(fullRoot, candidate, relativePath);
        EnsureNoLinkEscape(fullRoot, candidate, relativePath);
        return candidate;
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string trimmed = segment.TrimEnd(' ', '.');
        int extension = trimmed.IndexOf('.', StringComparison.Ordinal);
        string name = (extension < 0 ? trimmed : trimmed[..extension]).TrimEnd(' ', '.').ToUpperInvariant();
        if (name is "CON" or "PRN" or "AUX" or "NUL")
        {
            return true;
        }

        return name.Length == 4 &&
            (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
            name[3] is >= '1' and <= '9';
    }

    private static void EnsureNoLinkEscape(string fullRoot, string candidate, string original)
    {
        string relative = Path.GetRelativePath(fullRoot, candidate);
        string current = fullRoot;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);

            if (!entry.Exists || (entry.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                continue;
            }

            FileSystemInfo? target = entry.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                throw Unsafe(original);
            }

            EnsureContained(fullRoot, Path.GetFullPath(target.FullName), original);
        }
    }

    private static void EnsureContained(string fullRoot, string candidate, string original)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string rootWithSeparator = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, comparison))
        {
            throw Unsafe(original);
        }
    }

    private static NoticeSecurityException Unsafe(string path)
    {
        _ = path;
        return new NoticeSecurityException(
            NoticeDiagnosticCodes.UnsafePath,
            "The declared relative path is not contained by the configured root.");
    }
}
