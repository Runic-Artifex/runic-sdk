namespace Runic.Application.CsWebUi;

// WebUI owns native process-global state. Its present host contract permits
// only one host-start attempt in a process, including after startup failure.
// Both old and experimental hosts must therefore use this one claim.
internal static class CsWebUiNativeRuntimeClaim
{
    private static int _claimed;

    internal static void Claim()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("A CS-WebUI application host already owns this process's native runtime. Start another application in a separate process.");
    }
}
