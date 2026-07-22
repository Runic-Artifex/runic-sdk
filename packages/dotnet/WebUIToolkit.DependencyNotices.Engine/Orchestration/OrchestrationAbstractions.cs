using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;

namespace WebUIToolkit.DependencyNotices.Engine;

public sealed record InventoryAdapterContext(
    string RootDirectory,
    InventoryInput Input,
    INoticeFileSystem FileSystem,
    IDiagnosticSink Diagnostics,
    NoticeOperationPolicy OperationPolicy);

public interface IInventoryAdapter
{
    InventorySourceKind SourceKind { get; }

    ValueTask<InventoryAdapterResult> ScanAsync(
        InventoryAdapterContext context,
        CancellationToken cancellationToken);
}

public sealed record EvidenceResolutionContext(
    InventoryComponent Component,
    INoticeFileSystem FileSystem,
    IDiagnosticSink Diagnostics,
    NoticeOperationPolicy OperationPolicy);

public sealed record EvidenceResolutionResult
{
    public EvidenceResolutionResult(
        IEnumerable<NoticeAsset> assets,
        IEnumerable<NoticeDiagnostic>? diagnostics = null)
    {
        Assets = Snapshot.List(assets);
        Diagnostics = Snapshot.List(diagnostics ?? []);
    }

    public IReadOnlyList<NoticeAsset> Assets { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }
}

public interface INoticeEvidenceResolver
{
    ValueTask<EvidenceResolutionResult> ResolveAsync(
        EvidenceResolutionContext context,
        CancellationToken cancellationToken);
}

public sealed record SbomReconciliationContext(
    SbomInput Input,
    IReadOnlyList<InventoryComponent> Components,
    INoticeFileSystem FileSystem,
    IDiagnosticSink Diagnostics,
    NoticeOperationPolicy OperationPolicy);

public sealed record SbomReconciliationResult
{
    public SbomReconciliationResult(
        SbomLink link,
        IReadOnlyDictionary<string, string>? componentReferences = null,
        IEnumerable<NoticeDiagnostic>? diagnostics = null)
    {
        Link = link ?? throw new ArgumentNullException(nameof(link));
        ComponentReferences = Snapshot.Dictionary(componentReferences);
        Diagnostics = Snapshot.List(diagnostics ?? []);
    }

    public SbomLink Link { get; }

    public IReadOnlyDictionary<string, string> ComponentReferences { get; }

    public IReadOnlyList<NoticeDiagnostic> Diagnostics { get; }
}

public interface ISbomReconciler
{
    ValueTask<SbomReconciliationResult> ReconcileAsync(
        SbomReconciliationContext context,
        CancellationToken cancellationToken);
}

public interface INoticeRenderer
{
    string Format { get; }

    ValueTask<RenderedNoticeOutput> RenderAsync(
        DependencyNoticeDocument document,
        CancellationToken cancellationToken);
}

public interface INoticeFileSystem
{
    bool FileExists(string path);

    void CreateDirectory(string path);

    ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken);

    ValueTask WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}

public sealed class PhysicalNoticeFileSystem : INoticeFileSystem
{
    public static PhysicalNoticeFileSystem Instance { get; } = new();

    private PhysicalNoticeFileSystem()
    {
    }

    public bool FileExists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken) =>
        new(File.ReadAllBytesAsync(path, cancellationToken));

    public ValueTask WriteAllBytesAsync(
        string path,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken) =>
        new(File.WriteAllBytesAsync(path, content, cancellationToken));
}

public interface IDiagnosticSink
{
    void Report(NoticeDiagnostic diagnostic);
}

public sealed class NullDiagnosticSink : IDiagnosticSink
{
    public static NullDiagnosticSink Instance { get; } = new();

    private NullDiagnosticSink()
    {
    }

    public void Report(NoticeDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
    }
}
