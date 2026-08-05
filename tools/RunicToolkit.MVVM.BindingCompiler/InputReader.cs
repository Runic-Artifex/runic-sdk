using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RunicToolkit.MVVM.BindingCompiler;

internal sealed record CompilerInput(string LogicalPath, string Source);

internal static class InputReader
{
    internal const int MaximumInputBytes = 1 * 1024 * 1024;
    internal const int MaximumTotalInputBytes = 16 * 1024 * 1024;
    internal const int MaximumLogicalPathCharacters = 1_024;

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IReadOnlyList<CompilerInput> ReadAll(IReadOnlyList<string> paths, string currentDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrEmpty(currentDirectory);

        string root = Path.GetFullPath(currentDirectory);
        var inputs = new List<CompilerInput>(paths.Count);
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seenPaths = new HashSet<string>(pathComparer);
        int totalBytes = 0;
        foreach (string path in paths)
        {
            string fullPath = Path.GetFullPath(path, root);
            string logicalPath = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (Path.IsPathRooted(logicalPath) ||
                logicalPath == ".." ||
                logicalPath.StartsWith("../", StringComparison.Ordinal))
            {
                throw new CommandLineException($"Input path '{path}' is outside the current project directory.");
            }

            if (logicalPath.Length > MaximumLogicalPathCharacters ||
                logicalPath.Any(char.IsControl))
            {
                throw new CommandLineException(
                    $"An input logical path is invalid or exceeds {MaximumLogicalPathCharacters} characters.");
            }

            try
            {
                _ = StrictUtf8.GetByteCount(logicalPath);
            }
            catch (EncoderFallbackException exception)
            {
                throw new CommandLineException("An input logical path is not valid Unicode.", exception);
            }

            if (!seenPaths.Add(fullPath))
            {
                throw new CommandLineException($"Duplicate input path '{logicalPath}'.");
            }

            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.SequentialScan);
            if (stream.Length > MaximumInputBytes)
            {
                throw new InputLimitException(
                    $"Input '{logicalPath}' exceeds the {MaximumInputBytes} byte limit.");
            }

            int length = checked((int)stream.Length);
            totalBytes = checked(totalBytes + length);
            if (totalBytes > MaximumTotalInputBytes)
            {
                throw new InputLimitException(
                    $"Inputs exceed the {MaximumTotalInputBytes} total byte limit.");
            }

            byte[] bytes = new byte[length];
            stream.ReadExactly(bytes);
            ReadOnlySpan<byte> sourceBytes = bytes;
            ReadOnlySpan<byte> utf8Preamble = Encoding.UTF8.Preamble;
            if (sourceBytes.StartsWith(utf8Preamble))
            {
                sourceBytes = sourceBytes[utf8Preamble.Length..];
            }

            string source;
            try
            {
                source = StrictUtf8.GetString(sourceBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidInputEncodingException(
                    $"Input '{logicalPath}' is not valid UTF-8.",
                    exception);
            }

            inputs.Add(new CompilerInput(logicalPath, source));
        }

        CompilerInput[] sorted = inputs
            .OrderBy(static input => input.LogicalPath, StringComparer.Ordinal)
            .ToArray();
        return sorted;
    }
}

internal sealed class InputLimitException : Exception
{
    public InputLimitException(string message)
        : base(message)
    {
    }
}

internal sealed class InvalidInputEncodingException : Exception
{
    public InvalidInputEncodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
