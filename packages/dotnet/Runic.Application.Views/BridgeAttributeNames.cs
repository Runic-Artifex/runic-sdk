using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;

namespace Runic.Application.Views;

// The full names of the custom attributes on an assembly or member, read from
// the module's metadata. Reflection's IsDefined, GetCustomAttribute(s) and
// CustomAttributes resolve the type of every attribute on the target, so one
// attribute whose assembly this process cannot load fails a check for any
// other: a WPF application assembly carries [ThemeInfo] (PresentationFramework)
// and often [XmlnsDefinition] (System.Xaml), and the code generator runs
// without the Windows Desktop framework. Reading names resolves nothing.
//
// A module that is not a file on disk (single-file, in-memory or dynamic) falls
// back to reflection. That only happens at run time, where the application's
// frameworks are loaded.
internal static class BridgeAttributeNames
{
    private static readonly ConditionalWeakTable<Module, ModuleMetadata> Modules = new();

    internal static bool Has(Assembly assembly, string fullName) => Of(assembly).Contains(fullName, StringComparer.Ordinal);

    internal static bool Has(MemberInfo member, string fullName) => Of(member).Contains(fullName, StringComparer.Ordinal);

    internal static IEnumerable<string> Of(Assembly assembly) =>
        Reader(assembly.ManifestModule) is { } reader
            ? Names(reader, EntityHandle.AssemblyDefinition)
            : assembly.CustomAttributes.Select(attribute => attribute.AttributeType.FullName ?? "");

    internal static IEnumerable<string> Of(MemberInfo member) =>
        Reader(member.Module) is { } reader && member.MetadataToken != 0
            ? Names(reader, MetadataTokens.EntityHandle(member.MetadataToken))
            : member.CustomAttributes.Select(attribute => attribute.AttributeType.FullName ?? "");

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

    private static List<string> Names(MetadataReader reader, EntityHandle parent)
    {
        var names = new List<string>();
        foreach (var handle in reader.GetCustomAttributes(parent))
        {
            var constructor = reader.GetCustomAttribute(handle).Constructor;
            var type = constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default,
            };
            if (!type.IsNil && TypeName(reader, type) is { } name) names.Add(name);
        }
        return names;
    }

    // Keeps the PEReader, which owns the metadata memory, alive with its module.
    private sealed class ModuleMetadata(PEReader? image)
    {
        internal PEReader? Image { get; } = image;

        internal MetadataReader? Reader { get; } = image?.GetMetadataReader();
    }
}
