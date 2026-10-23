using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Runic.Platform.Runtime;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;
using Windows.Win32.Security.Cryptography;

namespace Runic.Platform.Windows;

// Generic credentials in the user's Credential Manager vault. The target names the service and the account; the
// user name is the account, so the entry reads naturally in Control Panel's Credential Manager.
//
// A credential blob holds at most 2560 bytes. A larger binary secret, or a small one that starts like a reference, is
// kept in a file under %LOCALAPPDATA%\Runic\Secrets, protected with DPAPI for this user with the target as entropy;
// the credential then holds a reference: the marker, the file's random id and the SHA-256 of the protected file. The
// hash covers the protected bytes, not the secret, so the credential reveals nothing about the secret, and a replaced
// or damaged file is InvalidData before anything is decrypted.
internal sealed class WindowsSecretStore : IDesktopSecretStore
{
    internal const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;
    private const int ErrorAccessDenied = 5;
    private const int ErrorCancelled = 1223;
    private const int ErrorInvalidParameter = 87;
    private const int ErrorBadUserName = 2202;

    // 0xFF never occurs in UTF-8, so no text secret starts with the marker.
    private static ReadOnlySpan<byte> Marker => [0xFF, (byte)'r', (byte)'u', (byte)'n', (byte)'i', (byte)'c', (byte)'-', (byte)'s', (byte)'e',
        (byte)'c', (byte)'r', (byte)'e', (byte)'t', (byte)'-', (byte)'f', (byte)'i', (byte)'l', (byte)'e', 1];
    private const int IdBytes = 16;
    internal static int ReferenceLength => Marker.Length + IdBytes + SHA256.HashSizeInBytes;

    private readonly ICredentialVault _vault;
    private readonly ISecretFileProtector _protector;
    private readonly string _directory;

    [SupportedOSPlatform("windows8.0")]
    internal WindowsSecretStore()
        : this(new NativeCredentialVault(), new DpapiProtector(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Runic", "Secrets"))
    {
    }

    internal WindowsSecretStore(ICredentialVault vault, ISecretFileProtector protector, string directory)
    {
        _vault = vault;
        _protector = protector;
        _directory = directory;
    }

    public ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return Result(SecretStoreValidation.DecodeResult(Read(Target(service, account))));
    }

    public ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return Result(Read(Target(service, account)));
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        byte[]? encoded = SecretEncoding.Encode(secret);
        if (encoded is null) return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Result(Write(Target(service, account), account, encoded));
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        if (!SecretStoreValidation.ValidateBinary(service, account, secret.Span))
            return Result<PlatformUnit>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge));
        cancellationToken.ThrowIfCancellationRequested();
        return Result(Write(Target(service, account), account, secret.Span));
    }

    public ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        string target = Target(service, account);
        string? file = null;
        int error = _vault.Read(target, out byte[]? blob);
        if (error == 0 && blob is not null)
        {
            if (IsReference(blob) && blob.Length == ReferenceLength) file = FilePath(blob);
            CryptographicOperations.ZeroMemory(blob);
        }
        error = _vault.Delete(target);
        if (error == ErrorNotFound) return Result<bool>(new PlatformResult<bool>.Success(false));
        if (error != 0) return Result(Failure<bool>(error));
        if (file is not null) TryDelete(file);
        return Result<bool>(new PlatformResult<bool>.Success(true));
    }

    // "service/account", with '%' and '/' escaped in the service so that no two pairs share a target.
    internal static string Target(string service, string account) =>
        service.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal) + "/" + account;

    internal static bool IsReference(ReadOnlySpan<byte> blob) => blob.StartsWith(Marker);

    private PlatformResult<byte[]?> Read(string target)
    {
        int error = _vault.Read(target, out byte[]? blob);
        if (error == ErrorNotFound) return new PlatformResult<byte[]?>.Success(null);
        if (error != 0) return Failure<byte[]?>(error);
        blob ??= [];
        if (!IsReference(blob)) return new PlatformResult<byte[]?>.Success(blob);
        try
        {
            if (blob.Length != ReferenceLength) return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
            byte[] protectedBytes;
            try { protectedBytes = File.ReadAllBytes(FilePath(blob)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData, Diagnostic(exception));
            }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(protectedBytes), blob.AsSpan(Marker.Length + IdBytes)))
                return new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData);
            int unprotectError = _protector.Unprotect(protectedBytes, Entropy(target), out byte[]? secret);
            return unprotectError == 0 && secret is not null
                ? new PlatformResult<byte[]?>.Success(secret)
                : new PlatformResult<byte[]?>.Failed(PlatformFailureCode.InvalidData, PlatformDiagnostic.FromWin32Error(unprotectError));
        }
        finally { CryptographicOperations.ZeroMemory(blob); }
    }

    private PlatformResult<PlatformUnit> Write(string target, string account, ReadOnlySpan<byte> secret)
    {
        // The file an earlier secret kept, removed once the credential no longer refers to it.
        string? previous = null;
        if (_vault.Read(target, out byte[]? old) == 0 && old is not null)
        {
            if (IsReference(old) && old.Length == ReferenceLength) previous = FilePath(old);
            CryptographicOperations.ZeroMemory(old);
        }

        int error;
        string? written = null;
        if (secret.Length <= SecretStoreValidation.MaximumSecretBytes && !IsReference(secret))
        {
            error = _vault.Write(target, account, secret);
        }
        else
        {
            int protectError = _protector.Protect(secret, Entropy(target), out byte[]? protectedBytes);
            if (protectError != 0 || protectedBytes is null)
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromWin32Error(protectError));
            byte[] reference = new byte[ReferenceLength];
            Marker.CopyTo(reference);
            RandomNumberGenerator.Fill(reference.AsSpan(Marker.Length, IdBytes));
            SHA256.HashData(protectedBytes, reference.AsSpan(Marker.Length + IdBytes));
            written = FilePath(reference);
            try { WriteFile(written, protectedBytes); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new PlatformResult<PlatformUnit>.Failed(
                    exception is UnauthorizedAccessException ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.Other, Diagnostic(exception));
            }
            error = _vault.Write(target, account, reference);
        }

        if (error != 0)
        {
            if (written is not null) TryDelete(written);
            return Failure<PlatformUnit>(error);
        }
        if (previous is not null && previous != written) TryDelete(previous);
        return new PlatformResult<PlatformUnit>.Success(default);
    }

    // A temporary file in the same directory, renamed over the target, so a reader never sees a partial file.
    private void WriteFile(string path, byte[] contents)
    {
        Directory.CreateDirectory(_directory);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private string FilePath(ReadOnlySpan<byte> reference) =>
        Path.Combine(_directory, Convert.ToHexStringLower(reference.Slice(Marker.Length, IdBytes)) + ".bin");

    private static byte[] Entropy(string target) => System.Text.Encoding.UTF8.GetBytes(target);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    // The HRESULT only: an exception message would carry the path.
    private static PlatformDiagnostic Diagnostic(Exception exception) => new("HRESULT", unchecked((uint)exception.HResult));

    internal static PlatformResult<T> Failure<T>(int error) => error switch
    {
        // No logon session holds a vault, as for a service or a network logon.
        ErrorNoSuchLogonSession => new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        ErrorAccessDenied => new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, PlatformDiagnostic.FromWin32Error(error)),
        ErrorCancelled => new PlatformResult<T>.Failed(PlatformFailureCode.UserDismissed, PlatformDiagnostic.FromWin32Error(error)),
        ErrorInvalidParameter or ErrorBadUserName => new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData, PlatformDiagnostic.FromWin32Error(error)),
        _ => new PlatformResult<T>.Failed(PlatformFailureCode.Other, PlatformDiagnostic.FromWin32Error(error)),
    };

    private static ValueTask<PlatformResult<T>> Result<T>(PlatformResult<T> result) => ValueTask.FromResult(result);
}

// The vault's generic credentials by target; the error is 0 or a Win32 code.
internal interface ICredentialVault
{
    int Read(string target, out byte[]? blob);
    int Write(string target, string userName, ReadOnlySpan<byte> blob);
    int Delete(string target);
}

// Protects a secret file's contents for the current user; the error is 0 or a Win32 code.
internal interface ISecretFileProtector
{
    int Protect(ReadOnlySpan<byte> data, byte[] entropy, out byte[]? protectedData);
    int Unprotect(ReadOnlySpan<byte> protectedData, byte[] entropy, out byte[]? data);
}

[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class NativeCredentialVault : ICredentialVault
{
    public int Read(string target, out byte[]? blob)
    {
        blob = null;
        fixed (char* name = target)
        {
            CREDENTIALW* credential;
            if (!PInvoke.CredRead(name, CRED_TYPE.CRED_TYPE_GENERIC, 0, &credential)) return Marshal.GetLastPInvokeError();
            try { blob = new ReadOnlySpan<byte>(credential->CredentialBlob, checked((int)credential->CredentialBlobSize)).ToArray(); }
            finally
            {
                // The vault's own copy of the secret.
                if (credential->CredentialBlob is not null) new Span<byte>(credential->CredentialBlob, (int)credential->CredentialBlobSize).Clear();
                PInvoke.CredFree(credential);
            }
            return 0;
        }
    }

    public int Write(string target, string userName, ReadOnlySpan<byte> blob)
    {
        fixed (char* name = target)
        fixed (char* user = userName)
        fixed (byte* bytes = blob)
        {
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE.CRED_TYPE_GENERIC,
                TargetName = name,
                UserName = user,
                CredentialBlob = bytes,
                CredentialBlobSize = (uint)blob.Length,
                // This user on this computer, kept across logons; not roamed with an enterprise profile.
                Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
            };
            return PInvoke.CredWrite(&credential, 0) ? 0 : Marshal.GetLastPInvokeError();
        }
    }

    public int Delete(string target)
    {
        fixed (char* name = target)
            return PInvoke.CredDelete(name, CRED_TYPE.CRED_TYPE_GENERIC, 0) ? 0 : Marshal.GetLastPInvokeError();
    }
}

// CryptProtectData for the current user, never showing UI.
[SupportedOSPlatform("windows8.0")]
internal sealed unsafe class DpapiProtector : ISecretFileProtector
{
    private const uint UiForbidden = 0x1; // CRYPTPROTECT_UI_FORBIDDEN

    public int Protect(ReadOnlySpan<byte> data, byte[] entropy, out byte[]? protectedData) => Run(data, entropy, protect: true, out protectedData);

    public int Unprotect(ReadOnlySpan<byte> protectedData, byte[] entropy, out byte[]? data) => Run(protectedData, entropy, protect: false, out data);

    private static int Run(ReadOnlySpan<byte> input, byte[] entropy, bool protect, out byte[]? output)
    {
        output = null;
        fixed (byte* inputBytes = input)
        fixed (byte* entropyBytes = entropy)
        {
            var inBlob = new CRYPT_INTEGER_BLOB { cbData = (uint)input.Length, pbData = inputBytes };
            var entropyBlob = new CRYPT_INTEGER_BLOB { cbData = (uint)entropy.Length, pbData = entropyBytes };
            var outBlob = default(CRYPT_INTEGER_BLOB);
            bool ok = protect
                ? PInvoke.CryptProtectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob)
                : PInvoke.CryptUnprotectData(&inBlob, null, &entropyBlob, null, null, UiForbidden, &outBlob);
            if (!ok) return Marshal.GetLastPInvokeError() is var error and not 0 ? error : 13; // ERROR_INVALID_DATA
            try { output = new ReadOnlySpan<byte>(outBlob.pbData, checked((int)outBlob.cbData)).ToArray(); }
            finally
            {
                new Span<byte>(outBlob.pbData, (int)outBlob.cbData).Clear();
                PInvoke.LocalFree((HLOCAL)(nint)outBlob.pbData);
            }
            return 0;
        }
    }
}
