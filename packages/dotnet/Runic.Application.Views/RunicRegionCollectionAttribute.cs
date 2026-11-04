namespace Runic.Application.Views;

/// <summary>
/// Presents a get-only <see cref="Runic.Navigation.NavigationSelection"/> property as a region collection
/// (Views protocol 4, feature <c>region-collections/1</c>): every region of the selection, in order, with a stable key
/// and its current content, and the selected index. The web view selects and closes regions by key; regions are added
/// by the ViewModel, with <see cref="Runic.Navigation.NavigationSelection.Add"/>.
/// </summary>
/// <remarks>
/// <para>
/// Without this attribute, a <see cref="Runic.Navigation.NavigationSelection"/> property is a selection slot whose
/// wire value is its <c>SelectedIndex</c>, and each region is a slot of its own. With it, the property's TypeScript type
/// is <c>RegionCollection&lt;R&gt;</c>, <c>{ regions: readonly { key: string; content: R | null }[]; selected: number | null }</c>,
/// where <c>R</c> is the union of the page references of the ViewModels that are a <see cref="ContentType"/>.
/// </para>
/// <para>
/// The generated client gets <c>select{Name}(key)</c> and, unless <see cref="PageCanClose"/> is <see langword="false"/>,
/// <c>close{Name}(key)</c>. They call <see cref="Runic.Navigation.NavigationSelection.SelectAsync"/> and
/// <see cref="Runic.Navigation.NavigationSelection.RemoveAsync"/>, so the departure guards of the region left or closed
/// decide, and they resolve with the request's <c>NavigationOutcome</c>.
/// </para>
/// </remarks>
/// <param name="contentType">
/// The interface or base class of the ViewModels that the regions present, as <c>TContent</c> of a
/// <c>NavigationRegion&lt;TContent&gt;</c> slot. It must not be <see cref="object"/>.
/// </param>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class RunicRegionCollectionAttribute(Type contentType) : Attribute
{
    /// <summary>The interface or base class of the ViewModels that the regions present.</summary>
    public Type ContentType { get; } = contentType ?? throw new ArgumentNullException(nameof(contentType));

    /// <summary>
    /// Whether the web view can close a region, which removes it from the selection after its departure guards agreed;
    /// <see langword="true"/> by default. Set it to <see langword="false"/> for tabs that only the ViewModel closes.
    /// </summary>
    public bool PageCanClose { get; set; } = true;
}
