using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Engine;
using WebUIToolkit.DependencyNotices.Policy;

return await IntegrationTests.RunAsync();

internal static class IntegrationTests
{
    private static int _passed;

    public static async Task<int> RunAsync()
    {
        await RunAsync("scan snapshots mutable input", ScanSnapshotsInputAsync);
        await RunAsync("scan merges in canonical order", ScanIsDeterministicAsync);
        await RunAsync("scan rejects duplicate canonical purl", ScanRejectsDuplicateAsync);
        await RunAsync("scan observes cancellation", ScanObservesCancellationAsync);
        await RunAsync("scan rejects online operation policy", ScanRejectsOnlinePolicyAsync);
        await RunAsync("manual projection preserves metadata", ManualProjectionPreservesMetadataAsync);
        await RunAsync("evaluate resolves evidence and policy", EvaluateResolvesEvidenceAndPolicyAsync);
        await RunAsync("generate writes stable renderer bytes", GenerateWritesRendererBytesAsync);
        await RunAsync("verify is non-mutating", VerifyDoesNotWriteAsync);
        await RunAsync("verify reports byte drift", VerifyReportsByteDriftAsync);
        Console.WriteLine($"Engine integration tests: {_passed} passed, 0 failed.");
        return 0;
    }

    private static async Task RunAsync(string name, Func<Task> test)
    {
        try
        {
            await test();
            _passed++;
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {name}: {exception}");
            Environment.ExitCode = 1;
            throw;
        }
    }

    private static async Task ScanSnapshotsInputAsync()
    {
        List<InventoryInput> inputs = [new(InventorySourceKind.NuGet, "a.lock.json")];
        NoticeScanRequest request = new("root", inputs);
        inputs.Clear();
        Equal(1, request.Inputs.Count);
        NoticeScanResult result = await Orchestrator().ScanAsync(request, [new FixedAdapter(InventorySourceKind.NuGet, Component("a"))]);
        Equal(1, result.Components.Count);
    }

    private static async Task ScanIsDeterministicAsync()
    {
        NoticeScanRequest request = new("root",
        [
            new InventoryInput(InventorySourceKind.Npm, "z.json"),
            new InventoryInput(InventorySourceKind.NuGet, "a.json"),
        ]);
        NoticeScanResult result = await Orchestrator().ScanAsync(request,
        [
            new FixedAdapter(InventorySourceKind.Npm, Component("z", "npm")),
            new FixedAdapter(InventorySourceKind.NuGet, Component("a", "nuget")),
        ]);
        Equal("pkg:npm/z@1.0.0", result.Components[0].PackageUrl.CanonicalValue);
        Equal("pkg:nuget/a@1.0.0", result.Components[1].PackageUrl.CanonicalValue);
    }

    private static async Task ScanRejectsDuplicateAsync()
    {
        InventoryComponent second = Component("same") with { SourcePath = "b.json" };
        NoticeScanResult result = await Orchestrator().ScanAsync(
            new NoticeScanRequest("root",
            [
                new InventoryInput(InventorySourceKind.Manual, "a.json"),
                new InventoryInput(InventorySourceKind.Manual, "b.json"),
            ]),
            [new FixedAdapter(InventorySourceKind.Manual, Component("same"), second)]);
        Equal(1, result.Components.Count);
        ContainsCode(result.Diagnostics, NoticeDiagnosticCodes.DuplicatePackageUrl);
    }

    private static async Task ScanObservesCancellationAsync()
    {
        using CancellationTokenSource source = new();
        source.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
            await Orchestrator().ScanAsync(
                new NoticeScanRequest("root", [new InventoryInput(InventorySourceKind.Manual, "manual.json")]),
                [new FixedAdapter(InventorySourceKind.Manual, Component("a"))],
                source.Token));
    }

    private static async Task ScanRejectsOnlinePolicyAsync()
    {
        await ThrowsAsync<NoticeSecurityException>(async () =>
            await Orchestrator().ScanAsync(
                new NoticeScanRequest(
                    "root",
                    [],
                    new NoticeOperationPolicy(NoticeNetworkAccess.AcquisitionOnly)),
                []));
    }

    private static Task ManualProjectionPreservesMetadataAsync()
    {
        ManualDependencyComponent component = new(
            PackageUrl.Parse("pkg:generic/fork@1.0.0"),
            "fork",
            "1.0.0",
            "abc",
            "MIT",
            Array.AsReadOnly(
            [
                new NoticeEvidence(NoticeAssetKind.License, new string('a', 64), "license.txt"),
            ]),
            IsModified: true,
            "Local patch");
        InventoryAdapterResult result = ManualInventoryProjection.Project(
            new ManualScanResult(Array.AsReadOnly([component]), Array.Empty<NoticeDiagnostic>()),
            "manual.json");
        Equal(InventorySourceKind.Manual, result.Components[0].SourceKind);
        True(result.Metadata[component.PackageUrl.CanonicalValue].IsModified);
        Equal("Local patch", result.Metadata[component.PackageUrl.CanonicalValue].ModificationNotice);
        return Task.CompletedTask;
    }

    private static async Task EvaluateResolvesEvidenceAndPolicyAsync()
    {
        InventoryComponent component = Component("allowed") with
        {
            Evidence = Array.AsReadOnly(
            [
                new NoticeEvidence(NoticeAssetKind.License, new string('a', 64), "license.txt"),
            ]),
        };
        NoticeScanResult scan = new([component], []);
        NoticeEvaluationResult result = await Orchestrator().EvaluateAsync(
            new NoticeEvaluateRequest(scan, LicensePolicy.Create(allowed: ["MIT"])),
            new FixedEvidenceResolver(Asset("MIT license")));
        True(result.Succeeded);
        Equal(LicensePolicyOutcome.Allow, result.Components[0].PolicyEvaluation.Outcome);
        Equal("MIT license", result.Components[0].Assets[0].Text);
    }

    private static async Task GenerateWritesRendererBytesAsync()
    {
        InMemoryFileSystem fileSystem = new();
        NoticeOrchestrator orchestrator = new(fileSystem);
        NoticeGenerateRequest request = GenerateRequest();
        GeneratedNoticeResult result = await orchestrator.GenerateAsync(
            request,
            [new FixedRenderer("text", "notices.txt", "stable")]);
        True(result.Succeeded);
        Equal(1, fileSystem.WriteCount);
        Equal("stable", Encoding.UTF8.GetString(fileSystem.SingleValue()));
    }

    private static async Task VerifyDoesNotWriteAsync()
    {
        InMemoryFileSystem fileSystem = new();
        NoticeGenerateRequest generation = GenerateRequest();
        string expectedPath = SafePath.ResolveContainedPath(generation.OutputDirectory, "notices.txt");
        fileSystem.Seed(expectedPath, Encoding.UTF8.GetBytes("stable"));
        int writesBefore = fileSystem.WriteCount;
        NoticeVerificationResult result = await new NoticeOrchestrator(fileSystem).VerifyAsync(
            new NoticeVerifyRequest(generation, generation.OutputDirectory, ["notices.txt"]),
            [new FixedRenderer("text", "notices.txt", "stable")]);
        True(result.Succeeded);
        Equal(writesBefore, fileSystem.WriteCount);
    }

    private static async Task VerifyReportsByteDriftAsync()
    {
        InMemoryFileSystem fileSystem = new();
        NoticeGenerateRequest generation = GenerateRequest();
        string expectedPath = SafePath.ResolveContainedPath(generation.OutputDirectory, "notices.txt");
        fileSystem.Seed(expectedPath, Encoding.UTF8.GetBytes("old"));
        NoticeVerificationResult result = await new NoticeOrchestrator(fileSystem).VerifyAsync(
            new NoticeVerifyRequest(generation, generation.OutputDirectory, ["notices.txt"]),
            [new FixedRenderer("text", "notices.txt", "new")]);
        True(!result.Succeeded);
        ContainsCode(result.Diagnostics, NoticeDiagnosticCodes.OutputDrift);
        Equal(0, fileSystem.WriteCount);
    }

    private static NoticeGenerateRequest GenerateRequest()
    {
        InventoryComponent component = Component("package");
        LicensePolicyEvaluation decision = new("MIT", "MIT", null, LicensePolicyOutcome.Allow, []);
        NoticeEvaluationResult evaluation = new(
            [new EvaluatedNoticeComponent(component, decision, [Asset("MIT")], new ComponentNoticeMetadata())],
            []);
        return new NoticeGenerateRequest(
            evaluation,
            "artifact",
            Path.Combine(Path.GetTempPath(), "wut-engine-tests", "output"),
            "1.0.0");
    }

    private static NoticeOrchestrator Orchestrator() => new(new InMemoryFileSystem());

    private static InventoryComponent Component(string name, string type = "generic") => new(
        PackageUrl.Parse($"pkg:{type}/{name}@1.0.0"),
        name,
        "1.0.0",
        type switch
        {
            "nuget" => InventorySourceKind.NuGet,
            "npm" => InventorySourceKind.Npm,
            _ => InventorySourceKind.Manual,
        },
        DependencyScope.Runtime,
        IsDirect: true,
        "MIT",
        null,
        "source.json",
        Array.Empty<NoticeEvidence>());

    private static NoticeAsset Asset(string text) => new(
        NoticeAssetKind.License,
        new string('a', 64),
        "text/plain",
        text,
        "fixture",
        IsOverride: false);

    private static void ContainsCode(IReadOnlyList<NoticeDiagnostic> diagnostics, string code)
    {
        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Code == code)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Expected diagnostic {code}.");
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
        }
    }

    private static void True(bool value)
    {
        if (!value)
        {
            throw new InvalidOperationException("Expected true.");
        }
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class FixedAdapter(InventorySourceKind sourceKind, params InventoryComponent[] components) : IInventoryAdapter
    {
        public InventorySourceKind SourceKind => sourceKind;

        public ValueTask<InventoryAdapterResult> ScanAsync(InventoryAdapterContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<InventoryComponent> selected = components;
            if (components.Length > 1)
            {
                selected = context.Input.RelativePath == "a.json" ? [components[0]] : [components[1]];
            }

            return ValueTask.FromResult(new InventoryAdapterResult(selected));
        }
    }

    private sealed class FixedEvidenceResolver(params NoticeAsset[] assets) : INoticeEvidenceResolver
    {
        public ValueTask<EvidenceResolutionResult> ResolveAsync(EvidenceResolutionContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new EvidenceResolutionResult(assets));
        }
    }

    private sealed class FixedRenderer(string format, string path, string content) : INoticeRenderer
    {
        public string Format => format;

        public ValueTask<RenderedNoticeOutput> RenderAsync(DependencyNoticeDocument document, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RenderedNoticeOutput(path, Encoding.UTF8.GetBytes(content)));
        }
    }

    private sealed class InMemoryFileSystem : INoticeFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public int WriteCount { get; private set; }

        public bool FileExists(string path) => _files.ContainsKey(Path.GetFullPath(path));

        public void CreateDirectory(string path)
        {
        }

        public ValueTask<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult((byte[])_files[Path.GetFullPath(path)].Clone());
        }

        public ValueTask WriteAllBytesAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files[Path.GetFullPath(path)] = content.ToArray();
            WriteCount++;
            return ValueTask.CompletedTask;
        }

        public void Seed(string path, byte[] content) =>
            _files[Path.GetFullPath(path)] = (byte[])content.Clone();

        public byte[] SingleValue()
        {
            foreach (byte[] value in _files.Values)
            {
                return value;
            }

            throw new InvalidOperationException("No values.");
        }
    }
}
