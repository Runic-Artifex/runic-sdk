using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

const string archiveVersion = "runic.assets.archive/1";
const string manifestEntryName = "runic-assets.json";
const string contentPrefix = "assets/";

if (args.Length < 2 || args.Length % 2 != 0)
{
    WriteUsage();
    return 2;
}

string sourceDirectory = Path.GetFullPath(args[0]);
string destination = Path.GetFullPath(args[1]);
string entryPoint = "index.html";
var excludedPaths = new HashSet<string>(StringComparer.Ordinal);

for (int index = 2; index < args.Length; index += 2)
{
    switch (args[index])
    {
        case "--entry-point":
            entryPoint = NormalizePath(args[index + 1]);
            break;
        case "--exclude":
            foreach (string path in args[index + 1].Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                excludedPaths.Add(NormalizePath(path));
            }

            break;
        default:
            WriteUsage();
            return 2;
    }
}

if (!Directory.Exists(sourceDirectory))
{
    Console.Error.WriteLine($"Source directory '{sourceDirectory}' does not exist.");
    return 3;
}

try
{
    FileItem[] files = EnumerateFiles(sourceDirectory, entryPoint, excludedPaths).ToArray();
    if (!files.Any(static file => file.IsEntryPoint))
    {
        Console.Error.WriteLine(
            $"Entry point '{entryPoint}' does not exist below '{sourceDirectory}' or was excluded.");
        return 4;
    }

    string? destinationDirectory = Path.GetDirectoryName(destination);
    if (!string.IsNullOrEmpty(destinationDirectory))
    {
        Directory.CreateDirectory(destinationDirectory);
    }

    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
    try
    {
        using (var output = new FileStream(
            temporary,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None))
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            WriteManifest(archive, files);
            WriteContent(archive, files);
        }

        File.Move(temporary, destination, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary))
        {
            File.Delete(temporary);
        }
    }

    long originalSize = files.Sum(static file => file.Length);
    long archiveSize = new FileInfo(destination).Length;
    Console.WriteLine(
        $"Packed {files.Length} Runic Assets files ({originalSize} bytes) into " +
        $"{archiveSize} bytes with entry point '{entryPoint}'.");
    return 0;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(exception.Message);
    return 5;
}

static IEnumerable<FileItem> EnumerateFiles(
    string root,
    string entryPoint,
    HashSet<string> excludedPaths)
{
    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    foreach (string file in Directory.EnumerateFiles(root, "*", options).Order(StringComparer.Ordinal))
    {
        string path = NormalizePath(
            Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/'));
        if (excludedPaths.Contains(path))
        {
            continue;
        }

        using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.SequentialScan);
        byte[] digest = SHA256.HashData(stream);
        yield return new FileItem(
            path,
            file,
            ResolveMediaType(path),
            stream.Length,
            Convert.ToHexStringLower(digest),
            StringComparer.Ordinal.Equals(path, entryPoint),
            path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                ? "Revalidate"
                : "Immutable");
    }
}

static void WriteManifest(ZipArchive archive, IReadOnlyList<FileItem> files)
{
    ZipArchiveEntry manifest = archive.CreateEntry(manifestEntryName, CompressionLevel.SmallestSize);
    manifest.LastWriteTime = GetStableTimestamp();
    using Stream stream = manifest.Open();
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
    writer.WriteStartObject();
    writer.WriteString("version", archiveVersion);
    writer.WriteStartArray("assets");
    foreach (FileItem file in files)
    {
        writer.WriteStartObject();
        writer.WriteString("path", file.RelativePath);
        writer.WriteString("mediaType", file.MediaType);
        writer.WriteNumber("length", file.Length);
        writer.WriteString("sha256", file.Sha256);
        writer.WriteBoolean("entryPoint", file.IsEntryPoint);
        writer.WriteString("cacheMode", file.CacheMode);
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
    writer.Flush();
}

static void WriteContent(ZipArchive archive, IReadOnlyList<FileItem> files)
{
    foreach (FileItem file in files)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            contentPrefix + file.RelativePath,
            IsAlreadyCompressed(file.RelativePath)
                ? CompressionLevel.NoCompression
                : CompressionLevel.SmallestSize);
        entry.LastWriteTime = GetStableTimestamp();
        using Stream output = entry.Open();
        using var input = new FileStream(
            file.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            FileOptions.SequentialScan);
        input.CopyTo(output);
    }
}

static string NormalizePath(string value)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(value);
    if (value != value.Trim())
    {
        throw new ArgumentException("An asset path cannot have surrounding whitespace.", nameof(value));
    }

    value = value.Replace('\\', '/');
    if (value[0] == '/' || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
    {
        throw new ArgumentException("An asset path must be application-relative.", nameof(value));
    }

    foreach (string segment in value.Split('/'))
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            throw new ArgumentException(
                "An asset path cannot contain empty, current-directory, or parent-directory segments.",
                nameof(value));
        }

        foreach (char character in segment)
        {
            if (char.IsControl(character) || character is ':' or '?' or '#')
            {
                throw new ArgumentException("An asset path contains an unsupported character.", nameof(value));
            }
        }

        for (int index = 0; index <= segment.Length - 3; index++)
        {
            if (segment[index] == '%'
                && char.IsAsciiHexDigit(segment[index + 1])
                && char.IsAsciiHexDigit(segment[index + 2]))
            {
                throw new ArgumentException(
                    "An asset path cannot contain percent-encoded octets.",
                    nameof(value));
            }
        }
    }

    return value;
}

static string ResolveMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
{
    ".avif" => "image/avif",
    ".css" => "text/css",
    ".gif" => "image/gif",
    ".htm" or ".html" => "text/html",
    ".ico" => "image/x-icon",
    ".jpeg" or ".jpg" => "image/jpeg",
    ".js" or ".mjs" => "text/javascript",
    ".json" or ".map" => "application/json",
    ".mp3" => "audio/mpeg",
    ".mp4" => "video/mp4",
    ".otf" => "font/otf",
    ".pdf" => "application/pdf",
    ".png" => "image/png",
    ".svg" => "image/svg+xml",
    ".txt" => "text/plain",
    ".ttf" => "font/ttf",
    ".wasm" => "application/wasm",
    ".wav" => "audio/wav",
    ".webm" => "video/webm",
    ".webmanifest" => "application/manifest+json",
    ".webp" => "image/webp",
    ".woff" => "font/woff",
    ".woff2" => "font/woff2",
    ".xml" => "application/xml",
    _ => "application/octet-stream",
};

static bool IsAlreadyCompressed(string path) => Path.GetExtension(path).ToLowerInvariant() is
    ".7z" or ".avif" or ".br" or ".gif" or ".gz" or ".ico" or ".jpeg" or ".jpg"
    or ".mp3" or ".mp4" or ".ogg" or ".opus" or ".pdf" or ".png" or ".rar"
    or ".webm" or ".webp" or ".woff" or ".woff2" or ".zip";

static void WriteUsage() => Console.Error.WriteLine(
    "Usage: RunicAssets.Packer <source-directory> <destination-archive> " +
    "[--entry-point <relative-path>] [--exclude <semicolon-separated-relative-paths>]");

static DateTimeOffset GetStableTimestamp() => new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

internal sealed record FileItem(
    string RelativePath,
    string FullPath,
    string MediaType,
    long Length,
    string Sha256,
    bool IsEntryPoint,
    string CacheMode);
