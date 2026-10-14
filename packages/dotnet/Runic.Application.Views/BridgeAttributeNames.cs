using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

// The custom attributes on an assembly or member, by attribute type full name.
// Reflection's IsDefined, GetCustomAttribute(s) and CustomAttributes resolve the
// type of every attribute on the target, so one attribute whose assembly this
// process cannot load fails a check for any other: a WPF application assembly
// carries [ThemeInfo] (PresentationFramework) and often [XmlnsDefinition]
// (System.Xaml), and the code generator runs without the Windows Desktop
// framework.
//
// Reflection is read first: in a running application every attribute type
// resolves, and reflection sees the loaded module, including Hot Reload
// metadata updates that the file on disk does not have. Only when an attribute
// type cannot be resolved (the generator's case) are the attributes read from
// the module file's metadata, which resolves nothing. That file is the one the
// generator loaded, so it has no updates.
internal static class BridgeAttributeNames
{
    private static readonly ConditionalWeakTable<Module, ModuleMetadata> Modules = new();

    /// <summary>One attribute: its type full name and its named arguments, decoded on demand.</summary>
    internal sealed record Entry(string Name, Func<IReadOnlyDictionary<string, object?>> NamedArguments);

    internal static bool Has(Assembly assembly, string fullName) => Of(assembly).Any(entry => entry.Name == fullName);

    internal static bool Has(MemberInfo member, string fullName) => Of(member).Any(entry => entry.Name == fullName);

    /// <summary>The first attribute named <paramref name="fullName"/> on <paramref name="member"/>, or null.</summary>
    internal static Entry? Find(MemberInfo member, string fullName) => Of(member).FirstOrDefault(entry => entry.Name == fullName);

    internal static IReadOnlyList<Entry> Of(Assembly assembly) =>
        Read(() => assembly.CustomAttributes, assembly.ManifestModule, EntityHandle.AssemblyDefinition);

    internal static IReadOnlyList<Entry> Of(MemberInfo member) =>
        Read(() => member.CustomAttributes, member.Module,
            member.MetadataToken == 0 ? default : MetadataTokens.EntityHandle(member.MetadataToken));

    private static List<Entry> Read(Func<IEnumerable<CustomAttributeData>> attributes, Module module, EntityHandle parent)
    {
        try
        {
            return [.. attributes().Select(attribute => new Entry(attribute.AttributeType.FullName ?? "",
                () => attribute.NamedArguments.ToDictionary(argument => argument.MemberName, argument => argument.TypedValue.Value, StringComparer.Ordinal)))];
        }
        // An attribute type that cannot be resolved; without a module file
        // there is nothing else to read, so the failure stands.
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException
            && !parent.IsNil && Reader(module) is { } reader)
        {
            return FromMetadata(reader, parent);
        }
    }

    private static List<Entry> FromMetadata(MetadataReader reader, EntityHandle parent)
    {
        var entries = new List<Entry>();
        foreach (var handle in reader.GetCustomAttributes(parent))
        {
            var attribute = reader.GetCustomAttribute(handle);
            var constructor = attribute.Constructor;
            var type = constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default,
            };
            if (type.IsNil || TypeName(reader, type) is not { } name) continue;
            entries.Add(new Entry(name, () => attribute.DecodeValue(ArgumentTypes.Instance).NamedArguments
                .Where(argument => argument.Name is not null)
                .GroupBy(argument => argument.Name!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)));
        }
        return entries;
    }

    /// <summary>The metadata of <paramref name="module"/>, or null when it is not a readable file.</summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3002", Justification = "A single-file module reports no file name and falls back to reflection.")]
    internal static MetadataReader? Reader(Module module)
    {
        if (module.Assembly.IsDynamic) return null;
        return Modules.GetValue(module, static module =>
        {
            try
            {
                var path = module.FullyQualifiedName;
                if (!Path.IsPathRooted(path) || !File.Exists(path)) return new ModuleMetadata(null);
                // Prefetching reads the metadata into memory and closes the file.
                var image = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchMetadata);
                return new ModuleMetadata(image.HasMetadata ? image : null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                return new ModuleMetadata(null);
            }
        }).Reader;
    }

    /// <summary>The reflection full name of a type reference or definition, or null for another handle.</summary>
    internal static string? TypeName(MetadataReader reader, EntityHandle type)
    {
        switch (type.Kind)
        {
            case HandleKind.TypeReference:
                var reference = reader.GetTypeReference((TypeReferenceHandle)type);
                return reference.ResolutionScope.Kind == HandleKind.TypeReference
                    ? TypeName(reader, (EntityHandle)reference.ResolutionScope) + "+" + reader.GetString(reference.Name)
                    : Join(reader.GetString(reference.Namespace), reader.GetString(reference.Name));
            case HandleKind.TypeDefinition:
                var definition = reader.GetTypeDefinition((TypeDefinitionHandle)type);
                var declaring = definition.GetDeclaringType();
                return declaring.IsNil
                    ? Join(reader.GetString(definition.Namespace), reader.GetString(definition.Name))
                    : TypeName(reader, declaring) + "+" + reader.GetString(definition.Name);
            default:
                // A type specification: a generic attribute, never one of Runic's.
                return null;
        }
    }

    private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    // Decodes argument values without resolving types. An enum argument is
    // read as Int32, the underlying type of every enum Runic reads
    // (JsonIgnoreCondition); other types decode to their names.
    private sealed class ArgumentTypes : ICustomAttributeTypeProvider<object?>
    {
        internal static readonly ArgumentTypes Instance = new();

        public object? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode;
        public object? GetSystemType() => "System.Type";
        public object? GetSZArrayType(object? elementType) => elementType;
        public object? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeName(reader, handle);
        public object? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => TypeName(reader, handle);
        public object? GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(object? type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(object? type) => type as string == "System.Type";
    }

    // Keeps the PEReader, which owns the metadata memory, alive with its module.
    private sealed class ModuleMetadata(PEReader? image)
    {
        internal PEReader? Image { get; } = image;

        internal MetadataReader? Reader { get; } = image?.GetMetadataReader();
    }
}
