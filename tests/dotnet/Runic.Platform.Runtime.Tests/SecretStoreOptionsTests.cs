using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Runtime.Tests;

// The 0.11 secret storage additions (B15): options, binary members with default implementations, and the limits.
internal sealed class SecretStoreOptionsTests
{
    [Test, DisplayName("SECRETS: options keep today's behaviour by default and compare by value")]
    public void OptionDefaults()
    {
        var options = new DesktopSecretStoreOptions();
        Check(!options.RequireEncryption && options.AllowUserInteraction && !options.UseDataProtectionKeychain, "Defaults change nothing.");
        Check(options with { AllowUserInteraction = false } != options && options == new DesktopSecretStoreOptions(), "Value equality.");
        Check(DesktopSecretStore.MaximumBinarySecretBytes == 65_536 && SecretStoreValidation.MaximumBinarySecretBytes == 65_536
            && SecretStoreValidation.MaximumSecretBytes == 2560, "64 KiB for bytes; the text limit stays 2560.");
    }

    [Test, DisplayName("SECRETS: binary validation checks names, then the size")]
    public void BinaryValidation()
    {
        Check(SecretStoreValidation.ValidateBinary("s", "a", new byte[65_536]), "64 KiB fit.");
        Check(!SecretStoreValidation.ValidateBinary("s", "a", new byte[65_537]), "One byte more does not.");
        Check(SecretStoreValidation.ValidateBinary("s", "a", []), "An empty secret is a secret.");
        Throws<ArgumentException>(() => SecretStoreValidation.ValidateBinary("", "a", []));
        Throws<ArgumentException>(() => SecretStoreValidation.ValidateBinary("s", "a\0", []));
        Check(SecretStoreValidation.DecodeResult(new PlatformResult<byte[]?>.Success([0xFF])) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData },
            "Bytes that are not UTF-8 are not text.");
        byte[] text = "tøken"u8.ToArray();
        Check(SecretStoreValidation.DecodeResult(new PlatformResult<byte[]?>.Success(text)) is PlatformResult<string?>.Success { Value: "tøken" }
            && text.All(b => b == 0), "UTF-8 bytes are text, and the read buffer is cleared.");
        Check(SecretStoreValidation.DecodeResult(new PlatformResult<byte[]?>.Success(null)) is PlatformResult<string?>.Success { Value: null }, "Nothing stored.");
        Check(SecretStoreValidation.DecodeResult(new PlatformResult<byte[]?>.Unavailable(PlatformUnavailableReason.BackendUnavailable))
            is PlatformResult<string?>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "Unavailable carries over.");
    }

    [Test, DisplayName("SECRETS: a store written before 0.11 gets binary members that check arguments and report unavailable")]
    public async Task DefaultMembersAsync()
    {
        IDesktopSecretStore store = new TextOnlyStore();
        Check(await store.GetBytesAsync("s", "a") is PlatformResult<byte[]?>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "GetBytesAsync");
        Check(await store.SetBytesAsync("s", "a", new byte[3]) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "SetBytesAsync");
        await ThrowsAsync<ArgumentException>(() => store.GetBytesAsync("", "a").AsTask());
        await ThrowsAsync<ArgumentException>(() => store.SetBytesAsync("s", new string('x', 257), default).AsTask());
        await ThrowsAsync<OperationCanceledException>(() => store.GetBytesAsync("s", "a", new CancellationToken(true)).AsTask());

        IDesktopSecretStore unavailable = DesktopSecretStore.Unavailable(PlatformUnavailableReason.ProviderNotConfigured);
        Check(await unavailable.GetBytesAsync("s", "a") is PlatformResult<byte[]?>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured }
            && await unavailable.SetBytesAsync("s", "a", new byte[3]) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.ProviderNotConfigured },
            "The unavailable store reports its own reason for bytes too.");
        await ThrowsAsync<ArgumentException>(() => unavailable.SetBytesAsync("s", "", default).AsTask());
    }

    private sealed class TextOnlyStore : IDesktopSecretStore
    {
        public ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<string?>>(new PlatformResult<string?>.Success(null));
        public ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Success(default));
        public ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PlatformResult<bool>>(new PlatformResult<bool>.Success(false));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Secret store check failed: " + message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
