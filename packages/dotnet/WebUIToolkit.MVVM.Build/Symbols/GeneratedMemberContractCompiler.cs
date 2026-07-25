using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using WebUIToolkit.MVVM.Build.Compiler;
using WebUIToolkit.MVVM.Build.Generation;

namespace WebUIToolkit.MVVM.Build.Symbols;

/// <summary>Inspects compiled metadata and emits direct-access adapters for generated MVVM members.</summary>
public static class GeneratedMemberContractCompiler
{
    private const string DiagnosticPath = "generated-member-contract";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Resolves the requested generated members from compiled PE metadata and, when all requirements are met,
    /// emits one deterministic direct-access C# adapter.
    /// </summary>
    public static GeneratedMemberContractResult Compile(GeneratedMemberContractRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Members);

        if (string.IsNullOrWhiteSpace(request.AssemblyPath) || !File.Exists(request.AssemblyPath))
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberAssemblyNotFound);
        }

        try
        {
            using FileStream stream = new(request.AssemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using PEReader peReader = new(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
            {
                return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
            }

            MetadataReader reader = peReader.GetMetadataReader();
            TypeDefinitionHandle[] matchingTypes = FindTypes(reader, request.MetadataTypeName);
            if (matchingTypes.Length != 1)
            {
                return Failure(matchingTypes.Length == 0
                    ? BindingDiagnosticIds.GeneratedMemberTypeNotFound
                    : BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate);
            }

            TypeDefinitionHandle typeHandle = matchingTypes[0];
            TypeDefinition type = reader.GetTypeDefinition(typeHandle);
            if (!IsPubliclyAccessible(reader, typeHandle))
            {
                return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
            }

            GeneratedMemberRequirement[] requirements = SnapshotAndOrder(request.Members);
            var diagnostics = new List<BindingDiagnostic>();
            var resolved = new List<ResolvedMember>(requirements.Length);
            var requestedBindings = new HashSet<string>(StringComparer.Ordinal);
            var requestedMembers = new HashSet<(string Name, GeneratedMemberKind Kind)>();
            foreach (GeneratedMemberRequirement requirement in requirements)
            {
                if (!IsSafeRequirement(requirement))
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible));
                    continue;
                }

                if (!requestedBindings.Add(requirement.BindingMemberId) ||
                    !requestedMembers.Add((requirement.GeneratedMemberName, requirement.Kind)))
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate));
                    continue;
                }

                PropertyDefinitionHandle[] candidates = type.GetProperties()
                    .Where(handle => string.Equals(
                        reader.GetString(reader.GetPropertyDefinition(handle).Name),
                        requirement.GeneratedMemberName,
                        StringComparison.Ordinal))
                    .ToArray();
                if (candidates.Length == 0)
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberMissing));
                    continue;
                }

                if (candidates.Length != 1)
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate));
                    continue;
                }

                PropertyDefinition property = reader.GetPropertyDefinition(candidates[0]);
                if (!TryDecodePropertyType(reader, property, out DecodedType propertyType) ||
                    !string.Equals(propertyType.MetadataName, requirement.ExpectedTypeMetadataName, StringComparison.Ordinal) ||
                    !propertyType.IsSafeCSharpType ||
                    !IsAccessibleForKind(reader, property, requirement.Kind))
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible));
                    continue;
                }

                resolved.Add(new ResolvedMember(requirement, propertyType));
            }

            if (diagnostics.Count != 0)
            {
                return new GeneratedMemberContractResult([], SortDiagnostics(diagnostics));
            }

            string metadataTypeName = GetFullMetadataName(reader, typeHandle);
            if (!TryGetCSharpTypeName(reader, typeHandle, out string csharpTypeName))
            {
                return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
            }

            return Success(metadataTypeName, csharpTypeName, resolved);
        }
        catch (BadImageFormatException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        }
        catch (IOException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberAssemblyNotFound);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberAssemblyNotFound);
        }
    }

    private static GeneratedMemberContractResult Success(
        string metadataTypeName,
        string csharpTypeName,
        IReadOnlyList<ResolvedMember> members)
    {
        string canonical = WriteCanonical(metadataTypeName, members);
        string fingerprint = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(canonical))).ToLowerInvariant();
        string source = WriteSource(csharpTypeName, members, fingerprint);
        string manifest = WriteManifest(metadataTypeName, members, fingerprint);
        var artifact = new GeneratedBindingArtifacts(
            source,
            manifest,
            fingerprint,
            "WebUIToolkit.MVVM.GeneratedMember." + fingerprint + ".g.cs",
            "webuitoolkit.mvvm.generated-member." + fingerprint + ".contract.json");
        return new GeneratedMemberContractResult([artifact], []);
    }

    private static GeneratedMemberContractResult Failure(string diagnosticId) =>
        new([], [CreateDiagnostic(diagnosticId)]);

    private static BindingDiagnostic CreateDiagnostic(string id) => new(
        id,
        BindingDiagnosticSeverity.Error,
        id switch
        {
            BindingDiagnosticIds.GeneratedMemberAssemblyNotFound => "The requested assembly could not be found.",
            BindingDiagnosticIds.GeneratedMemberTypeNotFound => "The requested metadata type was not found.",
            BindingDiagnosticIds.GeneratedMemberMissing => "A required generated member was not found.",
            BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate => "A required generated member is ambiguous or duplicated.",
            _ => "A required generated member is inaccessible or has an incompatible type.",
        },
        new BindingSourceSpan(DiagnosticPath, new BindingSourcePosition(0, 0, 0), new BindingSourcePosition(0, 0, 0)));

    private static GeneratedMemberRequirement[] SnapshotAndOrder(IReadOnlyList<GeneratedMemberRequirement> requirements)
    {
        var snapshot = new GeneratedMemberRequirement[requirements.Count];
        for (int index = 0; index < requirements.Count; index++)
        {
            snapshot[index] = requirements[index] ?? throw new ArgumentException("Generated member requirements cannot contain null values.", nameof(requirements));
        }

        Array.Sort(snapshot, static (left, right) =>
        {
            int id = StringComparer.Ordinal.Compare(left.BindingMemberId, right.BindingMemberId);
            if (id != 0)
            {
                return id;
            }

            int name = StringComparer.Ordinal.Compare(left.GeneratedMemberName, right.GeneratedMemberName);
            return name != 0 ? name : left.Kind.CompareTo(right.Kind);
        });
        return snapshot;
    }

    private static bool IsSafeRequirement(GeneratedMemberRequirement requirement) =>
        !string.IsNullOrEmpty(requirement.BindingMemberId) &&
        CSharpTextEncoder.IsIdentifier(requirement.GeneratedMemberName) &&
        !string.IsNullOrEmpty(requirement.ExpectedTypeMetadataName) &&
        Enum.IsDefined(requirement.Kind) &&
        IsWellFormedUnicode(requirement.BindingMemberId) &&
        IsWellFormedUnicode(requirement.ExpectedTypeMetadataName);

    private static bool IsWellFormedUnicode(string value)
    {
        try
        {
            CSharpTextEncoder.ValidateUnicode(value, nameof(value));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static TypeDefinitionHandle[] FindTypes(MetadataReader reader, string requestedTypeName) =>
        reader.TypeDefinitions
            .Where(handle => string.Equals(GetFullMetadataName(reader, handle), requestedTypeName, StringComparison.Ordinal))
            .ToArray();

    private static bool IsPubliclyAccessible(MetadataReader reader, TypeDefinitionHandle typeHandle)
    {
        TypeDefinitionHandle current = typeHandle;
        while (!current.IsNil)
        {
            TypeDefinition type = reader.GetTypeDefinition(current);
            TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (current.Equals(typeHandle))
            {
                if (visibility != TypeAttributes.Public && visibility != TypeAttributes.NestedPublic)
                {
                    return false;
                }
            }
            else if (visibility != TypeAttributes.NestedPublic)
            {
                return false;
            }

            current = type.GetDeclaringType();
        }

        return true;
    }

    private static bool IsAccessibleForKind(MetadataReader reader, PropertyDefinition property, GeneratedMemberKind kind)
    {
        PropertyAccessors accessors = property.GetAccessors();
        if (accessors.Getter.IsNil || !IsPublic(reader, accessors.Getter))
        {
            return false;
        }

        return kind != GeneratedMemberKind.Property ||
            (!accessors.Setter.IsNil && IsPublic(reader, accessors.Setter));
    }

    private static bool IsPublic(MetadataReader reader, MethodDefinitionHandle methodHandle) =>
        (reader.GetMethodDefinition(methodHandle).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

    private static bool TryDecodePropertyType(MetadataReader reader, PropertyDefinition property, out DecodedType type)
    {
        try
        {
            var provider = new MetadataTypeProvider();
            MethodSignature<DecodedType> signature = property.DecodeSignature(provider, genericContext: null);
            if (signature.ParameterTypes.Length != 0)
            {
                type = null!;
                return false;
            }

            type = signature.ReturnType;
            return true;
        }
        catch (BadImageFormatException)
        {
            type = null!;
            return false;
        }
    }

    private static string GetFullMetadataName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return GetFullMetadataName(reader, declaringType) + "+" + name;
        }

        string namespaceName = reader.GetString(type.Namespace);
        return namespaceName.Length == 0 ? name : namespaceName + "." + name;
    }

    private static bool TryGetCSharpTypeName(MetadataReader reader, TypeDefinitionHandle handle, out string value)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        if (!CSharpTextEncoder.IsIdentifier(name))
        {
            value = string.Empty;
            return false;
        }

        TypeDefinitionHandle declaringType = type.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            if (!TryGetCSharpTypeName(reader, declaringType, out string declaringName))
            {
                value = string.Empty;
                return false;
            }

            value = declaringName + "." + CSharpTextEncoder.EscapeIdentifier(name);
            return true;
        }

        string namespaceName = reader.GetString(type.Namespace);
        if (namespaceName.Length != 0 && !CSharpTextEncoder.IsNamespace(namespaceName))
        {
            value = string.Empty;
            return false;
        }

        value = "global::" + (namespaceName.Length == 0 ? string.Empty : CSharpTextEncoder.EscapeNamespace(namespaceName) + ".") +
            CSharpTextEncoder.EscapeIdentifier(name);
        return true;
    }

    private static string WriteCanonical(string metadataTypeName, IReadOnlyList<ResolvedMember> members)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, "format", "webuitoolkit.mvvm.generated-member-contract");
        AppendCanonical(builder, "schema", "1");
        AppendEncoded(builder, "type", metadataTypeName);
        AppendCanonical(builder, "members", members.Count.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < members.Count; index++)
        {
            string prefix = "member." + index.ToString("D8", CultureInfo.InvariantCulture) + ".";
            ResolvedMember member = members[index];
            AppendEncoded(builder, prefix + "binding", member.Requirement.BindingMemberId);
            AppendEncoded(builder, prefix + "name", member.Requirement.GeneratedMemberName);
            AppendCanonical(builder, prefix + "kind", member.Requirement.Kind.ToString());
            AppendEncoded(builder, prefix + "type", member.Type.MetadataName);
        }

        return builder.ToString();
    }

    private static void AppendEncoded(StringBuilder builder, string name, string value) =>
        AppendCanonical(builder, name, Convert.ToBase64String(StrictUtf8.GetBytes(value)));

    private static void AppendCanonical(StringBuilder builder, string name, string value)
    {
        builder.Append(name);
        builder.Append('=');
        builder.Append(value);
        builder.Append('\n');
    }

    private static string WriteManifest(string metadataTypeName, IReadOnlyList<ResolvedMember> members, string fingerprint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("kind", "generated-member-contract");
            writer.WriteString("type", metadataTypeName);
            writer.WriteString("fingerprint", fingerprint);
            writer.WriteStartArray("members");
            foreach (ResolvedMember member in members)
            {
                writer.WriteStartObject();
                writer.WriteString("bindingMemberId", member.Requirement.BindingMemberId);
                writer.WriteString("generatedMemberName", member.Requirement.GeneratedMemberName);
                writer.WriteString("kind", member.Requirement.Kind == GeneratedMemberKind.Property ? "property" : "command");
                writer.WriteString("type", member.Type.MetadataName);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return StrictUtf8.GetString(stream.ToArray()) + "\n";
    }

    private static string WriteSource(string csharpTypeName, IReadOnlyList<ResolvedMember> members, string fingerprint)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated/>\n#nullable enable\n\nnamespace WebUIToolkit.MVVM.Generated\n{\n");
        builder.Append("    [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"WebUIToolkit.MVVM.Build\", \"1\")]\n");
        builder.Append("    internal static class GeneratedMemberContractAdapter_");
        builder.Append(fingerprint.AsSpan(0, 16));
        builder.Append("\n    {\n");
        builder.Append("        internal const string Fingerprint = \"");
        builder.Append(fingerprint);
        builder.Append("\";\n\n");
        foreach (ResolvedMember member in members)
        {
            string memberName = member.Requirement.GeneratedMemberName;
            string escapedMemberName = CSharpTextEncoder.EscapeIdentifier(memberName);
            if (member.Requirement.Kind == GeneratedMemberKind.Property)
            {
                builder.Append("        internal static object? Get_");
                builder.Append(memberName);
                builder.Append('(');
                builder.Append(csharpTypeName);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedMemberName);
                builder.Append(";\n\n        internal static void Set_");
                builder.Append(memberName);
                builder.Append('(');
                builder.Append(csharpTypeName);
                builder.Append(" viewModel, object? value) => viewModel.");
                builder.Append(escapedMemberName);
                builder.Append(" = (");
                builder.Append(member.Type.CSharpTypeName);
                builder.Append(")value!;\n\n");
            }
            else
            {
                builder.Append("        internal static bool CanExecute_");
                builder.Append(memberName);
                builder.Append('(');
                builder.Append(csharpTypeName);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedMemberName);
                builder.Append(".CanExecute(null);\n\n        internal static void Execute_");
                builder.Append(memberName);
                builder.Append('(');
                builder.Append(csharpTypeName);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedMemberName);
                builder.Append(".Execute(null);\n\n");
            }
        }

        builder.Append("    }\n}\n");
        return builder.ToString();
    }

    private static IReadOnlyList<BindingDiagnostic> SortDiagnostics(IEnumerable<BindingDiagnostic> diagnostics) =>
        BindingDiagnosticBag.Sort(diagnostics);

    private sealed record ResolvedMember(GeneratedMemberRequirement Requirement, DecodedType Type);

    private sealed record DecodedType(string MetadataName, string CSharpTypeName, bool IsSafeCSharpType);

    private sealed class MetadataTypeProvider : ISignatureTypeProvider<DecodedType, object?>
    {
        public DecodedType GetArrayType(DecodedType elementType, ArrayShape shape) =>
            Unsupported(elementType.MetadataName + "[metadata-array]");

        public DecodedType GetByReferenceType(DecodedType elementType) => Unsupported(elementType.MetadataName + "&");

        public DecodedType GetFunctionPointerType(MethodSignature<DecodedType> signature) => Unsupported("fnptr");

        public DecodedType GetGenericInstantiation(DecodedType genericType, ImmutableArray<DecodedType> typeArguments) => Unsupported(genericType.MetadataName);

        public DecodedType GetGenericMethodParameter(object? genericContext, int index) => Unsupported("!!" + index.ToString(CultureInfo.InvariantCulture));

        public DecodedType GetGenericTypeParameter(object? genericContext, int index) => Unsupported("!" + index.ToString(CultureInfo.InvariantCulture));

        public DecodedType GetModifiedType(DecodedType modifier, DecodedType unmodifiedType, bool isRequired) => unmodifiedType;

        public DecodedType GetPinnedType(DecodedType elementType) => Unsupported(elementType.MetadataName);

        public DecodedType GetPointerType(DecodedType elementType) => Unsupported(elementType.MetadataName + "*");

        public DecodedType GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => Primitive("System.Boolean"),
            PrimitiveTypeCode.Byte => Primitive("System.Byte"),
            PrimitiveTypeCode.Char => Primitive("System.Char"),
            PrimitiveTypeCode.Double => Primitive("System.Double"),
            PrimitiveTypeCode.Int16 => Primitive("System.Int16"),
            PrimitiveTypeCode.Int32 => Primitive("System.Int32"),
            PrimitiveTypeCode.Int64 => Primitive("System.Int64"),
            PrimitiveTypeCode.IntPtr => Primitive("System.IntPtr"),
            PrimitiveTypeCode.Object => Primitive("System.Object"),
            PrimitiveTypeCode.SByte => Primitive("System.SByte"),
            PrimitiveTypeCode.Single => Primitive("System.Single"),
            PrimitiveTypeCode.String => Primitive("System.String"),
            PrimitiveTypeCode.TypedReference => Unsupported("System.TypedReference"),
            PrimitiveTypeCode.UInt16 => Primitive("System.UInt16"),
            PrimitiveTypeCode.UInt32 => Primitive("System.UInt32"),
            PrimitiveTypeCode.UInt64 => Primitive("System.UInt64"),
            PrimitiveTypeCode.UIntPtr => Primitive("System.UIntPtr"),
            PrimitiveTypeCode.Void => Primitive("System.Void"),
            _ => Unsupported(typeCode.ToString()),
        };

        public DecodedType GetSZArrayType(DecodedType elementType) =>
            new(elementType.MetadataName + "[]", elementType.CSharpTypeName + "[]", elementType.IsSafeCSharpType);

        public DecodedType GetTypeFromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            FromDefinition(metadataReader, handle);

        public DecodedType GetTypeFromReference(MetadataReader metadataReader, TypeReferenceHandle handle, byte rawTypeKind) =>
            FromReference(metadataReader, handle);

        public DecodedType GetTypeFromSpecification(MetadataReader metadataReader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static DecodedType Primitive(string metadataName) => new(metadataName, "global::" + metadataName, true);

        private static DecodedType Unsupported(string metadataName) => new(metadataName, string.Empty, false);

        private static DecodedType FromDefinition(MetadataReader metadataReader, TypeDefinitionHandle handle)
        {
            string metadataName = GetFullMetadataName(metadataReader, handle);
            return TryGetCSharpTypeName(metadataReader, handle, out string csharpTypeName)
                ? new(metadataName, csharpTypeName, true)
                : Unsupported(metadataName);
        }

        private static DecodedType FromReference(MetadataReader metadataReader, TypeReferenceHandle handle)
        {
            TypeReference type = metadataReader.GetTypeReference(handle);
            string namespaceName = metadataReader.GetString(type.Namespace);
            string name = metadataReader.GetString(type.Name);
            string metadataName = namespaceName.Length == 0 ? name : namespaceName + "." + name;
            if (type.ResolutionScope.Kind == HandleKind.TypeReference ||
                !CSharpTextEncoder.IsIdentifier(name) ||
                (namespaceName.Length != 0 && !CSharpTextEncoder.IsNamespace(namespaceName)))
            {
                return Unsupported(metadataName);
            }

            string csharpTypeName = "global::" +
                (namespaceName.Length == 0 ? string.Empty : CSharpTextEncoder.EscapeNamespace(namespaceName) + ".") +
                CSharpTextEncoder.EscapeIdentifier(name);
            return new(metadataName, csharpTypeName, true);
        }
    }
}

/// <summary>Describes one compiled assembly and its required generated members.</summary>
public sealed record GeneratedMemberContractRequest(
    string AssemblyPath,
    string MetadataTypeName,
    IReadOnlyList<GeneratedMemberRequirement> Members);

/// <summary>Describes one generated property or command required by a binding contract.</summary>
public sealed record GeneratedMemberRequirement(
    string BindingMemberId,
    string GeneratedMemberName,
    GeneratedMemberKind Kind,
    string ExpectedTypeMetadataName);

/// <summary>The generated-member shape understood by the direct-access adapter emitter.</summary>
public enum GeneratedMemberKind
{
    /// <summary>A readable and writable generated property.</summary>
    Property = 0,

    /// <summary>A readable generated command property.</summary>
    Command = 1,
}

/// <summary>The deterministic artifacts and diagnostics produced for a generated-member contract.</summary>
public sealed record GeneratedMemberContractResult(
    IReadOnlyList<GeneratedBindingArtifacts> Artifacts,
    IReadOnlyList<BindingDiagnostic> Diagnostics);
