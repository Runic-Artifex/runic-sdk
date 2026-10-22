using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Runic.Assets;
using Runic.CommandLine;
#if RUNIC_ASSETS_PACK_SUBCOMMAND
using Runic.Application.Tool;
#endif

namespace Runic.Assets.Packer;

// The pack command of Runic.Assets.Packer, which the Runic.Assets build runs, and of `dotnet runic assets pack`:
// tools/dotnet-runic compiles this file with RUNIC_ASSETS_PACK_SUBCOMMAND, so both have the same arguments,
// defaults, exit codes, RAS1xxx codes and runic.assets.pack-result/1 payload.
#if RUNIC_ASSETS_PACK_SUBCOMMAND
[CommandGroup("assets", Description = "Work with Runic Assets archives.")]
#endif
internal static class AssetPackCommand
{
#if RUNIC_ASSETS_PACK_SUBCOMMAND
    /// <summary>The command's path in dotnet runic.</summary>
    internal const string CommandPath = "assets pack";
#else
    /// <summary>The command's path in the packer, where it is the default command.</summary>
    internal const string CommandPath = "pack";
#endif

    /// <summary>The warning code for source maps in the archive, in MSBuild's canonical form on standard error.</summary>
    internal const string SourceMapWarningCode = "RAS1005";

    // Fault messages stay free of paths and exception text, so the JSON response
    // keeps the RAS code; Runic.CommandLine replaces a fault whose message looks
    // technical. Paths and exception messages go into details, which JSON output
    // redacts individually, and the human output below puts them back.
    private const string SourceDirectoryDetail = "sourceDirectory";
    private const string EntryPointDetail = "entryPoint";
    private const string ReasonDetail = "reason";

    [Command(CommandPath, Description = "Package a directory as a Runic asset archive.")]
#if !RUNIC_ASSETS_PACK_SUBCOMMAND
    [DefaultCommand]
#endif
    [CommandResult("runic.assets.pack-result/1", typeof(PackerJsonContext))]
    internal static async Task<CommandOutcome<PackerResult>> PackAsync(
        CommandExecutionContext context,
        [Argument("source-directory", Description = "Directory to package, usually a frontend build output such as dist.")] string sourceDirectory,
        [Argument("destination-archive", Description = "Archive file to write; it is replaced atomically.")] string destination,
        [Option("--exclude", ValueName = "paths", Description = "Asset paths to omit, separated by semicolons. Repeatable.")] IReadOnlyList<string> exclusions,
        [Option("--trusted-generated-output", Description = "Snapshot trusted build output; works on any OS.")] bool trustedGeneratedOutput,
        [Option("--no-default-excludes", Description = "Also pack .git directories and .env* files, which are left out by default.")] bool noDefaultExcludes,
        CancellationToken cancellationToken,
        [Option("--entry-point", ValueName = "path", Description = "Entry document relative to the source directory.")] string entryPoint = "index.html")
    {
        string fullSourceDirectory = Path.GetFullPath(sourceDirectory);
        string fullDestination = Path.GetFullPath(destination);
        if (!Directory.Exists(fullSourceDirectory))
        {
            return Failure(
                CommandExitCategory.Validation,
                "RAS1001",
                "The source directory does not exist.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SourceDirectoryDetail] = fullSourceDirectory,
                });
        }

        try
        {
            entryPoint = AssetPath.Normalize(entryPoint);
            var excludedPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string exclusion in exclusions)
            {
                foreach (string path in exclusion.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    excludedPaths.Add(AssetPath.Normalize(path));
                }
            }

            string? destinationDirectory = Path.GetDirectoryName(fullDestination);
            if (!string.IsNullOrEmpty(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            string temporary = fullDestination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            AddSourceRelativeExclusion(fullSourceDirectory, fullDestination, excludedPaths);
            AddSourceRelativeExclusion(fullSourceDirectory, temporary, excludedPaths);
            if (excludedPaths.Contains(entryPoint)
                || (!noDefaultExcludes && AssetDirectoryExclusions.IsDefaultPath(entryPoint))
                || !File.Exists(Path.Combine(fullSourceDirectory, entryPoint.Replace('/', Path.DirectorySeparatorChar))))
            {
                return Failure(
                    CommandExitCategory.Unavailable,
                    "RAS1002",
                    "The entry point does not exist below the source directory or was excluded.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        [EntryPointDetail] = entryPoint,
                        [SourceDirectoryDetail] = fullSourceDirectory,
                    });
            }

            var options = new AssetArchiveDirectoryOptions
            {
                EntryPoint = entryPoint,
                ExcludedPaths = [.. excludedPaths],
                ExcludeDefaultPatterns = !noDefaultExcludes,
            };
            int defaultExclusions = noDefaultExcludes ? 0 : CountDefaultExclusions(new DirectoryInfo(fullSourceDirectory));
            (int Count, string? First) sourceMaps;
            try
            {
                await using (var output = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    if (trustedGeneratedOutput)
                    {
                        await AssetArchive.WriteTrustedDirectoryAsync(
                            fullSourceDirectory,
                            output,
                            options,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else if (OperatingSystem.IsLinux())
                    {
                        await AssetArchive.WriteDirectoryAsync(
                            fullSourceDirectory,
                            output,
                            options,
                            cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        return Failure(
                            CommandExitCategory.CommandFailure,
                            "RAS1004",
                            "Directory archive compilation requires Linux handle-pinned traversal. " +
                            "Use --trusted-generated-output only for output generated by the current build.");
                    }
                }

                sourceMaps = FindSourceMaps(temporary);
                File.Move(temporary, fullDestination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            if (sourceMaps.Count != 0)
            {
                // MSBuild's Exec reports this canonical line as warning RAS1005, which NoWarn can silence.
                string noun = sourceMaps.Count == 1 ? "source map" : "source maps";
                await context.Console.WriteErrorAsync(
                    ($"warning {SourceMapWarningCode}: The archive contains {sourceMaps.Count} {noun} (*.map), " +
                    $"first '{sourceMaps.First}'. Source maps can expose the original source; exclude them " +
                    $"unless the application should ship them. Help: {RunicReleaseLinks.Diagnostic(SourceMapWarningCode)}" +
                    Environment.NewLine).AsMemory(),
                    cancellationToken).ConfigureAwait(false);
            }

            return CommandOutcome.Success(new PackerResult(new FileInfo(fullDestination).Length)
            {
                DefaultExclusions = defaultExclusions,
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or PlatformNotSupportedException)
        {
            return Failure(
                CommandExitCategory.CommandFailure,
                "RAS1003",
                "The archive could not be packed.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [ReasonDetail] = exception.Message,
                });
        }
    }

    private static CommandOutcome<PackerResult> Failure(
        CommandExitCategory category,
        string code,
        string message,
        IReadOnlyDictionary<string, string>? details = null) =>
        CommandOutcome.Failure<PackerResult>(category,
            new CommandFault(code, message, details) { HelpUri = new Uri(RunicReleaseLinks.Diagnostic(code)) });

    // Human output: the archive length on success; on failure the full message for a person at the terminal, with
    // the paths and the underlying reason that the JSON fault leaves out, and the fault's help link.
    internal static ValueTask PresentAsync(CommandOutcomePresentation<PackerResult> presentation, CancellationToken cancellationToken)
    {
        CommandOutcome<PackerResult> outcome = presentation.Outcome;
        ICommandConsole console = presentation.Context.Console;
        if (outcome.Succeeded && outcome.Value is { } result)
        {
            string text = $"Packed a canonical Runic Assets archive into {result.ArchiveLength} bytes.{Environment.NewLine}";
            if (result.DefaultExclusions != 0)
            {
                text += $"Left out {result.DefaultExclusions} .git and .env* {(result.DefaultExclusions == 1 ? "entry" : "entries")}; " +
                    $"pass --no-default-excludes to pack them.{Environment.NewLine}";
            }

            return console.WriteOutAsync(text.AsMemory(), cancellationToken);
        }

        string message = outcome.Fault is { } fault
            ? HumanMessage(fault) + (fault.HelpUri is { } help ? Environment.NewLine + $"Help for {fault.Code}: {help.AbsoluteUri}" : "")
            : "The archive could not be packed.";
        return console.WriteErrorAsync((message + Environment.NewLine).AsMemory(), cancellationToken);
    }

    // The full message for a person at the terminal, with the paths and the
    // underlying reason that the machine-readable message leaves out.
    internal static string HumanMessage(CommandFault fault)
    {
        IReadOnlyDictionary<string, string> details = fault.Details;
        return fault.Code switch
        {
            "RAS1001" when details.TryGetValue(SourceDirectoryDetail, out string? source) =>
                $"Source directory '{source}' does not exist.",
            "RAS1002" when details.TryGetValue(EntryPointDetail, out string? entry)
                && details.TryGetValue(SourceDirectoryDetail, out string? source) =>
                $"Entry point '{entry}' does not exist below '{source}' or was excluded.",
            "RAS1003" when details.TryGetValue(ReasonDetail, out string? reason) => reason,
            _ => fault.Message,
        };
    }

    // Counts the .git directories and files and the .env* files that the default patterns leave out, for the
    // human summary. It neither follows links nor enters a .git directory; the writer applies the patterns itself.
    private static int CountDefaultExclusions(DirectoryInfo root)
    {
        int count = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            foreach (FileSystemInfo entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry is DirectoryInfo
                    ? AssetDirectoryExclusions.IsDefaultDirectoryName(entry.Name)
                    : AssetDirectoryExclusions.IsDefaultFileName(entry.Name))
                {
                    count++;
                }
                else if (entry is DirectoryInfo child && (child.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }

        return count;
    }

    // The archive's source maps, read from its ZIP directory: the count and the first asset path.
    private static (int Count, string? First) FindSourceMaps(string archivePath)
    {
        const string ContentPrefix = "assets/";
        using ZipArchive archive = ZipFile.OpenRead(archivePath);
        int count = 0;
        string? first = null;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            if (entry.FullName.StartsWith(ContentPrefix, StringComparison.Ordinal)
                && entry.FullName.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
            {
                first ??= entry.FullName[ContentPrefix.Length..];
                count++;
            }
        }

        return (count, first);
    }

    private static void AddSourceRelativeExclusion(
        string sourceDirectory,
        string candidate,
        HashSet<string> excludedPaths)
    {
        string relative = Path.GetRelativePath(sourceDirectory, candidate);
        if (relative == "."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            return;
        }

        excludedPaths.Add(AssetPath.Normalize(relative.Replace(Path.DirectorySeparatorChar, '/')));
    }
}

// The runic.assets.pack-result/1 payload is { "archiveLength": <bytes> }; the name is spelled
// out so that the JSON context's options cannot rename it.
internal sealed record PackerResult([property: JsonPropertyName("archiveLength")] long ArchiveLength)
{
    /// <summary>The entries the default patterns left out, for human output only.</summary>
    [JsonIgnore]
    public int DefaultExclusions { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackerResult))]
internal sealed partial class PackerJsonContext : JsonSerializerContext;
