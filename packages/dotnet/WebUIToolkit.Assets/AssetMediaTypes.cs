using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;

namespace WebUIToolkit.Assets;

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
            [".png"] = "image/png",
            [".svg"] = "image/svg+xml",
            [".txt"] = "text/plain",
            [".wasm"] = "application/wasm",
            [".webp"] = "image/webp",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".xml"] = "application/xml",
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
