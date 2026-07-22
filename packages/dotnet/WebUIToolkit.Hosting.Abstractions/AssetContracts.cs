using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Hosting;

/// <summary>Describes one immutable frontend asset declared by a manifest.</summary>
public sealed record FrontendAsset
{
    /// <summary>Initializes frontend asset metadata.</summary>
    /// <param name="relativePath">The normalized, application-relative asset path.</param>
    /// <param name="mediaType">The asset's media type.</param>
    /// <param name="length">The uncompressed asset length in bytes.</param>
    /// <param name="sha256">The hexadecimal SHA-256 digest of the uncompressed asset.</param>
    /// <param name="isEntryPoint">Whether this asset is the application entry point.</param>
    /// <param name="brotliPath">The optional application-relative Brotli variant path.</param>
    /// <param name="gzipPath">The optional application-relative gzip variant path.</param>
    public FrontendAsset(
        string relativePath,
        string mediaType,
        long length,
        string sha256,
        bool isEntryPoint = false,
        string? brotliPath = null,
        string? gzipPath = null)
    {
        RelativePath = NormalizeRelativePath(relativePath, nameof(relativePath));
        MediaType = NormalizeMediaType(mediaType, nameof(mediaType));
        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "Asset length cannot be negative.");
        }

        Length = length;
        Sha256 = NormalizeSha256(sha256, nameof(sha256));
        IsEntryPoint = isEntryPoint;
        BrotliPath = NormalizeOptionalRelativePath(brotliPath, nameof(brotliPath));
        GzipPath = NormalizeOptionalRelativePath(gzipPath, nameof(gzipPath));

        if (StringComparer.OrdinalIgnoreCase.Equals(RelativePath, BrotliPath)
            || StringComparer.OrdinalIgnoreCase.Equals(RelativePath, GzipPath))
        {
            throw new ArgumentException("A compressed variant path must differ from the asset path.");
        }

        if (BrotliPath is not null && StringComparer.OrdinalIgnoreCase.Equals(BrotliPath, GzipPath))
        {
            throw new ArgumentException("Brotli and gzip variant paths must differ.");
        }
    }

    /// <summary>Gets the normalized, application-relative asset path.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the asset's media type.</summary>
    public string MediaType { get; }

    /// <summary>Gets the uncompressed asset length in bytes.</summary>
    public long Length { get; }

    /// <summary>Gets the lowercase hexadecimal SHA-256 digest.</summary>
    public string Sha256 { get; }

    /// <summary>Gets whether this asset is the application entry point.</summary>
    public bool IsEntryPoint { get; }

    /// <summary>Gets the normalized path of the optional Brotli variant.</summary>
    public string? BrotliPath { get; }

    /// <summary>Gets the normalized path of the optional gzip variant.</summary>
    public string? GzipPath { get; }

    private static string NormalizeRequiredText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptionalRelativePath(string? value, string parameterName)
        => value is null ? null : NormalizeRelativePath(value, parameterName);

    private static string NormalizeRelativePath(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("Asset paths cannot contain control characters.", parameterName);
            }
        }

        value = NormalizeRequiredText(value, parameterName).Replace('\\', '/');
        if (value[0] == '/' || IsDriveQualified(value))
        {
            throw new ArgumentException("Asset paths must be application-relative.", parameterName);
        }

        var segments = value.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException(
                    "Asset paths cannot contain empty, current-directory, or parent-directory segments.",
                    parameterName);
            }

            if (segment.IndexOfAny(['\0', ':', '?', '#']) >= 0)
            {
                throw new ArgumentException("Asset paths contain an unsupported character.", parameterName);
            }

            RejectEncodedPathSyntax(segment, parameterName);
        }

        return string.Join('/', segments);
    }

    private static bool IsDriveQualified(string value)
        => value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':';

    private static string NormalizeMediaType(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                throw new ArgumentException("Asset media types cannot contain control characters.", parameterName);
            }
        }

        return NormalizeRequiredText(value, parameterName);
    }

    private static void RejectEncodedPathSyntax(string segment, string parameterName)
    {
        for (var index = 0; index <= segment.Length - 3; index++)
        {
            if (segment[index] != '%'
                || !TryHexValue(segment[index + 1], out _)
                || !TryHexValue(segment[index + 2], out _))
            {
                continue;
            }

            throw new ArgumentException(
                "Asset paths cannot contain percent-encoded octets.",
                parameterName);
        }
    }

    private static bool TryHexValue(char character, out int value)
    {
        if (character is >= '0' and <= '9')
        {
            value = character - '0';
            return true;
        }

        character = char.ToUpperInvariant(character);
        if (character is >= 'A' and <= 'F')
        {
            value = character - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    private static string NormalizeSha256(string value, string parameterName)
    {
        value = NormalizeRequiredText(value, parameterName);
        if (value.Length != 64)
        {
            throw new ArgumentException(
                "A SHA-256 digest must contain exactly 64 hexadecimal characters.",
                parameterName);
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                throw new ArgumentException(
                    "A SHA-256 digest can contain only hexadecimal characters.",
                    parameterName);
            }
        }

        return value.ToLowerInvariant();
    }
}

/// <summary>Provides deterministic metadata for the complete frontend asset set.</summary>
public interface IFrontendAssetManifest
{
    /// <summary>Gets the manifest contract version.</summary>
    string ManifestVersion { get; }

    /// <summary>Gets the manifest assets in deterministic order.</summary>
    IReadOnlyList<FrontendAsset> Assets { get; }
}

/// <summary>Validates and opens only frontend assets declared by a manifest.</summary>
public interface IFrontendAssetProvider
{
    /// <summary>Gets the immutable manifest that defines the provider's addressable content.</summary>
    IFrontendAssetManifest Manifest { get; }

    /// <summary>Validates the provider's manifest and backing asset content.</summary>
    ValueTask ValidateAsync(CancellationToken cancellationToken);

    /// <summary>Opens a readable stream for one normalized manifest-relative path.</summary>
    ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
}
