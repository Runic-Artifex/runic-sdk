namespace Runic.Platform;

/// <summary>
/// Secrets, such as access tokens, kept by the user's credential store: the Secret Service on Linux, the Keychain on
/// macOS and Credential Manager on Windows. Each secret is identified by a service and an account.
/// </summary>
/// <remarks>
/// <para>
/// Services and accounts are application-chosen names, such as <c>com.example.Mail</c> and <c>user@example.com</c>,
/// each 1 to 256 characters without NUL. Text secrets are stored as UTF-8, of at most 2560 bytes on every platform,
/// the Credential Manager limit. Binary secrets (<see cref="GetBytesAsync"/>, <see cref="SetBytesAsync"/>) are at most
/// <see cref="DesktopSecretStore.MaximumBinarySecretBytes"/> bytes on every platform. One item exists per service and
/// account in either form: <see cref="GetAsync"/> reads a binary item that is UTF-8 text, and
/// <see cref="GetBytesAsync"/> reads a text item as its UTF-8 bytes.
/// </para>
/// <para>
/// <see cref="DesktopSecretStoreOptions"/> chooses non-interactive requests, a required encrypted transfer on Linux and
/// the macOS data-protection keychain; the providers' <c>CreateSecretStore(DesktopSecretStoreOptions)</c> overloads
/// take them.
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

    /// <summary>Reads the secret stored for <paramref name="service"/> and <paramref name="account"/> as bytes.</summary>
    /// <returns>The secret, or a successful <see langword="null"/> when none is stored. A text secret is its UTF-8 bytes.</returns>
    /// <remarks>
    /// The returned array belongs to the caller, which should clear it after use; the store clears its own buffers. The
    /// default implementation checks the arguments and returns <see cref="PlatformResult{T}.Unavailable"/> with
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </remarks>
    ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<byte[]?>>(new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }

    /// <summary>Stores the bytes of <paramref name="secret"/> for <paramref name="service"/> and <paramref name="account"/>,
    /// replacing an earlier secret in either form.</summary>
    /// <remarks>
    /// A secret longer than <see cref="DesktopSecretStore.MaximumBinarySecretBytes"/> bytes is
    /// <see cref="PlatformFailureCode.TooLarge"/>, and nothing is stored. The store copies the bytes and clears its copies;
    /// the caller's memory is not changed. The default implementation checks the arguments and returns
    /// <see cref="PlatformResult{T}.Unavailable"/> with <see cref="PlatformUnavailableReason.BackendUnavailable"/>.
    /// </remarks>
    ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default)
    {
        SecretNames.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable));
    }
}

/// <summary>Store-wide choices for an <see cref="IDesktopSecretStore"/>.</summary>
/// <remarks>A store keeps no native resources between requests, so an application creates one store per set of options.
/// Every option keeps today's behaviour at its default.</remarks>
public sealed record DesktopSecretStoreOptions
{
    /// <summary>Whether a secret must cross process boundaries encrypted.</summary>
    /// <remarks>
    /// On Linux the Secret Service session must be <c>dh-ietf1024-sha256-aes128-cbc-pkcs7</c>: a service without it
    /// fails requests with <see cref="PlatformFailureCode.Other"/> and the
    /// <c>secret-service-encryption-required</c> diagnostic instead of using a <c>plain</c> session, which only
    /// processes of the same user can observe. Set it when the application knows its users' providers support the
    /// algorithm, as GNOME Keyring and KeePassXC do. No effect on Windows and macOS, whose stores transfer nothing in
    /// the clear.
    /// </remarks>
    public bool RequireEncryption { get; init; }

    /// <summary>Whether a request may wait while the store asks the user to unlock it or to allow access. Defaults to
    /// <see langword="true"/>.</summary>
    /// <remarks>
    /// With <see langword="false"/>, a request that would need such a prompt fails with
    /// <see cref="PlatformFailureCode.Locked"/> instead, as a background refresh wants. On Linux the Secret Service
    /// prompt is dismissed unshown; the Secret portal has no such flag and may still ask once to unlock the login
    /// keyring. On macOS the keychain query does not allow authentication UI. Windows Credential Manager never prompts.
    /// </remarks>
    public bool AllowUserInteraction { get; init; } = true;

    /// <summary>Whether macOS stores the secrets in the data-protection keychain instead of the file-based login
    /// keychain.</summary>
    /// <remarks>
    /// The data-protection keychain needs a bundle signed with a team ID and the <c>keychain-access-groups</c>
    /// entitlement; without them a request fails with <see cref="PlatformFailureCode.PermissionDenied"/> and an
    /// <c>OSStatus -34018</c> diagnostic that names the entitlement. Items of the file-based keychain are not
    /// migrated: read them with a store without this option and write them with one that has it. No effect on Linux
    /// and Windows.
    /// </remarks>
    public bool UseDataProtectionKeychain { get; init; }
}

/// <summary>Secret storage for hosts and platforms that cannot provide it.</summary>
public static class DesktopSecretStore
{
    /// <summary>The largest binary secret, in bytes, that every provider stores: 64 KiB.</summary>
    /// <remarks>Text secrets stay limited to 2560 UTF-8 bytes. On Windows a binary secret above 2560 bytes is kept in a
    /// file protected with DPAPI for the user, referenced from its credential.</remarks>
    public const int MaximumBinarySecretBytes = 65_536;

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

        public ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default)
        {
            SecretNames.Validate(service, account);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<byte[]?>>(new PlatformResult<byte[]?>.Unavailable(reason));
        }

        public ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default)
        {
            SecretNames.Validate(service, account);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Unavailable(reason));
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
