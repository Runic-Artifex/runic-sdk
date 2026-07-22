using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Policy;

namespace WebUIToolkit.DependencyNotices.Engine;

public enum NoticeNetworkAccess
{
    Denied,
    AcquisitionOnly,
}

public sealed record NoticeOperationPolicy
{
    public static NoticeOperationPolicy Offline { get; } = new(NoticeNetworkAccess.Denied);

    public NoticeOperationPolicy(NoticeNetworkAccess networkAccess)
    {
        NetworkAccess = networkAccess;
    }

    public NoticeNetworkAccess NetworkAccess { get; }

    public void EnsureOffline(NoticeOperation operation)
    {
        if (NetworkAccess != NoticeNetworkAccess.Denied)
        {
            throw new NoticeSecurityException(
                NoticeDiagnosticCodes.NetworkAccessForbidden,
                $"Network access is forbidden for the '{operation}' operation.");
        }
    }

    public void EnsureNetworkPermitted(NoticeOperation operation) =>
        NetworkPolicy.EnsurePermitted(
            operation,
            NetworkAccess == NoticeNetworkAccess.AcquisitionOnly);
}

public sealed record InventoryInput
{
    public InventoryInput(InventorySourceKind sourceKind, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        SourceKind = sourceKind;
        RelativePath = relativePath;
    }

    public InventorySourceKind SourceKind { get; }

    public string RelativePath { get; }
}

public sealed record NoticeScanRequest
{
    public NoticeScanRequest(
        string rootDirectory,
        IEnumerable<InventoryInput> inputs,
        NoticeOperationPolicy? operationPolicy = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(inputs);
        RootDirectory = rootDirectory;
        Inputs = Snapshot.List(inputs);
        OperationPolicy = operationPolicy ?? NoticeOperationPolicy.Offline;
    }

    public string RootDirectory { get; }

    public IReadOnlyList<InventoryInput> Inputs { get; }

    public NoticeOperationPolicy OperationPolicy { get; }
}

public sealed record ComponentNoticeMetadata(
    bool IsModified = false,
    string? ModificationNotice = null);

public sealed record InventoryAdapterResult
{
    public InventoryAdapterResult(
        IEnumerable<InventoryComponent> components,
        IEnumerable<NoticeDiagnostic>? diagnostics = null,
        IReadOnlyDictionary<string, ComponentNoticeMetadata>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        Components = Snapshot.List(components);
        Diagnostics = Snapshot.List(diagnostics ?? []);
        Metadata = Snapshot.Dictionary(metadata);
    }

    public IReadOnlyList<InventoryComponent> Components { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }

    public IReadOnlyDictionary<string, ComponentNoticeMetadata> Metadata { get; }
}

public sealed record NoticeScanResult
{
    public NoticeScanResult(
        IEnumerable<InventoryComponent> components,
        IEnumerable<NoticeDiagnostic> diagnostics,
        IReadOnlyDictionary<string, ComponentNoticeMetadata>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(diagnostics);
        Components = Snapshot.List(components);
        Diagnostics = Snapshot.List(diagnostics);
        Metadata = Snapshot.Dictionary(metadata);
    }

    public IReadOnlyList<InventoryComponent> Components { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }

    public IReadOnlyDictionary<string, ComponentNoticeMetadata> Metadata { get; }

    public bool Succeeded => NoticeResult.HasNoErrors(Diagnostics);

    public InventoryResult ToInventoryResult() => new(Components, Diagnostics);
}

public sealed record NoticeEvaluateRequest
{
    public NoticeEvaluateRequest(
        NoticeScanResult scan,
        LicensePolicy policy,
        IReadOnlyDictionary<string, string>? selectedLicenseExpressions = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? fulfilledObligations = null,
        NoticeOperationPolicy? operationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(policy);
        Scan = scan;
        Policy = Snapshot.Policy(policy);
        SelectedLicenseExpressions = Snapshot.Dictionary(selectedLicenseExpressions);
        FulfilledObligations = Snapshot.SetDictionary(fulfilledObligations);
        OperationPolicy = operationPolicy ?? NoticeOperationPolicy.Offline;
    }

    public NoticeScanResult Scan { get; }

    public LicensePolicy Policy { get; }

    public IReadOnlyDictionary<string, string> SelectedLicenseExpressions { get; }

    public IReadOnlyDictionary<string, IReadOnlySet<string>> FulfilledObligations { get; }

    public NoticeOperationPolicy OperationPolicy { get; }
}

public sealed record EvaluatedNoticeComponent(
    InventoryComponent Component,
    LicensePolicyEvaluation PolicyEvaluation,
    IReadOnlyList<NoticeAsset> Assets,
    ComponentNoticeMetadata Metadata);

public sealed record NoticeEvaluationResult
{
    public NoticeEvaluationResult(
        IEnumerable<EvaluatedNoticeComponent> components,
        IEnumerable<NoticeDiagnostic> diagnostics)
    {
        Components = Snapshot.List(components);
        Diagnostics = Snapshot.List(diagnostics);
    }

    public IReadOnlyList<EvaluatedNoticeComponent> Components { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }

    public bool Succeeded => NoticeResult.HasNoErrors(Diagnostics);
}

public sealed record SbomInput
{
    public SbomInput(string format, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        Format = format;
        RelativePath = relativePath;
    }

    public string Format { get; }

    public string RelativePath { get; }
}

public sealed record NoticeGenerateRequest
{
    public NoticeGenerateRequest(
        NoticeEvaluationResult evaluation,
        string artifactName,
        string outputDirectory,
        string? artifactVersion = null,
        SbomInput? sbom = null,
        NoticeOperationPolicy? operationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Evaluation = evaluation;
        ArtifactName = artifactName;
        ArtifactVersion = artifactVersion;
        OutputDirectory = outputDirectory;
        Sbom = sbom;
        OperationPolicy = operationPolicy ?? NoticeOperationPolicy.Offline;
    }

    public NoticeEvaluationResult Evaluation { get; }

    public string ArtifactName { get; }

    public string? ArtifactVersion { get; }

    public string OutputDirectory { get; }

    public SbomInput? Sbom { get; }

    public NoticeOperationPolicy OperationPolicy { get; }
}

public sealed record RenderedNoticeOutput
{
    public RenderedNoticeOutput(string relativePath, ReadOnlyMemory<byte> content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        RelativePath = relativePath.Replace('\\', '/');
        Content = content.ToArray();
    }

    public string RelativePath { get; }

    public ReadOnlyMemory<byte> Content { get; }
}

public sealed record GeneratedNoticeResult
{
    public GeneratedNoticeResult(
        DependencyNoticeDocument document,
        IEnumerable<RenderedNoticeOutput> outputs,
        IEnumerable<NoticeDiagnostic> diagnostics)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Outputs = Snapshot.List(outputs);
        Diagnostics = Snapshot.List(diagnostics);
    }

    public DependencyNoticeDocument Document { get; }

    public IReadOnlyList<RenderedNoticeOutput> Outputs { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }

    public bool Succeeded => NoticeResult.HasNoErrors(Diagnostics);
}

public sealed record NoticeVerifyRequest
{
    public NoticeVerifyRequest(
        NoticeGenerateRequest generation,
        string expectedOutputDirectory,
        IEnumerable<string>? expectedRelativePaths = null,
        NoticeOperationPolicy? operationPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOutputDirectory);
        Generation = generation;
        ExpectedOutputDirectory = expectedOutputDirectory;
        ExpectedRelativePaths = Snapshot.List(expectedRelativePaths ?? []);
        OperationPolicy = operationPolicy ?? NoticeOperationPolicy.Offline;
    }

    public NoticeGenerateRequest Generation { get; }

    public string ExpectedOutputDirectory { get; }

    public IReadOnlyList<string> ExpectedRelativePaths { get; }

    public NoticeOperationPolicy OperationPolicy { get; }
}

public sealed record NoticeVerificationResult
{
    public NoticeVerificationResult(
        GeneratedNoticeResult generated,
        IEnumerable<NoticeDiagnostic> diagnostics)
    {
        Generated = generated ?? throw new ArgumentNullException(nameof(generated));
        Diagnostics = Snapshot.List(diagnostics);
    }

    public GeneratedNoticeResult Generated { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }

    public bool Succeeded => NoticeResult.HasNoErrors(Diagnostics);
}

internal static class Snapshot
{
    public static IReadOnlyList<T> List<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values is T[] array ? (T[])array.Clone() : [.. values]);

    public static IReadOnlyDictionary<string, TValue> Dictionary<TValue>(
        IReadOnlyDictionary<string, TValue>? values)
    {
        Dictionary<string, TValue> copy = new(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (KeyValuePair<string, TValue> pair in values)
            {
                copy.Add(pair.Key, pair.Value);
            }
        }

        return new ReadOnlyDictionary<string, TValue>(copy);
    }

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> SetDictionary(
        IReadOnlyDictionary<string, IReadOnlySet<string>>? values)
    {
        Dictionary<string, IReadOnlySet<string>> copy = new(StringComparer.Ordinal);
        if (values is not null)
        {
            foreach (KeyValuePair<string, IReadOnlySet<string>> pair in values)
            {
                copy.Add(pair.Key, new SnapshotReadOnlySet<string>(pair.Value, StringComparer.Ordinal));
            }
        }

        return new ReadOnlyDictionary<string, IReadOnlySet<string>>(copy);
    }

    public static LicensePolicy Policy(LicensePolicy policy)
    {
        Dictionary<string, IReadOnlyList<string>> obligations = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, IReadOnlyList<string>> pair in policy.Obligations)
        {
            obligations.Add(pair.Key, List(pair.Value));
        }

        return new LicensePolicy(
            new SnapshotReadOnlySet<string>(policy.Allowed, StringComparer.Ordinal),
            new SnapshotReadOnlySet<string>(policy.Denied, StringComparer.Ordinal),
            new SnapshotReadOnlySet<string>(policy.Review, StringComparer.Ordinal),
            new ReadOnlyDictionary<string, IReadOnlyList<string>>(obligations),
            policy.DefaultOutcome,
            policy.RequireExplicitOrSelection);
    }
}

internal sealed class SnapshotReadOnlySet<T> : IReadOnlySet<T>
{
    private readonly HashSet<T> _values;

    public SnapshotReadOnlySet(IEnumerable<T> values, IEqualityComparer<T>? comparer = null)
    {
        _values = new HashSet<T>(values, comparer);
    }

    public int Count => _values.Count;

    public bool Contains(T item) => _values.Contains(item);

    public bool IsProperSubsetOf(IEnumerable<T> other) => _values.IsProperSubsetOf(other);

    public bool IsProperSupersetOf(IEnumerable<T> other) => _values.IsProperSupersetOf(other);

    public bool IsSubsetOf(IEnumerable<T> other) => _values.IsSubsetOf(other);

    public bool IsSupersetOf(IEnumerable<T> other) => _values.IsSupersetOf(other);

    public bool Overlaps(IEnumerable<T> other) => _values.Overlaps(other);

    public bool SetEquals(IEnumerable<T> other) => _values.SetEquals(other);

    public IEnumerator<T> GetEnumerator() => _values.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class NoticeResult
{
    public static bool HasNoErrors(IReadOnlyList<NoticeDiagnostic> diagnostics)
    {
        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == NoticeDiagnosticSeverity.Error)
            {
                return false;
            }
        }

        return true;
    }
}
