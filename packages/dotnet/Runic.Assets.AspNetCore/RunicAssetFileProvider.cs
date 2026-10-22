using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Runic.Assets.AspNetCore;

/// <summary>
/// Exposes a Runic Assets source as an <see cref="IFileProvider"/>, for <c>UseStaticFiles</c>,
/// <c>UseDefaultFiles</c>, <c>UseDirectoryBrowser</c> and other consumers of ASP.NET Core file providers.
/// </summary>
/// <remarks>
/// <para>
/// Files and directories are the source's current manifest: a declared asset is a file, and every parent of one is
/// a directory. Paths are case-sensitive asset paths; a path that is not a valid asset path does not exist. Files have
/// no physical path, their length is the declared length, and their last-modified time is the fixed timestamp of the
/// archive format (1980-01-01 UTC).
/// </para>
/// <para>
/// <see cref="IFileInfo.CreateReadStream"/> opens a snapshot of the asset and waits for it: archive, embedded and
/// development-directory sources open synchronously; another source's <see cref="IAssetSource.OpenAsync"/> blocks the
/// calling thread. The stream is seekable, as range requests need.
/// </para>
/// <para>
/// <see cref="Watch"/> returns a token that fires on the source's next change when the source implements
/// <see cref="IAssetSourceChangeNotifier"/>, whatever the filter, and a token that never fires otherwise.
/// </para>
/// <para>
/// <c>UseStaticFiles</c> computes its own entity tags from the length and the last-modified time and has no
/// cache modes. <see cref="RunicAssetEndpointExtensions.MapRunicAssetSource"/> is the recommended way to serve
/// a source: it sends the manifest's SHA-256 entity tags, cache modes and Content-Security-Policy.
/// </para>
/// </remarks>
public sealed class RunicAssetFileProvider : IFileProvider
{
    // The ZIP timestamp every Runic Assets archive entry carries.
    private static readonly DateTimeOffset FixedTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly IAssetSource _source;

    /// <summary>Creates a provider over <paramref name="source"/>.</summary>
    /// <param name="source">The source whose manifest the provider lists.</param>
    public RunicAssetFileProvider(IAssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public IFileInfo GetFileInfo(string subpath)
    {
        string? path = Normalize(subpath);
        return path is { Length: > 0 } && _source.Manifest.TryGetAsset(path, out AssetDescriptor? descriptor)
            ? new AssetFileInfo(_source, descriptor)
            : new NotFoundFileInfo(subpath ?? string.Empty);
    }

    /// <inheritdoc />
    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        string? directory = Normalize(subpath);
        if (directory is null)
        {
            return NotFoundDirectoryContents.Singleton;
        }

        string prefix = directory.Length == 0 ? string.Empty : directory + "/";
        var entries = new SortedDictionary<string, IFileInfo>(StringComparer.Ordinal);
        bool exists = directory.Length == 0;
        foreach (AssetDescriptor descriptor in _source.Manifest.Assets)
        {
            if (!descriptor.RelativePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            exists = true;
            string rest = descriptor.RelativePath[prefix.Length..];
            int separator = rest.IndexOf('/');
            if (separator < 0)
            {
                entries[rest] = new AssetFileInfo(_source, descriptor);
            }
            else
            {
                string name = rest[..separator];
                entries.TryAdd(name, new AssetDirectoryInfo(name));
            }
        }

        return exists ? new AssetDirectoryContents([.. entries.Values]) : NotFoundDirectoryContents.Singleton;
    }

    /// <inheritdoc />
    public IChangeToken Watch(string filter)
    {
        if (_source is not IAssetSourceChangeNotifier notifier)
        {
            return NullChangeToken.Singleton;
        }

        return new SourceChangeToken(notifier);
    }

    // "/", "" and null are the root; "/assets/" and "assets" name the same directory.
    private static string? Normalize(string? subpath)
    {
        string trimmed = (subpath ?? string.Empty).Trim('/');
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return AssetPath.TryNormalize(trimmed, out string? normalized) ? normalized : null;
    }

    private sealed class AssetFileInfo(IAssetSource source, AssetDescriptor descriptor) : IFileInfo
    {
        public bool Exists => true;
        public long Length => descriptor.Length;
        public string? PhysicalPath => null;
        public string Name => descriptor.RelativePath[(descriptor.RelativePath.LastIndexOf('/') + 1)..];
        public DateTimeOffset LastModified => FixedTimestamp;
        public bool IsDirectory => false;

        public Stream CreateReadStream()
        {
            AssetReadSnapshot snapshot = source.OpenAsync(descriptor.RelativePath).AsTask().GetAwaiter().GetResult();
            try
            {
                // The provider described these bytes; never serve others under the same length.
                if (!StringComparer.Ordinal.Equals(snapshot.Descriptor.Sha256, descriptor.Sha256)
                    || snapshot.Descriptor.Length != descriptor.Length)
                {
                    throw new FileNotFoundException("The asset changed after its file information was read.", descriptor.RelativePath);
                }

                if (snapshot.Content.CanSeek)
                {
                    return new SnapshotStream(snapshot, snapshot.Content);
                }

                // Range requests seek; buffer a source whose stream cannot.
                var buffer = new MemoryStream(checked((int)descriptor.Length));
                snapshot.Content.CopyTo(buffer);
                buffer.Position = 0;
                snapshot.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return buffer;
            }
            catch
            {
                snapshot.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }
    }

    private sealed class AssetDirectoryInfo(string name) : IFileInfo
    {
        public bool Exists => true;
        public long Length => -1;
        public string? PhysicalPath => null;
        public string Name => name;
        public DateTimeOffset LastModified => FixedTimestamp;
        public bool IsDirectory => true;
        public Stream CreateReadStream() => throw new InvalidOperationException("A directory has no content stream.");
    }

    private sealed class AssetDirectoryContents(IFileInfo[] entries) : IDirectoryContents
    {
        public bool Exists => true;
        public IEnumerator<IFileInfo> GetEnumerator() => ((IEnumerable<IFileInfo>)entries).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Fires once, on the source's next change, and stops listening then.
    private sealed class SourceChangeToken : IChangeToken
    {
        private readonly IAssetSourceChangeNotifier _notifier;
        private readonly Lock _gate = new();
        private List<Registration>? _callbacks = [];

        internal SourceChangeToken(IAssetSourceChangeNotifier notifier)
        {
            _notifier = notifier;
            _notifier.Changed += OnChanged;
        }

        public bool HasChanged
        {
            get
            {
                lock (_gate) return _callbacks is null;
            }
        }

        public bool ActiveChangeCallbacks => true;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var registration = new Registration(this, callback, state);
            lock (_gate)
            {
                if (_callbacks is not null)
                {
                    _callbacks.Add(registration);
                    return registration;
                }
            }

            callback(state);
            return registration;
        }

        private void OnChanged(object? sender, AssetSourceChangedEventArgs change)
        {
            _notifier.Changed -= OnChanged;
            List<Registration>? callbacks;
            lock (_gate)
            {
                callbacks = _callbacks;
                _callbacks = null;
            }

            foreach (Registration registration in callbacks ?? [])
            {
                registration.Invoke();
            }
        }

        private void Remove(Registration registration)
        {
            lock (_gate) _callbacks?.Remove(registration);
        }

        private sealed class Registration(SourceChangeToken token, Action<object?> callback, object? state) : IDisposable
        {
            internal void Invoke() => callback(state);
            public void Dispose() => token.Remove(this);
        }
    }

    // The snapshot's content stream; disposing it releases the snapshot.
    private sealed class SnapshotStream(AssetReadSnapshot snapshot, Stream content) : Stream
    {
        public override bool CanRead => content.CanRead;
        public override bool CanSeek => content.CanSeek;
        public override bool CanWrite => false;
        public override long Length => content.Length;
        public override long Position { get => content.Position; set => content.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => content.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => content.Read(buffer);
        public override System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            content.ReadAsync(buffer, cancellationToken);
        public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            content.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => content.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                snapshot.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }
    }
}
