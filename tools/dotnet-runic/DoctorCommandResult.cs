using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Runic.CommandLine;

namespace Runic.Application.Tool;

/// <summary>
/// The <c>runic.application.tool.doctor/1</c> payload. README.md documents its
/// JSON schema; keep both in sync.
/// </summary>
internal sealed record DoctorCommandResult(
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("summary")] DoctorSummary Summary,
    [property: JsonPropertyName("checks")] IReadOnlyList<DoctorCheckResult> Checks)
{
    internal const string PayloadType = "runic.application.tool.doctor/1";
    // RCLI8000-8999 is the application range of the runic.commandline/1 protocol.
    internal const string FailedCheckDiagnosticCode = "RCLI8101";
    internal const string WarningCheckDiagnosticCode = "RCLI8102";

    [JsonIgnore]
    internal string HumanOutput { get; init; } = string.Empty;

    public override string ToString() => HumanOutput;

    internal static DoctorCommandResult Create(
        DoctorProjectConfiguration project,
        DoctorReport report,
        string humanOutput,
        DoctorTargetRid? target = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(report);
        return new DoctorCommandResult(
            project.ProjectPath,
            HostName(project.Host),
            target?.Value,
            report.IsHealthy,
            new DoctorSummary(report.Passed, report.Warnings, report.Failed),
            [.. report.Checks.Select(static check => new DoctorCheckResult(
                check.Name, StatusName(check.Status), check.Message, check.Remediation))])
        {
            HumanOutput = humanOutput,
        };
    }

    /// <summary>
    /// Mirrors failing and warning checks as envelope diagnostics. Checks at or
    /// above <paramref name="errorAt"/> are errors; the rest are warnings, as a
    /// successful envelope cannot hold errors. The envelope redacts the message
    /// and arguments of a diagnostic whose text contains a path, so the check id
    /// is also carried in the never-redacted message key.
    /// </summary>
    internal static IReadOnlyList<CommandDiagnostic> CreateDiagnostics(
        DoctorReport report,
        CommandPath path,
        DoctorStatus? errorAt = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        const int maximumDiagnostics = 32;
        return
        [
            .. report.Checks
                .Where(static check => check.Status != DoctorStatus.Pass)
                .OrderBy(static check => check.Status == DoctorStatus.Failure ? 0 : 1)
                .Take(maximumDiagnostics)
                .Select(check => new CommandDiagnostic(
                    check.Status == DoctorStatus.Failure ? FailedCheckDiagnosticCode : WarningCheckDiagnosticCode,
                    check.Status == DoctorStatus.Failure ? "doctor-check-failed" : "doctor-check-warning",
                    check.Message,
                    CommandDiagnosticPhase.Execution,
                    errorAt is { } threshold && check.Status >= threshold
                        ? CommandDiagnosticSeverity.Error
                        : CommandDiagnosticSeverity.Warning,
                    arguments: [check.Name],
                    path: path,
                    messageKey: $"doctor.{check.Name}.{(check.Status == DoctorStatus.Failure ? "failed" : "warning")}")),
        ];
    }

    internal static string StatusName(DoctorStatus status) =>
        status switch
        {
            DoctorStatus.Pass => "pass",
            DoctorStatus.Warning => "warn",
            DoctorStatus.Failure => "fail",
            _ => throw new InvalidOperationException($"Unknown doctor status '{status}'."),
        };

    internal static string HostName(RunicViewsHost host) =>
        host switch
        {
            RunicViewsHost.CsWebUi => "cswebui",
            RunicViewsHost.Desktop => "desktop",
            _ => "unknown",
        };
}

// Explicit names: the command codec does not apply context-level naming options.
/// <summary>The status at which doctor fails.</summary>
internal enum DoctorFailOn
{
    Never,
    Fail,
    Warn,
}

internal static class DoctorOutcome
{
    internal const string FaultCode = "RAPPCLI1009";

    /// <summary>
    /// Empty selects the mode default: human output fails on failing checks,
    /// while JSON reports every completed inspection as a payload because a
    /// failed runic.commandline/1 envelope cannot carry one.
    /// </summary>
    internal static DoctorFailOn ParseFailOn(string value, CommandOutputMode mode) =>
        value switch
        {
            "" or null => mode == CommandOutputMode.Json ? DoctorFailOn.Never : DoctorFailOn.Fail,
            "never" => DoctorFailOn.Never,
            "fail" => DoctorFailOn.Fail,
            "warn" => DoctorFailOn.Warn,
            _ => throw new DevUsageException("RAPPCLI1010", "--fail-on must be never, fail or warn."),
        };

    internal static CommandOutcome<DoctorCommandResult> Create(
        DoctorRun run,
        CommandOutputMode mode,
        DoctorFailOn failOn,
        CommandPath path,
        string report,
        string? boundedHumanOutput)
    {
        ArgumentNullException.ThrowIfNull(run);
        bool json = mode == CommandOutputMode.Json;
        DoctorStatus? threshold = failOn switch
        {
            DoctorFailOn.Fail => DoctorStatus.Failure,
            DoctorFailOn.Warn => DoctorStatus.Warning,
            _ => null,
        };
        bool failed = threshold is { } limit && run.Report.Checks.Any(check => check.Status >= limit);
        if (!failed)
        {
            DoctorCommandResult result = DoctorCommandResult.Create(run.Project, run.Report, report, run.Target);
            return json
                ? CommandOutcome.Success(result, DoctorCommandResult.CreateDiagnostics(run.Report, path))
                : CommandOutcome.Success(result);
        }

        const int maximumDetails = 32;
        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DoctorCheck check in run.Report.Checks
            .Where(static check => check.Status != DoctorStatus.Pass)
            .OrderByDescending(static check => check.Status))
        {
            if (details.Count == maximumDetails) break;
            details.TryAdd(check.Name, DoctorCommandResult.StatusName(check.Status));
        }
        return CommandOutcome.Failure<DoctorCommandResult>(
            CommandExitCategory.CommandFailure,
            new CommandFault(FaultCode, "Doctor checks failed.", details),
            json ? DoctorCommandResult.CreateDiagnostics(run.Report, path, threshold) : [],
            json ? null : boundedHumanOutput);
    }
}

internal sealed record DoctorSummary(
    [property: JsonPropertyName("passed")] int Passed,
    [property: JsonPropertyName("warnings")] int Warnings,
    [property: JsonPropertyName("failed")] int Failed);

internal sealed record DoctorCheckResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("remediation")] string? Remediation);

[JsonSerializable(typeof(DoctorCommandResult))]
internal sealed partial class DoctorCommandJsonContext : JsonSerializerContext;
