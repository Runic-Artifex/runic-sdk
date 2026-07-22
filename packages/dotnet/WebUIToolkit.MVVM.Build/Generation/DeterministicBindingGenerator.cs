using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WebUIToolkit.MVVM.Build.Generation;

/// <summary>Generates byte-stable manifests and closed dispatch metadata without reflection.</summary>
public static class DeterministicBindingGenerator
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Generates one C# dispatch source and its matching contract manifest.</summary>
    public static GeneratedBindingArtifacts Generate(
        BindingGenerationInput input,
        BindingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        BindingGenerationOptions selectedOptions = options ?? new BindingGenerationOptions();
        BindingGenerationMember[] members = ValidateAndOrder(input, selectedOptions, cancellationToken);
        string canonicalContract = WriteCanonicalContract(input, members, cancellationToken);
        string fingerprint = ComputeSha256(canonicalContract);
        string manifest = WriteManifest(input, members, fingerprint, cancellationToken);
        string source = WriteSource(input, members, fingerprint, cancellationToken);

        EnsureByteLimit(source, selectedOptions.MaximumGeneratedSourceBytes, "Generated C# source");
        EnsureByteLimit(manifest, selectedOptions.MaximumManifestBytes, "Generated manifest");

        return new GeneratedBindingArtifacts(
            source,
            manifest,
            fingerprint,
            "WebUIToolkit.MVVM." + fingerprint + ".g.cs",
            "webuitoolkit.mvvm." + fingerprint + ".contract.json");
    }

    private static BindingGenerationMember[] ValidateAndOrder(
        BindingGenerationInput input,
        BindingGenerationOptions options,
        CancellationToken cancellationToken)
    {
        ValidateContractName(input.ContractName);
        if (!CSharpTextEncoder.IsNamespace(input.NamespaceName))
        {
            throw new ArgumentException("The generated namespace must contain only dot-separated ASCII C# identifiers.", nameof(input));
        }

        if (input.NamespaceName.Length > 512)
        {
            throw new ArgumentException("The generated namespace exceeds the 512-character limit.", nameof(input));
        }

        if (!CSharpTextEncoder.IsIdentifier(input.TypeName))
        {
            throw new ArgumentException("The generated type name must be an ASCII C# identifier.", nameof(input));
        }

        if (input.TypeName.Length > 128)
        {
            throw new ArgumentException("The generated type name exceeds the 128-character limit.", nameof(input));
        }

        if (input.Members.Count > options.MaximumMembers)
        {
            throw new ArgumentException("The binding contract exceeds the configured member limit.", nameof(input));
        }

        var members = new BindingGenerationMember[input.Members.Count];
        for (int index = 0; index < input.Members.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BindingGenerationMember member = input.Members[index] ??
                throw new ArgumentException("A binding member cannot be null.", nameof(input));
            if (member.MemberId <= 0)
            {
                throw new ArgumentException("Every binding member must have an explicit positive identifier.", nameof(input));
            }

            if (!Enum.IsDefined(member.Kind))
            {
                throw new ArgumentException("A binding member kind is not defined by generator contract version 1.", nameof(input));
            }

            ValidateBindingName(member.BindingName);
            if (member.CanWrite && member.Kind != BindingGenerationMemberKind.Property)
            {
                throw new ArgumentException("Only properties can accept set-property mutations in protocol version 1.", nameof(input));
            }

            members[index] = member;
        }

        Array.Sort(members, CompareMembers);
        for (int index = 1; index < members.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (members[index - 1].MemberId == members[index].MemberId &&
                members[index - 1].Kind == members[index].Kind)
            {
                throw new ArgumentException("Binding member kind and identifier pairs must be unique.", nameof(input));
            }
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < members.Length; index++)
        {
            if (!names.Add(members[index].BindingName))
            {
                throw new ArgumentException("Binding member names must be ordinally unique.", nameof(input));
            }
        }

        return members;
    }

    private static int CompareMembers(BindingGenerationMember left, BindingGenerationMember right)
    {
        int idComparison = left.MemberId.CompareTo(right.MemberId);
        if (idComparison != 0)
        {
            return idComparison;
        }

        int kindComparison = GetKindRank(left.Kind).CompareTo(GetKindRank(right.Kind));
        return kindComparison != 0
            ? kindComparison
            : StringComparer.Ordinal.Compare(left.BindingName, right.BindingName);
    }

    private static void ValidateContractName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("The binding contract name cannot be empty.", nameof(value));
        }

        CSharpTextEncoder.ValidateUnicode(value, nameof(value));
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
            {
                throw new ArgumentException("The binding contract name cannot contain control characters.", nameof(value));
            }
        }

        EnsureByteLimit(value, 128, "Binding contract name");
    }

    private static void ValidateBindingName(string value)
    {
        if (!CSharpTextEncoder.IsIdentifier(value))
        {
            throw new ArgumentException("A binding member name must be an ASCII identifier.", nameof(value));
        }

        CSharpTextEncoder.ValidateUnicode(value, nameof(value));
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index]))
            {
                throw new ArgumentException("A binding member name cannot contain control characters.", nameof(value));
            }
        }

        EnsureByteLimit(value, 256, "Binding member name");
    }

    private static string WriteCanonicalContract(
        BindingGenerationInput input,
        BindingGenerationMember[] members,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, "format", "webuitoolkit.mvvm.binding-contract");
        AppendCanonical(builder, "schema", BindingGenerationContract.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "generator", BindingGenerationContract.GeneratorVersion);
        AppendEncodedCanonical(builder, "protocol", BindingGenerationContract.ProtocolIdentity);
        AppendEncodedCanonical(builder, "contract", input.ContractName);
        AppendEncodedCanonical(builder, "code.namespace", input.NamespaceName);
        AppendEncodedCanonical(builder, "code.type", input.TypeName);
        AppendCanonical(builder, "semantic.fields", input.SemanticCompatibilityFields.Count.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < input.SemanticCompatibilityFields.Count; index++)
        {
            string fieldName = "semantic.field." + index.ToString("D8", CultureInfo.InvariantCulture);
            string? field = input.SemanticCompatibilityFields[index];
            if (field is null)
            {
                AppendCanonical(builder, fieldName, "null");
            }
            else
            {
                AppendEncodedCanonical(builder, fieldName, "value:" + field);
            }
        }

        AppendCanonical(builder, "members", members.Length.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < members.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix = "member." + index.ToString("D8", CultureInfo.InvariantCulture) + ".";
            BindingGenerationMember member = members[index];
            AppendCanonical(builder, prefix + "id", member.MemberId.ToString(CultureInfo.InvariantCulture));
            AppendEncodedCanonical(builder, prefix + "name", member.BindingName);
            AppendCanonical(builder, prefix + "kind", GetKindName(member.Kind));
            AppendCanonical(builder, prefix + "writable", member.CanWrite ? "1" : "0");
        }

        return builder.ToString();
    }

    private static void AppendEncodedCanonical(StringBuilder builder, string name, string value) =>
        AppendCanonical(builder, name, Convert.ToBase64String(StrictUtf8.GetBytes(value)));

    private static void AppendCanonical(StringBuilder builder, string name, string value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value);
        builder.Append('\n');
    }

    private static string WriteManifest(
        BindingGenerationInput input,
        BindingGenerationMember[] members,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("{\"schemaVersion\":");
        builder.Append(BindingGenerationContract.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append(",\"generatorVersion\":");
        AppendJsonString(builder, BindingGenerationContract.GeneratorVersion);
        builder.Append(",\"protocol\":");
        AppendJsonString(builder, BindingGenerationContract.ProtocolIdentity);
        builder.Append(",\"contract\":");
        AppendJsonString(builder, input.ContractName);
        builder.Append(",\"fingerprint\":");
        AppendJsonString(builder, fingerprint);
        builder.Append(",\"members\":[");
        for (int index = 0; index < members.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index != 0)
            {
                builder.Append(',');
            }

            BindingGenerationMember member = members[index];
            builder.Append("{\"id\":");
            builder.Append(member.MemberId.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"name\":");
            AppendJsonString(builder, member.BindingName);
            builder.Append(",\"kind\":");
            AppendJsonString(builder, GetKindName(member.Kind));
            builder.Append(",\"writable\":");
            builder.Append(member.CanWrite ? "true" : "false");
            builder.Append('}');
        }

        builder.Append("]}\n");
        return builder.ToString();
    }

    private static void AppendJsonString(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ' || character is '\u2028' or '\u2029')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private static string WriteSource(
        BindingGenerationInput input,
        BindingGenerationMember[] members,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated/>\n");
        builder.Append("#nullable enable\n\n");
        builder.Append("namespace ");
        builder.Append(CSharpTextEncoder.EscapeNamespace(input.NamespaceName));
        builder.Append("\n{\n");

        var typeBuilder = new StringBuilder();
        typeBuilder.Append("[global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"WebUIToolkit.MVVM.Build\", \"");
        typeBuilder.Append('1');
        typeBuilder.Append("\")]\n");
        typeBuilder.Append("internal static class ");
        typeBuilder.Append(CSharpTextEncoder.EscapeIdentifier(input.TypeName));
        typeBuilder.Append("\n{\n");
        typeBuilder.Append("    internal const string ProtocolIdentity = global::WebUIToolkit.MVVM.MvvmProtocol.Identity;\n");
        typeBuilder.Append("    internal const string ContractName = ");
        typeBuilder.Append(CSharpTextEncoder.StringLiteral(input.ContractName));
        typeBuilder.Append(";\n");
        typeBuilder.Append("    internal const string Fingerprint = \"");
        typeBuilder.Append(fingerprint);
        typeBuilder.Append("\";\n\n");

        AppendTryGetMemberId(typeBuilder, members, cancellationToken);
        AppendTryGetMemberName(typeBuilder, members, cancellationToken);
        AppendAcceptsMutation(typeBuilder, members, cancellationToken);
        AppendDispatch(typeBuilder, members, cancellationToken);
        typeBuilder.Append("}\n");
        AppendIndented(builder, typeBuilder);
        builder.Append("}\n");
        return builder.ToString();
    }

    private static void AppendIndented(StringBuilder destination, StringBuilder source)
    {
        bool atLineStart = true;
        for (int index = 0; index < source.Length; index++)
        {
            char character = source[index];
            if (atLineStart && character != '\n')
            {
                destination.Append("    ");
                atLineStart = false;
            }

            destination.Append(character);
            if (character == '\n')
            {
                atLineStart = true;
            }
        }
    }

    private static void AppendTryGetMemberId(
        StringBuilder builder,
        BindingGenerationMember[] members,
        CancellationToken cancellationToken)
    {
        builder.Append("    internal static bool TryGetMemberId(string bindingName, out int memberId)\n");
        builder.Append("    {\n");
        builder.Append("        switch (bindingName)\n");
        builder.Append("        {\n");
        for (int index = 0; index < members.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("            case ");
            builder.Append(CSharpTextEncoder.StringLiteral(members[index].BindingName));
            builder.Append(":\n                memberId = ");
            builder.Append(members[index].MemberId.ToString(CultureInfo.InvariantCulture));
            builder.Append(";\n                return true;\n");
        }

        builder.Append("            default:\n                memberId = 0;\n                return false;\n");
        builder.Append("        }\n    }\n\n");
    }

    private static void AppendTryGetMemberName(
        StringBuilder builder,
        BindingGenerationMember[] members,
        CancellationToken cancellationToken)
    {
        builder.Append("    internal static bool TryGetMemberName(int memberId, string memberKind, out string? bindingName)\n");
        builder.Append("    {\n        switch ((memberId, memberKind))\n        {\n");
        for (int index = 0; index < members.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("            case (");
            builder.Append(members[index].MemberId.ToString(CultureInfo.InvariantCulture));
            builder.Append(", ");
            builder.Append(CSharpTextEncoder.StringLiteral(GetKindName(members[index].Kind)));
            builder.Append("):\n                bindingName = ");
            builder.Append(CSharpTextEncoder.StringLiteral(members[index].BindingName));
            builder.Append(";\n                return true;\n");
        }

        builder.Append("            default:\n                bindingName = null;\n                return false;\n");
        builder.Append("        }\n    }\n\n");
    }

    private static void AppendAcceptsMutation(
        StringBuilder builder,
        BindingGenerationMember[] members,
        CancellationToken cancellationToken)
    {
        builder.Append("    internal static bool AcceptsMutation(int memberId, global::WebUIToolkit.MVVM.MvvmMutationKind kind)\n");
        builder.Append("    {\n        switch (kind)\n        {\n");
        builder.Append("            case global::WebUIToolkit.MVVM.MvvmMutationKind.SetProperty:\n");
        AppendAcceptedIdExpression(builder, members, static member =>
            member.CanWrite && member.Kind == BindingGenerationMemberKind.Property,
            cancellationToken);
        builder.Append("            case global::WebUIToolkit.MVVM.MvvmMutationKind.ExecuteCommand:\n");
        AppendAcceptedIdExpression(builder, members, static member => member.Kind == BindingGenerationMemberKind.Command, cancellationToken);
        builder.Append("            default:\n                return false;\n");
        builder.Append("        }\n    }\n\n");
    }

    private static void AppendDispatch(
        StringBuilder builder,
        BindingGenerationMember[] members,
        CancellationToken cancellationToken)
    {
        builder.Append("    internal static global::System.Threading.Tasks.ValueTask<global::WebUIToolkit.MVVM.MvvmBindingResult> DispatchAsync(\n");
        builder.Append("        global::WebUIToolkit.MVVM.MvvmMutationRequest request,\n");
        builder.Append("        global::System.Func<int, global::System.Text.Json.JsonElement, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask<global::WebUIToolkit.MVVM.MvvmBindingResult>> setProperty,\n");
        builder.Append("        global::System.Func<int, global::System.Text.Json.JsonElement, global::System.Threading.CancellationToken, global::System.Threading.Tasks.ValueTask<global::WebUIToolkit.MVVM.MvvmBindingResult>> executeCommand,\n");
        builder.Append("        global::System.Threading.CancellationToken cancellationToken)\n");
        builder.Append("    {\n");
        builder.Append("        if (request is null)\n        {\n            throw new global::System.ArgumentNullException(nameof(request));\n        }\n\n");
        builder.Append("        switch (request.Kind)\n        {\n");
        builder.Append("            case global::WebUIToolkit.MVVM.MvvmMutationKind.SetProperty:\n");
        AppendDispatchKind(builder, members, static member =>
            member.CanWrite && member.Kind == BindingGenerationMemberKind.Property,
            "setProperty", cancellationToken);
        builder.Append("            case global::WebUIToolkit.MVVM.MvvmMutationKind.ExecuteCommand:\n");
        AppendDispatchKind(builder, members, static member => member.Kind == BindingGenerationMemberKind.Command,
            "executeCommand", cancellationToken);
        builder.Append("            default:\n                return RejectInvalidKind();\n");
        builder.Append("        }\n    }\n\n");
        AppendIsKnownMemberId(builder, members, cancellationToken);
        builder.Append("    private static global::System.Threading.Tasks.ValueTask<global::WebUIToolkit.MVVM.MvvmBindingResult> RejectUnknownMember() =>\n");
        builder.Append("        new(global::WebUIToolkit.MVVM.MvvmBindingResult.Rejected(new global::WebUIToolkit.MVVM.MvvmFault(\n");
        builder.Append("            \"member.unknown\",\n");
        builder.Append("            \"The requested generated member does not exist.\")));\n\n");
        builder.Append("    private static global::System.Threading.Tasks.ValueTask<global::WebUIToolkit.MVVM.MvvmBindingResult> RejectInvalidKind() =>\n");
        builder.Append("        new(global::WebUIToolkit.MVVM.MvvmBindingResult.Rejected(new global::WebUIToolkit.MVVM.MvvmFault(\n");
        builder.Append("            \"request.invalid\",\n");
        builder.Append("            \"The mutation kind is not valid for the requested member.\")));\n");
    }

    private static void AppendAcceptedIdExpression(
        StringBuilder builder,
        BindingGenerationMember[] members,
        Func<BindingGenerationMember, bool> predicate,
        CancellationToken cancellationToken)
    {
        builder.Append("                switch (memberId)\n                {\n");
        foreach (int memberId in GetDistinctMemberIds(members, predicate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("                    case ");
            builder.Append(memberId.ToString(CultureInfo.InvariantCulture));
            builder.Append(":\n                        return true;\n");
        }

        builder.Append("                    default:\n                        return false;\n");
        builder.Append("                }\n");
    }

    private static void AppendDispatchKind(
        StringBuilder builder,
        BindingGenerationMember[] members,
        Func<BindingGenerationMember, bool> predicate,
        string callback,
        CancellationToken cancellationToken)
    {
        builder.Append("                switch (request.MemberId)\n                {\n");
        foreach (int memberId in GetDistinctMemberIds(members, predicate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("                    case ");
            builder.Append(memberId.ToString(CultureInfo.InvariantCulture));
            builder.Append(":\n                        if (");
            builder.Append(callback);
            builder.Append(" is null)\n                        {\n                            throw new global::System.ArgumentNullException(nameof(");
            builder.Append(callback);
            builder.Append("));\n                        }\n\n");
            builder.Append("                        return ");
            builder.Append(callback);
            builder.Append("(request.MemberId, request.Payload, cancellationToken);\n");
        }

        builder.Append("                    default:\n                        return IsKnownMemberId(request.MemberId)\n");
        builder.Append("                            ? RejectInvalidKind()\n                            : RejectUnknownMember();\n");
        builder.Append("                }\n");
    }

    private static void AppendIsKnownMemberId(
        StringBuilder builder,
        BindingGenerationMember[] members,
        CancellationToken cancellationToken)
    {
        builder.Append("    private static bool IsKnownMemberId(int memberId)\n    {\n        switch (memberId)\n        {\n");
        foreach (int memberId in GetDistinctMemberIds(members, static _ => true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append("            case ");
            builder.Append(memberId.ToString(CultureInfo.InvariantCulture));
            builder.Append(":\n                return true;\n");
        }

        builder.Append("            default:\n                return false;\n        }\n    }\n\n");
    }

    private static IEnumerable<int> GetDistinctMemberIds(
        BindingGenerationMember[] members,
        Func<BindingGenerationMember, bool> predicate)
    {
        int previous = 0;
        bool hasPrevious = false;
        for (int index = 0; index < members.Length; index++)
        {
            BindingGenerationMember member = members[index];
            if (predicate(member) && (!hasPrevious || previous != member.MemberId))
            {
                previous = member.MemberId;
                hasPrevious = true;
                yield return member.MemberId;
            }
        }
    }

    private static string GetKindName(BindingGenerationMemberKind kind) => kind switch
    {
        BindingGenerationMemberKind.Property => "property",
        BindingGenerationMemberKind.Collection => "collection",
        BindingGenerationMemberKind.Command => "command",
        BindingGenerationMemberKind.Validation => "validation",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static int GetKindRank(BindingGenerationMemberKind kind) => kind switch
    {
        BindingGenerationMemberKind.Property => 0,
        BindingGenerationMemberKind.Collection => 1,
        BindingGenerationMemberKind.Command => 2,
        BindingGenerationMemberKind.Validation => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string ComputeSha256(string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void EnsureByteLimit(string value, int maximumBytes, string description)
    {
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(description + " contains invalid Unicode.", nameof(value), exception);
        }

        if (byteCount > maximumBytes)
        {
            throw new ArgumentException(description + " exceeds its UTF-8 byte limit.", nameof(value));
        }
    }
}
