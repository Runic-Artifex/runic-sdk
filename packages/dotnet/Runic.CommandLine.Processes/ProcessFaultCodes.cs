namespace Runic.CommandLine.Processes;

/// <summary>Defines stable process fault identities in the reserved CLI range.</summary>
public static class ProcessFaultCodes
{
    /// <summary>The executable policy rejected an executable identity.</summary>
    public const string ExecutableRejected = "RCLI6001";

    /// <summary>The executable policy rejected a working directory.</summary>
    public const string WorkingDirectoryRejected = "RCLI6002";

    /// <summary>The executable policy returned an invalid decision.</summary>
    public const string InvalidPolicyDecision = "RCLI6003";

    /// <summary>The executable policy could not evaluate a request safely.</summary>
    public const string PolicyEvaluationFailed = "RCLI6004";

    /// <summary>The operating system could not start the child.</summary>
    public const string StartFailed = "RCLI6005";

    /// <summary>The started process lifecycle could not be observed safely.</summary>
    public const string ExecutionFailed = "RCLI6006";
}
