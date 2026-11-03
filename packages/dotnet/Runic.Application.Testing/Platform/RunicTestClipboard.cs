using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Runic.Platform;

namespace Runic.Application.Testing;

/// <summary>
/// An in-memory <see cref="IDesktopClipboard"/> for tests: it holds one <see cref="DesktopClipboardContent"/>, checks
/// writes with the rules of the real clipboard, records each write and reports changes to watches.
/// </summary>
/// <remarks>
/// <para>
/// Set what the user copied with <see cref="SetContent"/> or <see cref="SetText"/> before the action that pastes, and
/// read what the application copied from <see cref="Content"/> or <see cref="Writes"/>. Reads beyond their limit report
/// <see cref="PlatformFailureCode.TooLarge"/>, as the real clipboard does. A write of files checks that they exist,
/// as the real clipboard does.
/// </para>
/// <para>
/// Set <see cref="UnavailableReason"/> to make every call report unavailable; when the test window closes, calls report
/// <see cref="PlatformUnavailableReason.OwnerClosed"/> and watches end. The type is safe to use from any thread.
/// </para>
/// </remarks>
public sealed class RunicTestClipboard : IDesktopClipboard
{
    private readonly object _gate = new();
    private readonly List<DesktopClipboardContent> _writes = [];
    private readonly List<Channel<PlatformResult<DesktopClipboardFormats>>> _watches = [];
    private readonly RunicTestPlatform _platform;
    private DesktopClipboardContent? _content;

    internal RunicTestClipboard(RunicTestPlatform platform) => _platform = platform;

    /// <summary>The clipboard's content, or <see langword="null"/> when it is empty.</summary>
    public DesktopClipboardContent? Content
    {
        get { lock (_gate) return _content; }
    }

    /// <summary>The content of each successful write by the application, oldest first.</summary>
    public IReadOnlyList<DesktopClipboardContent> Writes
    {
        get { lock (_gate) return [.. _writes]; }
    }

    /// <summary>When set, every call reports <see cref="PlatformResult{T}.Unavailable"/> with this reason.</summary>
    public PlatformUnavailableReason? UnavailableReason { get; set; }

    /// <summary>Replaces the content as another application's copy would; watches report the change.</summary>
    /// <param name="content">The new content, or <see langword="null"/> to empty the clipboard.</param>
    /// <remarks>Unlike a write, this does not check the content, so a test can offer content another application could.</remarks>
    public void SetContent(DesktopClipboardContent? content)
    {
        lock (_gate) _content = content;
        Changed();
    }

    /// <summary>Replaces the content with text as another application's copy would.</summary>
    /// <param name="text">The text.</param>
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        SetContent(new DesktopClipboardContent { Text = text });
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<string?>> ReadTextAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return Read(content => Bounded(content?.Text, maximumCharacters), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<string?>> ReadHtmlAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return Read(content => Bounded(content?.Html, maximumCharacters), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<string?>> ReadRtfAsync(int maximumCharacters, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCharacters);
        return Read(content => Bounded(content?.Rtf, maximumCharacters), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<byte[]?>> ReadImageAsync(int maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        return Read<byte[]?>(content => content is null || content.Image.IsEmpty
            ? new PlatformResult<byte[]?>.Success(null)
            : content.Image.Length > maximumBytes
                ? new PlatformResult<byte[]?>.Failed(PlatformFailureCode.TooLarge)
                : new PlatformResult<byte[]?>.Success(content.Image.ToArray()), cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<ImmutableArray<string>>> ReadFilesAsync(int maximumCount, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        return Read<ImmutableArray<string>>(content =>
        {
            var files = content is null || content.Files.IsDefault ? [] : content.Files;
            return files.Length > maximumCount
                ? new PlatformResult<ImmutableArray<string>>.Failed(PlatformFailureCode.TooLarge)
                : new PlatformResult<ImmutableArray<string>>.Success(files);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<DesktopClipboardFormats>> GetFormatsAsync(CancellationToken cancellationToken = default) =>
        Read<DesktopClipboardFormats>(content =>
            new PlatformResult<DesktopClipboardFormats>.Success(content is null ? DesktopClipboardFormats.None : DesktopClipboardRules.Formats(content)), cancellationToken);

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> WriteTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteAsync(new DesktopClipboardContent { Text = text }, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<PlatformResult<PlatformUnit>> WriteAsync(DesktopClipboardContent content, CancellationToken cancellationToken = default)
    {
        DesktopClipboardRules.Check(content);
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable() is { } reason) return Result(new PlatformResult<PlatformUnit>.Unavailable(reason));
        if (DesktopClipboardRules.Validate(content) is { } failure) return Result(new PlatformResult<PlatformUnit>.Failed(failure));
        lock (_gate)
        {
            _content = content;
            _writes.Add(content);
        }
        _platform.Record("clipboard", "write", [
            ("text", content.Text),
            ("html", content.Html),
            ("rtf", content.Rtf),
            ("image", content.Image.IsEmpty ? null : Convert.ToBase64String(content.Image.Span)),
            ("files", content.Files.IsDefaultOrEmpty ? null : string.Join('\n', content.Files)),
        ]);
        Changed();
        return Result(new PlatformResult<PlatformUnit>.Success(default));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PlatformResult<DesktopClipboardFormats>> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable() is { } reason)
        {
            yield return new PlatformResult<DesktopClipboardFormats>.Unavailable(reason);
            yield break;
        }
        var changes = Channel.CreateUnbounded<PlatformResult<DesktopClipboardFormats>>(new UnboundedChannelOptions { SingleReader = true });
        lock (_gate) _watches.Add(changes);
        try
        {
            yield return await GetFormatsAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var change in changes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
                if (change is PlatformResult<DesktopClipboardFormats>.Unavailable) yield break;
            }
        }
        finally
        {
            lock (_gate) _watches.Remove(changes);
        }
    }

    internal void Close()
    {
        Channel<PlatformResult<DesktopClipboardFormats>>[] watches;
        lock (_gate) watches = [.. _watches];
        foreach (var watch in watches)
        {
            watch.Writer.TryWrite(new PlatformResult<DesktopClipboardFormats>.Unavailable(PlatformUnavailableReason.OwnerClosed));
            watch.Writer.TryComplete();
        }
    }

    internal void EnqueueScripted(string operation, IReadOnlyDictionary<string, string?> values)
    {
        switch (operation)
        {
            case "set":
                var image = values.GetValueOrDefault("image");
                var files = values.GetValueOrDefault("files");
                SetContent(new DesktopClipboardContent
                {
                    Text = values.GetValueOrDefault("text"),
                    Html = values.GetValueOrDefault("html"),
                    Rtf = values.GetValueOrDefault("rtf"),
                    Image = image is null ? default : Convert.FromBase64String(image),
                    Files = files is null ? [] : [.. files.Split('\n')],
                });
                break;
            case "clear":
                SetContent(null);
                break;
            default:
                throw new InvalidOperationException($"Unknown scripted clipboard operation '{operation}'.");
        }
    }

    private void Changed()
    {
        PlatformResult<DesktopClipboardFormats> formats;
        Channel<PlatformResult<DesktopClipboardFormats>>[] watches;
        lock (_gate)
        {
            formats = new PlatformResult<DesktopClipboardFormats>.Success(_content is null ? DesktopClipboardFormats.None : DesktopClipboardRules.Formats(_content));
            watches = [.. _watches];
        }
        foreach (var watch in watches) watch.Writer.TryWrite(formats);
    }

    private PlatformUnavailableReason? Unavailable() =>
        _platform.IsClosed ? PlatformUnavailableReason.OwnerClosed : UnavailableReason;

    private ValueTask<PlatformResult<T>> Read<T>(Func<DesktopClipboardContent?, PlatformResult<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _platform.Pump();
        if (Unavailable() is { } reason) return Result(new PlatformResult<T>.Unavailable(reason));
        DesktopClipboardContent? content;
        lock (_gate) content = _content;
        return Result(read(content));
    }

    private static PlatformResult<string?> Bounded(string? text, int maximumCharacters) =>
        text is not null && text.Length > maximumCharacters
            ? new PlatformResult<string?>.Failed(PlatformFailureCode.TooLarge)
            : new PlatformResult<string?>.Success(text);

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);
}
