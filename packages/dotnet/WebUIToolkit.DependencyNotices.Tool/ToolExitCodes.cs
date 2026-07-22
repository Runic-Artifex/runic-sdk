namespace WebUIToolkit.DependencyNotices.Tool;

public static class ToolExitCodes
{
    public const int Success = 0;
    public const int UnexpectedFailure = 1;
    public const int InvalidCommandOrConfiguration = 2;
    public const int InventoryOrEvidenceIncomplete = 3;
    public const int PolicyRejected = 4;
    public const int OutputDrift = 5;
    public const int SbomMismatch = 6;
    public const int AcquisitionOrNetworkFailure = 7;
}
