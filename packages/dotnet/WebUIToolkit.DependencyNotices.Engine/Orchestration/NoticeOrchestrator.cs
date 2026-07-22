using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Policy;

namespace WebUIToolkit.DependencyNotices.Engine;

public sealed class NoticeOrchestrator
{
    private readonly INoticeFileSystem _fileSystem;
    private readonly IDiagnosticSink _diagnostics;

    public NoticeOrchestrator(
        INoticeFileSystem fileSystem,
        IDiagnosticSink? diagnostics = null)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _diagnostics = diagnostics ?? NullDiagnosticSink.Instance;
    }

    public async ValueTask<NoticeScanResult> ScanAsync(
        NoticeScanRequest request,
        IEnumerable<IInventoryAdapter> adapters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(adapters);
        request.OperationPolicy.EnsureOffline(NoticeOperation.Scan);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<InventorySourceKind, IInventoryAdapter> adaptersByKind = [];
        foreach (IInventoryAdapter adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!adaptersByKind.TryAdd(adapter.SourceKind, adapter))
            {
                throw new ArgumentException(
                    $"More than one inventory adapter was registered for '{adapter.SourceKind}'.",
                    nameof(adapters));
            }
        }

        List<InventoryInput> inputs = [.. request.Inputs];
        inputs.Sort(static (left, right) =>
        {
            int result = left.SourceKind.CompareTo(right.SourceKind);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath);
        });

        List<InventoryComponent> merged = [];
        List<NoticeDiagnostic> diagnostics = [];
        Dictionary<string, ComponentNoticeMetadata> metadataCandidates = new(StringComparer.Ordinal);
        foreach (InventoryInput input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!adaptersByKind.TryGetValue(input.SourceKind, out IInventoryAdapter? adapter))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                    NoticeDiagnosticSeverity.Error,
                    $"No inventory adapter is registered for '{input.SourceKind}'.",
                    Source: input.RelativePath));
                continue;
            }

            InventoryAdapterResult result = await adapter.ScanAsync(
                new InventoryAdapterContext(
                    request.RootDirectory,
                    input,
                    _fileSystem,
                    NullDiagnosticSink.Instance,
                    request.OperationPolicy),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            merged.AddRange(result.Components);
            diagnostics.AddRange(result.Diagnostics);
            foreach (KeyValuePair<string, ComponentNoticeMetadata> pair in result.Metadata)
            {
                metadataCandidates.TryAdd(pair.Key, pair.Value);
            }
        }

        merged.Sort(InventoryMergeComparer.Instance);
        List<InventoryComponent> distinct = new(merged.Count);
        Dictionary<string, ComponentNoticeMetadata> metadata = new(StringComparer.Ordinal);
        string? previousIdentity = null;
        foreach (InventoryComponent component in merged)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string identity = component.PackageUrl.CanonicalValue;
            if (StringComparer.Ordinal.Equals(identity, previousIdentity))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.DuplicatePackageUrl,
                    NoticeDiagnosticSeverity.Error,
                    "The canonical Package URL occurs in more than one inventory input.",
                    identity,
                    component.SourcePath));
                continue;
            }

            previousIdentity = identity;
            distinct.Add(component);
            if (metadataCandidates.TryGetValue(identity, out ComponentNoticeMetadata? componentMetadata))
            {
                metadata.Add(identity, componentMetadata);
            }
        }

        SortAndReport(diagnostics);
        return new NoticeScanResult(distinct, diagnostics, metadata);
    }

    public async ValueTask<NoticeEvaluationResult> EvaluateAsync(
        NoticeEvaluateRequest request,
        INoticeEvidenceResolver evidenceResolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidenceResolver);
        request.OperationPolicy.EnsureOffline(NoticeOperation.Evaluate);
        cancellationToken.ThrowIfCancellationRequested();

        List<InventoryComponent> components = [.. request.Scan.Components];
        components.Sort(InventoryMergeComparer.Instance);
        List<EvaluatedNoticeComponent> evaluated = new(components.Count);
        List<NoticeDiagnostic> diagnostics = [.. request.Scan.Diagnostics];

        foreach (InventoryComponent component in components)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EvidenceResolutionResult evidence = await evidenceResolver.ResolveAsync(
                new EvidenceResolutionContext(
                    component,
                    _fileSystem,
                    NullDiagnosticSink.Instance,
                    request.OperationPolicy),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.AddRange(evidence.Diagnostics);

            string identity = component.PackageUrl.CanonicalValue;
            LicensePolicyEvaluation policyEvaluation;
            if (string.IsNullOrWhiteSpace(component.ObservedLicenseExpression))
            {
                NoticeDiagnostic diagnostic = new(
                    NoticeDiagnosticCodes.InvalidSpdxExpression,
                    NoticeDiagnosticSeverity.Error,
                    "The inventory component has no observed SPDX license expression.",
                    identity,
                    component.SourcePath,
                    Remediation: "Provide an SPDX expression backed by exact evidence.");
                diagnostics.Add(diagnostic);
                policyEvaluation = new LicensePolicyEvaluation(
                    string.Empty,
                    string.Empty,
                    null,
                    LicensePolicyOutcome.Deny,
                    Array.AsReadOnly([diagnostic]));
            }
            else
            {
                request.SelectedLicenseExpressions.TryGetValue(identity, out string? selected);
                request.FulfilledObligations.TryGetValue(identity, out IReadOnlySet<string>? obligations);
                bool hasLicenseEvidence = HasLicenseEvidence(component, evidence.Assets);
                policyEvaluation = LicensePolicyEvaluator.Evaluate(
                    component.PackageUrl,
                    component.ObservedLicenseExpression,
                    selected,
                    hasLicenseEvidence,
                    request.Policy,
                    obligations);
                diagnostics.AddRange(policyEvaluation.Diagnostics);
            }

            List<NoticeAsset> assets = [.. evidence.Assets];
            assets.Sort(NoticeAssetComparer.Instance);
            request.Scan.Metadata.TryGetValue(identity, out ComponentNoticeMetadata? metadata);
            evaluated.Add(new EvaluatedNoticeComponent(
                component,
                policyEvaluation,
                Array.AsReadOnly(assets.ToArray()),
                metadata ?? new ComponentNoticeMetadata()));
        }

        SortAndReport(diagnostics, request.Scan.Diagnostics);
        return new NoticeEvaluationResult(evaluated, diagnostics);
    }

    public async ValueTask<GeneratedNoticeResult> GenerateAsync(
        NoticeGenerateRequest request,
        IEnumerable<INoticeRenderer> renderers,
        ISbomReconciler? sbomReconciler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.OperationPolicy.EnsureOffline(NoticeOperation.Generate);
        GeneratedNoticeResult result = await PrepareAsync(
            request,
            renderers,
            sbomReconciler,
            NoticeOperation.Generate,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return result;
        }

        _fileSystem.CreateDirectory(request.OutputDirectory);
        foreach (RenderedNoticeOutput output in result.Outputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string outputPath = SafePath.ResolveContainedPath(
                request.OutputDirectory,
                output.RelativePath);
            string? directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                _fileSystem.CreateDirectory(directory);
            }

            await _fileSystem.WriteAllBytesAsync(
                outputPath,
                output.Content,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public async ValueTask<NoticeVerificationResult> VerifyAsync(
        NoticeVerifyRequest request,
        IEnumerable<INoticeRenderer> renderers,
        ISbomReconciler? sbomReconciler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.OperationPolicy.EnsureOffline(NoticeOperation.Verify);
        request.Generation.OperationPolicy.EnsureOffline(NoticeOperation.Verify);
        cancellationToken.ThrowIfCancellationRequested();

        GeneratedNoticeResult generated = await PrepareAsync(
            request.Generation,
            renderers,
            sbomReconciler,
            NoticeOperation.Verify,
            cancellationToken).ConfigureAwait(false);
        List<NoticeDiagnostic> diagnostics = [.. generated.Diagnostics];

        if (generated.Succeeded)
        {
            ValidateDeclaredOutputs(request, generated.Outputs, diagnostics);
            foreach (RenderedNoticeOutput output in generated.Outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string expectedPath = SafePath.ResolveContainedPath(
                    request.ExpectedOutputDirectory,
                    output.RelativePath);
                if (!_fileSystem.FileExists(expectedPath))
                {
                    diagnostics.Add(OutputDrift(output.RelativePath, "The declared output is missing."));
                    continue;
                }

                byte[] expected = await _fileSystem.ReadAllBytesAsync(
                    expectedPath,
                    cancellationToken).ConfigureAwait(false);
                if (!VerificationByteComparison.Equals(expected, output.Content.Span))
                {
                    diagnostics.Add(OutputDrift(
                        output.RelativePath,
                        $"The declared output differs byte-for-byte (expected {expected.Length} bytes, generated {output.Content.Length} bytes)."));
                }
            }
        }

        SortAndReport(diagnostics, generated.Diagnostics);
        return new NoticeVerificationResult(generated, diagnostics);
    }

    private async ValueTask<GeneratedNoticeResult> PrepareAsync(
        NoticeGenerateRequest request,
        IEnumerable<INoticeRenderer> renderers,
        ISbomReconciler? sbomReconciler,
        NoticeOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(renderers);
        request.OperationPolicy.EnsureOffline(operation);
        cancellationToken.ThrowIfCancellationRequested();

        List<NoticeDiagnostic> diagnostics = [.. request.Evaluation.Diagnostics];
        SbomReconciliationResult? sbom = null;
        if (request.Sbom is not null)
        {
            if (sbomReconciler is null)
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.UnsupportedInventoryFormat,
                    NoticeDiagnosticSeverity.Error,
                    "An SBOM input was declared but no SBOM reconciler was provided.",
                    Source: request.Sbom.RelativePath));
            }
            else
            {
                List<InventoryComponent> inventory = new(request.Evaluation.Components.Count);
                foreach (EvaluatedNoticeComponent component in request.Evaluation.Components)
                {
                    inventory.Add(component.Component);
                }

                sbom = await sbomReconciler.ReconcileAsync(
                    new SbomReconciliationContext(
                        request.Sbom,
                        Array.AsReadOnly(inventory.ToArray()),
                        _fileSystem,
                        NullDiagnosticSink.Instance,
                        request.OperationPolicy),
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                diagnostics.AddRange(sbom.Diagnostics);
            }
        }

        DependencyNoticeDocument document = BuildDocument(request, sbom, diagnostics);
        List<RenderedNoticeOutput> outputs = [];
        if (NoticeResult.HasNoErrors(diagnostics))
        {
            List<INoticeRenderer> orderedRenderers = [.. renderers];
            orderedRenderers.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.Format, right.Format));
            HashSet<string> outputPaths = new(StringComparer.Ordinal);
            HashSet<string> formats = new(StringComparer.Ordinal);
            foreach (INoticeRenderer renderer in orderedRenderers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!formats.Add(renderer.Format))
                {
                    diagnostics.Add(new NoticeDiagnostic(
                        NoticeDiagnosticCodes.OutputDrift,
                        NoticeDiagnosticSeverity.Error,
                        $"More than one renderer is registered for format '{renderer.Format}'."));
                    continue;
                }

                RenderedNoticeOutput output = await renderer.RenderAsync(
                    document,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _ = SafePath.ResolveContainedPath(request.OutputDirectory, output.RelativePath);
                }
                catch (NoticeSecurityException exception)
                {
                    diagnostics.Add(new NoticeDiagnostic(
                        NoticeDiagnosticCodes.UnsafeOutputDestination,
                        NoticeDiagnosticSeverity.Error,
                        exception.Message,
                        Source: output.RelativePath));
                    continue;
                }

                if (!outputPaths.Add(output.RelativePath))
                {
                    diagnostics.Add(new NoticeDiagnostic(
                        NoticeDiagnosticCodes.OutputDrift,
                        NoticeDiagnosticSeverity.Error,
                        "More than one renderer declared the same output path.",
                        Source: output.RelativePath));
                    continue;
                }

                outputs.Add(output);
            }
        }

        outputs.Sort(RenderedOutputComparer.Instance);
        SortAndReport(diagnostics, request.Evaluation.Diagnostics);
        return new GeneratedNoticeResult(document, outputs, diagnostics);
    }

    private static DependencyNoticeDocument BuildDocument(
        NoticeGenerateRequest request,
        SbomReconciliationResult? sbom,
        List<NoticeDiagnostic> diagnostics)
    {
        List<DependencyNotice> dependencies = new(request.Evaluation.Components.Count);
        foreach (EvaluatedNoticeComponent evaluated in request.Evaluation.Components)
        {
            InventoryComponent component = evaluated.Component;
            string identity = component.PackageUrl.CanonicalValue;
            string? componentReference = null;
            if (sbom is not null)
            {
                sbom.ComponentReferences.TryGetValue(identity, out componentReference);
            }

            List<NoticePolicyDecision> decisions =
            [
                new NoticePolicyDecision(
                    evaluated.PolicyEvaluation.EffectiveExpression,
                    evaluated.PolicyEvaluation.Outcome,
                    "license-policy"),
            ];
            dependencies.Add(new DependencyNotice(
                identity,
                component.Name,
                component.Version,
                component.PackageUrl.Type switch
                {
                    "nuget" => DependencyEcosystem.NuGet,
                    "npm" => DependencyEcosystem.Npm,
                    _ => DependencyEcosystem.Generic,
                },
                component.Scope,
                component.IsDirect,
                evaluated.PolicyEvaluation.ObservedExpression,
                evaluated.PolicyEvaluation.EffectiveExpression,
                evaluated.PolicyEvaluation.SelectedExpression,
                evaluated.Assets,
                Array.AsReadOnly(decisions.ToArray()),
                componentReference,
                evaluated.Metadata.IsModified,
                evaluated.Metadata.ModificationNotice));
        }

        dependencies.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.PackageUrl, right.PackageUrl));
        return new DependencyNoticeDocument(
            SchemaVersion: NoticeContractVersions.NoticeDocument,
            request.ArtifactName,
            request.ArtifactVersion,
            Array.AsReadOnly(dependencies.ToArray()),
            sbom?.Link,
            Array.AsReadOnly(diagnostics.ToArray()));
    }

    private void SortAndReport(
        List<NoticeDiagnostic> diagnostics,
        IReadOnlyList<NoticeDiagnostic>? incomingDiagnostics = null)
    {
        diagnostics.Sort(DiagnosticComparer.Instance);
        if (incomingDiagnostics is null || incomingDiagnostics.Count == 0)
        {
            foreach (NoticeDiagnostic diagnostic in diagnostics)
            {
                _diagnostics.Report(diagnostic);
            }

            return;
        }

        Dictionary<NoticeDiagnostic, int> incoming = [];
        foreach (NoticeDiagnostic diagnostic in incomingDiagnostics)
        {
            incoming.TryGetValue(diagnostic, out int count);
            incoming[diagnostic] = count + 1;
        }

        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (incoming.TryGetValue(diagnostic, out int count) && count > 0)
            {
                incoming[diagnostic] = count - 1;
                continue;
            }

            _diagnostics.Report(diagnostic);
        }
    }

    private static bool HasLicenseEvidence(
        InventoryComponent component,
        IReadOnlyList<NoticeAsset> assets)
    {
        foreach (NoticeEvidence evidence in component.Evidence)
        {
            if (evidence.Kind == NoticeAssetKind.License)
            {
                return true;
            }
        }

        foreach (NoticeAsset asset in assets)
        {
            if (asset.Kind == NoticeAssetKind.License)
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateDeclaredOutputs(
        NoticeVerifyRequest request,
        IReadOnlyList<RenderedNoticeOutput> generated,
        List<NoticeDiagnostic> diagnostics)
    {
        if (request.ExpectedRelativePaths.Count == 0)
        {
            return;
        }

        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (string path in request.ExpectedRelativePaths)
        {
            string normalized = path.Replace('\\', '/');
            if (!declared.Add(normalized))
            {
                diagnostics.Add(OutputDrift(normalized, "The expected output path is declared more than once."));
            }
        }

        HashSet<string> actual = new(StringComparer.Ordinal);
        foreach (RenderedNoticeOutput output in generated)
        {
            actual.Add(output.RelativePath);
            if (!declared.Contains(output.RelativePath))
            {
                diagnostics.Add(OutputDrift(output.RelativePath, "The generated output path is not declared."));
            }
        }

        foreach (string path in declared)
        {
            if (!actual.Contains(path))
            {
                diagnostics.Add(OutputDrift(path, "A declared output was not generated."));
            }
        }
    }

    private static NoticeDiagnostic OutputDrift(string path, string message) => new(
        NoticeDiagnosticCodes.OutputDrift,
        NoticeDiagnosticSeverity.Error,
        message,
        Source: path,
        Remediation: "Regenerate the declared output from the same locked inputs.");
}

internal sealed class NoticeAssetComparer : IComparer<NoticeAsset>
{
    public static NoticeAssetComparer Instance { get; } = new();

    public int Compare(NoticeAsset? x, NoticeAsset? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int result = x.Kind.CompareTo(y.Kind);
        result = result != 0 ? result : StringComparer.Ordinal.Compare(x.Sha256, y.Sha256);
        result = result != 0 ? result : StringComparer.Ordinal.Compare(x.Origin, y.Origin);
        return result != 0 ? result : StringComparer.Ordinal.Compare(x.MediaType, y.MediaType);
    }
}

public static class VerificationByteComparison
{
    public static bool Equals(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual) =>
        expected.SequenceEqual(actual);
}
