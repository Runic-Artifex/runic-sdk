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

    // The content goes to the first constructor parameter whose type is a class the content can be assigned to: the
    // content's own type or one of its base classes, never object. Interface and object parameters, such as
    // INotifyPropertyChanged or IDisposable, come from the entry's services like every other parameter. A view
    // without such a parameter is created by ActivatorUtilities. Among constructors with a content parameter, the one
    // marked [ActivatorUtilitiesConstructor] wins, then the one with the most parameters.
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "View types come from MapView, whose type parameter keeps public constructors, or the convention.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "View types come from MapView, whose type parameter keeps public constructors, or the convention.")]
    internal static ObjectFactory CreateFactory(Type viewType, Type contentType)
    {
        var candidates = viewType.GetConstructors()
            .Select(constructor => (Constructor: constructor, Parameters: constructor.GetParameters()))
            .Select(candidate => (candidate.Constructor, candidate.Parameters,
                Index: Array.FindIndex(candidate.Parameters, parameter => TakesContent(parameter.ParameterType, contentType))))
            .Where(candidate => candidate.Index >= 0)
            .ToList();
        if (candidates.Count == 0)
        {
            var factory = ActivatorUtilities.CreateFactory(viewType, Type.EmptyTypes);
            return (services, _) => factory(services, []);
        }
        var chosen = candidates.FirstOrDefault(candidate => candidate.Constructor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false));
        if (chosen.Constructor is null) chosen = candidates.MaxBy(candidate => candidate.Parameters.Length);
        var (selected, parameters, contentIndex) = chosen;
        return (services, arguments) =>
        {
            var values = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                values[i] = i == contentIndex ? arguments![0] : Resolve(services, viewType, parameters[i]);
            return selected.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, values, culture: null);
        };
    }

    internal static bool TakesContent(Type parameterType, Type contentType) =>
        parameterType.IsClass && parameterType != typeof(object) && parameterType.IsAssignableFrom(contentType);

    private static object? Resolve(IServiceProvider services, Type viewType, ParameterInfo parameter)
    {
        var keyed = parameter.GetCustomAttribute<FromKeyedServicesAttribute>();
        var service = keyed is not null && services is IKeyedServiceProvider keyedServices
            ? keyedServices.GetKeyedService(parameter.ParameterType, keyed.Key)
            : services.GetService(parameter.ParameterType);
        if (service is not null) return service;
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        throw new InvalidOperationException(
            $"Unable to resolve service for type '{parameter.ParameterType}' while creating the view '{viewType}'.");
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
