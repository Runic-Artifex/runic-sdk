using System;
using System.Collections.Generic;

namespace WebUIToolkit.DependencyNotices.Policy;

public enum PolicyDecision
{
    Allow,
    Review,
    Deny,
}

public enum PolicyDiagnosticLevel
{
    Warning,
    Error,
}

public enum OrExpressionPolicy
{
    Allow,
    RequireExplicitSelection,
}

public sealed record LicenseRuleSet(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> Review,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Obligations);

public sealed record PolicyOverrideSet(string LicenseExpression, string LicenseEvidenceSha256);

public sealed record PolicyOverride(
    string Id,
    PackageUrl PackageUrl,
    PolicyOverrideSet Set,
    string Reason,
    string ApprovedBy,
    DateOnly CreatedOn,
    string ExpiresAfter);

public sealed record PolicyConfiguration(
    int SchemaVersion,
    PolicyDecision DefaultDecision,
    LicenseRuleSet Licenses,
    PolicyDiagnosticLevel MissingEvidence,
    OrExpressionPolicy OrExpressions,
    IReadOnlyList<PolicyOverride> Overrides);

public sealed record PolicyParserLimits(int MaximumUtf8Bytes = 1_048_576, int MaximumDepth = 32, int MaximumValues = 16_384)
{
    internal void Validate()
    {
        if (MaximumUtf8Bytes <= 0 || MaximumDepth <= 0 || MaximumValues <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PolicyParserLimits), "Parser limits must be positive.");
        }
    }
}

public sealed class PolicyConfigurationException : FormatException
{
    public PolicyConfigurationException(string code, string jsonPointer, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(jsonPointer);
        Code = code;
        JsonPointer = jsonPointer;
    }

    public PolicyConfigurationException(string code, string jsonPointer, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(jsonPointer);
        Code = code;
        JsonPointer = jsonPointer;
    }

    public string Code { get; }

    public string JsonPointer { get; }
}
