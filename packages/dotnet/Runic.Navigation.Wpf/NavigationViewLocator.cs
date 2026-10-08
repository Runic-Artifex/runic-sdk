using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation.Wpf;

// The locator AddRunicWpfNavigation registers: MapView pairs first, by the content type and then its base
// types, then the naming convention when enabled. Views are created with ActivatorUtilities from the entry's
// services, which are the entry scope when the navigator creates one, or the provider supplied by ViewHost.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationViewLocator(IReadOnlyDictionary<Type, Type> map, bool convention, IReadOnlyList<Assembly>? viewAssemblies = null)
    : INavigationViewLocator
{
    private readonly ConcurrentDictionary<Type, Type?> _resolved = new();
    private readonly ConcurrentDictionary<(Type View, Type Content), ObjectFactory> _factories = new();

    public FrameworkElement? ResolveView(INavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return ResolveView(entry.Content, entry.Services);
    }

    public FrameworkElement? ResolveView(object content, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(services);
        var contentType = content.GetType();
        var viewType = _resolved.GetOrAdd(contentType, Find);
        if (viewType is null) return null;
        var factory = _factories.GetOrAdd((viewType, contentType), static key => CreateFactory(key.View, key.Content));
        return (FrameworkElement)factory(services, [content]);
    }

    // The content goes to the first constructor parameter whose type is a class the content can be assigned to: the
    // content's own type or one of its base classes, never object. Interface and object parameters, such as
    // INotifyPropertyChanged or IDisposable, come from the entry's services like every other parameter. A view
    // without such a parameter is created by ActivatorUtilities. Among constructors with a content parameter, the one
    // marked [ActivatorUtilitiesConstructor] wins; otherwise, as with ActivatorUtilities, the longest one whose other
    // parameters can all be satisfied (a registered service, keyed with [FromKeyedServices], or a default value),
    // checked once against the first provider the view is created from. [ServiceKey] parameters aren't supported.
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "View types come from MapView, whose type parameter keeps public constructors, or the convention.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "View types come from MapView, whose type parameter keeps public constructors, or the convention.")]
    internal static ObjectFactory CreateFactory(Type viewType, Type contentType)
    {
        var candidates = viewType.GetConstructors()
            .Select(constructor => new Candidate(constructor, constructor.GetParameters(), contentType))
            .Where(candidate => candidate.ContentIndex >= 0)
            .OrderByDescending(candidate => candidate.Parameters.Length)
            .ToArray();
        if (candidates.Length == 0)
        {
            var factory = ActivatorUtilities.CreateFactory(viewType, Type.EmptyTypes);
            return (services, _) => factory(services, []);
        }
        Candidate? selected = candidates.FirstOrDefault(candidate =>
            candidate.Constructor.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), false));
        return (services, arguments) =>
        {
            var candidate = selected ??= candidates.FirstOrDefault(candidate => candidate.CanSatisfy(services)) ?? candidates[0];
            var parameters = candidate.Parameters;
            var values = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
                values[i] = i == candidate.ContentIndex ? arguments![0] : Resolve(services, viewType, parameters[i]);
            return candidate.Constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, values, culture: null);
        };
    }

    internal static bool TakesContent(Type parameterType, Type contentType) =>
        parameterType.IsClass && parameterType != typeof(object) && parameterType.IsAssignableFrom(contentType);

    private sealed class Candidate(ConstructorInfo constructor, ParameterInfo[] parameters, Type contentType)
    {
        public ConstructorInfo Constructor { get; } = constructor;

        public ParameterInfo[] Parameters { get; } = parameters;

        public int ContentIndex { get; } = Array.FindIndex(parameters, parameter => TakesContent(parameter.ParameterType, contentType));

        // Without IServiceProviderIsService the provider can't tell, and the candidate counts as satisfiable.
        public bool CanSatisfy(IServiceProvider services)
        {
            var isService = services.GetService<IServiceProviderIsService>();
            if (isService is null) return true;
            var isKeyed = services.GetService<IServiceProviderIsKeyedService>();
            for (var i = 0; i < Parameters.Length; i++)
            {
                var parameter = Parameters[i];
                if (i == ContentIndex || parameter.HasDefaultValue) continue;
                if (parameter.IsDefined(typeof(ServiceKeyAttribute), false)) return false;
                var keyed = parameter.GetCustomAttribute<FromKeyedServicesAttribute>();
                var available = keyed is null
                    ? isService.IsService(parameter.ParameterType)
                    : isKeyed?.IsKeyedService(parameter.ParameterType, keyed.Key) ?? false;
                if (!available) return false;
            }
            return true;
        }
    }

    private static object? Resolve(IServiceProvider services, Type viewType, ParameterInfo parameter)
    {
        if (parameter.IsDefined(typeof(ServiceKeyAttribute), false))
            throw new NotSupportedException(
                $"The view '{viewType}' takes a [ServiceKey] parameter '{parameter.Name}', which located views don't support.");
        object? service;
        if (parameter.GetCustomAttribute<FromKeyedServicesAttribute>() is { } keyed)
        {
            if (services is not IKeyedServiceProvider keyedServices)
                throw new InvalidOperationException(
                    $"The view '{viewType}' takes the keyed service '{parameter.ParameterType}', but the entry's service provider doesn't support keyed services.");
            service = keyedServices.GetKeyedService(parameter.ParameterType, keyed.Key);
        }
        else service = services.GetService(parameter.ParameterType);
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
