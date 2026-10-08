using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Runic.Navigation.Wpf;

/// <summary>Configures <see cref="RunicWpfNavigationServiceCollectionExtensions.AddRunicWpfNavigation"/>.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public sealed class RunicWpfNavigationOptions
{
    private readonly Dictionary<Type, Type> _views = [];

    /// <summary>
    /// Gets or sets the UI thread's dispatcher. The default is <see cref="Application.Current"/>'s dispatcher, read when the
    /// model context is first resolved.
    /// </summary>
    public Dispatcher? Dispatcher { get; set; }

    /// <summary>Gets or sets the priority of dispatched turns and hooks. The default is <see cref="DispatcherPriority.Normal"/>.</summary>
    public DispatcherPriority Priority { get; set; } = DispatcherPriority.Normal;

    /// <summary>
    /// Gets or sets the lifetime of the <see cref="RunicNavigator"/>: <see cref="ServiceLifetime.Singleton"/> (the default)
    /// for one shell, or <see cref="ServiceLifetime.Scoped"/> for one navigator per window scope.
    /// </summary>
    public ServiceLifetime NavigatorLifetime { get; set; } = ServiceLifetime.Singleton;

    /// <summary>
    /// Gets or sets whether the navigator creates a service scope per entry built from the container
    /// (<see cref="RunicNavigatorOptions.CreateEntryScopes"/>). The default is on for a singleton navigator and off for a
    /// scoped one; a scoped navigator can't create entry scopes.
    /// </summary>
    public bool? CreateEntryScopes { get; set; }

    internal IReadOnlyDictionary<Type, Type> Views => _views;

    internal bool ViewNamingConvention { get; private set; }

    /// <summary>
    /// Presents content of type <typeparamref name="TViewModel"/>, or a type derived from it, with a new
    /// <typeparamref name="TView"/> created from the entry's services.
    /// </summary>
    /// <typeparam name="TViewModel">The content type.</typeparam>
    /// <typeparam name="TView">The view type.</typeparam>
    /// <returns>These options.</returns>
    public RunicWpfNavigationOptions MapView<TViewModel, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TView>()
        where TViewModel : class
        where TView : FrameworkElement
    {
        _views[typeof(TViewModel)] = typeof(TView);
        return this;
    }

    /// <summary>
    /// Finds views by name for content without a <see cref="MapView{TViewModel, TView}"/> pair: <c>FooViewModel</c> is
    /// presented by <c>FooView</c>, then <c>FooPage</c>, in the ViewModel's namespace and then with a <c>ViewModels</c>
    /// namespace segment replaced by <c>Views</c>, from the ViewModel's assembly.
    /// </summary>
    /// <returns>These options.</returns>
    [RequiresUnreferencedCode("Finds view types by name. Use MapView when the app is trimmed.")]
    public RunicWpfNavigationOptions UseViewNamingConvention()
    {
        ViewNamingConvention = true;
        return this;
    }
}
