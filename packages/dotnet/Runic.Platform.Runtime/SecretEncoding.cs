using System.Buffers;
using System.Text.Unicode;

namespace Runic.Platform.Runtime;

// Encodes secrets for Runic's IDesktopSecretStore providers. Internal: a mutable array of secret material is not a
// public helper signature (W319-003 rt-02). The Windows and macOS providers reach it through their existing
// InternalsVisibleTo; Runic.Platform.Linux.Portal compiles this file itself, so no new cross-package internals are needed.
internal static class SecretEncoding
{
    // Encodes a secret as UTF-8. Returns the encoded secret, which the caller clears after use, or null when it exceeds
    // SecretStoreValidation.MaximumSecretBytes. Throws ArgumentException when the secret is not valid Unicode text.
    internal static byte[]? Encode(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        // Three bytes per UTF-16 unit bound the encoded length; a longer string cannot fit.
        if (secret.Length > SecretStoreValidation.MaximumSecretBytes) return null;
        var buffer = new byte[secret.Length * 3];
        if (Utf8.FromUtf16(secret, buffer, out _, out int written, replaceInvalidSequences: false) != OperationStatus.Done)
        {
            Array.Clear(buffer);
            throw new ArgumentException("The secret is not valid Unicode text.", nameof(secret));
        }
        if (written > SecretStoreValidation.MaximumSecretBytes) { Array.Clear(buffer); return null; }
        var encoded = buffer.AsSpan(0, written).ToArray();
        Array.Clear(buffer);
        return encoded;
    }
}
