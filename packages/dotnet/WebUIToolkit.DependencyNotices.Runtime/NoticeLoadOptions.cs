using System;

namespace WebUIToolkit.DependencyNotices.Runtime;

public sealed class NoticeLoadOptions
{
    public const int DefaultMaxDocumentBytes = 16 * 1024 * 1024;
    public const int DefaultMaxDependencies = 100_000;
    public const int DefaultMaxAssetsPerDependency = 10_000;
    public const int DefaultMaxDecisionsPerDependency = 10_000;
    public const int DefaultMaxDiagnostics = 100_000;
    public const int DefaultMaxStringBytes = 1024 * 1024;
    public const int DefaultMaxDepth = 32;

    public int MaxDocumentBytes { get; init; } = DefaultMaxDocumentBytes;

    public int MaxDependencies { get; init; } = DefaultMaxDependencies;

    public int MaxAssetsPerDependency { get; init; } = DefaultMaxAssetsPerDependency;

    public int MaxDecisionsPerDependency { get; init; } = DefaultMaxDecisionsPerDependency;

    public int MaxDiagnostics { get; init; } = DefaultMaxDiagnostics;

    public int MaxStringBytes { get; init; } = DefaultMaxStringBytes;

    public int MaxDepth { get; init; } = DefaultMaxDepth;

    internal void Validate()
    {
        ValidatePositive(MaxDocumentBytes, nameof(MaxDocumentBytes));
        ValidatePositive(MaxDependencies, nameof(MaxDependencies));
        ValidatePositive(MaxAssetsPerDependency, nameof(MaxAssetsPerDependency));
        ValidatePositive(MaxDecisionsPerDependency, nameof(MaxDecisionsPerDependency));
        ValidatePositive(MaxDiagnostics, nameof(MaxDiagnostics));
        ValidatePositive(MaxStringBytes, nameof(MaxStringBytes));
        ValidatePositive(MaxDepth, nameof(MaxDepth));
    }

    private static void ValidatePositive(int value, string name)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "The limit must be positive.");
        }
    }
}
