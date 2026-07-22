using System;
using System.Collections.Generic;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Sbom;

public enum SbomFormat
{
    CycloneDxJson,
    SpdxJson,
}

public sealed record SbomReadLimits(
    long MaximumBytes = 4 * 1024 * 1024,
    int MaximumDepth = 64,
    int MaximumProperties = 100_000,
    int MaximumComponents = 10_000)
{
    internal void Validate()
    {
        if (MaximumBytes <= 0 || MaximumDepth <= 0 || MaximumProperties <= 0 || MaximumComponents <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(SbomReadLimits), "All SBOM read limits must be positive.");
        }
    }
}

public sealed class SbomFormatException : FormatException
{
    public const string StableDiagnosticCode = NoticeDiagnosticCodes.SchemaIncompatible;

    public SbomFormatException(string message)
        : base(message)
    {
    }

    public SbomFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public NoticeDiagnostic ToDiagnostic(string? source = null) =>
        new(StableDiagnosticCode, NoticeDiagnosticSeverity.Error, Message, Source: source);
}

public sealed record SbomComponent(
    string ComponentReference,
    PackageUrl? PackageUrl,
    string? Ecosystem,
    string Name,
    string Version);

public sealed record SbomDocument(
    SbomFormat Format,
    string DocumentReference,
    string? SerialNumber,
    IReadOnlyList<SbomComponent> Components);

public sealed record SbomInventoryIdentity(PackageUrl PackageUrl, string Name, string Version);

public sealed record SbomComponentLink(string PackageUrl, string ComponentReference);

public sealed record SbomReconciliationResult(
    SbomFormat Format,
    string DocumentReference,
    string? SerialNumber,
    IReadOnlyList<SbomComponentLink> Links,
    IReadOnlyList<NoticeDiagnostic> Diagnostics)
{
    public bool Succeeded
    {
        get
        {
            foreach (NoticeDiagnostic diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
