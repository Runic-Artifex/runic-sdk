using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WebUIToolkit.DependencyNotices.Diagnostics;
using WebUIToolkit.DependencyNotices.Acquisition;
using WebUIToolkit.DependencyNotices.Engine;
using WebUIToolkit.DependencyNotices.Evidence;
using WebUIToolkit.DependencyNotices.Npm;
using WebUIToolkit.DependencyNotices.NuGet;
using WebUIToolkit.DependencyNotices.Policy;
using WebUIToolkit.DependencyNotices.Rendering;
using WebUIToolkit.DependencyNotices.Sbom;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Tool;

public static class ToolApplication
{
    private const int MaximumNuGetEvidenceBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RenderedOutputNames =
        [NoticeOutputNames.Html, NoticeOutputNames.Json, NoticeOutputNames.Manifest, NoticeOutputNames.Text];

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        ToolParseResult parsed = CommandLineParser.Parse(args);
        if (!parsed.Succeeded)
        {
            string error = parsed.Error!;
            string code = error.StartsWith(NoticeDiagnosticCodes.NetworkAccessForbidden, StringComparison.Ordinal)
                ? NoticeDiagnosticCodes.NetworkAccessForbidden
                : NoticeDiagnosticCodes.InvalidManualComponent;
            await WriteErrorAsync(RequestsJson(args) ? ToolOutputFormat.Json : ToolOutputFormat.Human, standardError, code, error).ConfigureAwait(false);
            return code == NoticeDiagnosticCodes.NetworkAccessForbidden
                ? ToolExitCodes.AcquisitionOrNetworkFailure
                : ToolExitCodes.InvalidCommandOrConfiguration;
        }

        ToolInvocation invocation = parsed.Invocation!;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return invocation.Command switch
            {
                ToolCommand.Help => await WriteHelpAsync(standardOutput).ConfigureAwait(false),
                ToolCommand.ManualScan => await RunManualScanAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.NuGetScan => await RunNuGetScanAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.NpmScan => await RunNpmScanAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.ContractPackageUrl => await RunPackageUrlAsync(invocation, standardOutput).ConfigureAwait(false),
                ToolCommand.ContractSpdx => await RunSpdxAsync(invocation, standardOutput).ConfigureAwait(false),
                ToolCommand.ContractDiagnostics => await RunDiagnosticsAsync(invocation, standardOutput).ConfigureAwait(false),
                ToolCommand.Policy => await RunPolicyAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.Generate => await RunGenerateAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.Verify => await RunVerifyAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.Sbom => await RunSbomAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                ToolCommand.Acquire => await RunAcquireAsync(invocation, standardOutput, cancellationToken).ConfigureAwait(false),
                _ => ToolExitCodes.InvalidCommandOrConfiguration,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return ToolExitCodes.UnexpectedFailure;
        }
        catch (PolicyConfigurationException exception)
        {
            await WriteErrorAsync(invocation.Format, standardError, exception.Code, exception.Message).ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (SbomFormatException exception)
        {
            await WriteErrorAsync(invocation.Format, standardError, NoticeDiagnosticCodes.UnsupportedInventoryFormat, exception.Message).ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (FormatException exception)
        {
            string code = invocation.Command == ToolCommand.ContractSpdx
                ? NoticeDiagnosticCodes.InvalidSpdxExpression
                : NoticeDiagnosticCodes.InvalidPackageUrl;
            await WriteErrorAsync(invocation.Format, standardError, code, exception.Message).ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (AcquisitionException exception)
        {
            await WriteErrorAsync(invocation.Format, standardError, exception.Code, exception.Message).ConfigureAwait(false);
            return ToolExitCodes.AcquisitionOrNetworkFailure;
        }
        catch (NoticeSecurityException exception)
        {
            await WriteErrorAsync(invocation.Format, standardError, exception.Code, exception.Message).ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (IOException)
        {
            await WriteErrorAsync(invocation.Format, standardError, "WUTNOTICE1002", "An input file could not be read.").ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (UnauthorizedAccessException)
        {
            await WriteErrorAsync(invocation.Format, standardError, "WUTNOTICE6001", "Access to an input path was denied.").ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (ArgumentException exception)
        {
            await WriteErrorAsync(invocation.Format, standardError, NoticeDiagnosticCodes.InvalidManualComponent, exception.Message).ConfigureAwait(false);
            return ToolExitCodes.InvalidCommandOrConfiguration;
        }
        catch (Exception)
        {
            await WriteErrorAsync(invocation.Format, standardError, "WUTNOTICE6004", "The command failed without exposing host details.").ConfigureAwait(false);
            return ToolExitCodes.UnexpectedFailure;
        }
    }

    private static async Task<int> RunManualScanAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(invocation.RootDirectory);
        ManualScanResult result = ManualComponentScanner.Scan(root, invocation.ConfigPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.Format == ToolOutputFormat.Json)
        {
            await output.WriteLineAsync(WriteManualJson(result)).ConfigureAwait(false);
        }
        else
        {
            foreach (ManualDependencyComponent component in result.Components)
            {
                await output.WriteLineAsync($"component {component.PackageUrl.CanonicalValue}").ConfigureAwait(false);
            }

            foreach (NoticeDiagnostic diagnostic in result.Diagnostics)
            {
                await output.WriteLineAsync(FormatDiagnostic(diagnostic)).ConfigureAwait(false);
            }

            await output.WriteLineAsync(FormattableString.Invariant($"components={result.Components.Count} diagnostics={result.Diagnostics.Count}")).ConfigureAwait(false);
        }

        if (result.Succeeded) return ToolExitCodes.Success;
        foreach (NoticeDiagnostic diagnostic in result.Diagnostics)
        {
            if (diagnostic.Code.StartsWith("WUTNOTICE2", StringComparison.Ordinal))
            {
                return ToolExitCodes.InventoryOrEvidenceIncomplete;
            }
        }
        return ToolExitCodes.InvalidCommandOrConfiguration;
    }

    private static async Task<int> RunNuGetScanAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InventoryResult result = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            invocation.GetValue("--lock")!,
            invocation.GetValue("--assets")!,
            invocation.GetValue("--framework")!,
            invocation.GetValue("--runtime"),
            invocation.GetValue("--packages-root")));
        cancellationToken.ThrowIfCancellationRequested();
        await WriteInventoryAsync(invocation.Format, output, result).ConfigureAwait(false);
        return InventoryExitCode(result);
    }

    private static async Task<int> RunNpmScanAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NpmInventoryProfile profile = invocation.GetValue("--profile") switch
        {
            null or "runtime" => NpmInventoryProfile.Runtime,
            "development" => NpmInventoryProfile.Development,
            _ => throw new ArgumentException("The npm profile must be 'runtime' or 'development'."),
        };
        InventoryResult result = NpmInventoryScanner.Scan(new NpmInventoryOptions(
            invocation.RootDirectory,
            invocation.GetValue("--lock")!,
            invocation.GetValue("--workspace") ?? ".",
            profile));
        cancellationToken.ThrowIfCancellationRequested();
        await WriteInventoryAsync(invocation.Format, output, result).ConfigureAwait(false);
        return InventoryExitCode(result);
    }

    private static async Task<int> RunPolicyAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] policyBytes = ReadBounded(invocation.GetValue("--policy")!, 1_048_576);
        PolicyConfiguration policy = PolicyConfigurationParser.Parse(policyBytes);
        if (!DateOnly.TryParseExact(invocation.GetValue("--evaluation-date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
        {
            throw new ArgumentException("The evaluation date must use yyyy-MM-dd.");
        }
        PolicyEvaluationInput input = new(
            PackageUrl.Parse(invocation.GetValue("--purl")!),
            invocation.GetValue("--license")!,
            invocation.GetValue("--selected-license"),
            new HashSet<string>(invocation.GetValues("--evidence-digest"), StringComparer.Ordinal),
            new HashSet<string>(invocation.GetValues("--obligation"), StringComparer.Ordinal));
        PolicyEvaluationReport report = PolicyEvaluator.Evaluate([input], policy, new PolicyEvaluationOptions(date));
        cancellationToken.ThrowIfCancellationRequested();
        await WritePolicyAsync(invocation.Format, output, report).ConfigureAwait(false);
        return report.HasErrors ? ToolExitCodes.PolicyRejected : ToolExitCodes.Success;
    }

    private static async Task<int> RunGenerateAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        (NoticeRenderResult rendered, int failure) = BuildRenderedManual(invocation, cancellationToken);
        if (failure != ToolExitCodes.Success)
        {
            await WriteDiagnosticsAsync(invocation.Format, output, rendered.Diagnostics).ConfigureAwait(false);
            return failure;
        }
        IReadOnlyList<NoticeDiagnostic> writeDiagnostics = NoticeOutputWriter.Write(invocation.GetValue("--output")!, rendered.Outputs);
        await WriteDiagnosticsAsync(invocation.Format, output, writeDiagnostics).ConfigureAwait(false);
        if (writeDiagnostics.Count != 0) return ToolExitCodes.UnexpectedFailure;
        if (invocation.Format == ToolOutputFormat.Human)
        {
            foreach (WebUIToolkit.DependencyNotices.Rendering.RenderedNoticeOutput item in rendered.Outputs)
            {
                await output.WriteLineAsync($"generated {item.FileName} sha256:{item.Sha256}").ConfigureAwait(false);
            }
        }
        return ToolExitCodes.Success;
    }

    private static async Task<int> RunVerifyAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        (NoticeRenderResult rendered, int failure) = BuildRenderedManual(invocation, cancellationToken);
        if (failure != ToolExitCodes.Success)
        {
            await WriteDiagnosticsAsync(invocation.Format, output, rendered.Diagnostics).ConfigureAwait(false);
            return failure;
        }
        string outputRoot = Path.GetFullPath(invocation.GetValue("--output")!);
        Dictionary<string, ReadOnlyMemory<byte>> expected = new(StringComparer.Ordinal);
        foreach (string name in RenderedOutputNames)
        {
            string path = Path.Combine(outputRoot, name);
            if (File.Exists(path)) expected.Add(name, ReadBounded(path, 32 * 1024 * 1024));
        }
        WebUIToolkit.DependencyNotices.Rendering.NoticeVerificationResult verification = NoticeOutputVerifier.Verify(expected, rendered.Outputs);
        await WriteDiagnosticsAsync(invocation.Format, output, verification.Diagnostics).ConfigureAwait(false);
        if (verification.Succeeded && invocation.Format == ToolOutputFormat.Human) await output.WriteLineAsync("verified").ConfigureAwait(false);
        return verification.Succeeded ? ToolExitCodes.Success : ToolExitCodes.OutputDrift;
    }

    private static async Task<int> RunSbomAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using FileStream stream = new(invocation.GetValue("--sbom")!, FileMode.Open, FileAccess.Read, FileShare.Read);
        SbomDocument document = SbomReader.Read(stream);
        List<SbomInventoryIdentity> inventory = [];
        foreach (string component in invocation.GetValues("--component"))
        {
            string[] fields = component.Split('|');
            if (fields.Length != 3 || fields.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Each --component must use 'purl|name|version'.");
            }
            inventory.Add(new SbomInventoryIdentity(PackageUrl.Parse(fields[0]), fields[1], fields[2]));
        }
        WebUIToolkit.DependencyNotices.Sbom.SbomReconciliationResult result = SbomReconciler.Reconcile(inventory, document);
        cancellationToken.ThrowIfCancellationRequested();
        await WriteSbomAsync(invocation.Format, output, result).ConfigureAwait(false);
        return result.Succeeded ? ToolExitCodes.Success : ToolExitCodes.SbomMismatch;
    }

    private static async Task<int> RunAcquireAsync(ToolInvocation invocation, TextWriter output, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(invocation.GetValue("--origin"), UriKind.Absolute, out Uri? origin))
        {
            throw new ArgumentException("The acquisition origin must be an absolute URI.");
        }
        long maximumBytes = ParsePositiveLong(invocation.GetValue("--max-bytes"), AcquisitionPolicy.DefaultMaximumBytes, "maximum bytes");
        long timeoutSeconds = ParsePositiveLong(invocation.GetValue("--timeout-seconds"), (long)AcquisitionPolicy.DefaultTimeout.TotalSeconds, "timeout seconds");
        AcquisitionPolicy policy = new(
            invocation.GetValues("--allow-host"),
            invocation.HasOption("--allow-http"),
            maximumBytes: maximumBytes,
            timeout: TimeSpan.FromSeconds(timeoutSeconds));
        ContentAddressedEvidenceStore store = new(invocation.GetValue("--cache")!);
        using EvidenceAcquirer acquirer = EvidenceAcquirer.CreateDefault(store, policy);
        AcquisitionResult result = await acquirer.AcquireAsync(new AcquisitionRequest(
            AcquisitionOperation.Acquire,
            invocation.AllowNetwork,
            origin,
            invocation.GetValue("--sha256")!), cancellationToken).ConfigureAwait(false);
        await WriteAcquisitionAsync(invocation.Format, output, result).ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private static (NoticeRenderResult Result, int ExitCode) BuildRenderedManual(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        string root = Path.GetFullPath(invocation.RootDirectory);
        string configPath = SafePath.ResolveContainedPath(root, invocation.ConfigPath);
        HashSet<string> sourcePaths = new(PathComparer()) { configPath };
        ManualScanResult scan = ManualComponentScanner.Scan(root, invocation.ConfigPath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!scan.Succeeded)
        {
            return (new NoticeRenderResult(Array.Empty<WebUIToolkit.DependencyNotices.Rendering.RenderedNoticeOutput>(), scan.Diagnostics), InventoryExitCode(scan.Diagnostics));
        }

        List<DependencyNotice> dependencies = [];
        HashSet<string> identities = new(StringComparer.Ordinal);
        try
        {
            foreach (ManualDependencyComponent component in scan.Components)
            {
                List<NoticeAsset> assets = [];
                foreach (NoticeEvidence evidence in component.Evidence)
                {
                    string evidencePath = SafePath.ResolveContainedPath(root, evidence.Path);
                    sourcePaths.Add(evidencePath);
                    string text = StrictUtf8.GetString(ReadBounded(evidencePath, 16 * 1024 * 1024));
                    assets.Add(new NoticeAsset(
                        evidence.Kind,
                        evidence.Sha256,
                        evidence.MediaType ?? "text/plain; charset=utf-8",
                        text,
                        evidence.Origin ?? evidence.Path,
                        false));
                }

                dependencies.Add(new DependencyNotice(
                    component.PackageUrl.CanonicalValue,
                    component.DisplayName,
                    component.Version,
                    component.Ecosystem,
                    DependencyScope.Runtime,
                    true,
                    component.LicenseExpression,
                    component.LicenseExpression,
                    null,
                    assets.AsReadOnly(),
                    Array.Empty<NoticePolicyDecision>(),
                    null,
                    component.IsModified,
                    component.ModificationNotice));
                identities.Add(component.PackageUrl.CanonicalValue);
            }

            List<NoticeDiagnostic> consumerDiagnostics = [];
            AddNuGetConsumerDependencies(invocation, sourcePaths, identities, dependencies, consumerDiagnostics);
            if (consumerDiagnostics.Count != 0)
            {
                return (new NoticeRenderResult(Array.Empty<WebUIToolkit.DependencyNotices.Rendering.RenderedNoticeOutput>(), consumerDiagnostics), InventoryExitCode(consumerDiagnostics));
            }
        }
        catch (DecoderFallbackException)
        {
            NoticeDiagnostic diagnostic = new(
                NoticeDiagnosticCodes.InvalidEvidenceEncoding,
                NoticeDiagnosticSeverity.Error,
                "Evidence text is not valid UTF-8.",
                Remediation: "Declare text evidence using valid UTF-8 bytes.");
            return (new NoticeRenderResult(Array.Empty<WebUIToolkit.DependencyNotices.Rendering.RenderedNoticeOutput>(), [diagnostic]), ToolExitCodes.InventoryOrEvidenceIncomplete);
        }

        string outputRoot = Path.GetFullPath(invocation.GetValue("--output")!);
        foreach (string outputName in RenderedOutputNames)
        {
            if (sourcePaths.Contains(Path.GetFullPath(Path.Combine(outputRoot, outputName))))
            {
                NoticeDiagnostic collision = new(
                    NoticeDiagnosticCodes.UnsafeOutputDestination,
                    NoticeDiagnosticSeverity.Error,
                    "An output destination collides with a declared input or evidence file.",
                    Remediation: "Choose a dedicated output directory outside the input set.");
                return (new NoticeRenderResult(Array.Empty<WebUIToolkit.DependencyNotices.Rendering.RenderedNoticeOutput>(), [collision]), ToolExitCodes.InvalidCommandOrConfiguration);
            }
        }

        List<NoticeManifestInput> manifestInputs =
        [
            new NoticeManifestInput(invocation.ConfigPath.Replace('\\', '/'), EvidenceDigest.ComputeSha256(ReadBounded(configPath, 1_048_576))),
        ];
        if (HasNuGetConsumerEvidence(invocation))
        {
            manifestInputs.Add(new NoticeManifestInput("nuget/packages.lock.json", EvidenceDigest.ComputeSha256(ReadBounded(invocation.GetValue("--nuget-lock")!, 32 * 1024 * 1024))));
            manifestInputs.Add(new NoticeManifestInput("nuget/project.assets.json", EvidenceDigest.ComputeSha256(ReadBounded(invocation.GetValue("--nuget-assets")!, 32 * 1024 * 1024))));
        }
        DependencyNoticeDocument document = new(
            DependencyNoticeRenderer.SupportedDocumentSchemaVersion,
            invocation.GetValue("--artifact-name")!,
            invocation.GetValue("--artifact-version"),
            dependencies.AsReadOnly(),
            null,
            scan.Diagnostics);
        NoticeRenderOptions options = new(
            "1.0.0",
            manifestInputs,
            null,
            [invocation.ConfigPath.Replace('\\', '/')],
            HasNuGetConsumerEvidence(invocation) ? ["manual", "nuget-consumer"] : ["manual"]);
        NoticeRenderResult rendered = DependencyNoticeRenderer.Render(document, options);
        return (rendered, rendered.Succeeded ? ToolExitCodes.Success : ToolExitCodes.UnexpectedFailure);
    }

    private static bool HasNuGetConsumerEvidence(ToolInvocation invocation) =>
        invocation.GetValue("--nuget-lock") is not null;

    private static void AddNuGetConsumerDependencies(
        ToolInvocation invocation,
        HashSet<string> sourcePaths,
        HashSet<string> identities,
        List<DependencyNotice> dependencies,
        List<NoticeDiagnostic> diagnostics)
    {
        if (!HasNuGetConsumerEvidence(invocation))
        {
            return;
        }

        string lockPath = invocation.GetValue("--nuget-lock")!;
        string assetsPath = invocation.GetValue("--nuget-assets")!;
        string packagesRoot = Path.GetFullPath(invocation.GetValue("--nuget-packages-root")!);
        sourcePaths.Add(Path.GetFullPath(lockPath));
        sourcePaths.Add(Path.GetFullPath(assetsPath));
        InventoryResult inventory = NuGetInventoryAdapter.Scan(new NuGetInventoryOptions(
            lockPath,
            assetsPath,
            invocation.GetValue("--nuget-framework")!,
            invocation.GetValue("--nuget-runtime"),
            packagesRoot));
        if (!inventory.Succeeded)
        {
            diagnostics.AddRange(inventory.Diagnostics);
            return;
        }

        foreach (InventoryComponent component in inventory.Components)
        {
            string identity = component.PackageUrl.CanonicalValue;
            if (!identities.Add(identity))
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.DuplicatePackageUrl,
                    NoticeDiagnosticSeverity.Error,
                    "A manual component duplicates a component from the explicit locked NuGet graph.",
                    identity,
                    "nuget-consumer"));
                continue;
            }

            if (string.IsNullOrWhiteSpace(component.ObservedLicenseExpression) || component.Evidence.Count == 0)
            {
                diagnostics.Add(new NoticeDiagnostic(
                    NoticeDiagnosticCodes.MissingEvidence,
                    NoticeDiagnosticSeverity.Error,
                    "A locked NuGet component must provide a local license expression and local evidence.",
                    identity,
                    component.SourcePath));
                continue;
            }

            List<NoticeAsset> assets = [];
            foreach (NoticeEvidence evidence in component.Evidence)
            {
                try
                {
                    string evidencePath = SafePath.ResolveContainedPath(packagesRoot, evidence.Path);
                    byte[] bytes = ReadBounded(evidencePath, MaximumNuGetEvidenceBytes);
                    string digest = EvidenceDigest.ComputeSha256(bytes);
                    if (!StringComparer.Ordinal.Equals(digest, evidence.Sha256))
                    {
                        diagnostics.Add(new NoticeDiagnostic(
                            NoticeDiagnosticCodes.EvidenceDigestMismatch,
                            NoticeDiagnosticSeverity.Error,
                            "The local NuGet evidence no longer matches its discovered SHA-256 digest.",
                            identity,
                            component.SourcePath));
                        continue;
                    }

                    sourcePaths.Add(evidencePath);
                    assets.Add(new NoticeAsset(
                        evidence.Kind,
                        evidence.Sha256,
                        evidence.MediaType ?? "text/plain; charset=utf-8",
                        StrictUtf8.GetString(bytes),
                        string.Concat("nuget:", identity),
                        false));
                }
                catch (DecoderFallbackException)
                {
                    diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.InvalidEvidenceEncoding, NoticeDiagnosticSeverity.Error,
                        "NuGet evidence is not valid UTF-8 text.", identity, component.SourcePath));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NoticeSecurityException)
                {
                    diagnostics.Add(new NoticeDiagnostic(NoticeDiagnosticCodes.MissingEvidence, NoticeDiagnosticSeverity.Error,
                        "NuGet evidence is unavailable or exceeds its byte limit.", identity, component.SourcePath));
                }
            }

            if (assets.Count != component.Evidence.Count)
            {
                continue;
            }

            dependencies.Add(new DependencyNotice(
                identity,
                component.Name,
                component.Version,
                component.SourceKind switch
                {
                    InventorySourceKind.NuGet => DependencyEcosystem.NuGet,
                    InventorySourceKind.Npm => DependencyEcosystem.Npm,
                    _ => DependencyEcosystem.Generic,
                },
                component.Scope,
                component.IsDirect,
                component.ObservedLicenseExpression,
                component.ObservedLicenseExpression,
                null,
                assets.AsReadOnly(),
                Array.Empty<NoticePolicyDecision>(),
                null,
                false,
                null));
        }
    }

    private static async Task WriteInventoryAsync(ToolOutputFormat format, TextWriter output, InventoryResult result)
    {
        if (format == ToolOutputFormat.Human)
        {
            foreach (InventoryComponent component in result.Components)
            {
                await output.WriteLineAsync($"component {component.PackageUrl.CanonicalValue} scope={component.Scope.ToString().ToLowerInvariant()} direct={component.IsDirect.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            }
            foreach (NoticeDiagnostic diagnostic in result.Diagnostics) await output.WriteLineAsync(FormatDiagnostic(diagnostic)).ConfigureAwait(false);
            return;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("components");
            foreach (InventoryComponent component in result.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("purl", component.PackageUrl.CanonicalValue);
                writer.WriteString("name", component.Name);
                writer.WriteString("version", component.Version);
                writer.WriteString("sourceKind", component.SourceKind.ToString().ToLowerInvariant());
                writer.WriteString("scope", component.Scope.ToString().ToLowerInvariant());
                writer.WriteBoolean("direct", component.IsDirect);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteDiagnostics(writer, result.Diagnostics);
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static async Task WritePolicyAsync(ToolOutputFormat format, TextWriter output, PolicyEvaluationReport report)
    {
        List<NoticeDiagnostic> diagnostics = [.. report.Diagnostics];
        foreach (ComponentPolicyEvaluation component in report.Components) diagnostics.AddRange(component.Diagnostics);
        if (format == ToolOutputFormat.Human)
        {
            foreach (ComponentPolicyEvaluation component in report.Components)
            {
                await output.WriteLineAsync($"policy {component.PackageUrl} decision={component.Decision.ToString().ToLowerInvariant()} effective={component.EffectiveExpression}").ConfigureAwait(false);
            }
            foreach (NoticeDiagnostic diagnostic in diagnostics) await output.WriteLineAsync(FormatDiagnostic(diagnostic)).ConfigureAwait(false);
            return;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("components");
            foreach (ComponentPolicyEvaluation component in report.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("purl", component.PackageUrl);
                writer.WriteString("observedExpression", component.ObservedExpression);
                writer.WriteString("effectiveExpression", component.EffectiveExpression);
                writer.WriteString("decision", component.Decision.ToString().ToLowerInvariant());
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteDiagnostics(writer, diagnostics);
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static async Task WriteSbomAsync(
        ToolOutputFormat format,
        TextWriter output,
        WebUIToolkit.DependencyNotices.Sbom.SbomReconciliationResult result)
    {
        if (format == ToolOutputFormat.Human)
        {
            foreach (SbomComponentLink link in result.Links) await output.WriteLineAsync($"sbom {link.PackageUrl} ref={link.ComponentReference}").ConfigureAwait(false);
            foreach (NoticeDiagnostic diagnostic in result.Diagnostics) await output.WriteLineAsync(FormatDiagnostic(diagnostic)).ConfigureAwait(false);
            return;
        }
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("format", result.Format.ToString());
            writer.WriteString("documentReference", result.DocumentReference);
            writer.WriteStartArray("links");
            foreach (SbomComponentLink link in result.Links)
            {
                writer.WriteStartObject();
                writer.WriteString("purl", link.PackageUrl);
                writer.WriteString("componentReference", link.ComponentReference);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            WriteDiagnostics(writer, result.Diagnostics);
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static async Task WriteAcquisitionAsync(ToolOutputFormat format, TextWriter output, AcquisitionResult result)
    {
        if (format == ToolOutputFormat.Human)
        {
            await output.WriteLineAsync($"acquired sha256:{result.Sha256} bytes={result.ByteCount} cached={result.WasAlreadyCached.ToString().ToLowerInvariant()}").ConfigureAwait(false);
            return;
        }
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("origin", OutputSanitizer.Sanitize(result.Origin.AbsoluteUri));
            writer.WriteString("effectiveOrigin", OutputSanitizer.Sanitize(result.EffectiveOrigin.AbsoluteUri));
            writer.WriteString("sha256", result.Sha256);
            writer.WriteNumber("byteCount", result.ByteCount);
            writer.WriteNumber("redirectCount", result.RedirectCount);
            writer.WriteBoolean("wasAlreadyCached", result.WasAlreadyCached);
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static async Task WriteDiagnosticsAsync(ToolOutputFormat format, TextWriter output, IReadOnlyList<NoticeDiagnostic> diagnostics)
    {
        if (format == ToolOutputFormat.Human)
        {
            foreach (NoticeDiagnostic diagnostic in diagnostics) await output.WriteLineAsync(FormatDiagnostic(diagnostic)).ConfigureAwait(false);
            return;
        }
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            WriteDiagnostics(writer, diagnostics);
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static void WriteDiagnostics(Utf8JsonWriter writer, IReadOnlyList<NoticeDiagnostic> diagnostics)
    {
        writer.WriteStartArray("diagnostics");
        foreach (NoticeDiagnostic diagnostic in diagnostics) WriteDiagnostic(writer, diagnostic);
        writer.WriteEndArray();
    }

    private static int InventoryExitCode(InventoryResult result) => result.Succeeded ? ToolExitCodes.Success : InventoryExitCode(result.Diagnostics);

    private static int InventoryExitCode(IReadOnlyList<NoticeDiagnostic> diagnostics)
    {
        foreach (NoticeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Code.StartsWith("WUTNOTICE2", StringComparison.Ordinal)) return ToolExitCodes.InventoryOrEvidenceIncomplete;
        }
        return ToolExitCodes.InvalidCommandOrConfiguration;
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new InvalidDataException("Input exceeds its byte limit.");
        byte[] bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static long ParsePositiveLong(string? text, long fallback, string label)
    {
        if (text is null) return fallback;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long value) || value <= 0)
        {
            throw new ArgumentException($"The {label} must be a positive integer.");
        }
        return value;
    }

    private static StringComparer PathComparer() => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static async Task<int> RunPackageUrlAsync(ToolInvocation invocation, TextWriter output)
    {
        string canonical = PackageUrl.Parse(invocation.Value!).CanonicalValue;
        await WriteContractValueAsync(invocation, output, "purl", canonical).ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private static async Task<int> RunSpdxAsync(ToolInvocation invocation, TextWriter output)
    {
        string canonical = SpdxParser.Parse(invocation.Value!).Canonical;
        await WriteContractValueAsync(invocation, output, "spdx", canonical).ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private static async Task<int> RunDiagnosticsAsync(ToolInvocation invocation, TextWriter output)
    {
        if (invocation.Format == ToolOutputFormat.Human)
        {
            foreach (string code in NoticeDiagnosticCodes.All) await output.WriteLineAsync(code).ConfigureAwait(false);
            return ToolExitCodes.Success;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("diagnosticCodes");
            foreach (string code in NoticeDiagnosticCodes.All) writer.WriteStringValue(code);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private static async Task WriteContractValueAsync(ToolInvocation invocation, TextWriter output, string kind, string value)
    {
        if (invocation.Format == ToolOutputFormat.Human)
        {
            await output.WriteLineAsync(value).ConfigureAwait(false);
            return;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("kind", kind);
            writer.WriteString("canonical", value);
            writer.WriteEndObject();
        }
        await output.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static string WriteManualJson(ManualScanResult result)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteBoolean("succeeded", result.Succeeded);
            writer.WriteStartArray("components");
            foreach (ManualDependencyComponent component in result.Components)
            {
                writer.WriteStartObject();
                writer.WriteString("purl", component.PackageUrl.CanonicalValue);
                writer.WriteString("displayName", component.DisplayName);
                writer.WriteString("version", component.Version);
                writer.WriteString("revision", component.Revision);
                writer.WriteString("licenseExpression", component.LicenseExpression);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("diagnostics");
            foreach (NoticeDiagnostic diagnostic in result.Diagnostics) WriteDiagnostic(writer, diagnostic);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteDiagnostic(Utf8JsonWriter writer, NoticeDiagnostic diagnostic)
    {
        writer.WriteStartObject();
        writer.WriteString("code", diagnostic.Code);
        writer.WriteString("severity", diagnostic.Severity.ToString().ToLowerInvariant());
        writer.WriteString("message", OutputSanitizer.Sanitize(diagnostic.Message));
        if (diagnostic.PackageUrl is not null) writer.WriteString("packageUrl", OutputSanitizer.Sanitize(diagnostic.PackageUrl));
        if (diagnostic.Source is not null) writer.WriteString("source", OutputSanitizer.Sanitize(diagnostic.Source));
        if (diagnostic.Offset is int offset) writer.WriteNumber("offset", offset);
        if (diagnostic.Remediation is not null) writer.WriteString("remediation", OutputSanitizer.Sanitize(diagnostic.Remediation));
        writer.WriteEndObject();
    }

    private static string FormatDiagnostic(NoticeDiagnostic diagnostic)
    {
        StringBuilder text = new();
        text.Append(diagnostic.Code).Append(' ').Append(diagnostic.Severity.ToString().ToLowerInvariant()).Append(": ").Append(OutputSanitizer.Sanitize(diagnostic.Message));
        if (diagnostic.PackageUrl is not null) text.Append(" [package=").Append(OutputSanitizer.Sanitize(diagnostic.PackageUrl)).Append(']');
        if (diagnostic.Source is not null) text.Append(" [source=").Append(OutputSanitizer.Sanitize(diagnostic.Source)).Append(']');
        return text.ToString();
    }

    private static async Task<int> WriteHelpAsync(TextWriter output)
    {
        await output.WriteLineAsync("Dependency Notices deterministic CLI").ConfigureAwait(false);
        await output.WriteLineAsync("Usage:").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices scan manual [--root PATH] [--config PATH] [--diagnostics-format human|json]").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices scan nuget --lock PATH --assets PATH --framework TFM [--runtime RID] [--packages-root PATH]").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices scan npm --root PATH --lock PATH [--workspace PATH] [--profile runtime|development]").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices policy --policy PATH --purl PURL --license SPDX --evaluation-date YYYY-MM-DD").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices generate|verify --root PATH --config PATH --output PATH --artifact-name NAME [--nuget-lock PATH --nuget-assets PATH --nuget-framework TFM --nuget-packages-root PATH]").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices sbom --sbom PATH --component PURL|NAME|VERSION").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices acquire --allow-network --origin URL --sha256 HEX --cache PATH --allow-host HOST").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices contract purl|spdx --value VALUE [--diagnostics-format human|json]").ConfigureAwait(false);
        await output.WriteLineAsync("  dependency-notices contract diagnostics [--diagnostics-format human|json]").ConfigureAwait(false);
        return ToolExitCodes.Success;
    }

    private static async Task WriteErrorAsync(ToolOutputFormat format, TextWriter error, string code, string message)
    {
        string safe = OutputSanitizer.Sanitize(message);
        if (format == ToolOutputFormat.Human)
        {
            await error.WriteLineAsync($"{code} error: {safe}").ConfigureAwait(false);
            return;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("diagnostics");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("severity", "error");
            writer.WriteString("message", safe);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        await error.WriteLineAsync(Encoding.UTF8.GetString(buffer.ToArray())).ConfigureAwait(false);
    }

    private static Utf8JsonWriter CreateWriter(Stream stream) => new(stream, new JsonWriterOptions { Indented = false });

    private static bool RequestsJson(IReadOnlyList<string> args)
    {
        for (int index = 0; index + 1 < args.Count; index++)
        {
            if ((args[index] == "--format" || args[index] == "--diagnostics-format") && args[index + 1] == "json") return true;
        }

        return false;
    }
}
