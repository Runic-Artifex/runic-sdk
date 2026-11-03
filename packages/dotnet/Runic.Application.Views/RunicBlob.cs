using System.Text.RegularExpressions;

namespace Runic.Application.Views;

/// <summary>
/// Binary content that a ViewModel shows or offers to the page, such as an image, a thumbnail or a download
/// (Views protocol 4, feature <c>blobs/1</c>).
/// </summary>
/// <remarks>
/// <para>
/// Expose it as a state member, inside a DTO or collection item, or as a command or operation result. It crosses the
/// Bridge as a <c>blob</c> value: a URL that the Window's host serves, with the content type, length and file name.
/// The page uses the URL directly, in <c>&lt;img src&gt;</c>, <c>&lt;a href download&gt;</c> or <c>fetch</c>; the
/// bytes never cross as JSON.
/// <code>
/// [ObservableProperty]
/// private RunicBlob? _avatar = RunicBlob.FromFile(avatarPath, "image/png");
/// </code>
/// </para>
/// <para>
/// The URL is a capability: it is unguessable, it works only on the Window it was issued for, and it is released when
/// the state member stops holding the blob, when the route (the ViewModel's presentation) is released, or when the
/// Window closes. A blob in a collection item or a result is released with its route. One <see cref="RunicBlob"/>
/// instance has one URL per Window; create a new instance for new content, because the page and the browser cache
/// assume that a URL's content never changes.
/// </para>
/// <para>
/// The host opens the content for each request, so a <see cref="FromFile"/> or <see cref="FromStream"/> blob is never
/// buffered. A seekable stream also answers byte ranges, which audio and video elements need.
/// </para>
/// </remarks>
public sealed partial class RunicBlob
{
    private readonly Func<CancellationToken, ValueTask<Stream>> _open;

    private RunicBlob(Func<CancellationToken, ValueTask<Stream>> open, string contentType, long? length, string? fileName,
        RunicBlobDisposition disposition)
    {
        _open = open;
        ContentType = ValidateContentType(contentType);
        if (length is < 0) throw new ArgumentOutOfRangeException(nameof(length), length, "A blob's length cannot be negative.");
        Length = length;
        FileName = ValidateFileName(fileName);
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unknown disposition.");
        Disposition = disposition;
    }

    /// <summary>The media type the host serves the content with, such as <c>image/png</c>.</summary>
    public string ContentType { get; }

    /// <summary>The content's length in bytes, or <see langword="null"/> when it is known only once it is opened.</summary>
    public long? Length { get; }

    /// <summary>
    /// The file name that a download saves the content as, and that the page receives as the blob's <c>name</c>; or
    /// <see langword="null"/>.
    /// </summary>
    public string? FileName { get; }

    /// <summary>Whether the browser shows the content or downloads it when the page navigates to its URL.</summary>
    public RunicBlobDisposition Disposition { get; }

    /// <summary>Creates a blob of bytes in memory. The blob keeps the memory; do not change it afterwards.</summary>
    /// <param name="content">The content.</param>
    /// <param name="contentType">The media type, such as <c>image/png</c>.</param>
    /// <param name="fileName">The file name of a download, or <see langword="null"/>.</param>
    /// <param name="disposition">Whether a navigation to the URL shows or downloads the content.</param>
    /// <exception cref="ArgumentException"><paramref name="contentType"/> is not a media type, or <paramref name="fileName"/> is not a file name.</exception>
    public static RunicBlob FromBytes(ReadOnlyMemory<byte> content, string contentType, string? fileName = null,
        RunicBlobDisposition disposition = RunicBlobDisposition.Inline) =>
        new(_ => new ValueTask<Stream>(new ReadOnlyMemoryStream(content)), contentType, content.Length, fileName, disposition);

    /// <summary>
    /// Creates a blob of a file, which the host opens for each request and streams. Its <see cref="Length"/> is the
    /// file's length now; a request reads the file as it is then.
    /// </summary>
    /// <param name="path">The file's full path.</param>
    /// <param name="contentType">The media type, such as <c>image/png</c>.</param>
    /// <param name="fileName">The file name of a download, or <see langword="null"/>.</param>
    /// <param name="disposition">Whether a navigation to the URL shows or downloads the content.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is not fully qualified, or another argument is invalid.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static RunicBlob FromFile(string path, string contentType, string? fileName = null,
        RunicBlobDisposition disposition = RunicBlobDisposition.Inline)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A blob's file path must be fully qualified.", nameof(path));
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The blob's file does not exist.", path);
        var fullPath = file.FullName;
        return new(_ => new ValueTask<Stream>(new FileStream(fullPath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        })), contentType, file.Length, fileName, disposition);
    }

    /// <summary>
    /// Creates a blob whose content <paramref name="open"/> returns for each request. The host reads the stream to its
    /// end and disposes it; a seekable stream also answers byte ranges.
    /// </summary>
    /// <param name="open">Opens the content; called once for each request, on any thread. Its token is cancelled when the request ends.</param>
    /// <param name="contentType">The media type, such as <c>image/png</c>.</param>
    /// <param name="length">The content's length in bytes, when known; a stream of another length ends the response early.</param>
    /// <param name="fileName">The file name of a download, or <see langword="null"/>.</param>
    /// <param name="disposition">Whether a navigation to the URL shows or downloads the content.</param>
    public static RunicBlob FromStream(Func<CancellationToken, ValueTask<Stream>> open, string contentType, long? length = null,
        string? fileName = null, RunicBlobDisposition disposition = RunicBlobDisposition.Inline)
    {
        ArgumentNullException.ThrowIfNull(open);
        return new(open, contentType, length, fileName, disposition);
    }

    /// <summary>
    /// Stops serving this blob in every Window that serves it now: its URLs answer 404 from now on, whatever still holds
    /// it. Release a blob that a long-lived View no longer shows, such as a command result the page has downloaded; a
    /// state property's blob is released when the property changes, and every blob when its View or Window goes away.
    /// </summary>
    /// <remarks>If the blob crosses the Bridge again, it gets a new URL.</remarks>
    public void Release()
    {
        BridgeBlobs[] issuers;
        lock (_issuers) issuers = [.. _issuers];
        foreach (var issuer in issuers) issuer.Release(this);
    }

    // The Windows that issued a URL for this blob, for Release; a Window that releases the blob forgets it.
    private readonly List<BridgeBlobs> _issuers = [];

    internal void IssuedBy(BridgeBlobs blobs)
    {
        lock (_issuers)
            if (!_issuers.Contains(blobs)) _issuers.Add(blobs);
    }

    internal void Forget(BridgeBlobs blobs)
    {
        lock (_issuers) _issuers.Remove(blobs);
    }

    /// <summary>Opens the content, as the host does for each request.</summary>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <returns>A readable stream that the caller disposes.</returns>
    public async ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = await _open(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The blob's open function returned no stream.");
        if (!stream.CanRead)
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("The blob's open function returned a stream that cannot be read.");
        }
        return stream;
    }

    // type "/" subtype, then parameters, as RFC 9110 media types; no line breaks, at most 256 characters.
    [GeneratedRegex("""^[A-Za-z0-9!#$&^_.+-]+/[A-Za-z0-9!#$&^_.+-]+(\s*;\s*[A-Za-z0-9!#$&^_.+-]+=([A-Za-z0-9!#$&^_.+-]+|"[^"\\\r\n]*"))*$""")]
    private static partial Regex MediaType();

    internal static string ValidateContentType(string contentType, string parameter = "contentType")
    {
        ArgumentException.ThrowIfNullOrEmpty(contentType, parameter);
        if (contentType.Length > 256 || !MediaType().IsMatch(contentType))
            throw new ArgumentException($"'{contentType}' is not a media type such as image/png.", parameter);
        return contentType;
    }

    private static string? ValidateFileName(string? fileName)
    {
        if (fileName is null) return null;
        if (fileName.Length is 0 or > 255 || fileName != RunicUpload.SanitizeFileName(fileName))
            throw new ArgumentException("A blob's file name must be 1 to 255 characters without path separators or control characters.", nameof(fileName));
        return fileName;
    }
}

/// <summary>What the browser does with a <see cref="RunicBlob"/> when the page navigates to its URL.</summary>
public enum RunicBlobDisposition
{
    /// <summary>Shows the content, as <c>Content-Disposition: inline</c>. An <c>&lt;a download&gt;</c> link still downloads it.</summary>
    Inline,

    /// <summary>Downloads the content, as <c>Content-Disposition: attachment</c>.</summary>
    Attachment,
}

// A read-only, seekable stream over memory that a blob keeps.
internal sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> content) : Stream
{
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => content.Length;

    public override long Position
    {
        get => _position;
        set => _position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= content.Length) return 0;
        var count = (int)Math.Min(buffer.Length, content.Length - _position);
        content.Span.Slice((int)_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => content.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        Position = target;
        return target;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
