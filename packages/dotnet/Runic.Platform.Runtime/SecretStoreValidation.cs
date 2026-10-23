using System.Buffers;
using System.Text.Unicode;

namespace Runic.Platform.Runtime;

/// <summary>Shared argument checks and encoding for <see cref="IDesktopSecretStore"/> providers.</summary>
internal static class SecretStoreValidation
{
    /// <summary>The largest secret, in UTF-8 bytes, that every provider stores: the Credential Manager limit.</summary>
    public const int MaximumSecretBytes = 2560;

    /// <summary>The largest binary secret, in bytes, that every provider stores (<see cref="DesktopSecretStore.MaximumBinarySecretBytes"/>).</summary>
    public const int MaximumBinarySecretBytes = DesktopSecretStore.MaximumBinarySecretBytes;

    /// <summary>Validates a service and an account, as <see cref="Validate"/>, and the size of a binary secret.</summary>
    /// <returns>Whether <paramref name="secret"/> fits <see cref="MaximumBinarySecretBytes"/>; a provider reports
    /// <see cref="PlatformFailureCode.TooLarge"/> otherwise.</returns>
    public static bool ValidateBinary(string service, string account, ReadOnlySpan<byte> secret)
    {
        Validate(service, account);
        return secret.Length <= MaximumBinarySecretBytes;
    }

    /// <summary>Validates a service and an account before touching native state.</summary>
    /// <exception cref="ArgumentException">A name is empty, longer than 256 characters, contains NUL or is not valid Unicode text.</exception>
    public static void Validate(string service, string account)
    {
        Check(service, nameof(service));
        Check(account, nameof(account));
    }

    /// <summary>Decodes a stored secret.</summary>
    /// <returns>The secret, or <see langword="null"/> when the stored bytes are not UTF-8 text.</returns>
    public static string? DecodeSecret(ReadOnlySpan<byte> stored)
    {
        if (!Utf8.IsValid(stored)) return null;
        return System.Text.Encoding.UTF8.GetString(stored);
    }

    /// <summary>The text result of a binary read: <see cref="PlatformFailureCode.InvalidData"/> when the stored bytes are
    /// not UTF-8. Clears the bytes it read.</summary>
    public static PlatformResult<string?> DecodeResult(PlatformResult<byte[]?> read)
    {
        switch (read)
        {
            case PlatformResult<byte[]?>.Success { Value: null }: return new PlatformResult<string?>.Success(null);
            case PlatformResult<byte[]?>.Success { Value: { } stored }:
                try
                {
                    return DecodeSecret(stored) is { } secret
                        ? new PlatformResult<string?>.Success(secret)
                        : new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(stored); }
            case PlatformResult<byte[]?>.Failed failed: return new PlatformResult<string?>.Failed(failed.Code, failed.Diagnostic);
            case PlatformResult<byte[]?>.Unavailable unavailable: return new PlatformResult<string?>.Unavailable(unavailable.Reason);
            default: throw new ArgumentOutOfRangeException(nameof(read));
        }
    }

    private static void Check(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (value.Length > 256 || value.Contains('\0')
            || Utf8.FromUtf16(value, stackalloc byte[1024], out _, out _, replaceInvalidSequences: false) != OperationStatus.Done)
            throw new ArgumentException("Use 1 to 256 characters of valid Unicode text without NUL.", name);
    }
}
