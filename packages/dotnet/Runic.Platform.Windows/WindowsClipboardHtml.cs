using System.Globalization;
using System.Text;

namespace Runic.Platform.Windows;

// The registered "HTML Format" (CF_HTML): a UTF-8 document after a header whose byte offsets mark the document and the
// fragment that is pasted. Portable: nothing here calls native code.
internal static class WindowsClipboardHtml
{
    internal const string StartMarker = "<!--StartFragment-->";
    internal const string EndMarker = "<!--EndFragment-->";
    private static readonly CompositeFormat HeaderFormat =
        CompositeFormat.Parse("Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n");
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // The clipboard bytes for html, NUL-terminated. A fragment is wrapped in a document; a document's body becomes the
    // fragment unless it already has the fragment markers.
    internal static byte[] Encode(string html)
    {
        string before, fragment, after;
        int start = html.IndexOf(StartMarker, StringComparison.Ordinal);
        int end = start < 0 ? -1 : html.IndexOf(EndMarker, start + StartMarker.Length, StringComparison.Ordinal);
        if (start >= 0 && end >= 0)
        {
            before = html[..(start + StartMarker.Length)];
            fragment = html[(start + StartMarker.Length)..end];
            after = html[end..];
        }
        else if (Body(html) is var (open, close))
        {
            before = html[..open] + StartMarker;
            fragment = html[open..close];
            after = EndMarker + html[close..];
        }
        else
        {
            before = "<html><body>" + StartMarker;
            fragment = html;
            after = EndMarker + "</body></html>";
        }
        int header = string.Format(CultureInfo.InvariantCulture, HeaderFormat, 0, 0, 0, 0).Length;
        int startFragment = header + Utf8.GetByteCount(before);
        int endFragment = startFragment + Utf8.GetByteCount(fragment);
        int endHtml = endFragment + Utf8.GetByteCount(after);
        string text = string.Format(CultureInfo.InvariantCulture, HeaderFormat, header, endHtml, startFragment, endFragment) + before + fragment + after;
        var bytes = new byte[endHtml + 1];
        Utf8.GetBytes(text, bytes);
        return bytes;
    }

    // The end of the body's opening tag and the start of its closing tag, or null without a body.
    private static (int Open, int Close)? Body(string html)
    {
        int tag = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (tag < 0 || tag + 5 < html.Length && html[tag + 5] is not ('>' or ' ' or '\t' or '\r' or '\n' or '/')) return null;
        int open = html.IndexOf('>', tag);
        if (open < 0) return null;
        int close = html.IndexOf("</body", open + 1, StringComparison.OrdinalIgnoreCase);
        return close < 0 ? null : (open + 1, close);
    }

    // The fragment of clipboard bytes, at most maximumCharacters long.
    internal static PlatformResult<string?> Decode(ReadOnlySpan<byte> data, int maximumCharacters)
    {
        int nul = data.IndexOf((byte)0);
        if (nul >= 0) data = data[..nul];
        long startHtml = -1, endHtml = -1, startFragment = -1, endFragment = -1;
        int position = 0;
        // The header is ASCII "Key:Value" lines before the document.
        while (position < data.Length && data[position] != (byte)'<')
        {
            int lineEnd = data[position..].IndexOfAny((byte)'\r', (byte)'\n');
            var line = lineEnd < 0 ? data[position..] : data.Slice(position, lineEnd);
            int colon = line.IndexOf((byte)':');
            if (colon < 0) break;
            var key = Encoding.ASCII.GetString(line[..colon]);
            if (long.TryParse(Encoding.ASCII.GetString(line[(colon + 1)..]), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            {
                if (key == "StartHTML") startHtml = value;
                else if (key == "EndHTML") endHtml = value;
                else if (key == "StartFragment") startFragment = value;
                else if (key == "EndFragment") endFragment = value;
            }
            if (lineEnd < 0) { position = data.Length; break; }
            position += lineEnd + 1;
            if (position < data.Length && data[position - 1] == (byte)'\r' && data[position] == (byte)'\n') position++;
        }
        // Writers that leave out the fragment offsets mark the document only.
        if (startFragment < 0 || endFragment < 0) (startFragment, endFragment) = (startHtml, endHtml);
        if (startFragment < 0 || endFragment < startFragment || endFragment > data.Length)
            return new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData);
        var fragment = data[(int)startFragment..(int)endFragment];
        // UTF-8 needs at least one byte per character and at most three per UTF-16 unit.
        if (fragment.Length > (long)maximumCharacters * 3) return new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge);
        try
        {
            string text = Utf8.GetString(fragment);
            return text.Length > maximumCharacters
                ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge)
                : new PlatformResult<string?>.Success(text);
        }
        catch (DecoderFallbackException) { return new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData); }
    }
}
