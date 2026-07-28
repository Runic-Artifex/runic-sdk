using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WebUIToolkit.Assets;

/// <summary>
/// Exposes a refreshable local directory for development. Reparse points are rejected,
/// paths stay below the fixed root, and every refresh publishes an immutable manifest.
/// </summary>
public sealed class DevelopmentDirectoryAssetSource : IAssetSource
{
    private readonly string _root;
    private readonly string _rootPrefix;
    private readonly string _entryPoint;
    private AssetManifest _manifest;

    /// <summary>Scans a local development directory and creates its first manifest snapshot.</summary>
    public DevelopmentDirectoryAssetSource(string rootDirectory, string entryPointRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.GetFullPath(rootDirectory);
        _rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        _entryPoint = AssetPath.Normalize(entryPointRelativePath);
        _manifest = Scan(CancellationToken.None);
    }

    /// <inheritdoc />
    public AssetManifest Manifest => Volatile.Read(ref _manifest);

    /// <summary>Atomically replaces the manifest with a fresh deterministic directory scan.</summary>
    public AssetManifest Refresh(CancellationToken cancellationToken = default)
    {
        AssetManifest replacement = Scan(cancellationToken);
        Interlocked.Exchange(ref _manifest, replacement);
        return replacement;
    }

    /// <inheritdoc />
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        AssetManifest snapshot = Manifest;
        foreach (AssetDescriptor descriptor in snapshot.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using FileStream stream = OpenFile(descriptor.RelativePath);
            AssetHashing.Verify(descriptor, stream);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = AssetPath.Normalize(relativePath);
        if (!Manifest.TryGetAsset(path, out _))
        {
            throw new FileNotFoundException("The requested asset is not declared by the current manifest.", path);
        }

        return ValueTask.FromResult<Stream>(OpenFile(path));
    }

    private AssetManifest Scan(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"Asset development directory '{_root}' does not exist.");
        }

        RejectReparsePoint(_root);
        var descriptors = new List<AssetDescriptor>();
        var pending = new Stack<string>();
        pending.Push(_root);
        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RejectReparsePoint(entry);
                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                    continue;
                }

                string relativePath = AssetPath.Normalize(
                    Path.GetRelativePath(_root, entry).Replace(Path.DirectorySeparatorChar, '/'));
                using FileStream stream = OpenFile(relativePath);
                descriptors.Add(AssetHashing.Describe(
                    relativePath,
                    stream,
                    mediaType: null,
                    StringComparer.Ordinal.Equals(relativePath, _entryPoint),
                    AssetCacheMode.NoStore));
            }
        }

        return new AssetManifest(descriptors);
    }

    private FileStream OpenFile(string relativePath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(
            _root,
            AssetPath.Normalize(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(_rootPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("An asset path resolved outside its development root.");
        }

        RejectPathReparsePoints(fullPath);
        return new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81_920,
            FileOptions.SequentialScan);
    }

    private void RejectPathReparsePoints(string fullPath)
    {
        RejectReparsePoint(_root);
        string relative = Path.GetRelativePath(_root, fullPath);
        string current = _root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Asset development directories cannot contain symbolic links or reparse points.");
        }
    }
}
