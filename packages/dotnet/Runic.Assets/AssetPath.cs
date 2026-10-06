using System;
using System.Diagnostics.CodeAnalysis;

namespace Runic.Assets;

/// <summary>Validates application-relative asset paths at every trust boundary.</summary>
public static class AssetPath
{
    /// <summary>Returns the canonical slash-separated form of a safe application-relative path.</summary>
    /// <exception cref="ArgumentException">The path is empty, rooted, ambiguous, or contains unsupported characters.</exception>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string? error = TryNormalizeCore(value, out string? normalized);
        return error is null ? normalized! : throw new ArgumentException(error, nameof(value));
    }

    /// <summary>
    /// Returns whether a path is a safe application-relative asset path and, if so, its canonical
    /// slash-separated form. Use this for untrusted request paths; it never throws.
    /// </summary>
    public static bool TryNormalize(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        return value is not null && TryNormalizeCore(value, out normalized) is null;
    }

    private static string? TryNormalizeCore(string value, out string? normalized)
    {
        normalized = null;
        if (value.Length == 0 || value != value.Trim())
        {
            return "An asset path cannot be empty or have surrounding whitespace.";
        }

        value = value.Replace('\\', '/');
        if (value[0] == '/' || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
        {
            return "An asset path must be application-relative.";
        }

        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return "An asset path cannot contain empty, current-directory, or parent-directory segments.";
            }

            foreach (char character in segment)
            {
                if (char.IsControl(character) || character is ':' or '?' or '#')
                {
                    return "An asset path contains an unsupported character.";
                }
            }

            for (int index = 0; index <= segment.Length - 3; index++)
            {
                if (segment[index] == '%'
                    && char.IsAsciiHexDigit(segment[index + 1])
                    && char.IsAsciiHexDigit(segment[index + 2]))
                {
                    return "An asset path cannot contain percent-encoded octets.";
                }
            }
        }

        normalized = string.Join('/', segments);
        return null;
    }
}
