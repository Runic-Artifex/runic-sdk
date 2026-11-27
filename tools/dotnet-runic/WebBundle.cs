using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Runic.Application.Tool;

/// <summary>One project's entry of a web bundle, as <c>bundle</c> summarizes it.</summary>
/// <param name="Assembly">The assembly name, by which a publish finds its entry.</param>
/// <param name="Kind"><c>application</c>, <c>library</c> or <c>clientLibrary</c>.</param>
/// <param name="Views">How many Views the entry's contracts list.</param>
/// <param name="Files">How many files its <c>www/</c> holds, as recorded; 0 for a client library.</param>
/// <param name="Sha256">The recorded SHA-256 of its <c>www/</c>, or null for a client library.</param>
/// <param name="Host">The <c>RunicApplicationHost</c> it was built for, or null.</param>
/// <param name="NodeEnv">The <c>NODE_ENV</c> of its frontend build, or null.</param>
/// <param name="Bytes">The size of its <c>www/</c> on disk; 0 without one.</param>
internal sealed record WebBundleEntry(string Assembly, string Kind, int Views, int Files, string? Sha256, string? Host, string? NodeEnv, long Bytes);

/// <summary>
/// Reads and checks a web bundle (<c>rax.web-bundle/1</c>, specs/application/web-bundle.md) offline, as <c>bundle</c>
/// summarizes it and <c>doctor --bundle</c> checks it: the manifest's format and required members, and that each
/// entry's folders exist and its <c>www/</c> hashes to the recorded <c>files</c>, as the publishing build's
/// RUNICBRIDGE093 check does. The View fingerprints need the build's contract IR, so only a publish checks them.
/// </summary>
internal static class WebBundle
{
    internal const string Format = "rax.web-bundle/1";
    internal const string ManifestName = "web-bundle.json";

    private static readonly string[] RequiredMembers = ["format", "producer", "createdBy", "projects"];
    private static readonly string[] RequiredEntryMembers = ["assembly", "project", "kind", "configuration", "targetFramework", "contracts"];
    private static readonly string[] Kinds = ["application", "library", "clientLibrary"];

    /// <summary>The entries and every problem found; no entries when the manifest is missing or unreadable.</summary>
    internal static (IReadOnlyList<WebBundleEntry> Entries, IReadOnlyList<string> Problems) Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string manifest = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifest))
        {
            return ([], [Directory.Exists(directory)
                ? $"{directory} has no {ManifestName}; it is not a web bundle, or the build that writes it did not finish"
                : $"the folder {directory} does not exist"]);
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(manifest));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ([], [$"{manifest} cannot be read: {error.Message}"]);
        }
        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ([], [$"{manifest} is not a JSON object"]);
            var problems = new List<string>();
            if (Text(root, "format") is not Format)
            {
                return ([], [$"{manifest} has the format '{Text(root, "format") ?? "(none)"}', not {Format}; this dotnet-runic reads {Format} only"]);
            }
            problems.AddRange(RequiredMembers.Where(member => !root.TryGetProperty(member, out _))
                .Select(member => $"{manifest} lacks the required member '{member}'"));
            var entries = new List<WebBundleEntry>();
            if (root.TryGetProperty("projects", out JsonElement projects) && projects.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement project in projects.EnumerateArray())
                {
                    if (ReadEntry(directory, project, problems) is { } entry) entries.Add(entry);
                }
                if (entries.Count == 0 && problems.Count == 0) problems.Add($"{manifest} lists no project");
            }
            else if (root.TryGetProperty("projects", out _))
            {
                problems.Add($"{manifest}'s 'projects' is not an array");
            }
            return (entries, problems);
        }
    }

    private static WebBundleEntry? ReadEntry(string directory, JsonElement project, List<string> problems)
    {
        if (project.ValueKind != JsonValueKind.Object)
        {
            problems.Add("an entry of 'projects' is not an object");
            return null;
        }
        string assembly = Text(project, "assembly") ?? "(no assembly)";
        string[] missing = [.. RequiredEntryMembers.Where(member => !project.TryGetProperty(member, out _))];
        if (missing.Length != 0) problems.Add($"the entry of {assembly} lacks {string.Join(", ", missing.Select(static member => $"'{member}'"))}");
        string kind = Text(project, "kind") ?? string.Empty;
        if (kind.Length != 0 && !Kinds.Contains(kind)) problems.Add($"the entry of {assembly} has the unknown kind '{kind}'");
        int views = project.TryGetProperty("contracts", out JsonElement contracts) && contracts.ValueKind == JsonValueKind.Object &&
            contracts.TryGetProperty("views", out JsonElement list) && list.ValueKind == JsonValueKind.Array
            ? list.GetArrayLength()
            : 0;
        int files = 0;
        long bytes = 0;
        string? sha256 = null;
        if (Text(project, "www") is { } www)
        {
            if (project.TryGetProperty("files", out JsonElement recorded) && recorded.ValueKind == JsonValueKind.Object)
            {
                files = recorded.TryGetProperty("count", out JsonElement count) && count.TryGetInt32(out int value) ? value : 0;
                sha256 = Text(recorded, "sha256");
            }
            if (Resolve(directory, www) is not { } folder)
            {
                problems.Add($"the entry of {assembly} names the folder '{www}', which is not a relative path inside the bundle");
            }
            else if (!Directory.Exists(folder))
            {
                problems.Add($"the page of {assembly}, {www}, is missing");
            }
            else if (sha256 is null)
            {
                problems.Add($"the entry of {assembly} records no 'files' for {www}");
            }
            else
            {
                bytes = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
                (int actualCount, string actualSha256) = FilesSummary(folder);
                if (actualCount != files || actualSha256 != sha256)
                {
                    problems.Add($"the page of {assembly}, {www}, has {actualCount} files with SHA-256 {actualSha256}, but {ManifestName} records " +
                        $"{files} files with SHA-256 {sha256} (a truncated download or an edited page; the publish fails with RUNICBRIDGE093)");
                }
            }
        }
        else if (kind is "application" or "library")
        {
            problems.Add($"the entry of {assembly} is an {kind} without 'www'");
        }
        if (Text(project, "clients") is { } clients && (Resolve(directory, clients) is not { } clientFolder || !Directory.Exists(clientFolder)))
        {
            problems.Add($"the generated clients of {assembly}, {clients}, are missing");
        }
        return new WebBundleEntry(assembly, kind, views, files, sha256, Text(project, "host"), Text(project, "nodeEnv"), bytes);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A bundle-relative path as a full path inside the bundle; null for a rooted path or one that leaves it.</summary>
    private static string? Resolve(string directory, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains('\\', StringComparison.Ordinal)) return null;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : null;
    }

    /// <summary>
    /// <c>{count, sha256}</c> of a folder as the manifest records it: the SHA-256 of the lines <c>&lt;sha256&gt;  &lt;path&gt;\n</c>
    /// (sha256sum's format) of every file, its path relative to the folder with <c>/</c>, sorted by path in ordinal order.
    /// </summary>
    internal static (int Count, string Sha256) FilesSummary(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(file => (Path: Path.GetRelativePath(folder, file).Replace('\\', '/'), Sha256: Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)))))
            .OrderBy(static file => file.Path, StringComparer.Ordinal)
            .ToList();
        var text = new StringBuilder();
        foreach ((string path, string sha256) in files) text.Append(sha256).Append("  ").Append(path).Append('\n');
        return (files.Count, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
    }
}
