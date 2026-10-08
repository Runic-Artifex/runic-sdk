using System.Diagnostics.CodeAnalysis;
using Runic.Application.Views;

namespace Runic.Application.Testing;

/// <summary>Lifecycle counts for tests of the experimental navigator.</summary>
/// <remarks>The navigator's own count is <see cref="RunicNavigator.UnretiredEntryCount"/>.</remarks>
[Experimental("RUNICNAV001")]
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
