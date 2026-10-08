using System.Diagnostics.CodeAnalysis;
using System.Windows;

namespace Runic.Navigation.Wpf;

/// <summary>Creates the view that presents a navigation entry's content.</summary>
/// <remarks>
/// <see cref="NavigationHost"/> and <see cref="NavigationDialogHost"/> ask their own <c>ViewLocator</c> first,
/// then an <see cref="INavigationViewLocator"/> from the region's <see cref="RunicNavigator.Services"/>, and
/// finally fall back to the implicit <see cref="DataTemplate"/> keyed by the content's type.
/// </remarks>
[Experimental(RunicNavigator.DiagnosticId)]
public interface INavigationViewLocator
{
    /// <summary>
    /// Returns a new view for this presentation of <paramref name="entry"/>, or <see langword="null"/> to fall back.
    /// The host never asks twice for the same presentation, and sets the view's <see cref="FrameworkElement.DataContext"/>
    /// to the content unless the view set one itself.
    /// </summary>
    /// <param name="entry">The entry to present.</param>
    /// <returns>A new view, or <see langword="null"/>.</returns>
    FrameworkElement? ResolveView(INavigationEntry entry);

    /// <summary>Creates a new view for plain content in a <see cref="ViewHost"/>, without a navigation entry.</summary>
    /// <remarks>
    /// The default returns null, so existing entry-specific locators fall back to implicit templates.
    /// ViewHost borrows content and never creates an entry, scope or retirement token.
    /// </remarks>
    FrameworkElement? ResolveView(object content, IServiceProvider services) => null;
}
