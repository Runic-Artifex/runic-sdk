namespace Runic.Platform.Runtime;

// Native detail for a managed file-system exception, for the diagnostics of picker and commit failures.
internal static class FileErrors
{
    // .NET reports the HRESULT of a Win32 error on Windows and the errno of an IOException on Linux and macOS.
    internal static PlatformDiagnostic? Diagnose(Exception error) => error.HResult switch
    {
        < 0 when OperatingSystem.IsWindows() => PlatformDiagnostic.FromHResult(error.HResult),
        > 0 and < 4096 when !OperatingSystem.IsWindows() && error is IOException => PlatformDiagnostic.FromErrno(error.HResult),
        _ => null,
    };
}
