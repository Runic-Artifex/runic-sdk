using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation.Wpf;

// The locator AddRunicWpfNavigation registers: MapView pairs first, by the content type and then its base
// types, then the naming convention when enabled. Views are created with ActivatorUtilities from the entry's
// services, which are the entry scope when the navigator creates one.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationViewLocator(IReadOnlyDictionary<Type, Type> map, bool convention, IReadOnlyList<Assembly>? viewAssemblies = null)
    : INavigationViewLocator
{
    private readonly ConcurrentDictionary<Type, Type?> _resolved = new();
    private readonly ConcurrentDictionary<(Type View, Type Content), ObjectFactory> _factories = new();

    public FrameworkElement? ResolveView(INavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var contentType = entry.Content.GetType();
        var viewType = _resolved.GetOrAdd(contentType, Find);
        if (viewType is null) return null;
        var factory = _factories.GetOrAdd((viewType, contentType), static key => CreateFactory(key.View, key.Content));
        return (FrameworkElement)factory(entry.Services, [entry.Content]);
    }

    // A view whose constructor takes the content's type gets the entry's content, never another instance from the
    // container; other parameters come from the entry's services.
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "View types come from MapView, whose type parameter keeps public constructors, or the convention.")]
    private static ObjectFactory CreateFactory(Type viewType, Type contentType)
    {
        var takesContent = viewType.GetConstructors().Any(constructor =>
            constructor.GetParameters().Any(parameter => parameter.ParameterType.IsAssignableFrom(contentType)));
        if (takesContent) return ActivatorUtilities.CreateFactory(viewType, [contentType]);
        var factory = ActivatorUtilities.CreateFactory(viewType, Type.EmptyTypes);
        return (services, _) => factory(services, []);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The convention is enabled only through UseViewNamingConvention, which carries RequiresUnreferencedCode.")]
    private Type? Find(Type contentType)
    {
        for (var type = contentType; type is not null && type != typeof(object); type = type.BaseType)
            if (map.TryGetValue(type, out var mapped)) return mapped;
        return convention ? FindByConvention(contentType, viewAssemblies) : null;
    }

    // FooViewModel → FooView, then FooPage; in the ViewModel's namespace, then with a "ViewModels" namespace
    // segment replaced by "Views"; in the ViewModel's assembly, then in the given view assemblies, where a view in
    // another namespace matches by its simple name when exactly one type has it.
    [RequiresUnreferencedCode("Finds view types by name.")]
    internal static Type? FindByConvention(Type contentType, IReadOnlyList<Assembly>? viewAssemblies = null)
    {
        const string Suffix = "ViewModel";
        var name = contentType.Name;
        if (contentType.IsGenericType || contentType.IsNested || !name.EndsWith(Suffix, StringComparison.Ordinal) || name.Length == Suffix.Length)
            return null;
        var stem = name[..^Suffix.Length];
        var ns = contentType.Namespace;
        List<string?> namespaces = [ns];
        if (ns is not null)
        {
            var segments = ns.Split('.');
            var replaced = string.Join('.', segments.Select(segment => segment == "ViewModels" ? "Views" : segment));
            if (replaced != ns) namespaces.Add(replaced);
        }
        string[] viewNames = [stem + "View", stem + "Page"];
        List<Assembly> assemblies = [contentType.Assembly, .. viewAssemblies ?? []];
        foreach (var assembly in assemblies.Distinct())
            foreach (var candidateNamespace in namespaces)
                foreach (var viewName in viewNames)
                {
                    var fullName = candidateNamespace is null ? viewName : candidateNamespace + "." + viewName;
                    if (assembly.GetType(fullName, throwOnError: false) is { } view && IsView(view)) return view;
                }
        // Views in another assembly usually live in another namespace: the one view type with the name.
        foreach (var assembly in (viewAssemblies ?? []).Distinct())
            foreach (var viewName in viewNames)
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException partial) { types = [.. partial.Types.OfType<Type>()]; }
                var matches = types.Where(type => type.Name == viewName && IsView(type)).Take(2).ToList();
                if (matches.Count == 1) return matches[0];
            }
        return null;

        static bool IsView(Type type) => typeof(FrameworkElement).IsAssignableFrom(type) && !type.IsAbstract && !type.IsGenericTypeDefinition;
    }
}
