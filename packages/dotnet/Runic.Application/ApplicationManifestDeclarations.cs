using System;

namespace Runic.Application;

/// <summary>Declares the single generated application composition manifest.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
public sealed class RunicApplicationManifestAttribute : Attribute
{
    /// <summary>Initializes the application entry-point identity.</summary>
    public RunicApplicationManifestAttribute(string entryPoint) => EntryPoint = entryPoint ?? throw new ArgumentNullException(nameof(entryPoint));

    /// <summary>Gets the application entry-point identity.</summary>
    public string EntryPoint { get; }

    /// <summary>Gets or sets the application version.</summary>
    public string Version { get; set; } = "0.0.0";

    /// <summary>Gets or sets the immutable build provenance.</summary>
    public string Provenance { get; set; } = "local";
}

/// <summary>Declares one requested application capability.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RunicApplicationCapabilityAttribute(string name) : Attribute
{
    /// <summary>Gets the capability identity.</summary>
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));
}

/// <summary>Declares a fingerprinted artifact owned by another Runic product.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RunicApplicationArtifactAttribute(string kind, string identity, string fingerprint) : Attribute
{
    /// <summary>Gets the owning artifact kind.</summary>
    public string Kind { get; } = kind ?? throw new ArgumentNullException(nameof(kind));
    /// <summary>Gets the external artifact identity.</summary>
    public string Identity { get; } = identity ?? throw new ArgumentNullException(nameof(identity));
    /// <summary>Gets the externally supplied fingerprint.</summary>
    public string Fingerprint { get; } = fingerprint ?? throw new ArgumentNullException(nameof(fingerprint));
}
