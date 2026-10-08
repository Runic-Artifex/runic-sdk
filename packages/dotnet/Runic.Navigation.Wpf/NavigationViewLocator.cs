using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation.Wpf;

// The locator AddRunicWpfNavigation registers: MapView pairs first, by the content type and then its base
// types, then the naming convention when enabled. Views are created with ActivatorUtilities from the entry's
// services, which are the entry scope when the navigator creates one.
[Experimental(RunicNavigator.DiagnosticId)]
internal sealed class NavigationViewLocator(IReadOnlyDictionary<Type, Type> map, bool convention) : INavigationViewLocator
{
    private readonly ConcurrentDictionary<Type, Type?> _resolved = new();

    public FrameworkElement? ResolveView(INavigationEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var viewType = _resolved.GetOrAdd(entry.Content.GetType(), Find);
        return viewType is null ? null : (FrameworkElement)ActivatorUtilities.CreateInstance(entry.Services, viewType);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "The convention is enabled only through UseViewNamingConvention, which carries RequiresUnreferencedCode.")]
    private Type? Find(Type contentType)
    {
        for (var type = contentType; type is not null && type != typeof(object); type = type.BaseType)
            if (map.TryGetValue(type, out var mapped)) return mapped;
        return convention ? FindByConvention(contentType) : null;
    }

    // FooViewModel → FooView, then FooPage; in the ViewModel's namespace, then with a "ViewModels" namespace
    // segment replaced by "Views"; in the ViewModel's assembly only.
    [RequiresUnreferencedCode("Finds view types by name.")]
    internal static Type? FindByConvention(Type contentType)
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
        foreach (var candidateNamespace in namespaces)
            foreach (var viewName in new[] { stem + "View", stem + "Page" })
            {
                var fullName = candidateNamespace is null ? viewName : candidateNamespace + "." + viewName;
                if (contentType.Assembly.GetType(fullName, throwOnError: false) is { } view
                    && typeof(FrameworkElement).IsAssignableFrom(view) && !view.IsAbstract)
                    return view;
            }
        return null;
    }
}
