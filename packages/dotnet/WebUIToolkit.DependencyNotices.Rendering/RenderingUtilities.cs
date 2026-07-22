using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace WebUIToolkit.DependencyNotices.Rendering;

internal static class RenderingUtilities
{
    internal static readonly UTF8Encoding Utf8NoBom = new(false, true);

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool IsSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    internal static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = true,
        }))
        {
            write(writer);
        }

        ReadOnlySpan<byte> written = buffer.WrittenSpan;
        int carriageReturns = 0;
        for (int index = 0; index + 1 < written.Length; index++)
        {
            if (written[index] == (byte)'\r' && written[index + 1] == (byte)'\n')
            {
                carriageReturns++;
            }
        }

        byte[] result = new byte[written.Length - carriageReturns + 1];
        int destination = 0;
        for (int index = 0; index < written.Length; index++)
        {
            if (written[index] == (byte)'\r' && index + 1 < written.Length && written[index + 1] == (byte)'\n')
            {
                continue;
            }

            result[destination++] = written[index];
        }

        result[destination] = (byte)'\n';
        return result;
    }

    internal static string EnumToken<T>(T value)
        where T : struct, Enum
    {
        string name = value.ToString();
        return name.Length == 0
            ? string.Empty
            : string.Concat(char.ToLowerInvariant(name[0]).ToString(CultureInfo.InvariantCulture), name.AsSpan(1));
    }

    internal static string HtmlEncode(string value)
    {
        StringBuilder builder = new(value.Length);
        foreach (char character in value)
        {
            if ((character < ' ' && character is not '\t' and not '\n' and not '\r') || character == '\u007f')
            {
                _ = builder.Append("&#x").Append(((int)character).ToString("X", CultureInfo.InvariantCulture)).Append(';');
                continue;
            }

            _ = character switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                '\'' => builder.Append("&#39;"),
                _ => builder.Append(character),
            };
        }

        return builder.ToString();
    }

    internal static bool IsPortableRelativeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] is '/' or '\\')
        {
            return false;
        }

        if (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':')
        {
            return false;
        }

        string normalized = value.Replace('\\', '/');
        foreach (string segment in normalized.Split('/'))
        {
            if (segment is "" or "." or "..")
            {
                return false;
            }
        }

        return true;
    }
}
