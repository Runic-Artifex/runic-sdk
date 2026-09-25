using System;
using System.IO;
using System.Text.Json;

namespace Runic.Application.Tool;

// This validates already-published MSBuild output and clears stale readiness.
// The managed View Bridge host, not the dev tool, must acknowledge the
// fingerprint it actually loaded.
internal static class ViewBridgeDevelopmentHandoff
{
    internal static void Prepare(DevProjectConfiguration configuration)
    {
        _ = ReadFingerprint(configuration.ViewBridgeReadyManifest)
            ?? throw new DevUsageException(
                "RAPPDEV1005",
                "The opt-in View Bridge ready manifest is missing or has no valid fingerprint. " +
                "Build the project's View Bridge MSBuild target before starting dev mode.");
        try
        {
            // A marker from an earlier dev session could happen to equal a new
            // fingerprint and cause Vite to reload before this host starts.
            File.Delete(configuration.ViewBridgeHostReadyPath);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new DevUsageException(
                "RAPPDEV1005",
                "The stale View Bridge host-ready marker could not be removed.");
        }
    }

    internal static string? ReadFingerprint(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("fingerprint", out JsonElement value) ||
                value.ValueKind != JsonValueKind.String) return null;
            string? fingerprint = value.GetString();
            return fingerprint is { Length: > 0 and <= 128 } &&
                   fingerprint.Trim() == fingerprint &&
                   !fingerprint.Contains('\n') && !fingerprint.Contains('\r')
                ? fingerprint
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
