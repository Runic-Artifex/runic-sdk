using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

/// <summary>
/// Maps a reflected member to its source line through the model assembly's
/// portable PDB, so a diagnostic points at the declaration that caused it.
/// Members without sequence points (for example auto-property accessors of a
/// source-generated property) fall back to the closest declaring type.
/// </summary>
internal static class SourceLocator
{
    private static readonly Dictionary<Assembly, MetadataReaderProvider?> Providers = [];

    internal readonly record struct Location(string Path, int Line, int Column);

    internal static Location? Find(MemberInfo? member)
    {
        for (var current = member; current is not null; current = current.DeclaringType)
        {
            try
            {
                if (FindDeclared(current) is { } location) return location;
            }
            // A location is a convenience. A missing or unreadable PDB must not
            // replace the diagnostic being reported.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }
        return null;
    }

    private static Location? FindDeclared(MemberInfo member)
    {
        IEnumerable<MethodBase> methods = member switch
        {
            PropertyInfo property => new[] { property.GetMethod, property.SetMethod }.OfType<MethodBase>(),
            MethodBase method => [method],
            Type type => type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                    | BindingFlags.Public | BindingFlags.NonPublic).OfType<MethodBase>(),
            _ => [],
        };
        // For a type, the earliest line of any member is the closest location
        // to its declaration; a property or method uses its own first line.
        return methods.Select(First).OfType<Location>()
            .OrderBy(location => location.Path, StringComparer.Ordinal)
            .ThenBy(location => location.Line).ThenBy(location => location.Column)
            .FirstOrDefault() is { Path.Length: > 0 } found ? found : null;
    }

    private static Location? First(MethodBase method)
    {
        if (Reader(method.Module.Assembly) is not { } reader) return null;
        var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
        if (handle.IsNil) return null;
        foreach (var point in reader.GetMethodDebugInformation(handle).GetSequencePoints())
        {
            if (point.IsHidden) continue;
            var path = reader.GetString(reader.GetDocument(point.Document).Name);
            // Source-generator documents and CI-mapped paths do not exist on
            // disk; MSBuild can only open real files.
            if (!File.Exists(path)) continue;
            return new(path, point.StartLine, point.StartColumn);
        }
        return null;
    }

    private static MetadataReader? Reader(Assembly assembly)
    {
        if (!Providers.TryGetValue(assembly, out var provider))
        {
            provider = Open(assembly);
            Providers.Add(assembly, provider);
        }
        return provider?.GetMetadataReader();
    }

    private static MetadataReaderProvider? Open(Assembly assembly)
    {
        if (assembly.IsDynamic || assembly.Location.Length == 0 || !File.Exists(assembly.Location)) return null;
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        return pe.TryOpenAssociatedPortablePdb(assembly.Location,
            path => File.Exists(path) ? File.OpenRead(path) : null, out var provider, out _) ? provider : null;
    }
}
