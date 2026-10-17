using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace Runic.Platform.Windows;

// Generic credentials in the user's Credential Manager vault. The target names the service and the account; the
// user name is the account, so the entry reads naturally in Control Panel's Credential Manager.
[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class WindowsSecretStore : IDesktopSecretStore
{
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorBadUserName = 2202;

    public ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        fixed (char* target = Target(service, account))
        {
            CREDENTIALW* credential;
            if (!PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, 0, &credential))
            {
                int error = Marshal.GetLastPInvokeError();
                return Result<string?>(error == ErrorNotFound ? new PlatformResult<string?>.Success(null) : Failure<string?>(error));
            }
            try
            {
                var stored = new ReadOnlySpan<byte>(credential->CredentialBlob, checked((int)credential->CredentialBlobSize));
                return Result<string?>(SecretStoreValidation.DecodeSecret(stored) is { } secret
                    ? new PlatformResult<string?>.Success(secret)
                    : new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData));
            }
            finally { PInvoke.CredFree(credential); }
        }
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        byte[]? encoded = SecretStoreValidation.EncodeSecret(secret);
        if (encoded is null) return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixed (char* target = Target(service, account))
            fixed (char* user = account)
            fixed (byte* blob = encoded)
            {
                var credential = new CREDENTIALW
                {
                    Type = CRED_TYPE.CRED_TYPE_GENERIC,
                    TargetName = target,
                    UserName = user,
                    CredentialBlob = blob,
                    CredentialBlobSize = (uint)encoded.Length,
                    // This user on this computer, kept across logons; not roamed with an enterprise profile.
                    Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
                };
                return PInvoke.CredWrite(&credential, 0)
                    ? Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Success(default))
                    : Result(Failure<PlatformUnit>(Marshal.GetLastPInvokeError()));
            }
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        fixed (char* target = Target(service, account))
        {
            if (PInvoke.CredDelete(target, CRED_TYPE.CRED_TYPE_GENERIC, 0)) return Result<bool>(new PlatformResult<bool>.Success(true));
            int error = Marshal.GetLastPInvokeError();
            return Result<bool>(error == ErrorNotFound ? new PlatformResult<bool>.Success(false) : Failure<bool>(error));
        }
    }

    // "service/account", with '%' and '/' escaped in the service so that no two pairs share a target.
    internal static string Target(string service, string account) =>
        service.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal) + "/" + account;

    internal static PlatformResult<T> Failure<T>(int error) => error switch
    {
        // No logon session holds a vault, as for a service or a network logon.
        ErrorNoSuchLogonSession => new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        ErrorAccessDenied => new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromWin32Error(error)),
        ErrorCancelled => new PlatformResult<T>.Failed(PlatformFailureCode.UserDismissed, PlatformDiagnostic.FromWin32Error(error)),
        ErrorInvalidParameter or ErrorBadUserName => new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData, PlatformDiagnostic.FromWin32Error(error)),
        _ => new PlatformResult<T>.Failed(PlatformFailureCode.IoError, PlatformDiagnostic.FromWin32Error(error)),
    };

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);
}
