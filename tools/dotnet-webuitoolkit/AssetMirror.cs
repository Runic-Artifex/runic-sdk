using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WebUIToolkit.DotNet.WebUIToolkit;

internal sealed class AssetMirror
{
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;
    private HashSet<string> _knownFiles;

    internal AssetMirror(string sourceRoot, string destinationRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _destinationRoot = Path.GetFullPath(destinationRoot);
        _knownFiles = CollectRelativeFiles(_sourceRoot);
    }

    internal int Synchronize()
    {
        if (StringComparer.OrdinalIgnoreCase.Equals(
            Path.TrimEndingDirectorySeparator(_sourceRoot),
            Path.TrimEndingDirectorySeparator(_destinationRoot)))
        {
            return 0;
        }

        HashSet<string> currentFiles = CollectRelativeFiles(_sourceRoot);
        Directory.CreateDirectory(_destinationRoot);
        int changes = 0;
        foreach (string removed in _knownFiles.Except(currentFiles, StringComparer.Ordinal))
        {
            string destination = ContainedPath(_destinationRoot, removed);
            if (File.Exists(destination))
            {
                File.Delete(destination);
                changes++;
            }
        }

        foreach (string relativePath in currentFiles.OrderBy(static path => path, StringComparer.Ordinal))
        {
            string source = ContainedPath(_sourceRoot, relativePath);
            string destination = ContainedPath(_destinationRoot, relativePath);
            string? destinationDirectory = Path.GetDirectoryName(destination);
            if (destinationDirectory is not null)
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (!FilesEqual(source, destination))
            {
                string temporary = destination + ".wutdev.tmp";
                File.Copy(source, temporary, overwrite: true);
                File.Move(temporary, destination, overwrite: true);
                changes++;
            }
        }

        _knownFiles = currentFiles;
        return changes;
    }

    private static HashSet<string> CollectRelativeFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(
                $"Frontend output directory '{root}' does not exist.");
        }

        RejectReparsePoint(root);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            RejectReparsePoint(path);
            string relative = Path.GetRelativePath(root, path);
            if (relative == ".."
                || relative.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                throw new IOException("A frontend asset escaped its output directory.");
            }

            result.Add(relative);
        }

        return result;
    }

    private static string ContainedPath(string root, string relativePath)
    {
        string candidate = Path.GetFullPath(relativePath, root);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new IOException("A frontend asset path escaped its output directory.");
        }

        return candidate;
    }

    private static bool FilesEqual(string left, string right)
    {
        if (!File.Exists(right))
        {
            return false;
        }

        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        if (leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        const int bufferSize = 64 * 1024;
        using FileStream leftStream = File.OpenRead(left);
        using FileStream rightStream = File.OpenRead(right);
        var leftBuffer = new byte[bufferSize];
        var rightBuffer = new byte[bufferSize];
        while (true)
        {
            int leftRead = leftStream.Read(leftBuffer);
            int rightRead = rightStream.Read(rightBuffer);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Frontend development assets cannot contain reparse points.");
        }
    }
}
