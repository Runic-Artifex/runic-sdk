using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

// Runs an operation under a network-only logon (the identity `runas /netonly` uses), for
// components such as GPMC that take no credential and use the calling thread's identity.
// Local access keeps the process identity; the password stays in memory.
internal static partial class NetworkLogon
{
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderWinNT50 = 3;

    internal static async Task RunAsync(NetworkCredential? credential, Func<Task> action)
    {
        if (credential is null) { await action(); return; }
        if (!LogonUser(credential.UserName, credential.Domain.Length == 0 ? null : credential.Domain, credential.Password,
                Logon32LogonNewCredentials, Logon32ProviderWinNT50, out var token))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "LogonUser for the network-only identity failed.");
        using (token) await WindowsIdentity.RunImpersonatedAsync(token, action);
    }

    [LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LogonUser(string userName, string? domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);
}
