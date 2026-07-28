using System;

namespace WebUIToolkit.Assets;

/// <summary>Validates application-relative asset paths at every trust boundary.</summary>
public static class AssetPath
{
    /// <summary>Returns the canonical slash-separated form of a safe application-relative path.</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value != value.Trim())
        {
            throw new ArgumentException("An asset path cannot be empty or have surrounding whitespace.", nameof(value));
        }

        value = value.Replace('\\', '/');
        if (value[0] == '/' || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':'))
        {
            throw new ArgumentException("An asset path must be application-relative.", nameof(value));
        }

        string[] segments = value.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new ArgumentException(
                    "An asset path cannot contain empty, current-directory, or parent-directory segments.",
                    nameof(value));
            }

            foreach (char character in segment)
            {
                if (char.IsControl(character) || character is ':' or '?' or '#')
                {
                    throw new ArgumentException("An asset path contains an unsupported character.", nameof(value));
                }
            }

            for (int index = 0; index <= segment.Length - 3; index++)
            {
                if (segment[index] == '%'
                    && char.IsAsciiHexDigit(segment[index + 1])
                    && char.IsAsciiHexDigit(segment[index + 2]))
                {
                    throw new ArgumentException(
                        "An asset path cannot contain percent-encoded octets.",
                        nameof(value));
                }
            }
        }

        return string.Join('/', segments);
    }
}
