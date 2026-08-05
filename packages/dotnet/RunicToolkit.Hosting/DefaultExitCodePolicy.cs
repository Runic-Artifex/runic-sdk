using System;

namespace RunicToolkit.Hosting;

/// <summary>Maps framework failures to the stable process exit codes defined by the hosting contract.</summary>
public sealed class DefaultExitCodePolicy : IExitCodePolicy
{
    /// <inheritdoc />
    public int GetExitCode(ApplicationFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return failure.Category switch
        {
            ApplicationFailureCategory.Usage => 2,
            ApplicationFailureCategory.Configuration => 10,
            ApplicationFailureCategory.HostStartup => 11,
            ApplicationFailureCategory.FrontendAssets => 12,
            ApplicationFailureCategory.NativeRuntime => 13,
            ApplicationFailureCategory.Command => 20,
            ApplicationFailureCategory.UserInterface => 30,
            ApplicationFailureCategory.Shutdown => 40,
            ApplicationFailureCategory.Cancelled => 130,
            ApplicationFailureCategory.Unhandled => 1,
            _ => 1,
        };
    }
}
