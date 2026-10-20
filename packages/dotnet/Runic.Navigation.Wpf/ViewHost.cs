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
[ContentProperty(nameof(Content))]
public class ViewHost : Decorator
{
    /// <summary>Identifies the Content dependency property.</summary>
    public static readonly DependencyProperty ContentProperty = DependencyProperty.Register(nameof(Content), typeof(object),
        typeof(ViewHost), new PropertyMetadata(null, Changed));

    /// <summary>Identifies the ViewLocator dependency property.</summary>
    public static readonly DependencyProperty ViewLocatorProperty = DependencyProperty.Register(nameof(ViewLocator), typeof(INavigationViewLocator),
        typeof(ViewHost), new PropertyMetadata(null, Changed));

    /// <summary>Identifies the Services attached dependency property, which descendants inherit.</summary>
    /// <remarks>
    /// <c>RunicViewHost</c> in <c>Runic.Application.Views.Wpf</c> shares this property, so one provider set on a window
    /// reaches both hosts.
    /// </remarks>
    public static readonly DependencyProperty ServicesProperty = DependencyProperty.RegisterAttached(nameof(Services), typeof(IServiceProvider),
        typeof(ViewHost), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, Changed));

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
    /// <remarks>
    /// Inherited: set it once on the window with <see cref="SetServices"/>, or on the host. The caller owns this
    /// provider. Without one, only the explicit locator and implicit templates are available.
    /// </remarks>
    public IServiceProvider? Services
    {
        get => (IServiceProvider?)GetValue(ServicesProperty);
        set => SetValue(ServicesProperty, value);
    }

    /// <summary>Gets the provider set on <paramref name="element"/>, or inherited from its ancestors.</summary>
    /// <param name="element">The element, such as a window.</param>
    /// <returns>The provider, or <see langword="null"/>.</returns>
    public static IServiceProvider? GetServices(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (IServiceProvider?)element.GetValue(ServicesProperty);
    }

    /// <summary>Sets the provider for every host <paramref name="element"/> contains, such as a window.</summary>
    /// <param name="element">The element, such as a window.</param>
    /// <param name="services">The provider, or <see langword="null"/> to clear it.</param>
    public static void SetServices(DependencyObject element, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ServicesProperty, services);
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        // The property is attached and inherited: only a loaded host presents again.
        if (target is ViewHost { IsLoaded: true } host) host.Refresh();
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
