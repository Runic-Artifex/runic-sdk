using System.Globalization;
using System.Text;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.DirectoryServices;

/// <summary>LDAP filter and distinguished-name escaping are deliberately separate operations.</summary>
public static class DirectoryNames
{
    /// <summary>Escapes a text assertion value according to RFC 4515, retaining valid Unicode as UTF-8 escapes.</summary>
    public static string EscapeFilterValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new UTF8Encoding(false, true).GetBytes(value);
        var result = new StringBuilder();
        foreach (var item in bytes)
        {
            if (item is 0 or 0x28 or 0x29 or 0x2a or 0x5c || item >= 0x80)
                result.Append('\\').Append(item.ToString("X2", CultureInfo.InvariantCulture));
            else result.Append((char)item);
        }
        return result.ToString();
    }

    /// <summary>Escapes one RDN attribute value, not an entire distinguished name.</summary>
    public static string EscapeRdnValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ = new UTF8Encoding(false, true).GetByteCount(value);
        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\0') { result.Append("\\00"); continue; }
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' ||
                (i == 0 && c is ' ' or '#') || (i == value.Length - 1 && c == ' '))
                result.Append('\\');
            result.Append(c);
        }
        return result.ToString();
    }

    internal static void Attribute(string value)
    {
        NativeError.Text(value, nameof(value));
        if (value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or ';' or '=' or '*')))
            throw new ArgumentException("Invalid LDAP attribute description.", nameof(value));
    }
}
