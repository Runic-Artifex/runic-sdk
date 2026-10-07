using System.Diagnostics.CodeAnalysis;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>Lifecycle counts for tests of the experimental navigator.</summary>
[Experimental(RunicNavigator.DiagnosticId)]
public static class RunicNavigationTestExtensions
{
    /// <summary>
    /// Gets the entries that have not finished retiring: pending, committed, and removed entries whose
    /// cleanup is still running. It is zero after <see cref="RunicNavigator.DisposeAsync"/>.
    /// </summary>
    public static int UnretiredEntryCount(this RunicNavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        return navigator.TrackedEntryCount;
    }

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
