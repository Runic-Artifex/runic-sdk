using System.Buffers;
using System.Text.Unicode;

namespace Runic.Platform.Runtime;

/// <summary>Shared argument checks and encoding for <see cref="IDesktopSecretStore"/> providers.</summary>
public static class SecretStoreValidation
{
    /// <summary>The largest secret, in UTF-8 bytes, that every provider stores: the Credential Manager limit.</summary>
    public const int MaximumSecretBytes = 2560;

    /// <summary>Validates a service and an account before touching native state.</summary>
    /// <exception cref="ArgumentException">A name is empty, longer than 256 characters, contains NUL or is not valid Unicode text.</exception>
    public static void Validate(string service, string account)
    {
        Check(service, nameof(service));
        Check(account, nameof(account));
    }

    /// <summary>Encodes a secret as UTF-8.</summary>
    /// <returns>The encoded secret, which the caller clears after use, or <see langword="null"/> when it exceeds <see cref="MaximumSecretBytes"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is not valid Unicode text.</exception>
    public static byte[]? EncodeSecret(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        // Three bytes per UTF-16 unit bound the encoded length; a longer string cannot fit.
        if (secret.Length > MaximumSecretBytes) return null;
        var buffer = new byte[secret.Length * 3];
        if (Utf8.FromUtf16(secret, buffer, out _, out int written, replaceInvalidSequences: false) != OperationStatus.Done)
        {
            Array.Clear(buffer);
            throw new ArgumentException("The secret is not valid Unicode text.", nameof(secret));
        }
        if (written > MaximumSecretBytes) { Array.Clear(buffer); return null; }
        var encoded = buffer.AsSpan(0, written).ToArray();
        Array.Clear(buffer);
        return encoded;
    }

    /// <summary>Decodes a stored secret.</summary>
    /// <returns>The secret, or <see langword="null"/> when the stored bytes are not UTF-8 text.</returns>
    public static string? DecodeSecret(ReadOnlySpan<byte> stored)
    {
        if (!Utf8.IsValid(stored)) return null;
        return System.Text.Encoding.UTF8.GetString(stored);
    }

    private static void Check(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (value.Length > 256 || value.Contains('\0')
            || Utf8.FromUtf16(value, stackalloc byte[1024], out _, out _, replaceInvalidSequences: false) != OperationStatus.Done)
            throw new ArgumentException("Use 1 to 256 characters of valid Unicode text without NUL.", name);
    }
}
