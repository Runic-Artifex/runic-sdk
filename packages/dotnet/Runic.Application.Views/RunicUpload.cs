using System.Text;

namespace Runic.Application.Views;

/// <summary>
/// A file the page uploaded to the Window's host, which a command or setter receives as its input
/// (Views protocol 4, feature <c>blobs/1</c>).
/// </summary>
/// <remarks>
/// <para>
/// The page streams the file over HTTP with <c>client.upload(file)</c>, which resolves with a <c>BlobRef</c>, and passes
/// that reference to a command whose parameter is a <see cref="RunicUpload"/>. The bytes never cross as JSON, so the
/// Bridge input limit does not apply; the Window's upload limits do (<see cref="WindowContentSessionOptions.MaximumUploadBytes"/>).
/// <code>
/// [RelayCommand]
/// private async Task AttachAsync(RunicUpload file, CancellationToken cancellationToken)
/// {
///     await file.SaveAsAsync(Path.Combine(attachments, Guid.NewGuid().ToString("N")), cancellationToken: cancellationToken);
/// }
/// </code>
/// </para>
/// <para>
/// An upload decodes only on the Window and the page connection that uploaded it, and only until it is claimed: the
/// first <see cref="OpenReadAsync"/>, <see cref="SaveAsAsync"/> or <see cref="Discard"/> takes it out of the Window, and a
/// later request that names it is rejected as invalid input. An unclaimed upload is deleted when its connection
/// closes, when the Window closes, or 10 minutes after it arrived.
/// </para>
/// <para>
/// Everything about an upload comes from the page and is untrusted: its content, <see cref="ContentType"/> and
/// <see cref="FileName"/>. Check the content before you use it, and never use the file name as a path.
/// </para>
/// </remarks>
public sealed class RunicUpload
{
    private readonly IRunicUploadContent _content;
    private int _claimed;

    internal RunicUpload(string id, string fileName, string contentType, long length, IRunicUploadContent content)
    {
        Id = id;
        FileName = fileName;
        ContentType = contentType;
        Length = length;
        _content = content;
    }

    /// <summary>The upload's id on the wire: the <c>id</c> of the page's <c>BlobRef</c>.</summary>
    public string Id { get; }

    /// <summary>
    /// The file name the page reported, reduced to a file name: no directories, path separators or control
    /// characters, at most 255 characters, and <c>upload</c> when nothing is left. Untrusted.
    /// </summary>
    public string FileName { get; }

    /// <summary>The media type the page reported, or <c>application/octet-stream</c>. Untrusted.</summary>
    public string ContentType { get; }

    /// <summary>The number of bytes the host received.</summary>
    public long Length { get; }

    /// <summary>Whether the upload was claimed, by this instance or by another request that named it.</summary>
    public bool IsClaimed => Volatile.Read(ref _claimed) != 0 || _content.IsGone;

    /// <summary>
    /// Creates an upload of bytes in memory, for tests of a ViewModel without a Window. A Window's uploads come from the
    /// page; <c>RunicWindowTestHost.UploadAsync</c> simulates one.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <param name="fileName">The file name, reduced as an uploaded one is.</param>
    /// <param name="contentType">The media type.</param>
    public static RunicUpload FromBytes(ReadOnlyMemory<byte> content, string fileName = "upload",
        string contentType = "application/octet-stream")
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return new RunicUpload(RunicUploadIds.Create(), SanitizeFileName(fileName),
            RunicBlob.ValidateContentType(contentType), content.Length, new MemoryUploadContent(content));
    }

    /// <summary>
    /// Claims the upload and opens its content. Disposing the stream deletes the content.
    /// </summary>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>A readable, seekable stream of <see cref="Length"/> bytes.</returns>
    /// <exception cref="InvalidOperationException">The upload was already claimed, or its Window closed.</exception>
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Claim();
        return new(_content.OpenRead());
    }

    /// <summary>
    /// Claims the upload and moves its content to <paramref name="path"/>: a rename when the path is on the volume of
    /// the host's upload folder, otherwise a copy, so a large upload is not read through the application.
    /// </summary>
    /// <param name="path">The destination's full path.</param>
    /// <param name="overwrite">Whether an existing file is replaced.</param>
    /// <param name="cancellationToken">Cancels a copy; the upload stays claimed and its content is deleted.</param>
    /// <exception cref="InvalidOperationException">The upload was already claimed, or its Window closed.</exception>
    /// <exception cref="IOException">The destination exists and <paramref name="overwrite"/> is <see langword="false"/>, or the move failed.</exception>
    public async ValueTask SaveAsAsync(string path, bool overwrite = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("The destination path must be fully qualified.", nameof(path));
        cancellationToken.ThrowIfCancellationRequested();
        Claim();
        await _content.SaveAsAsync(path, overwrite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Claims the upload and deletes its content without reading it. Does nothing when it was claimed already.</summary>
    public void Discard()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0) return;
        _content.Discard();
    }

    private void Claim()
    {
        if (Interlocked.Exchange(ref _claimed, 1) != 0 || !_content.TryClaim())
            throw new InvalidOperationException($"The upload '{FileName}' was already claimed, or its Window closed.");
    }

    /// <summary>The <see cref="FileName"/> of a name the page reported.</summary>
    internal static string SanitizeFileName(string name)
    {
        // The last segment of a path, as browsers report only a name but a page can send anything.
        var start = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\')) + 1;
        var builder = new StringBuilder(Math.Min(name.Length - start, 255));
        foreach (var rune in name.AsSpan(start).EnumerateRunes())
        {
            if (Rune.IsControl(rune) || rune.Value is '<' or '>' or ':' or '"' or '|' or '?' or '*' ||
                Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Format
                    or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator
                    or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned)
                continue;
            if (builder.Length + rune.Utf16SequenceLength > 255) break;
            builder.Append(rune.ToString());
        }
        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 || result == ".." ? "upload" : result;
    }
}

// The content behind a RunicUpload: a file the host stored, or memory for tests.
internal interface IRunicUploadContent
{
    // Whether the content is gone: claimed through another RunicUpload of the same id, expired or its Window closed.
    bool IsGone { get; }
    bool TryClaim();
    Stream OpenRead();
    ValueTask SaveAsAsync(string path, bool overwrite, CancellationToken cancellationToken);
    void Discard();
}

internal static class RunicUploadIds
{
    // 24 random bytes as unpadded base64url: 32 characters, 192 bits.
    internal static string Create() => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    internal static bool IsValid(string id) =>
        id.Length is >= 16 and <= 64 && id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

internal sealed class MemoryUploadContent(ReadOnlyMemory<byte> content) : IRunicUploadContent
{
    private int _claimed;
    public bool IsGone => false;
    public bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;
    public Stream OpenRead() => new ReadOnlyMemoryStream(content);

    public async ValueTask SaveAsAsync(string path, bool overwrite, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 81_920, FileOptions.Asynchronous);
        await file.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    public void Discard() { }
}
