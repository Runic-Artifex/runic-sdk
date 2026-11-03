using Runic.Application.Views;
using Runic.Navigation;

namespace Runic.Application.Testing;

/// <summary>Lifecycle counts for tests of the navigator.</summary>
/// <remarks>The navigator's own count is <see cref="RunicNavigator.UnretiredEntryCount"/>.</remarks>
public static class RunicNavigationTestExtensions
{
    /// <summary>
    /// Gets the content models whose model-context leases the window session holds, one per attached
    /// content presentation. Retired owned navigation content no longer counts.
    /// </summary>
    public static int RetainedContentModelCount(this WindowContentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.RetainedContentModelLeaseCount;
    }
}

/// <summary>Saves and restores navigation paths in tests, as an application does across a restart.</summary>
public static class RunicNavigationPathTesting
{
    /// <summary>
    /// Saves <paramref name="navigator"/>'s path through its JSON form, as an application's store would keep it, so the
    /// restore in a new window scope reads what a restart reads.
    /// </summary>
    /// <param name="navigator">The navigator of the window before the restart.</param>
    /// <param name="routes">The route map, or <see langword="null"/> for the navigator's registered one.</param>
    public static string SavePath(this RunicNavigator navigator, INavigationRouteMap? routes = null)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        return navigator.GetPath(routes).ToJson();
    }

    /// <summary>
    /// Restores <paramref name="saved"/>, a path <see cref="SavePath"/> returned, into <paramref name="navigator"/>, and reads
    /// the path back: <see cref="RunicNavigationRestore.IsExact"/> tells whether the restored navigation is the saved one.
    /// </summary>
    /// <param name="navigator">The navigator of the window after the restart.</param>
    /// <param name="saved">The saved JSON.</param>
    /// <param name="routes">The route map, or <see langword="null"/> for the navigator's registered one.</param>
    /// <param name="cancellationToken">Cancels the restore.</param>
    public static async Task<RunicNavigationRestore> RestorePathAsync(this RunicNavigator navigator, string saved,
        INavigationRouteMap? routes = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(saved);
        var result = await navigator.RestoreAsync(NavigationPath.FromJson(saved), routes, cancellationToken).ConfigureAwait(false);
        await navigator.WhenIdleAsync(cancellationToken).ConfigureAwait(false);
        return new RunicNavigationRestore(result, saved, navigator.GetPath(routes).ToJson());
    }
}

/// <summary>The outcome of <see cref="RunicNavigationPathTesting.RestorePathAsync"/>.</summary>
/// <param name="Result">What the restore left out.</param>
/// <param name="Saved">The JSON that was restored.</param>
/// <param name="Restored">The JSON of the navigator's path after the restore.</param>
public sealed record RunicNavigationRestore(NavigationRestoreResult Result, string Saved, string Restored)
{
    /// <summary>Gets whether every part was restored and the navigator's path is the saved one.</summary>
    public bool IsExact => Result.IsComplete && string.Equals(Saved, Restored, StringComparison.Ordinal);
}
