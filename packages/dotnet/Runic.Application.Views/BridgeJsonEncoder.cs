using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Runic.Application.Views;

// The string escaping of RFC 8785 (JCS), section 3.2.2.2, for every JSON the
// bridge writes: canonical values and messages. Only '"', '\' and U+0000 to
// U+001F are escaped; \b \t \n \f \r use the short form and the other
// control characters \u00xx in lowercase. Everything else is literal UTF-8,
// so a non-ASCII character takes its 2 to 4 UTF-8 bytes instead of 6 or 12.
//
// Ill-formed text (a lone surrogate in a .NET string, invalid UTF-8 bytes)
// is written as U+FFFD, as the default encoder does. The canonical form
// rejects it before it reaches a writer.
//
// The writer embeds the output in a JSON document, never in HTML or a script,
// so the default encoder's HTML-safe escaping of <>&'+ is not needed.
internal sealed class BridgeJsonEncoder : JavaScriptEncoder
{
    public static readonly BridgeJsonEncoder Instance = new();

    // Options for a bridge Utf8JsonWriter.
    public static readonly JsonWriterOptions WriterOptions = new() { Encoder = Instance };

    // Printable ASCII and DEL apart from '"' and '\': written as is.
    private const string Plain =
        " !#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~\u007F";
    private static readonly SearchValues<byte> s_plainBytes = SearchValues.Create(Encoding.ASCII.GetBytes(Plain));
    private static readonly SearchValues<char> s_plainChars = SearchValues.Create(Plain);

    private const int ShortText = 16;

    private BridgeJsonEncoder() { }

    private static bool IsPlain(int value) => (uint)(value - 0x20) < 0x60 && value != '"' && value != '\\';

    // \u00xx is the longest escape.
    public override int MaxOutputCharactersPerInputCharacter => 6;

    public override bool WillEncode(int unicodeScalar) => unicodeScalar is < 0x20 or '"' or '\\';

    // Returns the first byte to escape or of ill-formed UTF-8. The scan
    // skips plain ASCII and well-formed non-ASCII runs, both vectorized.
    public override int FindFirstCharacterToEncodeUtf8(ReadOnlySpan<byte> utf8Text)
    {
        // Most names and values are short, and a scalar loop finishes them
        // before a vectorized search has started.
        if (utf8Text.Length < ShortText)
        {
            var at = 0;
            while (at < utf8Text.Length && IsPlain(utf8Text[at])) at++;
            if (at == utf8Text.Length) return -1;
            if (utf8Text[at] < 0x80) return at;
        }
        for (var index = 0; ;)
        {
            var next = utf8Text[index..].IndexOfAnyExcept(s_plainBytes);
            if (next < 0) return -1;
            index += next;
            if (utf8Text[index] < 0x80) return index;
            // A multi-byte sequence has only bytes from 0x80, so a run of
            // them holds whole sequences when it is well-formed.
            var rest = utf8Text[index..];
            var end = rest.IndexOfAnyInRange((byte)0, (byte)0x7F);
            var run = end < 0 ? rest : rest[..end];
            if (!Utf8.IsValid(run)) return index + FirstInvalidUtf8(run);
            if (end < 0) return -1;
            index += end;
        }
    }

    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength) =>
        FindFirstCharacterToEncode(new ReadOnlySpan<char>(text, textLength));

    // Returns the first character to escape or lone surrogate.
    private static int FindFirstCharacterToEncode(ReadOnlySpan<char> text)
    {
        if (text.Length < ShortText)
        {
            var at = 0;
            while (at < text.Length && IsPlain(text[at])) at++;
            if (at == text.Length) return -1;
            if (text[at] < 0x80) return at;
        }
        for (var index = 0; ;)
        {
            var next = text[index..].IndexOfAnyExcept(s_plainChars);
            if (next < 0) return -1;
            index += next;
            if (text[index] < 0x80) return index;
            var rest = text[index..];
            var end = rest.IndexOfAnyInRange('\0', '\u007F');
            var run = end < 0 ? rest : rest[..end];
            var surrogate = run.IndexOfAnyInRange('\uD800', '\uDFFF');
            if (surrogate >= 0)
                for (var at = surrogate; at < run.Length; at++)
                {
                    if (!char.IsSurrogate(run[at])) continue;
                    // A well-formed pair is written as is; the base encoder
                    // replaces a lone surrogate with U+FFFD.
                    if (char.IsHighSurrogate(run[at]) && at + 1 < run.Length && char.IsLowSurrogate(run[at + 1])) at++;
                    else return index + at;
                }
            if (end < 0) return -1;
            index += end;
        }
    }

    private static int FirstInvalidUtf8(ReadOnlySpan<byte> text)
    {
        for (var index = 0; index < text.Length;)
        {
            if (Rune.DecodeFromUtf8(text[index..], out _, out var consumed) != OperationStatus.Done) return index;
            index += consumed;
        }
        return -1;
    }

    public override unsafe bool TryEncodeUnicodeScalar(int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
    {
        var destination = new Span<char>(buffer, bufferLength);
        char shortForm = unicodeScalar switch
        {
            '"' => '"',
            '\\' => '\\',
            '\b' => 'b',
            '\t' => 't',
            '\n' => 'n',
            '\f' => 'f',
            '\r' => 'r',
            _ => '\0',
        };
        if (shortForm != '\0')
        {
            if (destination.Length < 2) return Fail(out numberOfCharactersWritten);
            destination[0] = '\\';
            destination[1] = shortForm;
            numberOfCharactersWritten = 2;
            return true;
        }
        // The base encoder asks for U+FFFD in place of ill-formed text, and
        // for nothing else this encoder does not escape.
        if (unicodeScalar is < 0 or > 0xFFFF || destination.Length < 6) return Fail(out numberOfCharactersWritten);
        destination[0] = '\\';
        destination[1] = 'u';
        for (var digit = 0; digit < 4; digit++) destination[2 + digit] = "0123456789abcdef"[(unicodeScalar >> (12 - 4 * digit)) & 0xF];
        numberOfCharactersWritten = 6;
        return true;
    }

    private static bool Fail(out int numberOfCharactersWritten)
    {
        numberOfCharactersWritten = 0;
        return false;
    }
}
