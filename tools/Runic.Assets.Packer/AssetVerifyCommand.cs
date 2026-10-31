using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Runic.Assets;
using Runic.CommandLine;

namespace Runic.Assets.Packer;

// The verify command of Runic.Assets.Packer (since 0.15). The Runic.Assets build runs it on RunicAssetsEmbeddedArchive
// before embedding it: AssetArchive.ReadEmbedded does not check the SHA-256 digests of an embedded archive at run
// time, so an archive the build did not pack itself is checked here, once per change.
internal static class AssetVerifyCommand
{
    internal const string CommandPath = "verify";

    private const string ReasonDetail = "reason";

    [Command(CommandPath, Description = "Check that an archive is a valid Runic Assets archive whose assets match their digests.")]
    [CommandResult("runic.assets.verify-result/1", typeof(VerifierJsonContext))]
    internal static async Task<CommandOutcome<VerifierResult>> VerifyAsync(
        CommandExecutionContext context,
        [Argument("archive", Description = "Archive file to check.")] string archive,
        CancellationToken cancellationToken)
    {
        string fullArchive = Path.GetFullPath(archive);
        if (!File.Exists(fullArchive))
        {
            return Failure(CommandExitCategory.Unavailable, "RAS1006", "The archive does not exist.", fullArchive);
        }

        try
        {
            await using var stream = new FileStream(fullArchive, FileMode.Open, FileAccess.Read, FileShare.Read);
            AssetArchiveSource source = await AssetArchive.ReadAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return CommandOutcome.Success(new VerifierResult(source.Manifest.Assets.Count));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Failure(CommandExitCategory.CommandFailure, "RAS1007", "The archive is not a valid Runic Assets archive.", exception.Message);
        }
    }

    private static CommandOutcome<VerifierResult> Failure(CommandExitCategory category, string code, string message, string reason) =>
        CommandOutcome.Failure<VerifierResult>(category,
            new CommandFault(code, message, new Dictionary<string, string>(StringComparer.Ordinal) { [ReasonDetail] = reason })
            {
                HelpUri = new Uri(RunicReleaseLinks.Diagnostic(code)),
            });

    // Human output: the asset count on success; on failure the message with the path or the underlying reason.
    internal static ValueTask PresentAsync(CommandOutcomePresentation<VerifierResult> presentation, CancellationToken cancellationToken)
    {
        CommandOutcome<VerifierResult> outcome = presentation.Outcome;
        ICommandConsole console = presentation.Context.Console;
        if (outcome.Succeeded && outcome.Value is { } result)
        {
            return console.WriteOutAsync(
                $"The archive is valid; its {result.AssetCount} assets match their digests.{Environment.NewLine}".AsMemory(),
                cancellationToken);
        }

        string message = outcome.Fault is { } fault
            ? fault.Message
                + (fault.Details.TryGetValue(ReasonDetail, out string? reason) ? $" {reason}" : "")
                + (fault.HelpUri is { } help ? Environment.NewLine + $"Help for {fault.Code}: {help.AbsoluteUri}" : "")
            : "The archive could not be verified.";
        return console.WriteErrorAsync((message + Environment.NewLine).AsMemory(), cancellationToken);
    }
}

// The runic.assets.verify-result/1 payload is { "assetCount": <count> }.
internal sealed record VerifierResult([property: JsonPropertyName("assetCount")] int AssetCount);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VerifierResult))]
internal sealed partial class VerifierJsonContext : JsonSerializerContext;
