using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

/// <summary>
/// Maps a reflected member to its source line through the model assembly's
/// portable PDB, so a diagnostic points at the declaration that caused it.
/// Members without sequence points (for example auto-property accessors of a
/// source-generated property) fall back to the closest declaring type.
/// </summary>
/// <remarks>
/// A deterministic or continuous-integration build maps source paths in the
/// PDB, for example to <c>/_/</c>. The build passes its path map with
/// <c>--path-map</c>, and document names are mapped back to local files.
/// </remarks>
internal static class SourceLocator
{
    private static readonly Dictionary<Assembly, MetadataReaderProvider?> Providers = [];
    private static (string Local, string Mapped)[] pathMap = [];

    /// <summary>
    /// Sets the compiler path map, in <c>-pathmap</c> syntax: comma-separated
    /// <c>local=mapped</c> pairs, with a <c>,</c> or <c>=</c> in a path doubled.
    /// </summary>
    internal static void SetPathMap(string value)
    {
        var pairs = new List<(string Local, string Mapped)>();
        var current = new StringBuilder();
        string? local = null;
        for (var i = 0; i <= value.Length; i++)
        {
            var c = i < value.Length ? value[i] : ',';
            if (c is ',' or '=' && i + 1 < value.Length && value[i + 1] == c)
            {
                current.Append(c);
                i++;
            }
            else if (c == '=' && local is null)
            {
                local = current.ToString();
                current.Clear();
            }
            else if (c == ',')
            {
                if (local is { Length: > 0 } && current.Length > 0) pairs.Add((local, current.ToString()));
                local = null;
                current.Clear();
            }
            else current.Append(c);
        }
        // Reverse the longest mapped prefix first, so /_/a/ wins over /_/.
        pathMap = pairs.OrderByDescending(pair => pair.Mapped.Length).ToArray();
    }

    // The local path of a PDB document name, or the name when no pair maps it
    // to an existing file.
    private static string LocalPath(string document)
    {
        foreach (var (local, mapped) in pathMap)
        {
            if (!document.StartsWith(mapped, StringComparison.Ordinal)) continue;
            var path = local + document[mapped.Length..];
            if (File.Exists(path)) return path;
        }
        return document;
    }

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

    /// <summary>
    /// The existing source files that declare members of <paramref name="type"/>,
    /// from its methods' sequence points and, for a type without method bodies,
    /// the compiler's type-definition documents.
    /// </summary>
    internal static IReadOnlyList<string> Documents(Type type)
    {
        try
        {
            if (Reader(type.Assembly) is not { } reader) return [];
            var documents = new HashSet<DocumentHandle>();
            foreach (var method in type.GetMembers(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
                         | BindingFlags.Public | BindingFlags.NonPublic).OfType<MethodBase>())
            {
                var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken);
                if (handle.IsNil) continue;
                var information = reader.GetMethodDebugInformation(handle);
                if (!information.Document.IsNil) documents.Add(information.Document);
                foreach (var point in information.GetSequencePoints()) documents.Add(point.Document);
            }
            var definition = MetadataTokens.TypeDefinitionHandle(type.MetadataToken);
            foreach (var handle in reader.GetCustomDebugInformation(definition))
            {
                var information = reader.GetCustomDebugInformation(handle);
                if (reader.GetGuid(information.Kind) != TypeDefinitionDocuments) continue;
                var blob = reader.GetBlobReader(information.Value);
                while (blob.RemainingBytes > 0) documents.Add(MetadataTokens.DocumentHandle(blob.ReadCompressedInteger()));
            }
            return documents.Select(document => LocalPath(reader.GetString(reader.GetDocument(document).Name)))
                .Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        // As for locations, a missing or unreadable PDB only means no source.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            return [];
        }
    }

    // PortableCustomDebugInfoKinds.TypeDefinitionDocuments: the documents of a
    // type whose members have no sequence points.
    private static readonly Guid TypeDefinitionDocuments = new("932E74BC-DBA9-4478-8D46-0F32A7BAB3D3");

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
            var path = LocalPath(reader.GetString(reader.GetDocument(point.Document).Name));
            // Source-generator documents and unmapped CI paths do not exist on
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
