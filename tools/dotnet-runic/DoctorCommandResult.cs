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
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("summary")] DoctorSummary Summary,
    [property: JsonPropertyName("checks")] IReadOnlyList<DoctorCheckResult> Checks)
{
    internal const string PayloadType = "runic.application.tool.doctor/1";
    internal const string FailedCheckDiagnosticCode = "RCLI9101";
    internal const string WarningCheckDiagnosticCode = "RCLI9102";

    [JsonIgnore]
    internal string HumanOutput { get; init; } = string.Empty;

    public override string ToString() => HumanOutput;

    internal static DoctorCommandResult Create(
        DoctorProjectConfiguration project,
        DoctorReport report,
        string humanOutput)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(report);
        return new DoctorCommandResult(
            project.ProjectPath,
            HostName(project.Host),
            report.IsHealthy,
            new DoctorSummary(report.Passed, report.Warnings, report.Failed),
            [.. report.Checks.Select(static check => new DoctorCheckResult(
                check.Name, StatusName(check.Status), check.Message, check.Remediation))])
        {
            HumanOutput = humanOutput,
        };
    }

    /// <summary>
    /// Mirrors failing and warning checks as envelope diagnostics. A successful
    /// envelope cannot hold error diagnostics, so both use warning severity and
    /// differ by code and kind; <see cref="Checks"/> stays authoritative.
    /// </summary>
    internal static IReadOnlyList<CommandDiagnostic> CreateDiagnostics(DoctorReport report, CommandPath path)
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
                    CommandDiagnosticSeverity.Warning,
                    arguments: [check.Name, check.Remediation ?? string.Empty],
                    path: path)),
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
