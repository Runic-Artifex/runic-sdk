namespace WebUIToolkit.MVVM.Build.Compiler;

/// <summary>Severity of a binding compiler diagnostic.</summary>
public enum BindingDiagnosticSeverity
{
    /// <summary>The source cannot be compiled.</summary>
    Error,

    /// <summary>The source is accepted but warrants attention.</summary>
    Warning,
}

/// <summary>A stable binding compiler diagnostic with an exact source span.</summary>
public sealed class BindingDiagnostic
{
    internal BindingDiagnostic(
        string id,
        BindingDiagnosticSeverity severity,
        string message,
        BindingSourceSpan span,
        BindingSourceSpan? relatedSpan = null)
    {
        Id = id;
        Severity = severity;
        Message = message;
        Span = span;
        RelatedSpan = relatedSpan;
    }

    /// <summary>Gets the stable <c>WUTMVVM</c> identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the severity.</summary>
    public BindingDiagnosticSeverity Severity { get; }

    /// <summary>Gets the invariant message.</summary>
    public string Message { get; }

    /// <summary>Gets the primary source span.</summary>
    public BindingSourceSpan Span { get; }

    /// <summary>Gets the first declaration span for duplicate diagnostics.</summary>
    public BindingSourceSpan? RelatedSpan { get; }
}

/// <summary>Stable diagnostic identifiers emitted by binding language version 1.</summary>
public static class BindingDiagnosticIds
{
    /// <summary>The source exceeded a configured size limit.</summary>
    public const string SourceLimitExceeded = "WUTMVVM0001";
    /// <summary>The token limit was exceeded.</summary>
    public const string TokenLimitExceeded = "WUTMVVM0002";
    /// <summary>Further diagnostics were suppressed.</summary>
    public const string DiagnosticLimitExceeded = "WUTMVVM0003";
    /// <summary>An unexpected character was encountered.</summary>
    public const string UnexpectedCharacter = "WUTMVVM1001";
    /// <summary>A string or block comment was not terminated.</summary>
    public const string UnterminatedText = "WUTMVVM1002";
    /// <summary>A string escape or Unicode sequence is invalid.</summary>
    public const string InvalidEscape = "WUTMVVM1003";
    /// <summary>An unexpected token was encountered.</summary>
    public const string UnexpectedToken = "WUTMVVM1004";
    /// <summary>An integer is malformed or outside the positive Int32 range.</summary>
    public const string InvalidMemberId = "WUTMVVM1005";
    /// <summary>The configured nesting limit was exceeded.</summary>
    public const string NestingLimitExceeded = "WUTMVVM1006";
    /// <summary>The required protocol declaration is absent.</summary>
    public const string ProtocolRequired = "WUTMVVM2001";
    /// <summary>The protocol identity is unsupported.</summary>
    public const string ProtocolMismatch = "WUTMVVM2002";
    /// <summary>No contract was declared.</summary>
    public const string ContractRequired = "WUTMVVM2003";
    /// <summary>A contract identity is invalid.</summary>
    public const string InvalidContractName = "WUTMVVM2004";
    /// <summary>A CLR type spelling is invalid.</summary>
    public const string InvalidTypeName = "WUTMVVM2005";
    /// <summary>A member identifier is duplicated.</summary>
    public const string DuplicateMemberId = "WUTMVVM2006";
    /// <summary>A wire member name is duplicated.</summary>
    public const string DuplicateMemberName = "WUTMVVM2007";
    /// <summary>A member option is missing or invalid.</summary>
    public const string InvalidMemberOption = "WUTMVVM2008";
    /// <summary>A validation target is absent or has an unsupported kind.</summary>
    public const string InvalidValidationTarget = "WUTMVVM2009";
    /// <summary>The member limit was exceeded.</summary>
    public const string MemberLimitExceeded = "WUTMVVM2010";
    /// <summary>The contract limit was exceeded.</summary>
    public const string ContractLimitExceeded = "WUTMVVM2011";
    /// <summary>A wire or CLR member name is invalid.</summary>
    public const string InvalidMemberName = "WUTMVVM2012";
    /// <summary>A contract identity is duplicated.</summary>
    public const string DuplicateContract = "WUTMVVM2013";
}

internal sealed class BindingDiagnosticBag(BindingCompilerLimits limits)
{
    private readonly List<BindingDiagnostic> _items = [];
    private bool _suppressed;

    internal IReadOnlyList<BindingDiagnostic> Items => _items;

    internal void Add(string id, string message, BindingSourceSpan span, BindingSourceSpan? relatedSpan = null)
    {
        if (_suppressed)
        {
            return;
        }

        if (_items.Count >= limits.MaxDiagnostics - 1)
        {
            _items.Add(new BindingDiagnostic(
                BindingDiagnosticIds.DiagnosticLimitExceeded,
                BindingDiagnosticSeverity.Error,
                "The diagnostic limit was exceeded; further diagnostics were suppressed.",
                span));
            _suppressed = true;
            return;
        }

        _items.Add(new BindingDiagnostic(id, BindingDiagnosticSeverity.Error, message, span, relatedSpan));
    }

    internal void AddRange(IEnumerable<BindingDiagnostic> diagnostics)
    {
        foreach (BindingDiagnostic diagnostic in diagnostics)
        {
            Add(diagnostic.Id, diagnostic.Message, diagnostic.Span, diagnostic.RelatedSpan);
        }
    }

    internal static IReadOnlyList<BindingDiagnostic> Sort(IEnumerable<BindingDiagnostic> diagnostics) =>
        Array.AsReadOnly(diagnostics
            .OrderBy(static diagnostic => diagnostic.Span.LogicalPath, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Span.Start.Offset)
            .ThenBy(static diagnostic => diagnostic.Span.End.Offset)
            .ThenBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .DistinctBy(static diagnostic => (
                diagnostic.Id,
                diagnostic.Message,
                diagnostic.Span.LogicalPath,
                diagnostic.Span.Start.Offset,
                diagnostic.Span.End.Offset))
            .ToArray());
}
