using System;
using System.Collections.Generic;
using System.IO;

namespace WebUIToolkit.Hosting.Build;

/// <summary>Resolves stable media types from frontend asset extensions without platform registration.</summary>
public sealed class DefaultFrontendMediaTypeResolver : IFrontendMediaTypeResolver
{
    private static readonly Dictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".avif"] = "image/avif",
            [".css"] = "text/css; charset=utf-8",
            [".gif"] = "image/gif",
            [".htm"] = "text/html; charset=utf-8",
            [".html"] = "text/html; charset=utf-8",
            [".ico"] = "image/x-icon",
            [".jpeg"] = "image/jpeg",
            [".jpg"] = "image/jpeg",
            [".js"] = "text/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".png"] = "image/png",
            [".svg"] = "image/svg+xml",
            [".txt"] = "text/plain; charset=utf-8",
            [".wasm"] = "application/wasm",
            [".webmanifest"] = "application/manifest+json; charset=utf-8",
            [".webp"] = "image/webp",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".xml"] = "application/xml; charset=utf-8",
        };

    /// <summary>Resolves a deterministic media type, falling back to binary content.</summary>
    /// <param name="relativePath">The normalized or input asset path.</param>
    /// <returns>A stable Internet media type.</returns>
    public string Resolve(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var extension = Path.GetExtension(relativePath);
        return MediaTypes.TryGetValue(extension, out var mediaType)
            ? mediaType
            : "application/octet-stream";
    }
}

/// <summary>Maps a frontend asset path to a deterministic Internet media type.</summary>
public interface IFrontendMediaTypeResolver
{
    /// <summary>Resolves a media type for an application-relative asset path.</summary>
    /// <param name="relativePath">The asset path.</param>
    /// <returns>A non-empty stable media type.</returns>
    string Resolve(string relativePath);
}
