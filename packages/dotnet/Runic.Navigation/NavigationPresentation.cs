namespace Runic.Navigation;

/// <summary>
/// A presentation of a navigator's content, attached with <see cref="RunicNavigator.AttachPresentation"/>.
/// Presentation integrations, such as the Runic Views runtime, implement it; applications do not.
/// </summary>
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

    /// <summary>
    /// Releases everything the presentation holds for retiring owned content, like <see cref="Forget"/>, and returns a
    /// task that completes when work the presentation keeps for the content has ended, such as Runic Views operations
    /// declared with <c>RunicOperationRetirement.Cancel</c> or <c>Drain</c>. The navigator calls it instead of
    /// <see cref="Forget"/>; the default implementation calls <see cref="Forget"/> and completes at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The navigator disposes the content, its entry scope and its model-context lease only after the task completes.
    /// It does not wait for the task to finish the retirement: a Back or another transition that retires the entry
    /// commits and completes, and <see cref="RunicNavigator.IsBusy"/> and <see cref="RunicNavigator.WhenIdleAsync"/>
    /// do not wait for it either. <see cref="RunicNavigator.DisposeAsync"/> waits for it up to
    /// <see cref="RunicNavigatorOptions.CloseTimeout"/>, then logs event 1077 and returns while the content is still
    /// disposed once the task completes.
    /// </para>
    /// <para>
    /// The same rules as for <see cref="Forget"/> apply: it should not throw, and a failure, whether thrown or reported by
    /// the task, is logged (event 1064, step <c>Forget</c>) and retirement continues.
    /// </para>
    /// </remarks>
    /// <param name="content">The retiring content.</param>
    /// <returns>A task that completes when the content's work held by this presentation has ended.</returns>
    ValueTask ForgetAsync(object content)
    {
        Forget(content);
        return ValueTask.CompletedTask;
    }
}
