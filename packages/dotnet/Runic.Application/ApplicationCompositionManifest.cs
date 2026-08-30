using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Runic.Application;

/// <summary>Immutable application-level facts produced by the application generator.</summary>
public sealed class ApplicationCompositionManifest
{
    private readonly string _schema = "runic.application/1";
    /// <summary>Creates a manifest after validating and canonically ordering all facts.</summary>
    public ApplicationCompositionManifest(
        string entryPoint,
        string version,
        string provenance,
        IEnumerable<string>? capabilities = null,
        IEnumerable<ApplicationManifestArtifact>? artifacts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenance);
        EntryPoint = entryPoint;
        Version = version;
        Provenance = provenance;
        Capabilities = Canonicalize(capabilities, nameof(capabilities));
        Artifacts = (artifacts ?? [])
            .Select(static artifact => artifact ?? throw new ArgumentException("Artifacts cannot contain null entries."))
            .OrderBy(static artifact => artifact.Kind, StringComparer.Ordinal)
            .ThenBy(static artifact => artifact.Identity, StringComparer.Ordinal)
            .ThenBy(static artifact => artifact.Fingerprint, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>Gets the manifest schema identity.</summary>
    public string Schema => _schema;

    /// <summary>Gets the generated application entry-point identity.</summary>
    public string EntryPoint { get; }

    /// <summary>Gets the application version supplied at compilation.</summary>
    public string Version { get; }

    /// <summary>Gets the application provenance supplied at compilation.</summary>
    public string Provenance { get; }

    /// <summary>Gets requested application capabilities in ordinal order.</summary>
    public ImmutableArray<string> Capabilities { get; }

    /// <summary>Gets referenced authoritative artifacts in ordinal order.</summary>
    public ImmutableArray<ApplicationManifestArtifact> Artifacts { get; }

    /// <summary>Writes the canonical JSON form consumed by tooling and hosts.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ApplicationManifestJsonContext.Default.ApplicationCompositionManifest);

    private static ImmutableArray<string> Canonicalize(IEnumerable<string>? values, string parameterName) =>
        (values ?? [])
            .Select(value => string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Manifest values cannot be blank.", parameterName)
                : value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
}

/// <summary>References an externally authoritative artifact without copying its model.</summary>
public sealed record ApplicationManifestArtifact
{
    /// <summary>Validates an artifact reference.</summary>
    public ApplicationManifestArtifact(string kind, string identity, string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        Kind = kind;
        Identity = identity;
        Fingerprint = fingerprint;
    }

    /// <summary>Gets the artifact kind owned by its producing product.</summary>
    public string Kind { get; }

    /// <summary>Gets the external artifact identity.</summary>
    public string Identity { get; }

    /// <summary>Gets the producer's immutable artifact fingerprint.</summary>
    public string Fingerprint { get; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ApplicationCompositionManifest))]
[JsonSerializable(typeof(ApplicationManifestArtifact))]
internal sealed partial class ApplicationManifestJsonContext : JsonSerializerContext;
