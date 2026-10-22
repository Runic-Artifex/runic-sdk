namespace Runic.Platform;

/// <summary>
/// Secrets, such as access tokens, kept by the user's credential store: the Secret Service on Linux, the Keychain on
/// macOS and Credential Manager on Windows. Each secret is identified by a service and an account.
/// </summary>
/// <remarks>
/// <para>
/// Services and accounts are application-chosen names, such as <c>com.example.Mail</c> and <c>user@example.com</c>,
/// each 1 to 256 characters without NUL. Secrets are text, stored as UTF-8, of at most 2560 bytes on every platform,
/// the Credential Manager limit. Store a longer secret elsewhere, encrypted with a key kept here.
/// </para>
/// <para>
/// Outcomes common to every platform: no reachable store is
/// <see cref="PlatformResult{T}.Unavailable"/> with <see cref="PlatformUnavailableReason.BackendUnavailable"/>; a
/// locked store that cannot ask the user to unlock it is <see cref="PlatformFailureCode.Locked"/>; a dismissed unlock
/// or access prompt is <see cref="PlatformFailureCode.UserDismissed"/>; access refused by the store is
/// <see cref="PlatformFailureCode.PermissionDenied"/>. A request may wait while the store asks the user; cancellation
/// dismisses that prompt.
/// </para>
/// </remarks>
public interface IDesktopSecretStore
{
    /// <summary>Reads the secret stored for <paramref name="service"/> and <paramref name="account"/>.</summary>
    /// <returns>The secret, or a successful <see langword="null"/> when none is stored.</returns>
    /// <remarks>A stored value that is not UTF-8 text is <see cref="PlatformFailureCode.InvalidData"/>.</remarks>
    ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default);

    /// <summary>Stores <paramref name="secret"/> for <paramref name="service"/> and <paramref name="account"/>, replacing an earlier one.</summary>
    /// <remarks>A secret longer than 2560 UTF-8 bytes is <see cref="PlatformFailureCode.TooLarge"/>, and nothing is stored.</remarks>
    ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default);

    /// <summary>Removes the secret stored for <paramref name="service"/> and <paramref name="account"/>.</summary>
    /// <returns>Whether a secret was removed; <see langword="false"/> when none was stored.</returns>
    ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default);
}

/// <summary>Secret storage for hosts and platforms that cannot provide it.</summary>
public static class DesktopSecretStore
{
    /// <summary>Creates a secret store that reports every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Nothing throws except for a missing or invalid argument and caller cancellation.</remarks>
    public static IDesktopSecretStore Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailableSecretStore(reason);
    }

    private sealed class UnavailableSecretStore(PlatformUnavailableReason reason) : IDesktopSecretStore, IDesktopServiceStatus
    {
        public CapabilityStatus Status { get; } = DesktopServiceStatus.Unavailable(reason);
        public ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default)
        {
            SecretNames.Validate(service, account);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<string?>>(new PlatformResult<string?>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
        {
            SecretNames.Validate(service, account);
            ArgumentNullException.ThrowIfNull(secret);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
        {
            SecretNames.Validate(service, account);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Unavailable(reason));
        }
    }
}

// The same checks as Runic.Platform.Runtime.SecretStoreValidation, which providers use; this assembly has no reference to it.
internal static class SecretNames
{
    internal static void Validate(string service, string account)
    {
        Check(service, nameof(service));
        Check(account, nameof(account));
    }

    private static void Check(string value, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(value, name);
        if (value.Length > 256 || value.Contains('\0')
            || System.Text.Unicode.Utf8.FromUtf16(value, stackalloc byte[1024], out _, out _, replaceInvalidSequences: false)
                != System.Buffers.OperationStatus.Done)
            throw new ArgumentException("Use 1 to 256 characters of valid Unicode text without NUL.", name);
    }
}
