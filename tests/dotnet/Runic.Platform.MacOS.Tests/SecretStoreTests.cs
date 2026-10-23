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
        Check(MacSecretStore.Failure<bool>(-50) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.Other, Diagnostic.Code: -50 }, "other statuses");
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

    [Test, DisplayName("secret store: the missing-entitlement status names the entitlement")]
    public void MissingEntitlement()
    {
#pragma warning disable CA1416 // Failure is pure; nothing here calls native code.
        Check(MacSecretStore.Failure<bool>(MacSecretStore.MissingEntitlement) is PlatformResult<bool>.Failed
            { Code: PlatformFailureCode.PermissionDenied, Diagnostic: { Code: -34018, Message: { } message } } && message.Contains("keychain-access-groups", StringComparison.Ordinal),
            "errSecMissingEntitlement");
#pragma warning restore CA1416
    }

    // A temporary keychain file, never the user's: binary round trip, then locked without user interaction.
    [Test, Explicit, Category("NativeSecrets"), DisplayName("secret store: a temporary keychain stores bytes and reports Locked without user interaction")]
    public async Task TemporaryKeychainAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The Keychain requires macOS.");
        string directory = Path.Combine(Path.GetTempPath(), "runic-keychain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string keychain = Path.Combine(directory, "test.keychain-db");
        string password = Guid.NewGuid().ToString("N");
        string searchList = Security("list-keychains", "-d", "user");
        Security("create-keychain", "-p", password, keychain);
        try
        {
            // create-keychain may add the keychain to the user's search list; restore the list now.
            RestoreSearchList(searchList);
            Security("unlock-keychain", "-p", password, keychain);
            const string service = "org.runic.tests.binary", account = "user@example.com";
            var store = new MacSecretStore(null, keychain);
            byte[] secret = new byte[DesktopSecretStore.MaximumBinarySecretBytes];
            for (int i = 0; i < secret.Length; i++) secret[i] = (byte)(i * 7 + 0x80);
            var stored = await store.SetBytesAsync(service, account, secret);
            Check(stored is PlatformResult<PlatformUnit>.Success, $"64 KiB stored: {stored}");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var read } && read!.SequenceEqual(secret), "bytes read back");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData }, "binary bytes are not text");
            Check(await store.SetAsync(service, account, "tøken") is PlatformResult<PlatformUnit>.Success, "text replaces bytes");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var utf8 } && utf8!.SequenceEqual("tøken"u8.ToArray()),
                "text reads as UTF-8 bytes");
            Check(await store.SetBytesAsync(service, account, new byte[DesktopSecretStore.MaximumBinarySecretBytes + 1]) is PlatformResult<PlatformUnit>.Failed
                { Code: PlatformFailureCode.TooLarge }, "above 64 KiB");

            Security("lock-keychain", keychain);
            var quiet = new MacSecretStore(new DesktopSecretStoreOptions { AllowUserInteraction = false }, keychain);
            var locked = await quiet.GetAsync(service, account);
            Console.WriteLine($"Locked keychain without interaction: read {locked}");
            Check(locked is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Locked }, "a read reports Locked");
            Check(await quiet.SetBytesAsync(service, account, secret) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.Locked }, "a write reports Locked");
            Check(await quiet.DeleteAsync(service, account) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.Locked }, "a delete reports Locked");

            Security("unlock-keychain", "-p", password, keychain);
            Check(await quiet.GetAsync(service, account) is PlatformResult<string?>.Success { Value: "tøken" }, "unlocked, the same store reads without asking");
            Check(await quiet.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: true }, "deleted");
            Console.WriteLine("PASS macOS temporary keychain: 64 KiB bytes, text/bytes cross-reads, Locked without user interaction.");
        }
        finally
        {
            try { Security("delete-keychain", keychain); } catch (InvalidOperationException) { }
            RestoreSearchList(searchList);
            Directory.Delete(directory, recursive: true);
        }
    }

    // An unsigned test host has no keychain-access-groups entitlement: the data-protection keychain refuses it.
    [Test, Explicit, Category("NativeSecrets"), DisplayName("secret store: the data-protection keychain without the entitlement is PermissionDenied")]
    public async Task DataProtectionKeychainAsync()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("The Keychain requires macOS.");
        string service = "org.runic.tests." + Guid.NewGuid().ToString("N");
        var store = MacOSPlatformProvider.CreateSecretStore(new DesktopSecretStoreOptions { UseDataProtectionKeychain = true, AllowUserInteraction = false });
        var read = await store.GetAsync(service, "user@example.com");
        Console.WriteLine($"Data-protection keychain from the test host: {read}");
        Check(read is PlatformResult<string?>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Code: MacSecretStore.MissingEntitlement },
            $"errSecMissingEntitlement: {read}");
        Check(await store.SetAsync(service, "user@example.com", "x") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
            "nothing is stored");
        Console.WriteLine("PASS macOS data-protection keychain: PermissionDenied (-34018) without the entitlement.");
    }

    private static string Security(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("/usr/bin/security") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        // The arguments may hold the temporary keychain's password; report the command only.
        if (process.ExitCode != 0) throw new InvalidOperationException($"security {arguments[0]} exited with {process.ExitCode}.");
        return output;
    }

    private static void RestoreSearchList(string listing)
    {
        string[] keychains = listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Trim('"')).Where(line => line.Length > 0).ToArray();
        if (keychains.Length > 0) Security(["list-keychains", "-d", "user", "-s", .. keychains]);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Secret store check failed: " + message);
    }
}
