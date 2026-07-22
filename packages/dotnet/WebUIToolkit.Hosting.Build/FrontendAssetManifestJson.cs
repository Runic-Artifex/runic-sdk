using System;
using System.Text;
using System.Text.Json;

namespace WebUIToolkit.Hosting.Build;

/// <summary>Writes validated frontend asset manifests using canonical property and asset order.</summary>
public static class FrontendAssetManifestJson
{
    /// <summary>Serializes a validated manifest to deterministic UTF-8 JSON bytes.</summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    public static byte[] SerializeToUtf8Bytes(IFrontendAssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ThrowIfInvalid(manifest);

        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("manifestVersion", manifest.ManifestVersion);
            writer.WriteStartArray("assets");
            foreach (var asset in manifest.Assets)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", asset.RelativePath);
                writer.WriteString("mediaType", asset.MediaType);
                writer.WriteNumber("length", asset.Length);
                writer.WriteString("sha256", asset.Sha256);
                writer.WriteBoolean("isEntryPoint", asset.IsEntryPoint);
                if (asset.BrotliPath is not null)
                {
                    writer.WriteString("brotliPath", asset.BrotliPath);
                }

                if (asset.GzipPath is not null)
                {
                    writer.WriteString("gzipPath", asset.GzipPath);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Serializes a validated manifest to deterministic compact JSON.</summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>Canonical JSON text without a byte-order mark.</returns>
    public static string Serialize(IFrontendAssetManifest manifest)
        => Encoding.UTF8.GetString(SerializeToUtf8Bytes(manifest));

    private static void ThrowIfInvalid(IFrontendAssetManifest manifest)
    {
        var issues = FrontendAssetManifestValidator.Validate(manifest);
        if (issues.Count != 0)
        {
            throw new ArgumentException(issues[0].Message, nameof(manifest));
        }
    }
}
