using System.Collections.Immutable;

namespace Runic.Platform;

/// <summary>The formats on the clipboard, or in a <see cref="DesktopClipboardContent"/>.</summary>
/// <remarks>This set is open: a minor release may add formats, so ignore flags you do not know.</remarks>
[Flags]
public enum DesktopClipboardFormats
{
    /// <summary>Nothing Runic reads.</summary>
    None = 0,
    /// <summary>Unicode text, read through <see cref="ITextClipboard.ReadTextAsync"/>.</summary>
    Text = 1 << 0,
    /// <summary>HTML, read through <see cref="IDesktopClipboard.ReadHtmlAsync"/>.</summary>
    Html = 1 << 1,
    /// <summary>Rich Text Format, read through <see cref="IDesktopClipboard.ReadRtfAsync"/>.</summary>
    Rtf = 1 << 2,
    /// <summary>An image, read as PNG through <see cref="IDesktopClipboard.ReadImageAsync"/>.</summary>
    Image = 1 << 3,
    /// <summary>A list of local files or directories, read through <see cref="IDesktopClipboard.ReadFilesAsync"/>.</summary>
    Files = 1 << 4,
}

/// <summary>What one <see cref="IDesktopClipboard.WriteAsync"/> puts on the clipboard: every format set here, offered together.</summary>
/// <remarks>
/// <para>
/// Set at least one format. Other applications pick the format they understand best, so set the formats that describe
/// the same thing, such as a table as <see cref="Html"/> and as tab-separated <see cref="Text"/>. The write replaces
/// everything on the clipboard.
/// </para>
/// <para>
/// <see cref="Text"/>, <see cref="Html"/> and <see cref="Rtf"/> must not contain NUL characters, <see cref="Image"/> must
/// be a PNG file, and every entry of <see cref="Files"/> must exist when the write runs; otherwise the write reports
/// <see cref="PlatformFailureCode.InvalidData"/> or, for a missing file, <see cref="PlatformFailureCode.NotFound"/>, and
/// the clipboard is unchanged.
/// </para>
/// </remarks>
public sealed record DesktopClipboardContent
{
    /// <summary>Unicode text; <see langword="null"/> for none.</summary>
    public string? Text { get; init; }

    /// <summary>HTML, a fragment such as <c>&lt;b&gt;bold&lt;/b&gt;</c> or a whole document; <see langword="null"/> for none.</summary>
    /// <remarks>
    /// Windows offers it as <c>CF_HTML</c>, with the fragment, or the document's body, marked as what is pasted; macOS and
    /// Linux offer the string as it is.
    /// </remarks>
    public string? Html { get; init; }

    /// <summary>Rich Text Format source, starting with <c>{\rtf1</c>; <see langword="null"/> for none.</summary>
    /// <remarks>
    /// RTF is a byte format: each character is written as one byte (Latin-1), and RTF's escapes, such as <c>荤?</c>,
    /// carry other characters. A character above U+00FF reports <see cref="PlatformFailureCode.InvalidData"/>.
    /// </remarks>
    public string? Rtf { get; init; }

    /// <summary>A PNG file's bytes; empty for none.</summary>
    /// <remarks>Windows also offers the image as a device-independent bitmap for applications that read only those.</remarks>
    public ReadOnlyMemory<byte> Image { get; init; }

    /// <summary>Fully qualified paths of local files or directories, at most 4096; empty for none.</summary>
    /// <remarks>
    /// File managers paste them as a copy. The paths are offered, not the files' contents: an application that pastes
    /// them reads the files itself.
    /// </remarks>
    public ImmutableArray<string> Files { get; init; } = [];
}

/// <summary>The window's clipboard with text, HTML, Rich Text Format, PNG images and file lists, and a watch of its formats.</summary>
/// <remarks>
/// <para>
/// Every window clipboard of Runic implements this interface: take it in a ViewModel's constructor, or test
/// <see cref="IWindowPlatformServices.Clipboard"/> with <c>as IDesktopClipboard</c>. A custom
/// <see cref="ITextClipboard"/> stays text-only. Each operation runs on the window's native thread, as the text
/// operations do; before the window opens it reports <see cref="PlatformUnavailableReason.OwnerUnavailable"/>, and once it
/// closed <see cref="PlatformUnavailableReason.OwnerClosed"/>.
/// </para>
/// <para>
/// A read returns only the one format it names, bounded like <see cref="ITextClipboard.ReadTextAsync"/>: more than the
/// bound reports <see cref="PlatformFailureCode.TooLarge"/>. A successful <see langword="null"/>, or an empty list for
/// files, means the clipboard has no such format. The capabilities are
/// <see cref="PlatformCapabilityIds.ClipboardReadData"/>, <see cref="PlatformCapabilityIds.ClipboardWriteData"/> and
/// <see cref="PlatformCapabilityIds.ClipboardWatch"/>.
/// </para>
/// <para>
/// This is C# code's clipboard, for a ViewModel or background work such as copying an export. A page can still use the
/// browser's clipboard, under the web view's own rules.
/// </para>
/// </remarks>
public interface IDesktopClipboard : ITextClipboard
{
    /// <summary>Reads which formats the clipboard has, without reading any of them.</summary>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <returns>The formats; <see cref="DesktopClipboardFormats.None"/> for an empty clipboard or one with only formats Runic does not read.</returns>
    ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads bounded HTML; a successful <see langword="null"/> means no HTML.</summary>
    /// <param name="maximumCharacters">The most characters to return; more reports <see cref="PlatformFailureCode.TooLarge"/>.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>On Windows the result is the copied fragment of the <c>CF_HTML</c> document; elsewhere the HTML as offered.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumCharacters"/> is negative.</exception>
    ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default);

    /// <summary>Reads bounded Rich Text Format source; a successful <see langword="null"/> means no RTF.</summary>
    /// <param name="maximumCharacters">The most characters, which are bytes of RTF, to return; more reports <see cref="PlatformFailureCode.TooLarge"/>.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>Each byte of the RTF becomes one character (Latin-1), as <see cref="DesktopClipboardContent.Rtf"/> describes.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumCharacters"/> is negative.</exception>
    ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default);

    /// <summary>Reads the clipboard's image as PNG bytes; a successful <see langword="null"/> means no image.</summary>
    /// <param name="maximumBytes">The most PNG bytes to return; more reports <see cref="PlatformFailureCode.TooLarge"/>.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>
    /// An image that another application offers in another format, such as a bitmap on Windows or TIFF on macOS, is
    /// converted to PNG.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumBytes"/> is negative.</exception>
    ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default);

    /// <summary>Reads the clipboard's file list as fully qualified local paths; an empty list means no files.</summary>
    /// <param name="maximumCount">The most paths to return; more reports <see cref="PlatformFailureCode.TooLarge"/>.</param>
    /// <param name="cancellationToken">Cancels before the request reaches the window.</param>
    /// <remarks>Entries that are not local files, such as web URIs, are skipped.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumCount"/> is negative.</exception>
    ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default);

    /// <summary>Replaces the clipboard with every format of <paramref name="content"/>, in one ownership.</summary>
    /// <param name="content">The formats to offer.</param>
    /// <param name="cancellationToken">Prevents queued work; an initiated native write returns its actual outcome.</param>
    /// <returns>
    /// <see cref="PlatformResult{T}.Success"/> once the clipboard offers the content, or
    /// <see cref="PlatformResult{T}.Failed"/> with <see cref="PlatformFailureCode.InvalidData"/> or
    /// <see cref="PlatformFailureCode.NotFound"/> for content that breaks the rules of
    /// <see cref="DesktopClipboardContent"/>, before anything changed.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="content"/> sets no format, or <see cref="DesktopClipboardContent.Files"/> has more than 4096 entries
    /// or an entry that is null, empty or not fully qualified.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> is <see langword="null"/>.</exception>
    ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default);

    /// <summary>Reports the clipboard's formats now, then after each change; never the content.</summary>
    /// <param name="cancellationToken">Ends the watch, which then throws <see cref="OperationCanceledException"/>.</param>
    /// <remarks>
    /// <para>
    /// Every change is reported, also one that leaves the same formats, such as other copied text; changes close together
    /// may arrive as one. Read the content when the user pastes, not on every change: other applications' content is
    /// theirs. When the window closes the watch reports <see cref="PlatformUnavailableReason.OwnerClosed"/> and ends; a
    /// window that cannot watch reports <see cref="PlatformResult{T}.Unavailable"/> once and ends.
    /// </para>
    /// <para>
    /// macOS has no change notification: the watch compares the pasteboard's change count four times a second. On
    /// Wayland the compositor tells only a focused application about the clipboard, so changes arrive when a window of
    /// the application gains focus.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(CancellationToken cancellationToken = default);
}
