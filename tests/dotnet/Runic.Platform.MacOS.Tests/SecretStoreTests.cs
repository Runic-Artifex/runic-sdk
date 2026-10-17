using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

internal sealed class SecretStoreTests
{
    [Test, DisplayName("secret store: Keychain statuses map to typed outcomes")]
    public void Statuses()
    {
#pragma warning disable CA1416 // Failure is pure; nothing here calls native code.
        Check(MacSecretStore.Failure<bool>(MacSecretStore.InteractionNotAllowed) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.Locked, Diagnostic: { Domain: "OSStatus", Code: -25308 } }, "locked");
        Check(MacSecretStore.Failure<bool>(MacSecretStore.UserCanceled) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.UserDismissed }, "cancelled");
        Check(MacSecretStore.Failure<bool>(MacSecretStore.AuthFailed) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.PermissionDenied }, "denied");
        Check(MacSecretStore.Failure<bool>(MacSecretStore.NoSuchKeychain) is PlatformResult<bool>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "no keychain");
        Check(MacSecretStore.Failure<bool>(MacSecretStore.NoDefaultKeychain) is PlatformResult<bool>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "no default keychain");
        Check(MacSecretStore.Failure<bool>(-50) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Code: -50 }, "other statuses");
#pragma warning restore CA1416
    }

    // The runner's login keychain, under a service unique to this run, removed afterwards.
    [Test, Explicit, Category("NativeSecrets"), DisplayName("secret store: the login keychain stores, reads, replaces and deletes a generic password")]
    public async Task KeychainAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The Keychain requires macOS.");
        string service = "org.runic.tests." + Guid.NewGuid().ToString("N");
        const string account = "user@example.com";
        var store = MacOSPlatformProvider.CreateSecretStore();
        try
        {
            var first = await store.GetAsync(service, account);
            Console.WriteLine($"Keychain read before storing: {first}");
            Check(first is PlatformResult<string?>.Success { Value: null }, "nothing stored yet");
            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: false }, "nothing to delete");
            var stored = await store.SetAsync(service, account, "first-tøken");
            Console.WriteLine($"Keychain store: {stored}");
            Check(stored is PlatformResult<PlatformUnit>.Success, "stored");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: "first-tøken" }, "read back");
            Check(await store.GetAsync(service, "other") is PlatformResult<string?>.Success { Value: null }, "accounts are distinct");
            Check(await store.SetAsync(service, account, "second") is PlatformResult<PlatformUnit>.Success, "replaced");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: "second" }, "the replacement is read");
            Check(await store.SetAsync(service, account, new string('x', 2561)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.TooLarge }, "too large");
            Check(await store.SetAsync(service, account, "") is PlatformResult<PlatformUnit>.Success, "an empty secret");
            var empty = await store.GetAsync(service, account);
            Check(empty is PlatformResult<string?>.Success { Value: "" }, $"an empty secret is not missing: {empty}");
            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: true }, "deleted");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: null }, "gone");
            Console.WriteLine("PASS macOS Keychain: stored, read, replaced, size limit, empty secret, deleted.");
        }
        finally { await store.DeleteAsync(service, account); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Secret store check failed: " + message);
    }
}
