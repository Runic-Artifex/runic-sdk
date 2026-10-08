using System.Diagnostics.CodeAnalysis;

namespace Runic.Navigation;

/// <summary>
/// A presentation of a navigator's content, attached with <see cref="RunicNavigator.AttachPresentation"/>.
/// Presentation integrations, such as the Runic Views runtime, implement it; applications do not.
/// </summary>
[Experimental(RunicNavigator.DiagnosticId)]
public interface INavigationPresentation
{
    /// <summary>
    /// Releases everything the presentation holds for retiring owned content. The navigator calls it once
    /// per retiring owned entry, outside model turns, after the entry's child regions are closed and before
    /// the content is disposed.
    /// </summary>
    /// <remarks>
    /// It should not throw; a failure is logged (event 1064, step <c>Forget</c>) and retirement continues.
    /// It must not await navigation of the same navigator.
    /// </remarks>
    /// <param name="content">The retiring content.</param>
    void Forget(object content);
}
