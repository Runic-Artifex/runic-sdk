using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Runic.Navigation.Wpf;

/// <summary>Presents plain borrowed content through view location or an implicit WPF DataTemplate.</summary>
/// <remarks>
/// The host creates a fresh presentation when content changes or the host reloads. It never owns or
/// disposes content, creates navigation entries, or changes navigation. Its Child is managed by the host.
/// The host's ViewLocator is asked first, then the locator from Services, then implicit WPF templates.
/// Entry-specific locators can implement the plain-content overload for use with this host.
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
[ContentProperty(nameof(Content))]
public class ViewHost : Decorator
{
    /// <summary>Identifies the Content dependency property.</summary>
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(object),
        typeof(ViewHost), new PropertyMetadata(null, Changed));

    /// <summary>Identifies the ViewLocator dependency property.</summary>
    public static readonly DependencyProperty ViewLocatorProperty = DependencyProperty.Register(nameof(ViewLocator), typeof(INavigationViewLocator),
        typeof(ViewHost), new PropertyMetadata(null, Changed));

    /// <summary>Identifies the Services dependency property.</summary>
    public static readonly DependencyProperty ServicesProperty = DependencyProperty.Register(nameof(Services), typeof(IServiceProvider),
        typeof(ViewHost), new PropertyMetadata(null, Changed));

    /// <summary>Creates a plain-content view host.</summary>
    public ViewHost()
    {
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Child = null;
    }

    /// <summary>Gets or sets the borrowed content to present.</summary>
    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>Gets or sets the locator asked first for a plain-content view.</summary>
    public INavigationViewLocator? ViewLocator
    {
        get => (INavigationViewLocator?)GetValue(ViewLocatorProperty);
        set => SetValue(ViewLocatorProperty, value);
    }

    /// <summary>Gets or sets the provider for registered view location and view construction.</summary>
    /// <remarks>The caller owns this provider. Without one, only the explicit locator and implicit templates are available.</remarks>
    public IServiceProvider? Services
    {
        get => (IServiceProvider?)GetValue(ServicesProperty);
        set => SetValue(ServicesProperty, value);
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var host = (ViewHost)target;
        if (host.IsLoaded) host.Refresh();
    }

    private void Refresh()
    {
        // Clear first so a failed view constructor cannot leave the old content visible.
        Child = null;
        if (Content is not { } content) return;
        var services = Services ?? EmptyServices.Instance;
        var locator = ViewLocator;
        var view = locator?.ResolveView(content, services);
        if (view is null && services.GetService(typeof(INavigationViewLocator)) is INavigationViewLocator registered
            && !ReferenceEquals(registered, locator))
            view = registered.ResolveView(content, services);
        var presenter = new ContentPresenter();
        if (view is not null)
        {
            if (view.ReadLocalValue(DataContextProperty) == DependencyProperty.UnsetValue) view.DataContext = content;
            presenter.Content = view;
        }
        else
        {
            presenter.Content = content;
            if (content is not UIElement && NavigationPresenters.FindTemplate(this, content.GetType()) is null)
                NavigationPresenters.LogMissing(Services, content.GetType());
        }
        Child = presenter;
    }

    private sealed class EmptyServices : IServiceProvider
    {
        internal static readonly EmptyServices Instance = new();
        public object? GetService(Type serviceType) => null;
    }
}
