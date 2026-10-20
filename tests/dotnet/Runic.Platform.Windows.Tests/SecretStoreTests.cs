using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

internal sealed class SecretStoreTests
{
    // Pure target naming and error mapping; runs on every OS.
    [Test]
    public void TargetsAndErrors()
    {
#pragma warning disable CA1416 // Target and Failure are pure; nothing here calls native code.
        Check(WindowsSecretStore.Target("com.example.Mail", "me@example.com") == "com.example.Mail/me@example.com", "plain target");
        Check(WindowsSecretStore.Target("a/b", "c") != WindowsSecretStore.Target("a", "b/c"), "a slash in the service cannot shift into the account");
        Check(WindowsSecretStore.Target("a%2Fb", "c") != WindowsSecretStore.Target("a/b", "c"), "the escape character is escaped too");
        Check(WindowsSecretStore.Failure<bool>(1312) is PlatformResult<bool>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "no logon session");
        Check(WindowsSecretStore.Failure<bool>(5) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic: { Domain: "Win32", Code: 5 } }, "access denied");
        Check(WindowsSecretStore.Failure<bool>(1223) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.UserDismissed }, "cancelled");
        Check(WindowsSecretStore.Failure<bool>(1783) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.Other }, "other errors");
#pragma warning restore CA1416
    }

    // The user's real vault, under a service unique to this run, removed afterwards.
    [Test, Explicit, Category("NativeSecrets")]
    public async Task CredentialManagerAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("Credential Manager requires Windows.");
        string service = "org.runic.tests/" + Guid.NewGuid().ToString("N");
        const string account = "user@example.com";
        var store = WindowsPlatformProvider.CreateSecretStore();
        try
        {
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: null }, "nothing stored yet");
            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: false }, "nothing to delete");
            Check(await store.SetAsync(service, account, "first-tøken") is PlatformResult<PlatformUnit>.Success, "stored");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: "first-tøken" }, "read back");
            Check(await store.GetAsync(service, "other") is PlatformResult<string?>.Success { Value: null }, "accounts are distinct");
            string largest = new('x', 2560);
            Check(await store.SetAsync(service, account, largest) is PlatformResult<PlatformUnit>.Success, "2560 bytes fit");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: var read } && read == largest, "replaced");
            Check(await store.SetAsync(service, account, largest + "x") is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.TooLarge }, "2561 bytes do not");
            Check(await store.SetAsync(service, account, "") is PlatformResult<PlatformUnit>.Success, "an empty secret");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: "" }, "an empty secret is not missing");
            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: true }, "deleted");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: null }, "gone");
            Console.WriteLine("PASS Windows Credential Manager: stored, read, replaced, size limit, empty secret, deleted.");
        }
        finally { await store.DeleteAsync(service, account); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Secret store check failed: " + message);
    }
}
