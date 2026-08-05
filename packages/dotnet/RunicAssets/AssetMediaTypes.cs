using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;

namespace RunicAssets;

/// <summary>Provides deterministic, platform-independent asset media types.</summary>
public static class AssetMediaTypes
{
    private static readonly FrozenDictionary<string, string> Known =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".avif"] = "image/avif",
            [".css"] = "text/css",
            [".gif"] = "image/gif",
            [".htm"] = "text/html",
            [".html"] = "text/html",
            [".ico"] = "image/x-icon",
            [".jpeg"] = "image/jpeg",
            [".jpg"] = "image/jpeg",
            [".js"] = "text/javascript",
            [".json"] = "application/json",
            [".map"] = "application/json",
            [".mjs"] = "text/javascript",
            [".mp3"] = "audio/mpeg",
            [".mp4"] = "video/mp4",
            [".otf"] = "font/otf",
            [".pdf"] = "application/pdf",
            [".png"] = "image/png",
            [".svg"] = "image/svg+xml",
            [".txt"] = "text/plain",
            [".ttf"] = "font/ttf",
            [".wasm"] = "application/wasm",
            [".webp"] = "image/webp",
            [".webm"] = "video/webm",
            [".webmanifest"] = "application/manifest+json",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".xml"] = "application/xml",
            [".wav"] = "audio/wav",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves a known media type, falling back to <c>application/octet-stream</c>.</summary>
    public static string Resolve(string relativePath)
    {
        string normalized = AssetPath.Normalize(relativePath);
        return Known.TryGetValue(Path.GetExtension(normalized), out string? mediaType)
            ? mediaType
            : "application/octet-stream";
    }
}
