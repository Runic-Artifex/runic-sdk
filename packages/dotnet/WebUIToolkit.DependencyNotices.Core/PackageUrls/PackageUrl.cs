using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace WebUIToolkit.DependencyNotices;

/// <summary>
/// A parsed, canonical Package URL used as an exact dependency notice identity.
/// </summary>
public sealed class PackageUrl
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private PackageUrl(
        string type,
        string? packageNamespace,
        string name,
        string version,
        IReadOnlyDictionary<string, string> qualifiers,
        string? subpath,
        string originalValue,
        string canonicalValue)
    {
        Type = type;
        Namespace = packageNamespace;
        Name = name;
        Version = version;
        Qualifiers = qualifiers;
        Subpath = subpath;
        OriginalValue = originalValue;
        CanonicalValue = canonicalValue;
    }

    /// <summary>Gets the lower-case Package URL type.</summary>
    public string Type { get; }

    /// <summary>Gets the decoded package namespace, or <see langword="null"/>.</summary>
    public string? Namespace { get; }

    /// <summary>Gets the decoded package name.</summary>
    public string Name { get; }

    /// <summary>Gets the decoded exact package version.</summary>
    public string Version { get; }

    /// <summary>Gets decoded qualifiers keyed by canonical lower-case key.</summary>
    public IReadOnlyDictionary<string, string> Qualifiers { get; }

    /// <summary>Gets the decoded slash-separated package subpath, or <see langword="null"/>.</summary>
    public string? Subpath { get; }

    /// <summary>Gets the input spelling supplied to <see cref="Parse(string)"/>.</summary>
    public string OriginalValue { get; }

    /// <summary>Gets the normalized Package URL used for exact identity comparison.</summary>
    public string CanonicalValue { get; }

    /// <summary>Parses an exact-version Package URL for dependency notice identity.</summary>
    /// <param name="value">The Package URL text.</param>
    /// <returns>The parsed and canonicalized Package URL.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> is not a valid notice identity.</exception>
    public static PackageUrl Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryParseCore(value, out PackageUrl? result, out string? error))
        {
            throw new FormatException($"Invalid Package URL: {error}");
        }

        return result;
    }

    /// <summary>Attempts to parse an exact-version Package URL for dependency notice identity.</summary>
    /// <param name="value">The Package URL text.</param>
    /// <param name="packageUrl">The parsed Package URL when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when parsing succeeds; otherwise, <see langword="false"/>.</returns>
    public static bool TryParse(
        string? value,
        [NotNullWhen(true)] out PackageUrl? packageUrl)
    {
        if (value is null)
        {
            packageUrl = null;
            return false;
        }

        return TryParseCore(value, out packageUrl, out _);
    }

    /// <inheritdoc/>
    public override string ToString() => CanonicalValue;

    private static bool TryParseCore(
        string value,
        [NotNullWhen(true)] out PackageUrl? packageUrl,
        out string? error)
    {
        packageUrl = null;
        error = null;

        if (value.Length == 0)
        {
            error = "the value is empty.";
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character) || char.IsWhiteSpace(character) || character == '\\')
            {
                error = "raw whitespace, control characters, and backslashes are not permitted.";
                return false;
            }
        }

        int schemeSeparator = value.IndexOf(':');
        if (schemeSeparator < 0 ||
            !value.AsSpan(0, schemeSeparator).Equals("pkg", StringComparison.OrdinalIgnoreCase))
        {
            error = "the required pkg scheme is missing.";
            return false;
        }

        string remainder = value[(schemeSeparator + 1)..];
        if (remainder.StartsWith('/'))
        {
            error = "an authority or leading slash is not permitted.";
            return false;
        }

        string? rawSubpath = null;
        int fragmentSeparator = remainder.IndexOf('#');
        if (fragmentSeparator >= 0)
        {
            if (remainder.IndexOf('#', fragmentSeparator + 1) >= 0)
            {
                error = "multiple fragment separators are not permitted.";
                return false;
            }

            rawSubpath = remainder[(fragmentSeparator + 1)..];
            remainder = remainder[..fragmentSeparator];
            if (rawSubpath.Length == 0)
            {
                error = "an empty subpath fragment is not permitted.";
                return false;
            }
        }

        string? rawQuery = null;
        int querySeparator = remainder.IndexOf('?');
        if (querySeparator >= 0)
        {
            if (remainder.IndexOf('?', querySeparator + 1) >= 0)
            {
                error = "multiple query separators are not permitted.";
                return false;
            }

            rawQuery = remainder[(querySeparator + 1)..];
            remainder = remainder[..querySeparator];
            if (rawQuery.Length == 0)
            {
                error = "an empty qualifier query is not permitted.";
                return false;
            }
        }

        if (rawSubpath?.Contains('?') == true)
        {
            error = "a query separator cannot occur inside the subpath.";
            return false;
        }

        int typeSeparator = remainder.IndexOf('/');
        if (typeSeparator <= 0)
        {
            error = "a package type and name are required.";
            return false;
        }

        string rawType = remainder[..typeSeparator];
        if (!IsValidType(rawType))
        {
            error = "the package type is invalid.";
            return false;
        }

        string type = rawType.ToLowerInvariant();
        string rawIdentity = remainder[(typeSeparator + 1)..];
        int versionSeparator = rawIdentity.LastIndexOf('@');
        if (versionSeparator <= 0 || versionSeparator == rawIdentity.Length - 1)
        {
            error = "an exact package version is required for notice identity.";
            return false;
        }

        string rawPath = rawIdentity[..versionSeparator];
        string rawVersion = rawIdentity[(versionSeparator + 1)..];
        if (rawVersion.Contains('/'))
        {
            error = "a slash in the version must be percent encoded.";
            return false;
        }

        string[] rawSegments = rawPath.Split('/');
        if (rawSegments.Length == 0 || Array.Exists(rawSegments, static segment => segment.Length == 0))
        {
            error = "empty namespace or name segments are not permitted.";
            return false;
        }

        string[] decodedSegments = new string[rawSegments.Length];
        for (int index = 0; index < rawSegments.Length; index++)
        {
            if (!TryDecode(rawSegments[index], out string? segment) ||
                !IsNonEmptyComponent(segment) ||
                segment.Contains('/') ||
                segment.Contains('\\'))
            {
                error = "a namespace or name segment has invalid percent encoding or content.";
                return false;
            }

            decodedSegments[index] = segment;
        }

        string name = decodedSegments[^1];
        string? packageNamespace = decodedSegments.Length == 1
            ? null
            : string.Join('/', decodedSegments, 0, decodedSegments.Length - 1);

        if (type == "nuget" && packageNamespace is not null)
        {
            error = "NuGet Package URLs do not have a namespace.";
            return false;
        }

        if (type == "npm" && packageNamespace is not null &&
            (decodedSegments.Length != 2 || packageNamespace.Length == 1 || packageNamespace[0] != '@'))
        {
            error = "an npm namespace must be a single scope beginning with '@'.";
            return false;
        }

        if (!TryDecode(rawVersion, out string? version) || !IsNonEmptyComponent(version))
        {
            error = "the version has invalid percent encoding or content.";
            return false;
        }

        if (!TryParseQualifiers(rawQuery, out IReadOnlyDictionary<string, string>? qualifiers, out error))
        {
            return false;
        }

        if (!TryParseSubpath(rawSubpath, out string? subpath, out error))
        {
            return false;
        }

        StringBuilder canonical = new("pkg:");
        canonical.Append(type).Append('/');
        if (packageNamespace is not null)
        {
            string[] namespaceSegments = packageNamespace.Split('/');
            for (int index = 0; index < namespaceSegments.Length; index++)
            {
                if (index != 0)
                {
                    canonical.Append('/');
                }

                AppendEncoded(canonical, namespaceSegments[index]);
            }

            canonical.Append('/');
        }

        AppendEncoded(canonical, name);
        canonical.Append('@');
        AppendEncoded(canonical, version);

        if (qualifiers.Count != 0)
        {
            canonical.Append('?');
            bool first = true;
            foreach ((string key, string qualifierValue) in qualifiers)
            {
                if (!first)
                {
                    canonical.Append('&');
                }

                first = false;
                canonical.Append(key).Append('=');
                AppendEncoded(canonical, qualifierValue);
            }
        }

        if (subpath is not null)
        {
            canonical.Append('#');
            string[] subpathSegments = subpath.Split('/');
            for (int index = 0; index < subpathSegments.Length; index++)
            {
                if (index != 0)
                {
                    canonical.Append('/');
                }

                AppendEncoded(canonical, subpathSegments[index]);
            }
        }

        packageUrl = new PackageUrl(
            type,
            packageNamespace,
            name,
            version,
            qualifiers,
            subpath,
            value,
            canonical.ToString());
        return true;
    }

    private static bool TryParseQualifiers(
        string? rawQuery,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, string>? qualifiers,
        out string? error)
    {
        SortedDictionary<string, string> parsed = new(StringComparer.Ordinal);
        qualifiers = null;
        error = null;

        if (rawQuery is not null)
        {
            foreach (string pair in rawQuery.Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0 || equals == pair.Length - 1 || pair.IndexOf('=', equals + 1) >= 0)
                {
                    error = "each qualifier must contain one non-empty key=value pair.";
                    return false;
                }

                string rawKey = pair[..equals];
                if (!IsValidQualifierKey(rawKey))
                {
                    error = "a qualifier key is invalid.";
                    return false;
                }

                string key = rawKey.ToLowerInvariant();
                if (!TryDecode(pair[(equals + 1)..], out string? value) || !IsNonEmptyComponent(value))
                {
                    error = "a qualifier value has invalid percent encoding or content.";
                    return false;
                }

                if (!parsed.TryAdd(key, value))
                {
                    error = $"duplicate qualifier key '{key}'.";
                    return false;
                }
            }
        }

        qualifiers = new ReadOnlyDictionary<string, string>(parsed);
        return true;
    }

    private static bool TryParseSubpath(
        string? rawSubpath,
        out string? subpath,
        out string? error)
    {
        subpath = null;
        error = null;
        if (rawSubpath is null)
        {
            return true;
        }

        string[] rawSegments = rawSubpath.Split('/');
        string[] decodedSegments = new string[rawSegments.Length];
        for (int index = 0; index < rawSegments.Length; index++)
        {
            if (rawSegments[index].Length == 0 ||
                !TryDecode(rawSegments[index], out string? segment) ||
                !IsNonEmptyComponent(segment) ||
                segment is "." or ".." ||
                segment.Contains('/') ||
                segment.Contains('\\'))
            {
                error = "the subpath contains an empty, dot, traversal, or invalid segment.";
                return false;
            }

            decodedSegments[index] = segment;
        }

        subpath = string.Join('/', decodedSegments);
        return true;
    }

    private static bool IsValidType(string value)
    {
        if (value.Length == 0 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!(IsAsciiLetter(character) || IsAsciiDigit(character) || character is '.' or '+' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidQualifierKey(string value)
    {
        if (value.Length == 0 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!(IsAsciiLetter(character) || IsAsciiDigit(character) || character is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDecode(string value, [NotNullWhen(true)] out string? decoded)
    {
        decoded = null;
        ArrayBufferWriter<byte> bytes = new(value.Length);
        int chunkStart = 0;

        try
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] != '%')
                {
                    continue;
                }

                WriteUtf8(value.AsSpan(chunkStart, index - chunkStart), bytes);
                if (index + 2 >= value.Length ||
                    !TryHex(value[index + 1], out int high) ||
                    !TryHex(value[index + 2], out int low))
                {
                    return false;
                }

                Span<byte> destination = bytes.GetSpan(1);
                destination[0] = (byte)((high << 4) | low);
                bytes.Advance(1);
                index += 2;
                chunkStart = index + 1;
            }

            WriteUtf8(value.AsSpan(chunkStart), bytes);
            decoded = StrictUtf8.GetString(bytes.WrittenSpan);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteUtf8(ReadOnlySpan<char> value, ArrayBufferWriter<byte> bytes)
    {
        if (value.Length == 0)
        {
            return;
        }

        int byteCount = StrictUtf8.GetByteCount(value);
        Span<byte> destination = bytes.GetSpan(byteCount);
        int written = StrictUtf8.GetBytes(value, destination);
        bytes.Advance(written);
    }

    private static void AppendEncoded(StringBuilder builder, string value)
    {
        Span<byte> bytes = stackalloc byte[4];
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && IsUnreservedOrColon((char)rune.Value))
            {
                builder.Append((char)rune.Value);
                continue;
            }

            int byteCount = rune.EncodeToUtf8(bytes);
            for (int index = 0; index < byteCount; index++)
            {
                builder.Append('%');
                builder.Append(ToUpperHex(bytes[index] >> 4));
                builder.Append(ToUpperHex(bytes[index] & 0x0f));
            }
        }
    }

    private static bool IsNonEmptyComponent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnreservedOrColon(char value) =>
        IsAsciiLetter(value) || IsAsciiDigit(value) || value is '-' or '.' or '_' or '~' or ':';

    private static bool IsAsciiLetter(char value) =>
        (value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z');

    private static bool IsAsciiDigit(char value) => value >= '0' && value <= '9';

    private static bool TryHex(char value, out int hex)
    {
        if (value >= '0' && value <= '9')
        {
            hex = value - '0';
            return true;
        }

        if (value >= 'a' && value <= 'f')
        {
            hex = value - 'a' + 10;
            return true;
        }

        if (value >= 'A' && value <= 'F')
        {
            hex = value - 'A' + 10;
            return true;
        }

        hex = 0;
        return false;
    }

    private static char ToUpperHex(int value) =>
        (char)(value < 10 ? '0' + value : 'A' + value - 10);
}
