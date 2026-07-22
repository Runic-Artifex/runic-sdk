using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WebUIToolkit.MVVM.BindingCompiler;

internal static class OutputWriter
{
    private static readonly UTF8Encoding StrictUtf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static void Write(string outputPath, string content, IReadOnlyList<string> inputPaths)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(inputPaths);

        byte[] bytes;
        try
        {
            bytes = StrictUtf8WithoutBom.GetBytes(content);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("Generated output is not valid Unicode.", exception);
        }

        if (outputPath == "-")
        {
            Stream standardOutput = Console.OpenStandardOutput();
            standardOutput.Write(bytes);
            standardOutput.Flush();
            return;
        }

        string destination = Path.GetFullPath(outputPath);
        StringComparer pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (inputPaths
            .Select(Path.GetFullPath)
            .Any(input => pathComparer.Equals(input, destination)))
        {
            throw new CommandLineException("The output path cannot be one of the input paths.");
        }

        string? directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"Cannot determine the output directory for '{outputPath}'.");
        }

        Directory.CreateDirectory(directory);
        if (File.Exists(destination) && ContentEquals(destination, bytes))
        {
            return;
        }

        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // The destination was already written. A best-effort cleanup failure
                // must not convert a successful compilation into a failing one.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IOException case above.
            }
        }
    }

    private static bool ContentEquals(string path, byte[] expected)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.SequentialScan);
        if (stream.Length != expected.Length)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[16_384];
        int offset = 0;
        while (offset < expected.Length)
        {
            int count = stream.Read(buffer[..Math.Min(buffer.Length, expected.Length - offset)]);
            if (count == 0 || !buffer[..count].SequenceEqual(expected.AsSpan(offset, count)))
            {
                return false;
            }

            offset += count;
        }

        return true;
    }
}
