using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Runic.Application.Tool.Packaging;

namespace Runic.Application.Tool;

/// <summary>The options of <c>dotnet runic bundle</c>.</summary>
internal sealed record BundleOptions
{
    internal string? Project { get; init; }
    internal string Configuration { get; init; } = "Release";
    internal string? OutputDirectory { get; init; }
    internal IReadOnlyList<KeyValuePair<string, string>> Properties { get; init; } = [];
}

/// <summary>
/// <c>dotnet runic bundle</c> (split CI F4, D082): builds the project or solution with
/// <c>RunicApplicationFrontendBundleDirectory</c>, so every project that declares Views writes its built page into a web
/// bundle, then checks the bundle and summarizes it. A job on another machine publishes from it with
/// <c>dotnet runic package &lt;target&gt; --frontend-bundle</c> and needs neither Node.js nor Bun.
/// </summary>
internal static class BundleApplication
{
    /// <summary>RAPPBUNDLE1001: the output folder holds other files than a web bundle, which bundle would mix in or delete.</summary>
    internal const string OutputInUse = "RAPPBUNDLE1001";

    /// <summary>RAPPBUNDLE1002: the build that writes the bundle failed.</summary>
    internal const string BuildFailed = "RAPPBUNDLE1002";

    /// <summary>RAPPBUNDLE1003: the build succeeded, but the bundle is missing or does not check.</summary>
    internal const string BundleInvalid = "RAPPBUNDLE1003";

    /// <summary>The properties that bundle sets itself; <c>--property</c> cannot set them.</summary>
    internal static readonly string[] OptionProperties =
        ["Configuration", "RunicApplicationFrontendBundleDirectory", "RunicApplicationFrontendPrebuiltDirectory"];

    internal const string PropertyFormatCode = "RAPPBUNDLE1004";

    internal static IReadOnlyList<KeyValuePair<string, string>> ParseProperties(IReadOnlyList<string> values) =>
        ToolOptionValues.ParseProperties(values, OptionProperties, PropertyFormatCode, PropertyFormatCode,
            name => $"--property cannot set {name}: use --configuration and --output-directory; a bundle is never built from another bundle.");

    /// <summary>The project or solution to build: a .sln or .slnx as given, otherwise the project as package finds it.</summary>
    internal static string FindInput(string workingDirectory, string? requested)
    {
        string root = Path.GetFullPath(workingDirectory);
        if (requested is { Length: > 0 } && Path.GetFullPath(requested, root) is var selected &&
            File.Exists(selected) && Path.GetExtension(selected) is ".sln" or ".slnx")
        {
            return selected;
        }
        string folder = requested is { Length: > 0 } && Directory.Exists(Path.GetFullPath(requested, root)) ? Path.GetFullPath(requested, root) : root;
        if ((requested is not { Length: > 0 } || Directory.Exists(Path.GetFullPath(requested, root))) &&
            Directory.GetFiles(folder, "*.csproj").Length == 0 &&
            Directory.GetFiles(folder).Where(static file => Path.GetExtension(file) is ".sln" or ".slnx").ToArray() is [var solution])
        {
            return solution;
        }
        return ProjectDiscovery.Find(root, requested);
    }

    /// <summary>
    /// Prepares the output folder: a new or empty folder, or an earlier bundle, whose manifest and entries are deleted so
    /// that no stale entry stays in the manifest. Any other content is <see cref="OutputInUse"/>.
    /// </summary>
    internal static void PrepareOutput(string output)
    {
        if (!Directory.Exists(output))
        {
            Directory.CreateDirectory(output);
            return;
        }
        string[] entries = [.. Directory.EnumerateFileSystemEntries(output).Select(Path.GetFileName).OfType<string>()];
        if (entries.Length == 0) return;
        if (!entries.Contains(WebBundle.ManifestName) && !entries.Contains("projects") ||
            entries.Any(static name => name is not (WebBundle.ManifestName or "projects")))
        {
            throw new DevUsageException(OutputInUse,
                $"The output directory holds files other than a web bundle ({string.Join(", ", entries.Order(StringComparer.Ordinal).Take(5))}), which bundle would delete. " +
                "Choose an empty or new directory with --output-directory, or one that an earlier dotnet runic bundle wrote.",
                $"Output directory: {output}\n");
        }
        PackageTools.Delete(Path.Combine(output, WebBundle.ManifestName));
        PackageTools.Delete(Path.Combine(output, "projects"));
    }

    internal static IReadOnlyList<string> CreateBuildArguments(string input, string output, BundleOptions options) =>
    [
        "build", input, "--nologo", "-c", options.Configuration,
        ToolOptionValues.PropertyArgument(new("RunicApplicationFrontendBundleDirectory", output)),
        .. options.Properties.Select(ToolOptionValues.PropertyArgument),
    ];

    internal static async Task<BundleCommandResult> RunAsync(BundleOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        string input = FindInput(Environment.CurrentDirectory, options.Project);
        string output = Path.GetFullPath(options.OutputDirectory is { Length: > 0 } selected
            ? Path.GetFullPath(selected, Environment.CurrentDirectory)
            : Path.Combine(Path.GetDirectoryName(input)!, "artifacts", "web-bundle"));
        PrepareOutput(output);
        string dotnet = DevApplication.ResolveDotNetHost();
        await PackageTools.RunAsync(dotnet, CreateBuildArguments(input, output, options), Path.GetDirectoryName(input)!,
            cancellationToken, BuildFailed).ConfigureAwait(false);
        (IReadOnlyList<WebBundleEntry> entries, IReadOnlyList<string> problems) = WebBundle.Read(output);
        if (problems.Count != 0)
        {
            throw new DevDevelopmentException(BundleInvalid,
                $"The build succeeded, but the web bundle it wrote does not check: {string.Join("; ", problems)}. " +
                "A project whose frontend build is off (RunicApplicationFrontendBuildEnabled=false) writes no entry (RUNICBRIDGE092).",
                $"Bundle: {output}\n");
        }
        foreach (string line in Summary(output, entries)) Console.Out.WriteLine(line);
        return new BundleCommandResult(output, WebBundle.Format,
            [.. entries.Select(static entry => new BundleCommandProject(entry.Assembly, entry.Kind, entry.Views, entry.Files, entry.Bytes, entry.Sha256, entry.Host, entry.NodeEnv))]);
    }

    /// <summary>The human summary: the bundle, then one line per project with its Views, files, size and hash.</summary>
    internal static IReadOnlyList<string> Summary(string output, IReadOnlyList<WebBundleEntry> entries)
    {
        var lines = new List<string> { $"Web bundle ({WebBundle.Format}): {output}" };
        foreach (WebBundleEntry entry in entries)
        {
            string page = entry.Sha256 is null
                ? "no page"
                : $"{entry.Files.ToString(CultureInfo.InvariantCulture)} files, {Size(entry.Bytes)}, SHA-256 {entry.Sha256[..Math.Min(12, entry.Sha256.Length)]}";
            string built = string.Join(", ", new[] { entry.Host is { } host ? $"host {host}" : null, entry.NodeEnv is { } env ? $"NODE_ENV {env}" : null }.OfType<string>());
            lines.Add($"  {entry.Assembly} ({entry.Kind}): {entry.Views.ToString(CultureInfo.InvariantCulture)} View(s), {page}{(built.Length != 0 ? $"; {built}" : string.Empty)}");
        }
        lines.Add($"Publish from it with 'dotnet runic package <target> --frontend-bundle {output}', or -p:RunicApplicationFrontendPrebuiltDirectory={output}.");
        return lines;
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KiB",
        _ => $"{(bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} MiB",
    };
}

/// <summary>
/// The <c>runic.application.tool.bundle/1</c> payload: the bundle's folder and format, and its projects. README.md
/// documents its JSON schema; keep both in sync.
/// </summary>
internal sealed record BundleCommandResult(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("projects")] IReadOnlyList<BundleCommandProject> Projects) : IToolCommandResult<BundleCommandResult>
{
    internal const string PayloadType = "runic.application.tool.bundle/1";

    static string IToolCommandResult<BundleCommandResult>.PayloadType => PayloadType;

    static JsonTypeInfo<BundleCommandResult> IToolCommandResult<BundleCommandResult>.JsonTypeInfo =>
        BundleCommandJsonContext.Default.BundleCommandResult;

    [JsonIgnore]
    public string HumanOutput { get; init; } = "";

    public BundleCommandResult WithHumanOutput(string humanOutput) => this with { HumanOutput = humanOutput };

    public override string ToString() => HumanOutput;
}

/// <summary>One project of the bundle: its assembly, kind, View count and page (<c>files</c>, <c>bytes</c>, <c>sha256</c>; null for a client library).</summary>
internal sealed record BundleCommandProject(
    [property: JsonPropertyName("assembly")] string Assembly,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("views")] int Views,
    [property: JsonPropertyName("files")] int Files,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("nodeEnv")] string? NodeEnv);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BundleCommandResult))]
internal sealed partial class BundleCommandJsonContext : JsonSerializerContext;
