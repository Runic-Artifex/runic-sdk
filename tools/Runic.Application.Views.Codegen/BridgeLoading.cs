using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using Runic.Application.Views;

/// <summary>
/// Loading the application assembly in the generator, which runs on
/// Microsoft.NETCore.App only. The application may also use other shared
/// frameworks: every Runic Desktop application uses Microsoft.AspNetCore.App,
/// which Runic.Desktop references, and a WPF application uses
/// Microsoft.WindowsDesktop.App.
/// </summary>
static class BridgeLoading
{
    /// <summary>
    /// Resolves the application's assemblies from the shared frameworks that
    /// its build references (<c>--framework</c>), such as
    /// Microsoft.Extensions.Hosting.Abstractions from Microsoft.AspNetCore.App
    /// for a <c>BackgroundService</c>. The installed version that matches the
    /// generator's runtime is used. Microsoft.WindowsDesktop.App is never
    /// resolved, even on Windows: a Windows application is also built on Linux
    /// and macOS, where that framework does not exist, and must generate the
    /// same Bridge there. Its types are skipped instead (see <see cref="InspectableTypes"/>).
    /// </summary>
    internal static void UseSharedFrameworks(IEnumerable<string> frameworks, string applicationAssembly)
    {
        var shared = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", ".."));
        var directories = frameworks.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => name != "Microsoft.NETCore.App" && !name.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase))
            .Select(name => FrameworkDirectory(Path.Combine(shared, name)))
            .OfType<string>().ToArray();
        if (directories.Length == 0) return;
        var applicationDirectory = Path.GetDirectoryName(Path.GetFullPath(applicationAssembly))!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name is not { Length: > 0 } simpleName) return null;
            // The application's own copy, for example a newer package version
            // than the framework's, is loaded beside it as before.
            if (File.Exists(Path.Combine(applicationDirectory, simpleName + ".dll"))) return null;
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, simpleName + ".dll");
                if (File.Exists(path) && (name.Version is null || AssemblyName.GetAssemblyName(path).Version >= name.Version))
                    return context.LoadFromAssemblyPath(path);
            }
            return null;
        };
    }

    // The installed version of a shared framework for this runtime: the same
    // major and minor version as the runtime, highest patch first, else the
    // newest older one. A newer major version may need a newer runtime.
    private static string? FrameworkDirectory(string root)
    {
        if (!Directory.Exists(root)) return null;
        var runtime = Environment.Version;
        return Directory.GetDirectories(root)
            .Select(directory => (Directory: directory, Version: Version.TryParse(Path.GetFileName(directory).Split('-', '+')[0], out var version) ? version : null))
            .Where(candidate => candidate.Version is { } version && version.Major <= runtime.Major)
            .OrderByDescending(candidate => candidate.Version!.Major == runtime.Major && candidate.Version.Minor == runtime.Minor)
            .ThenByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Directory).FirstOrDefault();
    }

    /// <summary>
    /// The application's types that load. A WPF or Windows Forms project also
    /// declares its Application, windows and controls, which derive from
    /// Windows Desktop framework types this process cannot load; other types
    /// may need a framework or assembly that is missing too. Runic Views and
    /// ViewModels must not depend on them, so the types that load are
    /// inspected. A Runic View that does not load (because of its ViewModel, or
    /// its own base) is reported, naming the missing assemblies.
    /// </summary>
    internal static IEnumerable<Type> InspectableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException error) when (error.LoaderExceptions.All(inner => MissingAssembly(inner) is not null))
        {
            var loaded = error.Types.OfType<Type>().ToArray();
            var missing = error.LoaderExceptions.Select(MissingAssembly).OfType<string>().Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var views = UnloadedViews(assembly, loaded);
            if (views.Length > 0)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.AssemblyLoad,
                    $"{assembly.GetName().Name}: the Runic {(views.Length == 1 ? "View" : "Views")} {string.Join(", ", views)} could not be loaded because {(missing.Length == 1 ? "the assembly" : "the assemblies")} {string.Join(", ", missing)} could not be loaded.{WindowsDesktopHint(missing)}");
            var skipped = error.Types.Count(type => type is null);
            Console.WriteLine(missing.All(IsWindowsDesktopAssembly)
                ? $"{assembly.GetName().Name}: skipped {skipped} types that depend on {string.Join(", ", missing)}. Runic Views and ViewModels cannot depend on Windows Desktop framework types."
                : $"{assembly.GetName().Name}: skipped {skipped} types that depend on {string.Join(", ", missing)}, which the Bridge generator could not load. None of them is a Runic View.");
            return loaded;
        }
    }

    /// <summary>The simple name of the assembly whose absence <paramref name="error"/> reports, or null.</summary>
    internal static string? MissingAssembly(Exception? error) =>
        error is FileNotFoundException { FileName: { Length: > 0 } fileName } ? new AssemblyName(fileName).Name : null;

    internal static bool IsWindowsDesktopAssembly(string? name) =>
        name is "WindowsBase" or "System.Xaml" or "ReachFramework" or "Accessibility" or "WindowsFormsIntegration"
            or "System.Printing" or "DirectWriteForwarder"
        || name is not null && (name.StartsWith("Presentation", StringComparison.Ordinal)
            || name.StartsWith("UIAutomation", StringComparison.Ordinal)
            || name.StartsWith("System.Windows.", StringComparison.Ordinal));

    internal static string WindowsDesktopHint(IEnumerable<string?> assemblies) => assemblies.Any(IsWindowsDesktopAssembly)
        ? " Runic Views and ViewModels cannot depend on Windows Desktop (WPF or Windows Forms) types."
        : "";

    // The full names of the types that did not load and are Runic Views, found
    // in metadata: a View derives from RunicView<TViewModel> (or RunicWindow),
    // directly, through a base class in the same assembly, or through a base
    // class in another assembly that loads.
    private static string[] UnloadedViews(Assembly assembly, IReadOnlyCollection<Type> loaded)
    {
        if (BridgeAttributeNames.Reader(assembly.ManifestModule) is not { } reader) return [];
        var loadedNames = loaded.Select(type => type.FullName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var views = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var name = BridgeAttributeNames.TypeName(reader, handle);
            if (name is null || name == "<Module>" || loadedNames.Contains(name)) continue;
            if (IsView(reader, handle, 0)) views.Add(name);
        }
        return [.. views.Order(StringComparer.Ordinal)];
    }

    private static bool IsView(MetadataReader reader, EntityHandle type, int depth)
    {
        if (type.IsNil || depth > 32) return false;
        switch (type.Kind)
        {
            case HandleKind.TypeDefinition:
                return IsView(reader, reader.GetTypeDefinition((TypeDefinitionHandle)type).BaseType, depth + 1);
            case HandleKind.TypeSpecification:
                // A generic instantiation: GENERICINST (CLASS|VALUETYPE) TypeDefOrRef ...
                var signature = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)type).Signature);
                if (signature.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance) return false;
                signature.ReadSignatureTypeCode();
                return IsView(reader, signature.ReadTypeHandle(), depth + 1);
            case HandleKind.TypeReference:
                var name = BridgeAttributeNames.TypeName(reader, type);
                if (name is "Runic.Application.Views.RunicView`1" or "Runic.Application.Views.RunicWindow`1") return true;
                return ExternalBaseIsView(reader, (TypeReferenceHandle)type, name);
            default:
                return false;
        }
    }

    // A base class in another assembly, such as an application's shared View
    // base in a library. One that cannot be loaded is not a Runic View base: a
    // View's own base always loads with Runic.Application.Views.
    private static bool ExternalBaseIsView(MetadataReader reader, TypeReferenceHandle handle, string? name)
    {
        var reference = reader.GetTypeReference(handle);
        if (name is null || reference.ResolutionScope.Kind != HandleKind.AssemblyReference) return false;
        var assemblyName = reader.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).GetAssemblyName();
        try
        {
            for (var current = Assembly.Load(assemblyName).GetType(name, throwOnError: false)?.BaseType; current is not null; current = current.BaseType)
                if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Runic.Application.Views.RunicView`1")
                    return true;
        }
        catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException)
        {
        }
        return false;
    }

    /// <summary>
    /// Checks that the bridged surface of <paramref name="model"/> loads: its
    /// public properties that are not <c>[RunicIgnore]</c>, their generic
    /// arguments and the members of the DTOs they reach. A member whose type
    /// needs an assembly the generator cannot load is reported at that member,
    /// before the contract of the application's ViewModels is computed (which
    /// would otherwise fail for every ViewModel).
    /// </summary>
    internal static void CheckSurface(Type model)
    {
        var honorJsonIgnore = BridgeDtoContract.HonorsJsonIgnore(model.Assembly);
        var visited = new HashSet<Type>();
        foreach (var property in model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
            .Where(property => !BridgeDtoContract.IsRunicIgnored(property)).OrderBy(property => property.MetadataToken))
            CheckProperty(property, $"{model.Name}.{property.Name}", visited, honorJsonIgnore);
    }

    private static void CheckProperty(PropertyInfo property, string path, HashSet<Type> visited, bool honorJsonIgnore)
    {
        Type type;
        try
        {
            if (property.GetIndexParameters().Length != 0) return;
            type = property.PropertyType;
        }
        catch (Exception error) when (MissingAssembly(error) is not null || error is FileLoadException or TypeLoadException)
        {
            throw Unloadable(path, property, error);
        }
        CheckType(type, path, property, visited, honorJsonIgnore);
    }

    private static void CheckType(Type type, string path, PropertyInfo origin, HashSet<Type> visited, bool honorJsonIgnore)
    {
        if (!visited.Add(type)) return;
        try
        {
            if (type.HasElementType)
            {
                CheckType(type.GetElementType()!, path, origin, visited, honorJsonIgnore);
                return;
            }
            if (type.IsGenericType)
                foreach (var argument in type.GenericTypeArguments)
                    CheckType(argument, path, origin, visited, honorJsonIgnore);
            // Framework types, ViewModels (content, checked as models of their
            // own Views) and abstract types are not DTOs whose members cross.
            if (type.IsPrimitive || type.IsEnum || type.IsInterface || type.IsAbstract || type.IsGenericParameter
                || type.Namespace is { } ns && (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                    || BridgeMvvmConventions.IsFrameworkNamespace(ns))
                || typeof(INotifyPropertyChanged).IsAssignableFrom(type))
                return;
        }
        catch (Exception error) when (MissingAssembly(error) is not null || error is FileLoadException or TypeLoadException)
        {
            throw Unloadable(path, origin, error);
        }
        for (var current = type; current is not null && current != typeof(object) && !BridgeContractShape.IsFrameworkBase(current); current = current.BaseType)
            foreach (var member in current.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
                .Where(member => member.GetMethod is not null && !BridgeDtoContract.IsIgnored(member, honorJsonIgnore)))
                CheckProperty(member, $"{path}.{member.Name}", visited, honorJsonIgnore);
    }

    private static BridgeDiagnosticException Unloadable(string path, MemberInfo member, Exception error)
    {
        var assembly = MissingAssembly(error);
        if (assembly is null && error is FileLoadException { FileName: { Length: > 0 } fileName })
            assembly = new AssemblyName(fileName).Name;
        var reason = assembly is not null
            ? $"its type needs the assembly {assembly}, which the Bridge generator could not load"
            : $"its type could not be loaded ({error.Message})";
        return new BridgeDiagnosticException(BridgeDiagnosticCodes.AssemblyLoad,
            $"{path}: {reason}.{WindowsDesktopHint([assembly])} Mark the property [RunicIgnore], or expose a Bridge value instead.", member);
    }
}
