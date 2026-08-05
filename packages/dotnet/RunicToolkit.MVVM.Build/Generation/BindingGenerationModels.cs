using System;
using System.Collections.Generic;

namespace RunicToolkit.MVVM.Build.Generation;

/// <summary>Versioned constants for deterministic MVVM binding artifacts.</summary>
public static class BindingGenerationContract
{
    /// <summary>The generated contract-manifest schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The generator contract version.</summary>
    public const string GeneratorVersion = "1";

    /// <summary>The runtime protocol consumed by generated dispatch code.</summary>
    public const string ProtocolIdentity = "runic.toolkit.mvvm/1";
}

/// <summary>The closed binding-member vocabulary understood by the generator.</summary>
public enum BindingGenerationMemberKind
{
    /// <summary>A scalar property.</summary>
    Property,

    /// <summary>A projected collection.</summary>
    Collection,

    /// <summary>An executable command.</summary>
    Command,

    /// <summary>A validation projection.</summary>
    Validation,
}

/// <summary>One member in a generated binding contract.</summary>
public sealed class BindingGenerationMember
{
    /// <summary>Creates a binding member.</summary>
    /// <param name="memberId">The explicit positive protocol member identifier.</param>
    /// <param name="bindingName">The ordinal, case-sensitive wire name.</param>
    /// <param name="kind">The member's binding kind.</param>
    /// <param name="canWrite">Whether a property or collection accepts set-property mutations.</param>
    public BindingGenerationMember(
        int memberId,
        string bindingName,
        BindingGenerationMemberKind kind,
        bool? canWrite = null)
    {
        MemberId = memberId;
        BindingName = bindingName;
        Kind = kind;
        CanWrite = canWrite ?? kind == BindingGenerationMemberKind.Property;
    }

    /// <summary>Gets the explicit protocol member identifier.</summary>
    public int MemberId { get; }

    /// <summary>Gets the ordinal, case-sensitive wire name.</summary>
    public string BindingName { get; }

    /// <summary>Gets the member's binding kind.</summary>
    public BindingGenerationMemberKind Kind { get; }

    /// <summary>Gets whether a property or collection accepts set-property mutations.</summary>
    public bool CanWrite { get; }
}

/// <summary>The narrow, compiler-independent input to deterministic artifact generation.</summary>
public sealed class BindingGenerationInput
{
    private readonly IReadOnlyList<BindingGenerationMember> _members;
    private readonly IReadOnlyList<string?> _semanticCompatibilityFields;

    /// <summary>Creates a generation input snapshot.</summary>
    public BindingGenerationInput(
        string contractName,
        string namespaceName,
        string typeName,
        IReadOnlyList<BindingGenerationMember> members)
        : this(contractName, namespaceName, typeName, members, [])
    {
    }

    internal BindingGenerationInput(
        string contractName,
        string namespaceName,
        string typeName,
        IReadOnlyList<BindingGenerationMember> members,
        IReadOnlyList<string?> semanticCompatibilityFields)
    {
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(semanticCompatibilityFields);

        ContractName = contractName;
        NamespaceName = namespaceName;
        TypeName = typeName;
        var memberSnapshot = new BindingGenerationMember[members.Count];
        for (int index = 0; index < members.Count; index++)
        {
            memberSnapshot[index] = members[index];
        }

        _members = Array.AsReadOnly(memberSnapshot);

        var compatibilitySnapshot = new string?[semanticCompatibilityFields.Count];
        for (int index = 0; index < semanticCompatibilityFields.Count; index++)
        {
            compatibilitySnapshot[index] = semanticCompatibilityFields[index];
        }

        _semanticCompatibilityFields = Array.AsReadOnly(compatibilitySnapshot);
    }

    /// <summary>Gets the logical MVVM contract identifier.</summary>
    public string ContractName { get; }

    /// <summary>Gets the namespace of the emitted dispatch type.</summary>
    public string NamespaceName { get; }

    /// <summary>Gets the exact name of the emitted dispatch type.</summary>
    public string TypeName { get; }

    /// <summary>Gets a snapshot of binding members.</summary>
    public IReadOnlyList<BindingGenerationMember> Members => _members;

    internal IReadOnlyList<string?> SemanticCompatibilityFields => _semanticCompatibilityFields;
}

/// <summary>Resource ceilings applied before and during generation.</summary>
public sealed class BindingGenerationOptions
{
    /// <summary>The default maximum number of members in one contract.</summary>
    public const int DefaultMaximumMembers = 4_096;

    /// <summary>The default maximum UTF-8 size of one emitted C# source.</summary>
    public const int DefaultMaximumGeneratedSourceBytes = 4 * 1024 * 1024;

    /// <summary>The default maximum UTF-8 size of one emitted manifest.</summary>
    public const int DefaultMaximumManifestBytes = 2 * 1024 * 1024;

    /// <summary>Creates generation limits.</summary>
    public BindingGenerationOptions(
        int maximumMembers = DefaultMaximumMembers,
        int maximumGeneratedSourceBytes = DefaultMaximumGeneratedSourceBytes,
        int maximumManifestBytes = DefaultMaximumManifestBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMembers);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumMembers, DefaultMaximumMembers);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumGeneratedSourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumManifestBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumGeneratedSourceBytes, DefaultMaximumGeneratedSourceBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumManifestBytes, DefaultMaximumManifestBytes);

        MaximumMembers = maximumMembers;
        MaximumGeneratedSourceBytes = maximumGeneratedSourceBytes;
        MaximumManifestBytes = maximumManifestBytes;
    }

    /// <summary>Gets the maximum member count.</summary>
    public int MaximumMembers { get; }

    /// <summary>Gets the maximum emitted C# size in UTF-8 bytes.</summary>
    public int MaximumGeneratedSourceBytes { get; }

    /// <summary>Gets the maximum emitted manifest size in UTF-8 bytes.</summary>
    public int MaximumManifestBytes { get; }
}

/// <summary>Byte-stable artifacts produced for one binding contract.</summary>
public sealed class GeneratedBindingArtifacts
{
    internal GeneratedBindingArtifacts(
        string source,
        string manifest,
        string fingerprint,
        string sourceHintName,
        string manifestFileName)
    {
        Source = source;
        Manifest = manifest;
        Fingerprint = fingerprint;
        SourceHintName = sourceHintName;
        ManifestFileName = manifestFileName;
    }

    /// <summary>Gets the generated C# source, normalized to LF and terminated by LF.</summary>
    public string Source { get; }

    /// <summary>Gets the canonical JSON manifest, normalized to LF and terminated by LF.</summary>
    public string Manifest { get; }

    /// <summary>Gets the lowercase SHA-256 of the canonical semantic input.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets a path-free deterministic C# hint name.</summary>
    public string SourceHintName { get; }

    /// <summary>Gets a path-free deterministic manifest file name.</summary>
    public string ManifestFileName { get; }
}
