namespace WebUIToolkit.MVVM.Build.Compiler;

/// <summary>Hard, configurable resource limits for one binding source file.</summary>
public sealed record BindingCompilerLimits
{
    /// <summary>The hard UTF-16 source ceiling.</summary>
    public const int MaximumSourceCharacters = 1_048_576;
    /// <summary>The hard UTF-8 source ceiling.</summary>
    public const int MaximumSourceUtf8Bytes = 1_048_576;
    /// <summary>The hard lexical token ceiling.</summary>
    public const int MaximumTokens = 131_072;
    /// <summary>The hard contract ceiling.</summary>
    public const int MaximumContracts = 1_024;
    /// <summary>The hard member ceiling per contract.</summary>
    public const int MaximumMembersPerContract = 4_096;
    /// <summary>The hard generic nesting ceiling.</summary>
    public const int MaximumNestingDepth = 32;
    /// <summary>The hard decoded string ceiling in UTF-8 bytes.</summary>
    public const int MaximumStringUtf8Bytes = 65_536;
    /// <summary>The hard identifier ceiling.</summary>
    public const int MaximumIdentifierCharacters = 256;
    /// <summary>The hard CLR type spelling ceiling.</summary>
    public const int MaximumTypeCharacters = 1_024;
    /// <summary>The hard diagnostic ceiling per file.</summary>
    public const int MaximumDiagnostics = 100;

    /// <summary>Gets the production defaults.</summary>
    public static BindingCompilerLimits Default { get; } = new();

    /// <summary>Gets the maximum UTF-16 source length.</summary>
    public int MaxSourceCharacters { get; init; } = MaximumSourceCharacters;

    /// <summary>Gets the maximum UTF-8 source length.</summary>
    public int MaxSourceUtf8Bytes { get; init; } = MaximumSourceUtf8Bytes;

    /// <summary>Gets the maximum number of lexical tokens.</summary>
    public int MaxTokens { get; init; } = MaximumTokens;

    /// <summary>Gets the maximum contracts per source.</summary>
    public int MaxContracts { get; init; } = MaximumContracts;

    /// <summary>Gets the maximum members per contract.</summary>
    public int MaxMembersPerContract { get; init; } = MaximumMembersPerContract;

    /// <summary>Gets the maximum generic type nesting depth.</summary>
    public int MaxNestingDepth { get; init; } = MaximumNestingDepth;

    /// <summary>Gets the maximum decoded UTF-8 string size.</summary>
    public int MaxStringUtf8Bytes { get; init; } = MaximumStringUtf8Bytes;

    /// <summary>Gets the maximum identifier length.</summary>
    public int MaxIdentifierCharacters { get; init; } = MaximumIdentifierCharacters;

    /// <summary>Gets the maximum type spelling length.</summary>
    public int MaxTypeCharacters { get; init; } = MaximumTypeCharacters;

    /// <summary>Gets the maximum diagnostics retained per file.</summary>
    public int MaxDiagnostics { get; init; } = MaximumDiagnostics;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSourceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxSourceUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxContracts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxMembersPerContract);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNestingDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxStringUtf8Bytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxIdentifierCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTypeCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxDiagnostics);

        if (MaxSourceCharacters > MaximumSourceCharacters ||
            MaxSourceUtf8Bytes > MaximumSourceUtf8Bytes ||
            MaxTokens > MaximumTokens ||
            MaxContracts > MaximumContracts ||
            MaxMembersPerContract > MaximumMembersPerContract ||
            MaxNestingDepth > MaximumNestingDepth ||
            MaxStringUtf8Bytes > MaximumStringUtf8Bytes ||
            MaxIdentifierCharacters > MaximumIdentifierCharacters ||
            MaxTypeCharacters > MaximumTypeCharacters ||
            MaxDiagnostics > MaximumDiagnostics)
        {
            throw new ArgumentOutOfRangeException(nameof(BindingCompilerLimits), "A configured limit exceeds its binding language version 1 hard ceiling.");
        }
    }
}
