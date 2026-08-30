using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.Assets;

/// <summary>Maps explicit assembly resources to deterministic application asset paths.</summary>
public sealed record EmbeddedAssetRegistration(
    string RelativePath,
    string ResourceName,
    bool IsEntryPoint = false,
    string? MediaType = null,
    AssetCacheMode CacheMode = AssetCacheMode.Revalidate);

/// <summary>
/// Reads explicitly registered manifest resources without extraction, filesystem access,
/// dynamic code generation, or dependency injection.
/// </summary>
public sealed class EmbeddedAssetSource : IAssetSnapshotSource
{
    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _resources;

    /// <summary>Builds an immutable source from embedded assembly resources.</summary>
    public EmbeddedAssetSource(Assembly assembly, IEnumerable<EmbeddedAssetRegistration> registrations)
    {
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        ArgumentNullException.ThrowIfNull(registrations);
        var resources = new Dictionary<string, string>(StringComparer.Ordinal);
        var descriptors = new List<AssetDescriptor>();
        foreach (EmbeddedAssetRegistration registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            string path = AssetPath.Normalize(registration.RelativePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.ResourceName);
            if (!resources.TryAdd(path, registration.ResourceName))
            {
                throw new ArgumentException("Embedded asset paths must be unique.", nameof(registrations));
            }

            using Stream stream = OpenResource(registration.ResourceName);
            descriptors.Add(AssetHashing.Describe(
                path,
                stream,
                registration.MediaType,
                registration.IsEntryPoint,
                registration.CacheMode));
        }

        _resources = resources;
        Manifest = new AssetManifest(descriptors);
    }

    /// <inheritdoc />
    public AssetManifest Manifest { get; }

    /// <inheritdoc />
    public ValueTask ValidateAsync(CancellationToken cancellationToken = default)
    {
        foreach (AssetDescriptor descriptor in Manifest.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Stream stream = OpenResource(_resources[descriptor.RelativePath]);
            AssetHashing.Verify(descriptor, stream);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        ValueTask<AssetReadSnapshot> snapshot = OpenSnapshotAsync(relativePath, cancellationToken);
        if (snapshot.IsCompletedSuccessfully)
        {
            return ValueTask.FromResult(snapshot.Result.Content);
        }

        return OpenStreamAsync(snapshot);
    }

    /// <inheritdoc />
    public ValueTask<AssetReadSnapshot> OpenSnapshotAsync(
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = AssetPath.Normalize(relativePath);
        if (!_resources.TryGetValue(path, out string? resourceName))
        {
            throw new FileNotFoundException("The requested asset is not declared by the manifest.", path);
        }

        return ValueTask.FromResult(new AssetReadSnapshot(Manifest.Assets[
            FindAssetIndex(path)], OpenResource(resourceName)));
    }

    private int FindAssetIndex(string path)
    {
        for (int index = 0; index < Manifest.Assets.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(Manifest.Assets[index].RelativePath, path))
            {
                return index;
            }
        }

        throw new FileNotFoundException("The requested asset is not declared by the manifest.", path);
    }

    private static async ValueTask<Stream> OpenStreamAsync(ValueTask<AssetReadSnapshot> snapshot) =>
        (await snapshot.ConfigureAwait(false)).Content;

    private Stream OpenResource(string resourceName) =>
        _assembly.GetManifestResourceStream(resourceName)
        ?? throw new InvalidDataException($"Embedded asset resource '{resourceName}' does not exist.");
}
