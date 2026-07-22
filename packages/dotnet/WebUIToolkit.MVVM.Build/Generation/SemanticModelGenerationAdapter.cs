using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WebUIToolkit.MVVM.Build.Compiler;

namespace WebUIToolkit.MVVM.Build.Generation;

/// <summary>Adapts the validated compiler model to the pure deterministic generation kernel.</summary>
public static class SemanticModelGenerationAdapter
{
    private const string DefaultNamespace = "WebUIToolkit.MVVM.Generated";
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>Generates every contract in a semantic model in ordinal contract-name order.</summary>
    public static IReadOnlyList<GeneratedBindingArtifacts> Generate(
        BindingSemanticModel model,
        string namespaceName = DefaultNamespace,
        BindingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!string.Equals(model.ProtocolIdentity, BindingGenerationContract.ProtocolIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException("The semantic model protocol does not match the generation contract.", nameof(model));
        }

        BindingContractModel[] contracts = [.. model.Contracts];
        Array.Sort(contracts, static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        var artifacts = new GeneratedBindingArtifacts[contracts.Length];
        for (int index = 0; index < contracts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            artifacts[index] = Generate(contracts[index], namespaceName, typeName: null, options, cancellationToken);
        }

        return artifacts;
    }

    /// <summary>Generates one validated semantic contract.</summary>
    public static GeneratedBindingArtifacts Generate(
        BindingContractModel contract,
        string namespaceName = DefaultNamespace,
        string? typeName = null,
        BindingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        string selectedTypeName = typeName ?? "BindingContract_" + ComputeIdentitySuffix(contract.Name);
        var members = new BindingGenerationMember[contract.Members.Count];
        var compatibilityFields = new List<string?>(1 + (contract.Members.Count * 6))
        {
            contract.ModelType,
        };

        for (int index = 0; index < contract.Members.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BindingMemberModel member = contract.Members[index];
            members[index] = new BindingGenerationMember(
                member.Id,
                member.Name,
                ConvertKind(member.Kind),
                member.Access == BindingAccess.ReadWrite);
            compatibilityFields.Add(member.SourceMember);
            compatibilityFields.Add(member.ValueType);
            compatibilityFields.Add(member.ParameterType);
            compatibilityFields.Add(member.ResultType);
            compatibilityFields.Add(member.ValidationTarget);
            compatibilityFields.Add(member.Access == BindingAccess.ReadWrite ? "read-write" : "read-only");
        }

        var input = new BindingGenerationInput(
            contract.Name,
            namespaceName,
            selectedTypeName,
            members,
            compatibilityFields);
        return DeterministicBindingGenerator.Generate(input, options, cancellationToken);
    }

    private static BindingGenerationMemberKind ConvertKind(BindingMemberKind kind) => kind switch
    {
        BindingMemberKind.Property => BindingGenerationMemberKind.Property,
        BindingMemberKind.Collection => BindingGenerationMemberKind.Collection,
        BindingMemberKind.Command => BindingGenerationMemberKind.Command,
        BindingMemberKind.Validation => BindingGenerationMemberKind.Validation,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ComputeIdentitySuffix(string value)
    {
        byte[] hash = SHA256.HashData(StrictUtf8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}
