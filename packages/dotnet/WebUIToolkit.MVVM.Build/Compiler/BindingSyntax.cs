namespace WebUIToolkit.MVVM.Build.Compiler;

/// <summary>Base type for immutable binding syntax nodes.</summary>
public abstract class BindingSyntaxNode
{
    private protected BindingSyntaxNode(BindingSourceSpan span) => Span = span;

    /// <summary>Gets the full source span of this node.</summary>
    public BindingSourceSpan Span { get; }
}

/// <summary>A parsed binding source file.</summary>
public sealed class BindingDocumentSyntax : BindingSyntaxNode
{
    internal BindingDocumentSyntax(
        BindingProtocolSyntax? protocol,
        IReadOnlyList<BindingContractSyntax> contracts,
        BindingSourceSpan span)
        : base(span)
    {
        Protocol = protocol;
        Contracts = contracts;
    }

    /// <summary>Gets the protocol declaration, when present.</summary>
    public BindingProtocolSyntax? Protocol { get; }

    /// <summary>Gets contracts in source order.</summary>
    public IReadOnlyList<BindingContractSyntax> Contracts { get; }
}

/// <summary>The source protocol compatibility declaration.</summary>
public sealed class BindingProtocolSyntax : BindingSyntaxNode
{
    internal BindingProtocolSyntax(string identity, BindingSourceSpan identitySpan, BindingSourceSpan span)
        : base(span)
    {
        Identity = identity;
        IdentitySpan = identitySpan;
    }

    /// <summary>Gets the declared protocol identity.</summary>
    public string Identity { get; }

    /// <summary>Gets the exact identity-token span.</summary>
    public BindingSourceSpan IdentitySpan { get; }
}

/// <summary>A contract declaration and its closed binding surface.</summary>
public sealed class BindingContractSyntax : BindingSyntaxNode
{
    internal BindingContractSyntax(
        string name,
        BindingSourceSpan nameSpan,
        string modelType,
        BindingSourceSpan modelTypeSpan,
        IReadOnlyList<BindingMemberSyntax> members,
        BindingSourceSpan span)
        : base(span)
    {
        Name = name;
        NameSpan = nameSpan;
        ModelType = modelType;
        ModelTypeSpan = modelTypeSpan;
        Members = members;
    }

    /// <summary>Gets the ordinal, case-sensitive logical contract identity.</summary>
    public string Name { get; }

    /// <summary>Gets the exact contract identity span.</summary>
    public BindingSourceSpan NameSpan { get; }

    /// <summary>Gets the conservatively parsed CLR model type spelling.</summary>
    public string ModelType { get; }

    /// <summary>Gets the model type span.</summary>
    public BindingSourceSpan ModelTypeSpan { get; }

    /// <summary>Gets member declarations in source order.</summary>
    public IReadOnlyList<BindingMemberSyntax> Members { get; }
}

/// <summary>The closed binding member vocabulary.</summary>
public enum BindingMemberKind
{
    /// <summary>A scalar state member.</summary>
    Property,
    /// <summary>A projected collection member.</summary>
    Collection,
    /// <summary>An executable command member.</summary>
    Command,
    /// <summary>Validation state associated with another member.</summary>
    Validation,
}

/// <summary>Whether a state member accepts browser-originated writes.</summary>
public enum BindingAccess
{
    /// <summary>The member is projected from .NET only.</summary>
    ReadOnly,
    /// <summary>The member is projected and accepts set-property mutations.</summary>
    ReadWrite,
}

/// <summary>Base type for a binding member declaration.</summary>
public abstract class BindingMemberSyntax : BindingSyntaxNode
{
    private protected BindingMemberSyntax(
        BindingMemberKind kind,
        int? id,
        BindingSourceSpan idSpan,
        string name,
        BindingSourceSpan nameSpan,
        string sourceMember,
        BindingSourceSpan sourceMemberSpan,
        BindingSourceSpan span)
        : base(span)
    {
        Kind = kind;
        Id = id;
        IdSpan = idSpan;
        Name = name;
        NameSpan = nameSpan;
        SourceMember = sourceMember;
        SourceMemberSpan = sourceMemberSpan;
    }

    /// <summary>Gets the closed member kind.</summary>
    public BindingMemberKind Kind { get; }

    /// <summary>Gets the positive protocol member identifier, or <see langword="null"/> after recovery.</summary>
    public int? Id { get; }

    /// <summary>Gets the exact ID token span.</summary>
    public BindingSourceSpan IdSpan { get; }

    /// <summary>Gets the ASCII wire member name.</summary>
    public string Name { get; }

    /// <summary>Gets the exact wire-name token span.</summary>
    public BindingSourceSpan NameSpan { get; }

    /// <summary>Gets the direct CLR member targeted by generated code.</summary>
    public string SourceMember { get; }

    /// <summary>Gets the exact CLR member span.</summary>
    public BindingSourceSpan SourceMemberSpan { get; }
}

/// <summary>A scalar property binding declaration.</summary>
public sealed class PropertyBindingSyntax : BindingMemberSyntax
{
    internal PropertyBindingSyntax(
        int? id, BindingSourceSpan idSpan, string name, BindingSourceSpan nameSpan,
        string valueType, BindingSourceSpan valueTypeSpan, string sourceMember,
        BindingSourceSpan sourceMemberSpan, BindingAccess? access, BindingSourceSpan span)
        : base(BindingMemberKind.Property, id, idSpan, name, nameSpan, sourceMember, sourceMemberSpan, span)
    {
        ValueType = valueType;
        ValueTypeSpan = valueTypeSpan;
        Access = access;
    }

    /// <summary>Gets the CLR value type spelling.</summary>
    public string ValueType { get; }
    /// <summary>Gets the value type span.</summary>
    public BindingSourceSpan ValueTypeSpan { get; }
    /// <summary>Gets the explicit access, or <see langword="null"/> after recovery.</summary>
    public BindingAccess? Access { get; }
}

/// <summary>A projected collection binding declaration.</summary>
public sealed class CollectionBindingSyntax : BindingMemberSyntax
{
    internal CollectionBindingSyntax(
        int? id, BindingSourceSpan idSpan, string name, BindingSourceSpan nameSpan,
        string itemType, BindingSourceSpan itemTypeSpan, string sourceMember,
        BindingSourceSpan sourceMemberSpan, BindingSourceSpan span)
        : base(BindingMemberKind.Collection, id, idSpan, name, nameSpan, sourceMember, sourceMemberSpan, span)
    {
        ItemType = itemType;
        ItemTypeSpan = itemTypeSpan;
    }

    /// <summary>Gets the projected CLR item type spelling.</summary>
    public string ItemType { get; }
    /// <summary>Gets the item type span.</summary>
    public BindingSourceSpan ItemTypeSpan { get; }
}

/// <summary>An executable command binding declaration.</summary>
public sealed class CommandBindingSyntax : BindingMemberSyntax
{
    internal CommandBindingSyntax(
        int? id, BindingSourceSpan idSpan, string name, BindingSourceSpan nameSpan,
        string parameterType, BindingSourceSpan parameterTypeSpan, string resultType,
        BindingSourceSpan resultTypeSpan, string sourceMember, BindingSourceSpan sourceMemberSpan,
        BindingSourceSpan span)
        : base(BindingMemberKind.Command, id, idSpan, name, nameSpan, sourceMember, sourceMemberSpan, span)
    {
        ParameterType = parameterType;
        ParameterTypeSpan = parameterTypeSpan;
        ResultType = resultType;
        ResultTypeSpan = resultTypeSpan;
    }

    /// <summary>Gets the explicit wire argument CLR type spelling.</summary>
    public string ParameterType { get; }
    /// <summary>Gets the argument type span.</summary>
    public BindingSourceSpan ParameterTypeSpan { get; }
    /// <summary>Gets the explicit command result CLR type spelling.</summary>
    public string ResultType { get; }
    /// <summary>Gets the result type span.</summary>
    public BindingSourceSpan ResultTypeSpan { get; }
}

/// <summary>A validation state binding declaration.</summary>
public sealed class ValidationBindingSyntax : BindingMemberSyntax
{
    internal ValidationBindingSyntax(
        int? id, BindingSourceSpan idSpan, string name, BindingSourceSpan nameSpan,
        string targetName, BindingSourceSpan targetNameSpan, string sourceMember,
        BindingSourceSpan sourceMemberSpan, BindingSourceSpan span)
        : base(BindingMemberKind.Validation, id, idSpan, name, nameSpan, sourceMember, sourceMemberSpan, span)
    {
        TargetName = targetName;
        TargetNameSpan = targetNameSpan;
    }

    /// <summary>Gets the property or collection wire name whose errors are projected.</summary>
    public string TargetName { get; }
    /// <summary>Gets the target-name span.</summary>
    public BindingSourceSpan TargetNameSpan { get; }
}

/// <summary>The recoverable result of parsing one binding source file.</summary>
public sealed class BindingParseResult
{
    internal BindingParseResult(
        BindingDocumentSyntax syntax,
        IReadOnlyList<BindingDiagnostic> diagnostics,
        BindingCompilerLimits limits)
    {
        Syntax = syntax;
        Diagnostics = diagnostics;
        Limits = limits;
    }

    /// <summary>Gets the recovered immutable syntax tree.</summary>
    public BindingDocumentSyntax Syntax { get; }

    /// <summary>Gets deterministically ordered diagnostics.</summary>
    public IReadOnlyList<BindingDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether parsing emitted an error.</summary>
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == BindingDiagnosticSeverity.Error);

    internal BindingCompilerLimits Limits { get; }
}
