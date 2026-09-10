using Windows.Win32.Foundation;
using Windows.Win32.System.Services;

namespace Runic.Platform.Administration.Windows.Services;

internal static class ServiceNative
{
    internal static string Text(PWSTR value) => value.ToString();
    internal static unsafe string Text(char* value) => value == null ? "" : new string(value);
    internal static ServiceStatus Snapshot(this SERVICE_STATUS_PROCESS value) =>
        new((ServiceState)value.dwCurrentState, value.dwProcessId, (uint)value.dwControlsAccepted,
            value.dwWin32ExitCode, value.dwServiceSpecificExitCode, value.dwCheckPoint, TimeSpan.FromMilliseconds(value.dwWaitHint));
}
