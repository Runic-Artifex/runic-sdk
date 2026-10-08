using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;

namespace Runic.Navigation.Wpf;

// Builds the presenter for one presentation of an entry (W240-001 §8.3, M3): a new ContentPresenter per entry
// ID, never a changed Content on an existing one, so WPF can't reuse a template's visual tree across entries.
[Experimental(RunicNavigator.DiagnosticId)]
internal static class NavigationPresenters
{
    private static readonly object MissingGate = new();
    private static readonly HashSet<Type> MissingLogged = [];

    // Locator order: the host's locator, the navigator's registered locator, the implicit DataTemplate.
    // With explicitTemplate, the template is resolved from `resourceScope` and set on the presenter, for
    // windows that don't inherit the host's resources.
    public static ContentPresenter Create(FrameworkElement resourceScope, INavigationRegion region, INavigationEntry entry,
        INavigationViewLocator? hostLocator, bool explicitTemplate)
    {
        var presenter = new ContentPresenter();
        var services = region.Navigator.Services;
        var locator = hostLocator;
        var view = locator?.ResolveView(entry);
        if (view is null && services?.GetService(typeof(INavigationViewLocator)) is INavigationViewLocator registered
            && !ReferenceEquals(registered, locator))
            view = registered.ResolveView(entry);
        if (view is not null)
        {
            if (view.ReadLocalValue(FrameworkElement.DataContextProperty) == DependencyProperty.UnsetValue)
                view.DataContext = entry.Content;
            presenter.Content = view;
            return presenter;
        }

        var content = entry.Content;
        presenter.Content = content;
        if (content is UIElement) return presenter;
        var template = FindTemplate(resourceScope, content.GetType());
        if (template is null)
        {
            bool first;
            lock (MissingGate) first = MissingLogged.Add(content.GetType());
            if (first) WpfNavigationLog.ViewNotFound(WpfNavigationLog.For(services), WpfNavigationLog.TypeName(content.GetType()));
        }
        else if (explicitTemplate) presenter.ContentTemplate = template;
        return presenter;
    }

    // The implicit template on the content type's base-type chain, as ContentPresenter looks it up. A template keyed
    // by object is a catch-all, not a template for the content's type, so the walk stops before it.
    public static DataTemplate? FindTemplate(FrameworkElement scope, Type contentType)
    {
        for (var type = contentType; type is not null && type != typeof(object); type = type.BaseType)
            if (scope.TryFindResource(new DataTemplateKey(type)) is DataTemplate template)
                return template;
        return null;
    }
}
