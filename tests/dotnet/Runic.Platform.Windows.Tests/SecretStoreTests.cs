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

    // The reference format, file round trip and clean-up against a fake vault and protector; runs on every OS.
    [Test]
    public async Task BinaryAndLargeSecretsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "runic-secrets-" + Guid.NewGuid().ToString("N"));
        var vault = new FakeVault();
        var store = new WindowsSecretStore(vault, new FakeProtector(), directory);
        const string service = "com.example.Mail", account = "me@example.com";
        string target = WindowsSecretStore.Target(service, account);
        try
        {
            Check(await store.SetAsync(service, account, "tøken") is PlatformResult<PlatformUnit>.Success, "text stored");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var utf8 } && utf8!.SequenceEqual("tøken"u8.ToArray()),
                "a text secret reads as its UTF-8 bytes");

            byte[] small = Pattern(2560);
            Check(await store.SetBytesAsync(service, account, small) is PlatformResult<PlatformUnit>.Success, "2560 bytes stored");
            Check(vault.Items[target].SequenceEqual(small) && !Directory.Exists(directory), "2560 bytes stay in the credential blob");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData }, "binary bytes are not text");

            byte[] large = Pattern(DesktopSecretStore.MaximumBinarySecretBytes);
            Check(await store.SetBytesAsync(service, account, large) is PlatformResult<PlatformUnit>.Success, "64 KiB stored");
            Check(WindowsSecretStore.IsReference(vault.Items[target]) && vault.Items[target].Length == WindowsSecretStore.ReferenceLength,
                "the credential holds a reference");
            string[] files = Directory.GetFiles(directory);
            Check(files.Length == 1 && files[0].EndsWith(".bin", StringComparison.Ordinal), "one protected file");
            Check(!File.ReadAllBytes(files[0]).AsSpan().SequenceEqual(large), "the file is protected");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var read } && read!.SequenceEqual(large), "64 KiB read back");

            string text = new('x', 5000);
            Check(await store.SetBytesAsync(service, account, System.Text.Encoding.UTF8.GetBytes(text)) is PlatformResult<PlatformUnit>.Success, "large text bytes");
            Check(await store.GetAsync(service, account) is PlatformResult<string?>.Success { Value: var largeText } && largeText == text,
                "GetAsync reads a large binary item that is UTF-8");
            Check(Directory.GetFiles(directory).Length == 1 && !File.Exists(files[0]), "the replaced file is removed");

            byte[] lookalike = vault.Items[target].ToArray();
            lookalike[^1] ^= 1;
            Check(await store.SetBytesAsync(service, account, lookalike) is PlatformResult<PlatformUnit>.Success, "a small secret that looks like a reference");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var echoed } && echoed!.SequenceEqual(lookalike),
                "goes through a file and reads back unchanged");

            string file = Directory.GetFiles(directory).Single();
            byte[] contents = File.ReadAllBytes(file);
            contents[^1] ^= 0x55;
            File.WriteAllBytes(file, contents);
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.InvalidData }, "a damaged file");
            File.Delete(file);
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.InvalidData }, "a missing file");

            Check(await store.SetBytesAsync(service, account, large) is PlatformResult<PlatformUnit>.Success, "stored again");
            vault.Items[WindowsSecretStore.Target(service, "other")] = vault.Items[target];
            Check(await store.GetBytesAsync(service, "other") is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.InvalidData },
                "a reference copied to another target does not decrypt: the target is the entropy");
            vault.Items.Remove(WindowsSecretStore.Target(service, "other"));

            Check(await store.SetBytesAsync(service, account, new byte[DesktopSecretStore.MaximumBinarySecretBytes + 1]) is PlatformResult<PlatformUnit>.Failed
                { Code: PlatformFailureCode.TooLarge }, "above 64 KiB");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var kept } && kept!.SequenceEqual(large), "nothing changed");

            vault.FailWrites = 5;
            Check(await store.SetBytesAsync(service, "denied", large) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied },
                "a refused credential write");
            Check(Directory.GetFiles(directory).Length == 1, "leaves no file behind");
            vault.FailWrites = 0;

            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: true }, "deleted");
            Check(!vault.Items.ContainsKey(target) && Directory.GetFiles(directory).Length == 0, "the credential and its file are gone");
            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: false }, "nothing left");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    // The user's real vault and DPAPI, under a service unique to this run and a temporary file directory, both removed.
    [Test, Explicit, Category("NativeSecrets")]
    public async Task CredentialManagerBinaryAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(8)) throw new PlatformNotSupportedException("Credential Manager requires Windows.");
        string service = "org.runic.tests/" + Guid.NewGuid().ToString("N");
        const string account = "user@example.com";
        string directory = Path.Combine(Path.GetTempPath(), "runic-secrets-" + Guid.NewGuid().ToString("N"));
        var store = new WindowsSecretStore(new NativeCredentialVault(), new DpapiProtector(), directory);
        try
        {
            byte[] small = Pattern(2560);
            Check(await store.SetBytesAsync(service, account, small) is PlatformResult<PlatformUnit>.Success, "2560 bytes stored");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var read } && read!.SequenceEqual(small), "read back");
            Check(!Directory.Exists(directory), "no file for a blob that fits");
            byte[] large = Pattern(DesktopSecretStore.MaximumBinarySecretBytes);
            var stored = await store.SetBytesAsync(service, account, large);
            Check(stored is PlatformResult<PlatformUnit>.Success, $"64 KiB stored through DPAPI: {stored}");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: var readLarge } && readLarge!.SequenceEqual(large), "64 KiB read back");
            string file = Directory.GetFiles(directory).Single();
            byte[] contents = File.ReadAllBytes(file);
            contents[contents.Length / 2] ^= 0x55;
            File.WriteAllBytes(file, contents);
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Failed { Code: PlatformFailureCode.InvalidData }, "a damaged file");
            Check(await store.SetBytesAsync(service, account, large) is PlatformResult<PlatformUnit>.Success && !File.Exists(file), "replaced, old file removed");
            Check(await store.DeleteAsync(service, account) is PlatformResult<bool>.Success { Value: true }, "deleted");
            Check(Directory.GetFiles(directory).Length == 0, "delete removes the file too");
            Check(await store.GetBytesAsync(service, account) is PlatformResult<byte[]?>.Success { Value: null }, "gone");
            Console.WriteLine("PASS Windows Credential Manager binary: 2560-byte blob, 64 KiB DPAPI file, damaged file, replace and delete.");
        }
        finally
        {
            await store.DeleteAsync(service, account);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 0x80);
        return bytes;
    }

    private sealed class FakeVault : ICredentialVault
    {
        internal Dictionary<string, byte[]> Items { get; } = new(StringComparer.Ordinal);
        internal int FailWrites { get; set; }

        public int Read(string target, out byte[]? blob)
        {
            blob = Items.TryGetValue(target, out var stored) ? stored.ToArray() : null;
            return blob is null ? WindowsSecretStore.ErrorNotFound : 0;
        }

        public int Write(string target, string userName, ReadOnlySpan<byte> blob)
        {
            if (FailWrites != 0) return FailWrites;
            if (blob.Length > 2560) return 1783;
            Items[target] = blob.ToArray();
            return 0;
        }

        public int Delete(string target) => Items.Remove(target) ? 0 : WindowsSecretStore.ErrorNotFound;
    }

    // XOR with a key stream from the entropy, behind a hash of it, so the wrong entropy fails as DPAPI does.
    private sealed class FakeProtector : ISecretFileProtector
    {
        public int Protect(ReadOnlySpan<byte> data, byte[] entropy, out byte[]? protectedData)
        {
            byte[] tag = System.Security.Cryptography.SHA256.HashData(entropy);
            protectedData = [.. tag, .. Xor(data, tag)];
            return 0;
        }

        public int Unprotect(ReadOnlySpan<byte> protectedData, byte[] entropy, out byte[]? data)
        {
            byte[] tag = System.Security.Cryptography.SHA256.HashData(entropy);
            data = null;
            if (protectedData.Length < tag.Length || !protectedData[..tag.Length].SequenceEqual(tag)) return 13;
            data = Xor(protectedData[tag.Length..], tag);
            return 0;
        }

        private static byte[] Xor(ReadOnlySpan<byte> data, byte[] key)
        {
            var result = new byte[data.Length];
            for (int i = 0; i < data.Length; i++) result[i] = (byte)(data[i] ^ key[i % key.Length] ^ 0xA5);
            return result;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Secret store check failed: " + message);
    }
}
