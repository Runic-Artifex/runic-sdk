using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using WebUIToolkit.MVVM.Build.Compiler;
using WebUIToolkit.MVVM.Build.Generation;

namespace WebUIToolkit.MVVM.Build.Symbols;

/// <summary>Version information for the framework-neutral post-generator semantic handoff.</summary>
public static class PostGeneratorSemanticContract
{
    /// <summary>The current semantic handoff schema version.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The stable identity of the current semantic handoff.</summary>
    public const string Identity = "webuitoolkit.mvvm.post-generator-semantics/1";
}

/// <summary>
/// Inspects a compiled post-generator PE surface and emits a closed, strongly typed direct-access artifact.
/// </summary>
public static class PostGeneratorSemanticCompiler
{
    private const string DiagnosticPath = "post-generator-semantics";
    private const int MaximumMembers = 4_096;
    private const int MaximumReferences = 256;
    private const int MaximumTextBytes = 65_536;
    private const long MaximumPeBytes = 256L * 1024 * 1024;
    private const long MaximumTotalPeBytes = 512L * 1024 * 1024;
    private const PostGeneratorSemanticCapabilities KnownCapabilities =
        PostGeneratorSemanticCapabilities.PropertyGet |
        PostGeneratorSemanticCapabilities.PropertySet |
        PostGeneratorSemanticCapabilities.CommandCanExecute |
        PostGeneratorSemanticCapabilities.CommandExecute |
        PostGeneratorSemanticCapabilities.AsyncCommandExecute |
        PostGeneratorSemanticCapabilities.AsyncCommandCancel |
        PostGeneratorSemanticCapabilities.AsyncCommandIsRunning |
        PostGeneratorSemanticCapabilities.AsyncCommandCanBeCanceled |
        PostGeneratorSemanticCapabilities.ValidationErrors |
        PostGeneratorSemanticCapabilities.SourceGeneratedSerializerMetadata;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Compiles one normalized framework-adapter request against a post-generator PE.</summary>
    public static PostGeneratorSemanticResult Compile(PostGeneratorSemanticRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Adapter);
        ArgumentNullException.ThrowIfNull(request.ReferenceAssemblyPaths);
        ArgumentNullException.ThrowIfNull(request.Members);

        if (request.SchemaVersion != PostGeneratorSemanticContract.SchemaVersion)
        {
            return Failure(BindingDiagnosticIds.PostGeneratorSemanticContractUnsupported);
        }

        if (!IsSafeRequest(request))
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        }

        if (!File.Exists(request.AssemblyPath))
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberAssemblyNotFound);
        }

        string[] referencePaths;
        PostGeneratorMemberRequirement[] requirements;
        try
        {
            long producerBytes = GetBoundedPeLength(request.AssemblyPath);
            referencePaths = SnapshotReferences(request.ReferenceAssemblyPaths, request.AssemblyPath);
            EnsureBoundedReferenceBytes(referencePaths, producerBytes);
            requirements = SnapshotRequirements(request.Members);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        }

        try
        {
            using var universe = new MetadataUniverse(request.AssemblyPath, referencePaths);
            PeModule producer = universe.Producer;
            TypeDefinitionHandle[] matchingTypes = producer.Reader.TypeDefinitions
                .Where(handle => string.Equals(
                    GetFullMetadataName(producer.Reader, handle),
                    request.MetadataTypeName,
                    StringComparison.Ordinal))
                .ToArray();
            if (matchingTypes.Length != 1)
            {
                return Failure(matchingTypes.Length == 0
                    ? BindingDiagnosticIds.GeneratedMemberTypeNotFound
                    : BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate);
            }

            TypeDefinitionHandle viewModelHandle = matchingTypes[0];
            if (!IsPubliclyAccessible(producer.Reader, viewModelHandle) ||
                !TryGetCSharpTypeName(producer.Reader, viewModelHandle, out string viewModelCSharpType))
            {
                return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
            }

            DecodedType? validationContract = null;
            if ((request.Adapter.Capabilities & PostGeneratorSemanticCapabilities.ValidationErrors) != 0)
            {
                if (!universe.TryFindType(request.Adapter.ValidationContractTypeMetadataName!, out TypeLocation location) ||
                    !TryGetCSharpTypeName(location.Module.Reader, location.Handle, out string validationCSharpType))
                {
                    return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
                }

                validationContract = new DecodedType(
                    request.Adapter.ValidationContractTypeMetadataName!,
                    validationCSharpType,
                    true,
                    0,
                    false);
            }

            var diagnostics = new List<BindingDiagnostic>();
            var resolved = new List<ResolvedSemanticMember>(requirements.Length);
            var bindingIds = new HashSet<string>(StringComparer.Ordinal);
            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            TypeDefinition viewModel = producer.Reader.GetTypeDefinition(viewModelHandle);
            foreach (PostGeneratorMemberRequirement requirement in requirements)
            {
                if (!bindingIds.Add(requirement.BindingMemberId) || !memberNames.Add(requirement.GeneratedMemberName))
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate));
                    continue;
                }

                PropertyDefinitionHandle[] properties = viewModel.GetProperties()
                    .Where(handle => string.Equals(
                        producer.Reader.GetString(producer.Reader.GetPropertyDefinition(handle).Name),
                        requirement.GeneratedMemberName,
                        StringComparison.Ordinal))
                    .ToArray();
                if (properties.Length == 0)
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberMissing));
                    continue;
                }

                if (properties.Length != 1)
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate));
                    continue;
                }

                PropertyDefinition property = producer.Reader.GetPropertyDefinition(properties[0]);
                if (!TryDecodePropertyType(producer.Reader, property, out DecodedType propertyType) ||
                    !string.Equals(propertyType.MetadataName, requirement.ExpectedTypeMetadataName, StringComparison.Ordinal) ||
                    !propertyType.IsSafeCSharpType ||
                    !HasRequiredAccessors(producer.Reader, property, requirement.Kind) ||
                    !HasRequiredCapabilities(request.Adapter.Capabilities, requirement) ||
                    IsObjectFallback(requirement, propertyType) ||
                    !TryResolveParameterType(universe, requirement, out DecodedType? parameterType))
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible));
                    continue;
                }

                if (requirement.IncludesValidation &&
                    (validationContract is null ||
                     !IsAssignableTo(universe, new TypeLocation(producer, viewModelHandle), validationContract.MetadataName)))
                {
                    diagnostics.Add(CreateDiagnostic(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible));
                    continue;
                }

                resolved.Add(new ResolvedSemanticMember(requirement, propertyType, parameterType));
            }

            if (diagnostics.Count != 0)
            {
                return new PostGeneratorSemanticResult([], BindingDiagnosticBag.Sort(diagnostics));
            }

            string metadataTypeName = GetFullMetadataName(producer.Reader, viewModelHandle);
            return Success(request.Adapter, metadataTypeName, viewModelCSharpType, validationContract, resolved);
        }
        catch (BadImageFormatException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        }
        catch (IOException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        }
        catch (UnauthorizedAccessException)
        {
            return Failure(BindingDiagnosticIds.GeneratedMemberInaccessibleOrIncompatible);
        }
    }

    private static bool IsSafeRequest(PostGeneratorSemanticRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AssemblyPath) ||
            !IsSafeText(request.MetadataTypeName) ||
            !IsSafeText(request.Adapter.Identity) ||
            request.Adapter.Version <= 0 ||
            request.ReferenceAssemblyPaths.Count > MaximumReferences ||
            request.Members.Count is 0 or > MaximumMembers ||
            (request.Adapter.Capabilities & ~KnownCapabilities) != 0)
        {
            return false;
        }

        bool hasValidation = (request.Adapter.Capabilities & PostGeneratorSemanticCapabilities.ValidationErrors) != 0;
        return hasValidation
            ? IsSafeMetadataTypeName(request.Adapter.ValidationContractTypeMetadataName)
            : request.Adapter.ValidationContractTypeMetadataName is null;
    }

    private static string[] SnapshotReferences(IReadOnlyList<string> paths, string producerPath)
    {
        string producerFullPath = Path.GetFullPath(producerPath);
        var unique = new HashSet<string>(PathComparer);
        var snapshot = new List<string>(paths.Count);
        for (int index = 0; index < paths.Count; index++)
        {
            string path = paths[index] ?? throw new ArgumentException("Reference paths cannot contain null values.", nameof(paths));
            if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            {
                throw new ArgumentException("Reference paths cannot be empty or contain control characters.", nameof(paths));
            }

            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
            {
                throw new ArgumentException("Every reference path must identify a compiled PE.", nameof(paths));
            }

            if (!PathComparer.Equals(fullPath, producerFullPath) && unique.Add(fullPath))
            {
                snapshot.Add(fullPath);
            }
        }

        snapshot.Sort(StringComparer.Ordinal);
        return snapshot.ToArray();
    }

    private static void EnsureBoundedReferenceBytes(IReadOnlyList<string> paths, long producerBytes)
    {
        long totalBytes = producerBytes;
        foreach (string path in paths)
        {
            totalBytes = checked(totalBytes + GetBoundedPeLength(path));
            if (totalBytes > MaximumTotalPeBytes)
            {
                throw new ArgumentException("The combined PE inputs exceed the semantic compiler limit.", nameof(paths));
            }
        }
    }

    private static long GetBoundedPeLength(string path)
    {
        long length = new FileInfo(path).Length;
        if (length is <= 0 or > MaximumPeBytes)
        {
            throw new ArgumentException("A PE input exceeds the semantic compiler limit.", nameof(path));
        }

        return length;
    }

    private static PostGeneratorMemberRequirement[] SnapshotRequirements(
        IReadOnlyList<PostGeneratorMemberRequirement> requirements)
    {
        var snapshot = new PostGeneratorMemberRequirement[requirements.Count];
        for (int index = 0; index < requirements.Count; index++)
        {
            PostGeneratorMemberRequirement requirement = requirements[index] ??
                throw new ArgumentException("Semantic member requirements cannot contain null values.", nameof(requirements));
            if (!IsSafeRequirement(requirement))
            {
                throw new ArgumentException("A semantic member requirement is invalid.", nameof(requirements));
            }

            snapshot[index] = requirement;
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

    private static bool IsSafeRequirement(PostGeneratorMemberRequirement requirement)
    {
        if (!IsSafeText(requirement.BindingMemberId) ||
            !CSharpTextEncoder.IsIdentifier(requirement.GeneratedMemberName) ||
            !IsSafeMetadataTypeName(requirement.ExpectedTypeMetadataName) ||
            !Enum.IsDefined(requirement.Kind))
        {
            return false;
        }

        bool hasParameter = requirement.ParameterTypeMetadataName is not null;
        if (hasParameter && !IsSafeMetadataTypeName(requirement.ParameterTypeMetadataName))
        {
            return false;
        }

        return requirement.Kind switch
        {
            PostGeneratorMemberKind.Property => !hasParameter,
            PostGeneratorMemberKind.Command or PostGeneratorMemberKind.AsyncCommand => true,
            _ => false,
        };
    }

    private static bool IsSafeMetadataTypeName(string? value) =>
        value is not null && IsSafeText(value) && !value.Any(char.IsWhiteSpace);

    private static bool IsSafeText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            CSharpTextEncoder.ValidateUnicode(value, nameof(value));
            return StrictUtf8.GetByteCount(value) <= MaximumTextBytes;
        }
        catch (Exception exception) when (exception is ArgumentException or EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool HasRequiredCapabilities(
        PostGeneratorSemanticCapabilities capabilities,
        PostGeneratorMemberRequirement requirement)
    {
        PostGeneratorSemanticCapabilities required = requirement.Kind switch
        {
            PostGeneratorMemberKind.Property =>
                PostGeneratorSemanticCapabilities.PropertyGet |
                PostGeneratorSemanticCapabilities.PropertySet,
            PostGeneratorMemberKind.Command =>
                PostGeneratorSemanticCapabilities.CommandCanExecute |
                PostGeneratorSemanticCapabilities.CommandExecute,
            PostGeneratorMemberKind.AsyncCommand =>
                PostGeneratorSemanticCapabilities.CommandCanExecute |
                PostGeneratorSemanticCapabilities.AsyncCommandExecute |
                PostGeneratorSemanticCapabilities.AsyncCommandCancel |
                PostGeneratorSemanticCapabilities.AsyncCommandIsRunning |
                PostGeneratorSemanticCapabilities.AsyncCommandCanBeCanceled,
            _ => 0,
        };
        if (requirement.IncludesValidation)
        {
            required |= PostGeneratorSemanticCapabilities.ValidationErrors;
        }

        if (requirement.RequiresSerializerMetadata ||
            requirement.ParameterTypeMetadataName is not null)
        {
            required |= PostGeneratorSemanticCapabilities.SourceGeneratedSerializerMetadata;
        }

        return (capabilities & required) == required;
    }

    private static bool IsObjectFallback(PostGeneratorMemberRequirement requirement, DecodedType propertyType) =>
        (requirement.Kind == PostGeneratorMemberKind.Property &&
         string.Equals(propertyType.MetadataName, "System.Object", StringComparison.Ordinal)) ||
        string.Equals(requirement.ParameterTypeMetadataName, "System.Object", StringComparison.Ordinal);

    private static bool TryResolveParameterType(
        MetadataUniverse universe,
        PostGeneratorMemberRequirement requirement,
        out DecodedType? parameterType)
    {
        if (requirement.ParameterTypeMetadataName is null)
        {
            parameterType = null;
            return true;
        }

        return universe.TryResolveTypeSpelling(requirement.ParameterTypeMetadataName, out parameterType);
    }

    private static bool HasRequiredAccessors(
        MetadataReader reader,
        PropertyDefinition property,
        PostGeneratorMemberKind kind)
    {
        PropertyAccessors accessors = property.GetAccessors();
        if (accessors.Getter.IsNil || !IsPublic(reader, accessors.Getter))
        {
            return false;
        }

        return kind != PostGeneratorMemberKind.Property ||
            (!accessors.Setter.IsNil && IsPublic(reader, accessors.Setter));
    }

    private static bool IsPublic(MetadataReader reader, MethodDefinitionHandle method) =>
        (reader.GetMethodDefinition(method).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;

    private static bool TryDecodePropertyType(
        MetadataReader reader,
        PropertyDefinition property,
        out DecodedType type)
    {
        try
        {
            MethodSignature<DecodedType> signature = property.DecodeSignature(new MetadataTypeProvider(), genericContext: null);
            NullabilityDescriptor nullability = GetPropertyNullability(reader, property);
            int annotationIndex = 0;
            type = ApplyNullability(signature.ReturnType, nullability, ref annotationIndex);
            return signature.ParameterTypes.Length == 0;
        }
        catch (BadImageFormatException)
        {
            type = null!;
            return false;
        }
    }

    private static NullabilityDescriptor GetPropertyNullability(
        MetadataReader reader,
        PropertyDefinition property)
    {
        ImmutableArray<byte> annotations = [];
        if (TryReadNullableAnnotations(reader, property.GetCustomAttributes(), out ImmutableArray<byte> propertyAnnotations))
        {
            annotations = propertyAnnotations;
        }

        PropertyAccessors accessors = property.GetAccessors();
        byte annotation;
        if (!accessors.Getter.IsNil)
        {
            MethodDefinition getter = reader.GetMethodDefinition(accessors.Getter);
            if (annotations.IsEmpty)
            {
                foreach (ParameterHandle handle in getter.GetParameters())
                {
                    Parameter parameter = reader.GetParameter(handle);
                    if (parameter.SequenceNumber == 0 &&
                        TryReadNullableAnnotations(
                            reader,
                            parameter.GetCustomAttributes(),
                            out ImmutableArray<byte> returnAnnotations))
                    {
                        annotations = returnAnnotations;
                        break;
                    }
                }
            }

            if (TryReadNullableContext(reader, getter.GetCustomAttributes(), out annotation))
            {
                return new NullabilityDescriptor(annotations, annotation);
            }

            TypeDefinitionHandle declaringType = getter.GetDeclaringType();
            while (!declaringType.IsNil)
            {
                TypeDefinition definition = reader.GetTypeDefinition(declaringType);
                if (TryReadNullableContext(reader, definition.GetCustomAttributes(), out annotation))
                {
                    return new NullabilityDescriptor(annotations, annotation);
                }

                declaringType = definition.GetDeclaringType();
            }
        }

        if (TryReadNullableContext(
            reader,
            reader.GetModuleDefinition().GetCustomAttributes(),
            out annotation))
        {
            return new NullabilityDescriptor(annotations, annotation);
        }

        byte context = reader.IsAssembly &&
            TryReadNullableContext(
                reader,
                reader.GetAssemblyDefinition().GetCustomAttributes(),
                out annotation)
            ? annotation
            : (byte)0;
        return new NullabilityDescriptor(annotations, context);
    }

    private static bool TryReadNullableAnnotations(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        out ImmutableArray<byte> annotations)
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (!TryGetAttributeTypeName(reader, attribute.Constructor, out string typeName) ||
                !string.Equals(
                    typeName,
                    "System.Runtime.CompilerServices.NullableAttribute",
                    StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                BlobReader value = reader.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1)
                {
                    break;
                }

                if (value.RemainingBytes == 3)
                {
                    byte scalar = value.ReadByte();
                    if (scalar <= 2)
                    {
                        annotations = [scalar];
                        return true;
                    }

                    break;
                }

                int count = value.ReadInt32();
                if (count <= 0 || count > value.RemainingBytes - 2)
                {
                    break;
                }

                var builder = ImmutableArray.CreateBuilder<byte>(count);
                for (int index = 0; index < count; index++)
                {
                    byte annotation = value.ReadByte();
                    if (annotation > 2)
                    {
                        annotations = [];
                        return false;
                    }

                    builder.Add(annotation);
                }

                annotations = builder.MoveToImmutable();
                return true;
            }
            catch (BadImageFormatException)
            {
                break;
            }
        }

        annotations = [];
        return false;
    }

    private static bool TryReadNullableContext(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        out byte annotation) =>
        TryReadNullableByte(
            reader,
            attributes,
            "System.Runtime.CompilerServices.NullableContextAttribute",
            out annotation);

    private static bool TryReadNullableByte(
        MetadataReader reader,
        CustomAttributeHandleCollection attributes,
        string attributeMetadataName,
        out byte annotation)
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (!TryGetAttributeTypeName(reader, attribute.Constructor, out string typeName) ||
                !string.Equals(typeName, attributeMetadataName, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                BlobReader value = reader.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() != 1)
                {
                    break;
                }

                if (value.RemainingBytes >= 3)
                {
                    annotation = value.ReadByte();
                    return annotation <= 2;
                }
            }
            catch (BadImageFormatException)
            {
                break;
            }
        }

        annotation = 0;
        return false;
    }

    private static bool TryGetAttributeTypeName(
        MetadataReader reader,
        EntityHandle constructor,
        out string typeName)
    {
        EntityHandle declaringType = constructor.Kind switch
        {
            HandleKind.MethodDefinition =>
                reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
            HandleKind.MemberReference =>
                reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
            _ => default,
        };
        return TryGetEntityTypeName(reader, declaringType, out typeName);
    }

    private static DecodedType ApplyNullability(
        DecodedType type,
        NullabilityDescriptor descriptor,
        ref int annotationIndex)
    {
        byte annotation = annotationIndex < descriptor.Annotations.Length
            ? descriptor.Annotations[annotationIndex]
            : descriptor.Context;
        annotationIndex++;
        ImmutableArray<DecodedType> arguments = type.TypeArguments;
        if (!arguments.IsDefaultOrEmpty)
        {
            var builder = ImmutableArray.CreateBuilder<DecodedType>(arguments.Length);
            foreach (DecodedType argument in arguments)
            {
                builder.Add(ApplyNullability(argument, descriptor, ref annotationIndex));
            }

            arguments = builder.MoveToImmutable();
        }

        DecodedType? elementType = type.ElementType is null
            ? null
            : ApplyNullability(type.ElementType, descriptor, ref annotationIndex);
        return type with
        {
            IsNullable = !type.IsValueType && annotation == 2,
            TypeArguments = arguments,
            ElementType = elementType,
        };
    }

    private static bool IsAssignableTo(
        MetadataUniverse universe,
        TypeLocation start,
        string expectedMetadataName)
    {
        var pending = new Stack<TypeLocation>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        pending.Push(start);
        while (pending.Count != 0)
        {
            TypeLocation current = pending.Pop();
            string currentName = GetFullMetadataName(current.Module.Reader, current.Handle);
            string key = current.Module.Path + "\0" + currentName;
            if (!visited.Add(key))
            {
                continue;
            }

            if (string.Equals(currentName, expectedMetadataName, StringComparison.Ordinal))
            {
                return true;
            }

            TypeDefinition definition = current.Module.Reader.GetTypeDefinition(current.Handle);
            foreach (InterfaceImplementationHandle interfaceHandle in definition.GetInterfaceImplementations())
            {
                EntityHandle interfaceType = current.Module.Reader.GetInterfaceImplementation(interfaceHandle).Interface;
                if (TryGetEntityTypeName(current.Module.Reader, interfaceType, out string interfaceName))
                {
                    if (string.Equals(interfaceName, expectedMetadataName, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (universe.TryFindType(GetGenericDefinitionName(interfaceName), out TypeLocation interfaceLocation))
                    {
                        pending.Push(interfaceLocation);
                    }
                }
            }

            EntityHandle baseType = definition.BaseType;
            if (!baseType.IsNil && TryGetEntityTypeName(current.Module.Reader, baseType, out string baseName))
            {
                if (string.Equals(baseName, expectedMetadataName, StringComparison.Ordinal))
                {
                    return true;
                }

                if (universe.TryFindType(GetGenericDefinitionName(baseName), out TypeLocation baseLocation))
                {
                    pending.Push(baseLocation);
                }
            }
        }

        return false;
    }

    private static bool TryGetEntityTypeName(MetadataReader reader, EntityHandle handle, out string value)
    {
        try
        {
            value = handle.Kind switch
            {
                HandleKind.TypeDefinition => GetFullMetadataName(reader, (TypeDefinitionHandle)handle),
                HandleKind.TypeReference => GetFullMetadataName(reader, (TypeReferenceHandle)handle),
                HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(new MetadataTypeProvider(), genericContext: null).MetadataName,
                _ => string.Empty,
            };
            return value.Length != 0;
        }
        catch (BadImageFormatException)
        {
            value = string.Empty;
            return false;
        }
    }

    private static string GetGenericDefinitionName(string metadataName)
    {
        int genericStart = metadataName.IndexOf('<');
        return genericStart < 0 ? metadataName : metadataName[..genericStart];
    }

    private static bool IsPubliclyAccessible(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinitionHandle current = handle;
        while (!current.IsNil)
        {
            TypeDefinition type = reader.GetTypeDefinition(current);
            TypeAttributes visibility = type.Attributes & TypeAttributes.VisibilityMask;
            if (current.Equals(handle))
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

    private static PostGeneratorSemanticResult Success(
        PostGeneratorAdapterCapabilities adapter,
        string metadataTypeName,
        string viewModelCSharpType,
        DecodedType? validationContract,
        IReadOnlyList<ResolvedSemanticMember> members)
    {
        string canonical = WriteCanonical(adapter, metadataTypeName, validationContract, members);
        string fingerprint = Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(canonical))).ToLowerInvariant();
        string source = WriteSource(adapter, viewModelCSharpType, validationContract, members, fingerprint);
        string manifest = WriteManifest(adapter, metadataTypeName, validationContract, members, fingerprint);
        var artifact = new GeneratedBindingArtifacts(
            source,
            manifest,
            fingerprint,
            "WebUIToolkit.MVVM.PostGenerator." + fingerprint + ".g.cs",
            "webuitoolkit.mvvm.post-generator." + fingerprint + ".semantics.json");
        return new PostGeneratorSemanticResult([artifact], []);
    }

    private static string WriteCanonical(
        PostGeneratorAdapterCapabilities adapter,
        string metadataTypeName,
        DecodedType? validationContract,
        IReadOnlyList<ResolvedSemanticMember> members)
    {
        var builder = new StringBuilder();
        AppendCanonical(builder, "format", PostGeneratorSemanticContract.Identity);
        AppendCanonical(builder, "schema", PostGeneratorSemanticContract.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        AppendEncoded(builder, "adapter", adapter.Identity);
        AppendCanonical(builder, "adapter.version", adapter.Version.ToString(CultureInfo.InvariantCulture));
        AppendCanonical(builder, "adapter.capabilities", ((ulong)adapter.Capabilities).ToString(CultureInfo.InvariantCulture));
        AppendEncoded(builder, "type", metadataTypeName);
        AppendEncoded(builder, "validation", validationContract?.MetadataName ?? string.Empty);
        AppendCanonical(builder, "members", members.Count.ToString(CultureInfo.InvariantCulture));
        for (int index = 0; index < members.Count; index++)
        {
            ResolvedSemanticMember member = members[index];
            string prefix = "member." + index.ToString("D8", CultureInfo.InvariantCulture) + ".";
            AppendEncoded(builder, prefix + "binding", member.Requirement.BindingMemberId);
            AppendEncoded(builder, prefix + "name", member.Requirement.GeneratedMemberName);
            AppendCanonical(builder, prefix + "kind", GetKindName(member.Requirement.Kind));
            AppendEncoded(builder, prefix + "type", member.PropertyType.MetadataName);
            AppendCanonical(builder, prefix + "nullable", member.PropertyType.IsNullable ? "1" : "0");
            AppendEncoded(builder, prefix + "parameter", member.ParameterType?.MetadataName ?? string.Empty);
            AppendCanonical(builder, prefix + "serializer", member.Requirement.RequiresSerializerMetadata ? "1" : "0");
            AppendCanonical(builder, prefix + "validation", member.Requirement.IncludesValidation ? "1" : "0");
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

    private static string WriteManifest(
        PostGeneratorAdapterCapabilities adapter,
        string metadataTypeName,
        DecodedType? validationContract,
        IReadOnlyList<ResolvedSemanticMember> members,
        string fingerprint)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.Default,
            Indented = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", PostGeneratorSemanticContract.SchemaVersion);
            writer.WriteString("contract", PostGeneratorSemanticContract.Identity);
            writer.WriteString("adapter", adapter.Identity);
            writer.WriteNumber("adapterVersion", adapter.Version);
            writer.WriteString("type", metadataTypeName);
            writer.WriteString("fingerprint", fingerprint);
            writer.WriteStartArray("capabilities");
            foreach (string capability in GetCapabilityNames(adapter.Capabilities))
            {
                writer.WriteStringValue(capability);
            }

            writer.WriteEndArray();
            if (validationContract is not null)
            {
                writer.WriteStartObject("validation");
                writer.WriteString("contractType", validationContract.MetadataName);
                writer.WriteString("hasErrors", "HasErrors");
                writer.WriteString("getErrors", "GetErrors");
                writer.WriteEndObject();
            }

            writer.WriteStartArray("members");
            foreach (ResolvedSemanticMember member in members)
            {
                writer.WriteStartObject();
                writer.WriteString("bindingMemberId", member.Requirement.BindingMemberId);
                writer.WriteString("generatedMemberName", member.Requirement.GeneratedMemberName);
                writer.WriteString("kind", GetKindName(member.Requirement.Kind));
                writer.WriteString("generatedType", member.PropertyType.MetadataName);
                writer.WriteBoolean("generatedTypeNullable", member.PropertyType.IsNullable);
                if (member.ParameterType is not null)
                {
                    writer.WriteString("parameterType", member.ParameterType.MetadataName);
                }

                writer.WriteBoolean("validation", member.Requirement.IncludesValidation);
                writer.WriteStartArray("operations");
                foreach (string operation in GetOperationNames(member.Requirement.Kind, member.Requirement.IncludesValidation))
                {
                    writer.WriteStringValue(operation);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("serializerMetadataRequirements");
            foreach (ResolvedSemanticMember member in members)
            {
                if (member.Requirement.RequiresSerializerMetadata &&
                    member.Requirement.Kind == PostGeneratorMemberKind.Property)
                {
                    WriteSerializerRequirement(writer, member.Requirement.BindingMemberId, "value", member.PropertyType.MetadataName);
                }

                if (member.ParameterType is not null)
                {
                    WriteSerializerRequirement(writer, member.Requirement.BindingMemberId, "command-parameter", member.ParameterType.MetadataName);
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return StrictUtf8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteSerializerRequirement(
        Utf8JsonWriter writer,
        string bindingMemberId,
        string purpose,
        string metadataTypeName)
    {
        writer.WriteStartObject();
        writer.WriteString("bindingMemberId", bindingMemberId);
        writer.WriteString("purpose", purpose);
        writer.WriteString("type", metadataTypeName);
        writer.WriteString("metadataKind", "System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>");
        writer.WriteEndObject();
    }

    private static string WriteSource(
        PostGeneratorAdapterCapabilities adapter,
        string viewModelCSharpType,
        DecodedType? validationContract,
        IReadOnlyList<ResolvedSemanticMember> members,
        string fingerprint)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated/>\n#nullable enable\n\nnamespace WebUIToolkit.MVVM.Generated\n{\n");
        builder.Append("    [global::System.CodeDom.Compiler.GeneratedCodeAttribute(\"WebUIToolkit.MVVM.Build\", \"");
        builder.Append(PostGeneratorSemanticContract.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        builder.Append("\")]\n    internal static class PostGeneratorSemanticArtifact_");
        builder.Append(fingerprint.AsSpan(0, 16));
        builder.Append("\n    {\n        internal const string ContractIdentity = ");
        builder.Append(CSharpTextEncoder.StringLiteral(PostGeneratorSemanticContract.Identity));
        builder.Append(";\n        internal const string AdapterIdentity = ");
        builder.Append(CSharpTextEncoder.StringLiteral(adapter.Identity));
        builder.Append(";\n        internal const int AdapterVersion = ");
        builder.Append(adapter.Version.ToString(CultureInfo.InvariantCulture));
        builder.Append(";\n        internal const string Fingerprint = \"");
        builder.Append(fingerprint);
        builder.Append("\";\n\n");
        foreach (ResolvedSemanticMember member in members)
        {
            AppendMemberSource(builder, viewModelCSharpType, validationContract, member);
        }

        builder.Append("    }\n}\n");
        return builder.ToString();
    }

    private static void AppendMemberSource(
        StringBuilder builder,
        string viewModelType,
        DecodedType? validationContract,
        ResolvedSemanticMember member)
    {
        string name = member.Requirement.GeneratedMemberName;
        string escapedName = CSharpTextEncoder.EscapeIdentifier(name);
        if (member.Requirement.Kind is PostGeneratorMemberKind.Command or PostGeneratorMemberKind.AsyncCommand)
        {
            builder.Append("        internal static ");
            builder.Append(GetCSharpUseTypeName(member.PropertyType));
            builder.Append(" Get_");
            builder.Append(name);
            builder.Append('(');
            builder.Append(viewModelType);
            builder.Append(" viewModel) => viewModel.");
            builder.Append(escapedName);
            builder.Append(";\n\n");
        }

        switch (member.Requirement.Kind)
        {
            case PostGeneratorMemberKind.Property:
                builder.Append("        internal static ");
                builder.Append(GetCSharpUseTypeName(member.PropertyType));
                builder.Append(" Get_");
                builder.Append(name);
                builder.Append('(');
                builder.Append(viewModelType);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedName);
                builder.Append(";\n\n        internal static void Set_");
                builder.Append(name);
                builder.Append('(');
                builder.Append(viewModelType);
                builder.Append(" viewModel, ");
                builder.Append(GetCSharpUseTypeName(member.PropertyType));
                builder.Append(" value) => viewModel.");
                builder.Append(escapedName);
                builder.Append(" = value;\n\n");
                break;
            case PostGeneratorMemberKind.Command:
                AppendCanExecute(builder, viewModelType, escapedName, name, member.ParameterType);
                builder.Append("        internal static void Execute_");
                builder.Append(name);
                AppendCommandParameters(builder, viewModelType, member.ParameterType);
                builder.Append(" => viewModel.");
                builder.Append(escapedName);
                builder.Append(".Execute(");
                builder.Append(member.ParameterType is null ? "null" : "parameter");
                builder.Append(");\n\n");
                break;
            case PostGeneratorMemberKind.AsyncCommand:
                AppendCanExecute(builder, viewModelType, escapedName, name, member.ParameterType);
                builder.Append("        internal static global::System.Threading.Tasks.Task ExecuteAsync_");
                builder.Append(name);
                AppendCommandParameters(builder, viewModelType, member.ParameterType);
                builder.Append(" => viewModel.");
                builder.Append(escapedName);
                builder.Append(".ExecuteAsync(");
                builder.Append(member.ParameterType is null ? "null" : "parameter");
                builder.Append(");\n\n        internal static void Cancel_");
                builder.Append(name);
                builder.Append('(');
                builder.Append(viewModelType);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedName);
                builder.Append(".Cancel();\n\n        internal static bool IsRunning_");
                builder.Append(name);
                builder.Append('(');
                builder.Append(viewModelType);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedName);
                builder.Append(".IsRunning;\n\n        internal static bool CanBeCanceled_");
                builder.Append(name);
                builder.Append('(');
                builder.Append(viewModelType);
                builder.Append(" viewModel) => viewModel.");
                builder.Append(escapedName);
                builder.Append(".CanBeCanceled;\n\n");
                break;
        }

        if (member.Requirement.IncludesValidation)
        {
            builder.Append("        internal static bool HasErrors_");
            builder.Append(name);
            builder.Append('(');
            builder.Append(viewModelType);
            builder.Append(" viewModel) => ((");
            builder.Append(validationContract!.CSharpTypeName);
            builder.Append(")viewModel).HasErrors;\n\n        internal static global::System.Collections.IEnumerable? GetErrors_");
            builder.Append(name);
            builder.Append('(');
            builder.Append(viewModelType);
            builder.Append(" viewModel) => ((");
            builder.Append(validationContract.CSharpTypeName);
            builder.Append(")viewModel).GetErrors(");
            builder.Append(CSharpTextEncoder.StringLiteral(name));
            builder.Append(");\n\n");
        }
    }

    private static void AppendCanExecute(
        StringBuilder builder,
        string viewModelType,
        string escapedName,
        string name,
        DecodedType? parameterType)
    {
        builder.Append("        internal static bool CanExecute_");
        builder.Append(name);
        AppendCommandParameters(builder, viewModelType, parameterType);
        builder.Append(" => viewModel.");
        builder.Append(escapedName);
        builder.Append(".CanExecute(");
        builder.Append(parameterType is null ? "null" : "parameter");
        builder.Append(");\n\n");
    }

    private static void AppendCommandParameters(
        StringBuilder builder,
        string viewModelType,
        DecodedType? parameterType)
    {
        builder.Append('(');
        builder.Append(viewModelType);
        builder.Append(" viewModel");
        if (parameterType is not null)
        {
            builder.Append(", ");
            builder.Append(GetCSharpUseTypeName(parameterType));
            builder.Append(" parameter");
        }

        builder.Append(')');
    }

    private static IEnumerable<string> GetCapabilityNames(PostGeneratorSemanticCapabilities capabilities)
    {
        foreach ((PostGeneratorSemanticCapabilities Value, string Name) item in CapabilityNames)
        {
            if ((capabilities & item.Value) != 0)
            {
                yield return item.Name;
            }
        }
    }

    private static IEnumerable<string> GetOperationNames(PostGeneratorMemberKind kind, bool validation)
    {
        yield return "get";
        if (kind == PostGeneratorMemberKind.Property)
        {
            yield return "set";
        }
        else
        {
            yield return "can-execute";
            yield return kind == PostGeneratorMemberKind.Command ? "execute" : "execute-async";
            if (kind == PostGeneratorMemberKind.AsyncCommand)
            {
                yield return "cancel";
                yield return "is-running";
                yield return "can-be-canceled";
            }
        }

        if (validation)
        {
            yield return "has-errors";
            yield return "get-errors";
        }
    }

    private static string GetKindName(PostGeneratorMemberKind kind) => kind switch
    {
        PostGeneratorMemberKind.Property => "property",
        PostGeneratorMemberKind.Command => "command",
        PostGeneratorMemberKind.AsyncCommand => "async-command",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string GetCSharpUseTypeName(DecodedType type)
    {
        string name;
        if (type.ElementType is not null)
        {
            name = GetCSharpUseTypeName(type.ElementType) + "[]";
        }
        else if (!type.TypeArguments.IsDefaultOrEmpty)
        {
            name = type.CSharpTypeName + "<" +
                string.Join(", ", type.TypeArguments.Select(GetCSharpUseTypeName)) + ">";
        }
        else
        {
            name = type.CSharpTypeName;
        }

        return type.IsNullable ? name + "?" : name;
    }

    private static readonly (PostGeneratorSemanticCapabilities Value, string Name)[] CapabilityNames =
    [
        (PostGeneratorSemanticCapabilities.PropertyGet, "property-get"),
        (PostGeneratorSemanticCapabilities.PropertySet, "property-set"),
        (PostGeneratorSemanticCapabilities.CommandCanExecute, "command-can-execute"),
        (PostGeneratorSemanticCapabilities.CommandExecute, "command-execute"),
        (PostGeneratorSemanticCapabilities.AsyncCommandExecute, "async-command-execute"),
        (PostGeneratorSemanticCapabilities.AsyncCommandCancel, "async-command-cancel"),
        (PostGeneratorSemanticCapabilities.AsyncCommandIsRunning, "async-command-is-running"),
        (PostGeneratorSemanticCapabilities.AsyncCommandCanBeCanceled, "async-command-can-be-canceled"),
        (PostGeneratorSemanticCapabilities.ValidationErrors, "validation-errors"),
        (PostGeneratorSemanticCapabilities.SourceGeneratedSerializerMetadata, "source-generated-serializer-metadata"),
    ];

    private static PostGeneratorSemanticResult Failure(string diagnosticId) =>
        new([], [CreateDiagnostic(diagnosticId)]);

    private static BindingDiagnostic CreateDiagnostic(string id) => new(
        id,
        BindingDiagnosticSeverity.Error,
        id switch
        {
            BindingDiagnosticIds.PostGeneratorSemanticContractUnsupported =>
                "The requested post-generator semantic contract version is unsupported.",
            BindingDiagnosticIds.GeneratedMemberAssemblyNotFound =>
                "The requested assembly could not be found.",
            BindingDiagnosticIds.GeneratedMemberTypeNotFound =>
                "The requested metadata type was not found.",
            BindingDiagnosticIds.GeneratedMemberMissing =>
                "A required generated member was not found.",
            BindingDiagnosticIds.GeneratedMemberAmbiguousOrDuplicate =>
                "A required generated member is ambiguous or duplicated.",
            _ =>
                "A required generated member is inaccessible or has an incompatible type.",
        },
        new BindingSourceSpan(
            DiagnosticPath,
            new BindingSourcePosition(0, 0, 0),
            new BindingSourcePosition(0, 0, 0)));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string GetFullMetadataName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return GetFullMetadataName(reader, declaring) + "+" + name;
        }

        string namespaceName = reader.GetString(type.Namespace);
        return namespaceName.Length == 0 ? name : namespaceName + "." + name;
    }

    private static string GetFullMetadataName(MetadataReader reader, TypeReferenceHandle handle)
    {
        TypeReference type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return GetFullMetadataName(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + name;
        }

        string namespaceName = reader.GetString(type.Namespace);
        return namespaceName.Length == 0 ? name : namespaceName + "." + name;
    }

    private static bool TryGetCSharpTypeName(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        out string value)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        string rawName = reader.GetString(type.Name);
        if (!TryGetNonGenericName(rawName, out string name, out int arity) || arity != 0)
        {
            value = string.Empty;
            return false;
        }

        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            if (!TryGetCSharpTypeName(reader, declaring, out string declaringName))
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

        value = "global::" +
            (namespaceName.Length == 0 ? string.Empty : CSharpTextEncoder.EscapeNamespace(namespaceName) + ".") +
            CSharpTextEncoder.EscapeIdentifier(name);
        return true;
    }

    private static bool TryGetNonGenericName(string rawName, out string name, out int arity)
    {
        int separator = rawName.LastIndexOf('`');
        if (separator < 0)
        {
            name = rawName;
            arity = 0;
            return CSharpTextEncoder.IsIdentifier(name);
        }

        name = rawName[..separator];
        if (!CSharpTextEncoder.IsIdentifier(name) ||
            !int.TryParse(rawName.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out arity))
        {
            arity = 0;
            return false;
        }

        return arity > 0;
    }

    private sealed record ResolvedSemanticMember(
        PostGeneratorMemberRequirement Requirement,
        DecodedType PropertyType,
        DecodedType? ParameterType);

    private sealed record DecodedType(
        string MetadataName,
        string CSharpTypeName,
        bool IsSafeCSharpType,
        int GenericArity,
        bool IsValueType,
        bool IsNullable = false,
        ImmutableArray<DecodedType> TypeArguments = default,
        DecodedType? ElementType = null);

    private readonly record struct NullabilityDescriptor(
        ImmutableArray<byte> Annotations,
        byte Context);

    private sealed class MetadataTypeProvider : ISignatureTypeProvider<DecodedType, object?>
    {
        public DecodedType GetArrayType(DecodedType elementType, ArrayShape shape) =>
            Unsupported(elementType.MetadataName + "[metadata-array]");

        public DecodedType GetByReferenceType(DecodedType elementType) =>
            Unsupported(elementType.MetadataName + "&");

        public DecodedType GetFunctionPointerType(MethodSignature<DecodedType> signature) =>
            Unsupported("fnptr");

        public DecodedType GetGenericInstantiation(
            DecodedType genericType,
            ImmutableArray<DecodedType> typeArguments)
        {
            bool safe = genericType.IsSafeCSharpType &&
                genericType.GenericArity == typeArguments.Length &&
                typeArguments.All(static argument => argument.IsSafeCSharpType);
            string metadataName = genericType.MetadataName + "<" +
                string.Join(",", typeArguments.Select(static argument => argument.MetadataName)) + ">";
            string csharpName = safe
                ? genericType.CSharpTypeName
                : string.Empty;
            return new DecodedType(
                metadataName,
                csharpName,
                safe,
                0,
                genericType.IsValueType,
                TypeArguments: typeArguments);
        }

        public DecodedType GetGenericMethodParameter(object? genericContext, int index) =>
            Unsupported("!!" + index.ToString(CultureInfo.InvariantCulture));

        public DecodedType GetGenericTypeParameter(object? genericContext, int index) =>
            Unsupported("!" + index.ToString(CultureInfo.InvariantCulture));

        public DecodedType GetModifiedType(DecodedType modifier, DecodedType unmodifiedType, bool isRequired) =>
            unmodifiedType;

        public DecodedType GetPinnedType(DecodedType elementType) =>
            Unsupported(elementType.MetadataName);

        public DecodedType GetPointerType(DecodedType elementType) =>
            Unsupported(elementType.MetadataName + "*");

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
            new(
                elementType.MetadataName + "[]",
                string.Empty,
                elementType.IsSafeCSharpType,
                0,
                false,
                ElementType: elementType);

        public DecodedType GetTypeFromDefinition(
            MetadataReader metadataReader,
            TypeDefinitionHandle handle,
            byte rawTypeKind) =>
            FromDefinition(
                metadataReader,
                handle,
                metadataReader.ResolveSignatureTypeKind(handle, rawTypeKind) == SignatureTypeKind.ValueType);

        public DecodedType GetTypeFromReference(
            MetadataReader metadataReader,
            TypeReferenceHandle handle,
            byte rawTypeKind) =>
            FromReference(
                metadataReader,
                handle,
                metadataReader.ResolveSignatureTypeKind(handle, rawTypeKind) == SignatureTypeKind.ValueType);

        public DecodedType GetTypeFromSpecification(
            MetadataReader metadataReader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind) =>
            metadataReader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        private static DecodedType Primitive(string metadataName) =>
            new(
                metadataName,
                "global::" + metadataName,
                true,
                0,
                metadataName is not "System.Object" and not "System.String");

        private static DecodedType Unsupported(string metadataName) =>
            new(metadataName, string.Empty, false, 0, false);

        private static DecodedType FromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            bool isValueType)
        {
            string metadataName = GetFullMetadataName(reader, handle);
            TypeDefinition definition = reader.GetTypeDefinition(handle);
            string rawName = reader.GetString(definition.Name);
            if (!TryGetOpenCSharpTypeName(reader, handle, out string csharpName) ||
                !TryGetNonGenericName(rawName, out _, out int arity))
            {
                return Unsupported(metadataName);
            }

            return new DecodedType(
                metadataName,
                csharpName,
                true,
                arity,
                isValueType);
        }

        private static DecodedType FromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            bool isValueType)
        {
            string metadataName = GetFullMetadataName(reader, handle);
            TypeReference reference = reader.GetTypeReference(handle);
            string rawName = reader.GetString(reference.Name);
            if (!TryGetOpenCSharpTypeName(reader, handle, out string csharpName) ||
                !TryGetNonGenericName(rawName, out _, out int arity))
            {
                return Unsupported(metadataName);
            }

            return new DecodedType(
                metadataName,
                csharpName,
                true,
                arity,
                isValueType);
        }
    }

    private static bool IsValueTypeDefinition(MetadataReader reader, TypeDefinitionHandle handle)
    {
        EntityHandle baseType = reader.GetTypeDefinition(handle).BaseType;
        return !baseType.IsNil &&
            TryGetEntityTypeName(reader, baseType, out string baseName) &&
            baseName is "System.ValueType" or "System.Enum";
    }

    private static bool TryGetOpenCSharpTypeName(
        MetadataReader reader,
        TypeDefinitionHandle handle,
        out string value)
    {
        TypeDefinition type = reader.GetTypeDefinition(handle);
        if (!TryGetNonGenericName(reader.GetString(type.Name), out string name, out _))
        {
            value = string.Empty;
            return false;
        }

        TypeDefinitionHandle declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            if (!TryGetOpenCSharpTypeName(reader, declaring, out string declaringName))
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

        value = "global::" +
            (namespaceName.Length == 0 ? string.Empty : CSharpTextEncoder.EscapeNamespace(namespaceName) + ".") +
            CSharpTextEncoder.EscapeIdentifier(name);
        return true;
    }

    private static bool TryGetOpenCSharpTypeName(
        MetadataReader reader,
        TypeReferenceHandle handle,
        out string value)
    {
        TypeReference type = reader.GetTypeReference(handle);
        if (!TryGetNonGenericName(reader.GetString(type.Name), out string name, out _))
        {
            value = string.Empty;
            return false;
        }

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            if (!TryGetOpenCSharpTypeName(reader, (TypeReferenceHandle)type.ResolutionScope, out string declaringName))
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

        value = "global::" +
            (namespaceName.Length == 0 ? string.Empty : CSharpTextEncoder.EscapeNamespace(namespaceName) + ".") +
            CSharpTextEncoder.EscapeIdentifier(name);
        return true;
    }

    private readonly record struct TypeLocation(PeModule Module, TypeDefinitionHandle Handle);

    private sealed class MetadataUniverse : IDisposable
    {
        private readonly PeModule[] _modules;

        public MetadataUniverse(string producerPath, IReadOnlyList<string> referencePaths)
        {
            var modules = new List<PeModule>(referencePaths.Count + 1);
            try
            {
                Producer = new PeModule(Path.GetFullPath(producerPath));
                modules.Add(Producer);
                foreach (string path in referencePaths)
                {
                    modules.Add(new PeModule(path));
                }

                _modules = modules.ToArray();
            }
            catch
            {
                foreach (PeModule module in modules)
                {
                    module.Dispose();
                }

                throw;
            }
        }

        public PeModule Producer { get; }

        public bool TryFindType(string metadataName, out TypeLocation location)
        {
            TypeLocation? match = null;
            foreach (PeModule module in _modules)
            {
                foreach (TypeDefinitionHandle handle in module.Reader.TypeDefinitions)
                {
                    if (!string.Equals(GetFullMetadataName(module.Reader, handle), metadataName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (match is not null)
                    {
                        location = default;
                        return false;
                    }

                    match = new TypeLocation(module, handle);
                }
            }

            location = match.GetValueOrDefault();
            return match is not null;
        }

        public bool TryResolveTypeSpelling(string metadataName, out DecodedType? type)
        {
            if (TryPrimitive(metadataName, out type))
            {
                return true;
            }

            if (metadataName.EndsWith("[]", StringComparison.Ordinal) &&
                TryResolveTypeSpelling(metadataName[..^2], out DecodedType? element))
            {
                type = new DecodedType(
                    metadataName,
                    element!.CSharpTypeName + "[]",
                    element.IsSafeCSharpType,
                    0,
                    false);
                return type.IsSafeCSharpType;
            }

            int genericStart = metadataName.IndexOf('<');
            if (genericStart >= 0 && metadataName.EndsWith('>'))
            {
                string definitionName = metadataName[..genericStart];
                if (!TryFindType(definitionName, out TypeLocation definition) ||
                    !TryGetOpenCSharpTypeName(definition.Module.Reader, definition.Handle, out string openCSharp))
                {
                    type = null;
                    return false;
                }

                string argumentsText = metadataName[(genericStart + 1)..^1];
                string[] argumentNames = SplitGenericArguments(argumentsText);
                TypeDefinition definitionType = definition.Module.Reader.GetTypeDefinition(definition.Handle);
                int arity = definitionType.GetGenericParameters().Count;
                if (argumentNames.Length != arity)
                {
                    type = null;
                    return false;
                }

                var arguments = new DecodedType[argumentNames.Length];
                for (int index = 0; index < argumentNames.Length; index++)
                {
                    if (!TryResolveTypeSpelling(argumentNames[index], out DecodedType? argument))
                    {
                        type = null;
                        return false;
                    }

                    arguments[index] = argument!;
                }

                type = new DecodedType(
                    metadataName,
                    openCSharp + "<" + string.Join(", ", arguments.Select(static item => item.CSharpTypeName)) + ">",
                    true,
                    0,
                    IsValueTypeDefinition(definition.Module.Reader, definition.Handle));
                return true;
            }

            if (!TryFindType(metadataName, out TypeLocation location) ||
                !TryGetCSharpTypeName(location.Module.Reader, location.Handle, out string csharpName))
            {
                type = null;
                return false;
            }

            type = new DecodedType(
                metadataName,
                csharpName,
                true,
                0,
                IsValueTypeDefinition(location.Module.Reader, location.Handle));
            return true;
        }

        public void Dispose()
        {
            foreach (PeModule module in _modules)
            {
                module.Dispose();
            }
        }

        private static string[] SplitGenericArguments(string value)
        {
            var arguments = new List<string>();
            int depth = 0;
            int start = 0;
            for (int index = 0; index < value.Length; index++)
            {
                switch (value[index])
                {
                    case '<':
                        depth++;
                        break;
                    case '>':
                        depth--;
                        if (depth < 0)
                        {
                            return [];
                        }

                        break;
                    case ',' when depth == 0:
                        arguments.Add(value[start..index]);
                        start = index + 1;
                        break;
                }
            }

            if (depth != 0 || start == value.Length)
            {
                return [];
            }

            arguments.Add(value[start..]);
            return arguments.Any(static argument => argument.Length == 0) ? [] : arguments.ToArray();
        }

        private static bool TryPrimitive(string metadataName, out DecodedType? type)
        {
            if (metadataName is "System.Boolean" or "System.Byte" or "System.Char" or "System.Double" or
                "System.Int16" or "System.Int32" or "System.Int64" or "System.IntPtr" or "System.Object" or
                "System.SByte" or "System.Single" or "System.String" or "System.UInt16" or "System.UInt32" or
                "System.UInt64" or "System.UIntPtr")
            {
                type = new DecodedType(
                    metadataName,
                    "global::" + metadataName,
                    true,
                    0,
                    metadataName is not "System.Object" and not "System.String");
                return true;
            }

            type = null;
            return false;
        }
    }

    private sealed class PeModule : IDisposable
    {
        private readonly FileStream _stream;
        private readonly PEReader _peReader;

        public PeModule(string path)
        {
            Path = path;
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _peReader = new PEReader(_stream, PEStreamOptions.LeaveOpen);
            if (!_peReader.HasMetadata)
            {
                throw new BadImageFormatException("The reference has no metadata.");
            }

            Reader = _peReader.GetMetadataReader();
        }

        public string Path { get; }

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _peReader.Dispose();
            _stream.Dispose();
        }
    }
}

/// <summary>Describes one compiled post-generator surface and a normalized adapter requirement.</summary>
public sealed record PostGeneratorSemanticRequest(
    int SchemaVersion,
    string AssemblyPath,
    string MetadataTypeName,
    PostGeneratorAdapterCapabilities Adapter,
    IReadOnlyList<string> ReferenceAssemblyPaths,
    IReadOnlyList<PostGeneratorMemberRequirement> Members);

/// <summary>Describes the semantic operations a framework adapter can consume.</summary>
public sealed record PostGeneratorAdapterCapabilities(
    string Identity,
    int Version,
    PostGeneratorSemanticCapabilities Capabilities,
    string? ValidationContractTypeMetadataName);

/// <summary>Normalized, framework-neutral post-generator operations.</summary>
[Flags]
public enum PostGeneratorSemanticCapabilities : ulong
{
    /// <summary>No semantic operation is available.</summary>
    None = 0,
    /// <summary>Strongly typed property reads are supported.</summary>
    PropertyGet = 1UL << 0,
    /// <summary>Strongly typed property writes are supported.</summary>
    PropertySet = 1UL << 1,
    /// <summary>Command availability checks are supported.</summary>
    CommandCanExecute = 1UL << 2,
    /// <summary>Synchronous command execution is supported.</summary>
    CommandExecute = 1UL << 3,
    /// <summary>Task-returning command execution is supported.</summary>
    AsyncCommandExecute = 1UL << 4,
    /// <summary>Asynchronous command cancellation is supported.</summary>
    AsyncCommandCancel = 1UL << 5,
    /// <summary>Asynchronous command running state is supported.</summary>
    AsyncCommandIsRunning = 1UL << 6,
    /// <summary>Asynchronous command cancellation availability is supported.</summary>
    AsyncCommandCanBeCanceled = 1UL << 7,
    /// <summary>Error-state and per-property validation reads are supported.</summary>
    ValidationErrors = 1UL << 8,
    /// <summary>Closed source-generated serializer metadata is supplied by the consuming adapter.</summary>
    SourceGeneratedSerializerMetadata = 1UL << 9,
}

/// <summary>Describes one generated property or command required by a downstream build-only adapter.</summary>
public sealed record PostGeneratorMemberRequirement(
    string BindingMemberId,
    string GeneratedMemberName,
    PostGeneratorMemberKind Kind,
    string ExpectedTypeMetadataName,
    string? ParameterTypeMetadataName,
    bool RequiresSerializerMetadata,
    bool IncludesValidation);

/// <summary>The normalized semantic kind of one post-generator member.</summary>
public enum PostGeneratorMemberKind
{
    /// <summary>A readable and writable generated property.</summary>
    Property = 0,
    /// <summary>A generated synchronous command.</summary>
    Command = 1,
    /// <summary>A generated asynchronous command with cancellation and running state.</summary>
    AsyncCommand = 2,
}

/// <summary>The deterministic semantic artifacts and stable diagnostics produced by the compiler.</summary>
public sealed record PostGeneratorSemanticResult(
    IReadOnlyList<GeneratedBindingArtifacts> Artifacts,
    IReadOnlyList<BindingDiagnostic> Diagnostics);
