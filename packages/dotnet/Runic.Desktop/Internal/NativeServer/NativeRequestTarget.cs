using System.Text;

namespace Runic.Desktop.Internal.NativeServer;

// The request path as Kestrel presents it: percent-decoded except an encoded slash, which stays %2F, interpreted as
// UTF-8, with dot segments removed (RFC 3986 section 5.2.4). WebUiWindow and the content handlers rely on that shape.
internal static class NativeRequestTarget
{
    internal static bool TryParse(string target, out string path, out string query)
    {
        var questionMark = target.IndexOf('?', StringComparison.Ordinal);
        var raw = questionMark < 0 ? target : target[..questionMark];
        query = questionMark < 0 ? string.Empty : target[questionMark..];
        path = string.Empty;
        if (raw.Length == 0 || raw[0] != '/')
        {
            return false;
        }
        if (!TryDecode(raw, out var decoded))
        {
            return false;
        }
        path = RemoveDotSegments(decoded);
        return true;
    }

    private static bool TryDecode(string raw, out string decoded)
    {
        if (!raw.Contains('%', StringComparison.Ordinal))
        {
            decoded = raw;
            return true;
        }
        var bytes = new byte[raw.Length];
        var length = 0;
        for (var index = 0; index < raw.Length; index++)
        {
            var character = raw[index];
            if (character > 0x7f)
            {
                decoded = string.Empty;
                return false;
            }
            if (character == '%' && index + 2 < raw.Length &&
                Uri.IsHexDigit(raw[index + 1]) && Uri.IsHexDigit(raw[index + 2]))
            {
                var value = (byte)((Uri.FromHex(raw[index + 1]) << 4) | Uri.FromHex(raw[index + 2]));
                if (value == (byte)'/')
                {
                    bytes[length++] = (byte)'%';
                    bytes[length++] = (byte)raw[index + 1];
                    bytes[length++] = (byte)raw[index + 2];
                }
                else
                {
                    bytes[length++] = value;
                }
                index += 2;
                continue;
            }
            bytes[length++] = (byte)character;
        }
        try
        {
            decoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, 0, length);
            return true;
        }
        catch (DecoderFallbackException)
        {
            decoded = string.Empty;
            return false;
        }
    }

    internal static string RemoveDotSegments(string path)
    {
        if (!path.Contains('.', StringComparison.Ordinal))
        {
            return path;
        }
        var segments = path.Split('/');
        var output = new List<string>(segments.Length);
        var trailingSlash = false;
        for (var index = 1; index < segments.Length; index++)
        {
            var segment = segments[index];
            var last = index == segments.Length - 1;
            if (segment == ".")
            {
                trailingSlash = last;
                continue;
            }
            if (segment == "..")
            {
                if (output.Count > 0) output.RemoveAt(output.Count - 1);
                trailingSlash = last;
                continue;
            }
            output.Add(segment);
            trailingSlash = false;
        }
        var result = "/" + string.Join('/', output);
        return trailingSlash && !result.EndsWith('/') ? result + "/" : result;
    }
}
