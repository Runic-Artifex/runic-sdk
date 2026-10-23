namespace Runic.Platform;

// The rules of a DesktopClipboardContent. One source: Runic.Platform checks content with this copy, and
// Runic.Platform.Runtime compiles the file too for the providers, as it does DesktopMenuRules.
internal static class DesktopClipboardRules
{
    internal const int MaximumFiles = 4096;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // Throws for content that cannot be written at all.
    internal static void Check(DesktopClipboardContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Formats(content) == DesktopClipboardFormats.None)
            throw new ArgumentException("Clipboard content needs at least one format.", nameof(content));
        var files = content.Files.IsDefault ? [] : content.Files;
        if (files.Length > MaximumFiles) throw new ArgumentException($"Clipboard content has at most {MaximumFiles} files.", nameof(content));
        foreach (var path in files)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A clipboard file path is null or empty.", nameof(content));
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Clipboard file paths must be fully qualified.", nameof(content));
        }
    }

    // The formats the content sets.
    internal static DesktopClipboardFormats Formats(DesktopClipboardContent content) =>
        (content.Text is null ? 0 : DesktopClipboardFormats.Text)
        | (content.Html is null ? 0 : DesktopClipboardFormats.Html)
        | (content.Rtf is null ? 0 : DesktopClipboardFormats.Rtf)
        | (content.Image.IsEmpty ? 0 : DesktopClipboardFormats.Image)
        | (content.Files.IsDefaultOrEmpty ? 0 : DesktopClipboardFormats.Files);

    // The failure of content that Check accepted but that cannot be written now: InvalidData or NotFound.
    internal static PlatformFailureCode? Validate(DesktopClipboardContent content)
    {
        if (content.Text?.Contains('\0') == true || content.Html?.Contains('\0') == true || content.Rtf?.Contains('\0') == true)
            return PlatformFailureCode.InvalidData;
        if (content.Rtf is { } rtf && !IsLatin1(rtf)) return PlatformFailureCode.InvalidData;
        if (!content.Image.IsEmpty && !IsPng(content.Image.Span)) return PlatformFailureCode.InvalidData;
        if (!content.Files.IsDefault)
            foreach (var path in content.Files)
                if (!File.Exists(path) && !Directory.Exists(path)) return PlatformFailureCode.NotFound;
        return null;
    }

    internal static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.StartsWith(PngSignature);

    internal static bool IsLatin1(string text)
    {
        foreach (char character in text)
            if (character > 'ÿ') return false;
        return true;
    }

    // RTF as bytes, one per character; Validate accepted the text.
    internal static byte[] RtfBytes(string rtf) => System.Text.Encoding.Latin1.GetBytes(rtf);

    // RTF bytes as text, up to the first NUL that some writers add.
    internal static string RtfText(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return System.Text.Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
