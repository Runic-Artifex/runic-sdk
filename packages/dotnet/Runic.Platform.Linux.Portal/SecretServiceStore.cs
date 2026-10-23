using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Runic.Platform.Linux.DBus;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// The freedesktop.org Secret Service API (org.freedesktop.secrets), served by GNOME Keyring, KeePassXC, KWallet and
// others. Every request opens its own session on the shared connection and closes it, so a restarted service needs no
// recovery. Items carry libsecret's generic schema attributes, which secret-tool and keytar also use.
internal sealed class SecretServiceStore(string? address = null, string destination = SecretServiceStore.ServiceName,
    PortalApplication? application = null, bool requireEncryption = false) : IDesktopSecretStore
{
    internal const string ServiceName = "org.freedesktop.secrets";
    internal const string GenericSchema = "org.freedesktop.Secret.Generic";

    // The algorithm the last request negotiated, for tests that check a real service encrypts.
    internal string? LastAlgorithm { get; private set; }

    // Whether a plain session is refused (DesktopSecretStoreOptions.RequireEncryption).
    internal bool RequireEncryption => requireEncryption;

    public ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return RunAsync<string?>(async (client, token) =>
        {
            var (unlocked, locked) = await client.SearchAsync(service, account, token).ConfigureAwait(false);
            string? item = unlocked.Length > 0 ? unlocked[0] : null;
            if (item is null && locked.Length > 0)
            {
                await client.UnlockAsync(locked[0], token).ConfigureAwait(false);
                item = locked[0];
            }
            if (item is null) return new PlatformResult<string?>.Success(null);
            byte[] stored = await client.GetSecretAsync(item, token).ConfigureAwait(false);
            try
            {
                return SecretStoreValidation.DecodeSecret(stored) is { } secret
                    ? new PlatformResult<string?>.Success(secret)
                    : new PlatformResult<string?>.Failed(PlatformFailureCode.InvalidData);
            }
            finally { CryptographicOperations.ZeroMemory(stored); }
        }, cancellationToken);
    }

    public ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        byte[]? encoded = SecretEncoding.Encode(secret);
        if (encoded is null) return ValueTask.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.TooLarge));
        if (cancellationToken.IsCancellationRequested) { CryptographicOperations.ZeroMemory(encoded); cancellationToken.ThrowIfCancellationRequested(); }
        return SetCoreAsync(service, account, encoded, cancellationToken);
    }

    private async ValueTask<PlatformResult<PlatformUnit>> SetCoreAsync(string service, string account, byte[] encoded, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync<PlatformUnit>(async (client, token) =>
            {
                string collection = await client.DefaultCollectionAsync(token).ConfigureAwait(false);
                await client.UnlockAsync(collection, token).ConfigureAwait(false);
                await client.CreateItemAsync(collection, service, account, encoded, token).ConfigureAwait(false);
                return new PlatformResult<PlatformUnit>.Success(default);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }

    public ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default)
    {
        SecretStoreValidation.Validate(service, account);
        cancellationToken.ThrowIfCancellationRequested();
        return RunAsync<bool>(async (client, token) =>
        {
            var (unlocked, locked) = await client.SearchAsync(service, account, token).ConfigureAwait(false);
            // A locked item cannot be deleted; unlock it as a read would.
            foreach (string item in locked) await client.UnlockAsync(item, token).ConfigureAwait(false);
            bool deleted = false;
            foreach (string item in unlocked.Concat(locked)) deleted |= await client.DeleteItemAsync(item, token).ConfigureAwait(false);
            return new PlatformResult<bool>.Success(deleted);
        }, cancellationToken);
    }

    private async ValueTask<PlatformResult<T>> RunAsync<T>(Func<SecretServiceClient, CancellationToken, Task<PlatformResult<T>>> body,
        CancellationToken cancellationToken)
    {
        SecretServiceClient? client = null;
        try
        {
            client = await SecretServiceClient.OpenAsync(address, destination, requireEncryption, application, cancellationToken).ConfigureAwait(false);
            LastAlgorithm = client.Algorithm;
            using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, client.OwnerChanged);
            try { return await body(client, stopped.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new NativeBackendUnavailableException(); }
        }
        catch (SecretServiceFailure failure) { return new PlatformResult<T>.Failed(failure.Code, failure.Diagnostic); }
        catch (DBusException error)
        {
            var diagnostic = PortalErrors.Reply(error);
            switch (error.ErrorName)
            {
                case DBusException.Disconnected:
                    Unavailable();
                    return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                case "org.freedesktop.Secret.Error.IsLocked":
                    return new PlatformResult<T>.Failed(PlatformFailureCode.Locked, diagnostic);
                case "org.freedesktop.DBus.Error.AccessDenied":
                    return new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, diagnostic);
                case "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner":
                case var name when name.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal):
                    Unavailable();
                    return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                default:
                    return new PlatformResult<T>.Failed(PlatformFailureCode.Other, diagnostic);
            }
        }
        catch (Exception error) when (error is NativeBackendUnavailableException or TimeoutException)
        {
            Unavailable();
            return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        catch (Exception error) when (error is InvalidOperationException and not ObjectDisposedException or IndexOutOfRangeException)
        {
            // A reply of the wrong shape.
            return new PlatformResult<T>.Failed(PlatformFailureCode.InvalidData);
        }
        finally { if (client is not null) await client.CloseAsync().ConfigureAwait(false); }
    }

    private void Unavailable() => application?.Diagnose("secret-service-unavailable",
        "No Secret Service answered on the session bus.",
        "Run a Secret Service provider, such as GNOME Keyring or KeePassXC with Secret Service integration. Inside Flatpak, " +
        "allow talking to org.freedesktop.secrets (--talk-name=org.freedesktop.secrets); inside Snap, connect the " +
        "password-manager-service interface.");
}

// A typed failure raised inside a request and returned as its result.
internal sealed class SecretServiceFailure(PlatformFailureCode code, PlatformDiagnostic? diagnostic = null) : Exception
{
    internal PlatformFailureCode Code { get; } = code;
    internal PlatformDiagnostic? Diagnostic { get; } = diagnostic;
}

internal sealed class SecretServiceClient
{
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    internal const string EncryptedAlgorithm = "dh-ietf1024-sha256-aes128-cbc-pkcs7";
    internal const string PlainAlgorithm = "plain";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly PortalConnection _session;
    private string _sessionPath = "/";
    private byte[]? _key;

    private SecretServiceClient(PortalConnection session) => _session = session;

    private DBusConnection Connection => _session.Connection;
    private string Destination => _session.Destination;
    internal CancellationToken OwnerChanged => _session.OwnerChanged;
    internal string? Algorithm { get; private set; }

    internal static async Task<SecretServiceClient> OpenAsync(string? address, string destination, bool requireEncryption,
        PortalApplication? application, CancellationToken cancellationToken)
    {
        // The Secret Service is no portal: the connection of the application's identity serves it without registering.
        var session = await PortalConnection.OpenAsync(address, destination, null, cancellationToken).ConfigureAwait(false);
        var client = new SecretServiceClient(session);
        try
        {
            await client.OpenSessionAsync(requireEncryption, application, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch { await client.CloseAsync().ConfigureAwait(false); throw; }
    }

    private async Task OpenSessionAsync(bool requireEncryption, PortalApplication? application, CancellationToken cancellationToken)
    {
        byte[] privateBytes = RandomNumberGenerator.GetBytes(SecretServiceDh.KeyBytes);
        try
        {
            var privateKey = SecretServiceDh.PrivateKey(privateBytes);
            byte[] publicKey = SecretServiceDh.PublicKey(privateKey);
            var reply = Expect(await CallAsync(ServicePath, ServiceInterface, "OpenSession",
                [DBusValue.From(EncryptedAlgorithm), DBusValue.Variant(DBusValue.ByteArray(publicKey))], cancellationToken).ConfigureAwait(false), "vo");
            var peer = reply[0].GetVariant().GetByteArray();
            _sessionPath = reply[1].GetObjectPath().Value;
            _key = SecretServiceDh.DeriveKey(privateKey, peer.AsSpan());
            Algorithm = EncryptedAlgorithm;
        }
        catch (DBusException error) when (error.ErrorName == "org.freedesktop.DBus.Error.NotSupported" && !requireEncryption)
        {
            var reply = Expect(await CallAsync(ServicePath, ServiceInterface, "OpenSession",
                [DBusValue.From(PlainAlgorithm), DBusValue.Variant(DBusValue.From(""))], cancellationToken).ConfigureAwait(false), "vo");
            _sessionPath = reply[1].GetObjectPath().Value;
            Algorithm = PlainAlgorithm;
            application?.Diagnose("secret-service-plain-session",
                "The Secret Service does not support encrypted sessions, so secrets cross the session bus unencrypted.",
                "Use a Secret Service provider that supports the dh-ietf1024-sha256-aes128-cbc-pkcs7 algorithm, as GNOME Keyring and KeePassXC do.");
        }
        catch (DBusException error) when (error.ErrorName == "org.freedesktop.DBus.Error.NotSupported")
        {
            // RequireEncryption: no plain session; the request fails with the service's error.
            application?.Diagnose("secret-service-encryption-required",
                "The Secret Service does not support encrypted sessions, and the secret store requires one.",
                "Use a Secret Service provider that supports the dh-ietf1024-sha256-aes128-cbc-pkcs7 algorithm, as GNOME Keyring and KeePassXC do, " +
                "or create the store without DesktopSecretStoreOptions.RequireEncryption.");
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }

    internal async Task<(string[] Unlocked, string[] Locked)> SearchAsync(string service, string account, CancellationToken cancellationToken)
    {
        var reply = Expect(await CallAsync(ServicePath, ServiceInterface, "SearchItems", [Attributes(service, account, schema: false)], cancellationToken)
            .ConfigureAwait(false), "aoao");
        return (Paths(reply[0]), Paths(reply[1]));
    }

    // Unlocks one item or collection, asking the user when the service needs to.
    internal async Task UnlockAsync(string path, CancellationToken cancellationToken)
    {
        var reply = Expect(await CallAsync(ServicePath, ServiceInterface, "Unlock", [DBusValue.ObjectPathArray([new DBusObjectPath(path)])], cancellationToken)
            .ConfigureAwait(false), "aoo");
        string[] unlocked = Paths(reply[0]);
        string prompt = reply[1].GetObjectPath().Value;
        if (unlocked.Contains(path) || prompt == "/") return;
        // A service that cannot show its unlock prompt leaves the store locked.
        await PromptAsync(prompt, PlatformFailureCode.Locked, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<byte[]> GetSecretAsync(string item, CancellationToken cancellationToken)
    {
        var reply = Expect(await CallAsync(item, ItemInterface, "GetSecret", [DBusValue.From(new DBusObjectPath(_sessionPath))], cancellationToken)
            .ConfigureAwait(false), "(oayays)");
        var fields = reply[0].GetFields();
        var parameters = fields[1].GetByteArray();
        var value = fields[2].GetByteArray();
        try
        {
            if (_key is null) return [.. value];
            try { return SecretServiceDh.Decrypt(_key, parameters.AsSpan(), value.AsSpan()); }
            catch (CryptographicException) { throw new SecretServiceFailure(PlatformFailureCode.InvalidData); }
        }
        finally { Zero(value); }
    }

    // The collection behind the "default" alias, created as libsecret does when the service has none.
    internal async Task<string> DefaultCollectionAsync(CancellationToken cancellationToken)
    {
        string collection = Expect(await CallAsync(ServicePath, ServiceInterface, "ReadAlias", [DBusValue.From("default")], cancellationToken)
            .ConfigureAwait(false), "o")[0].GetObjectPath().Value;
        if (collection != "/") return collection;
        var created = Expect(await CallAsync(ServicePath, ServiceInterface, "CreateCollection",
            [DBusValue.VariantDictionary([KeyValuePair.Create(CollectionInterface + ".Label", DBusValue.From("Default keyring"))]), DBusValue.From("default")],
            cancellationToken).ConfigureAwait(false), "oo");
        collection = created[0].GetObjectPath().Value;
        string prompt = created[1].GetObjectPath().Value;
        if (prompt != "/") collection = (await PromptAsync(prompt, PlatformFailureCode.Other, cancellationToken).ConfigureAwait(false)).GetObjectPath().Value;
        if (collection == "/") throw new SecretServiceFailure(PlatformFailureCode.Other);
        return collection;
    }

    internal async Task CreateItemAsync(string collection, string service, string account, byte[] secret, CancellationToken cancellationToken)
    {
        byte[] parameters = [], value = secret;
        if (_key is not null) (parameters, value) = SecretServiceDh.Encrypt(_key, secret);
        // The message's copy of the secret is cleared too once the call is written.
        DBusValue sent = DBusValue.ByteArray(value);
        if (!ReferenceEquals(value, secret)) CryptographicOperations.ZeroMemory(value);
        ImmutableArray<DBusValue> reply;
        try
        {
            reply = Expect(await CallAsync(collection, CollectionInterface, "CreateItem",
            [
                DBusValue.VariantDictionary(
                [
                    // A label people recognise in Seahorse, KeePassXC or KWallet.
                    KeyValuePair.Create(ItemInterface + ".Label", DBusValue.From(service + " (" + account + ")")),
                    KeyValuePair.Create(ItemInterface + ".Attributes", Attributes(service, account, schema: true)),
                ]),
                DBusValue.Struct(DBusValue.From(new DBusObjectPath(_sessionPath)), DBusValue.ByteArray(parameters), sent, DBusValue.From("text/plain")),
                DBusValue.From(true),
            ], cancellationToken).ConfigureAwait(false), "oo");
        }
        finally { Zero(sent.GetByteArray()); }
        string prompt = reply[1].GetObjectPath().Value;
        if (prompt != "/") await PromptAsync(prompt, PlatformFailureCode.Other, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> DeleteItemAsync(string item, CancellationToken cancellationToken)
    {
        string prompt;
        try { prompt = Expect(await CallAsync(item, ItemInterface, "Delete", [], cancellationToken).ConfigureAwait(false), "o")[0].GetObjectPath().Value; }
        // Another client removed it first.
        catch (DBusException error) when (error.ErrorName is "org.freedesktop.Secret.Error.NoSuchObject" or "org.freedesktop.DBus.Error.UnknownObject")
        { return false; }
        if (prompt != "/") await PromptAsync(prompt, PlatformFailureCode.Other, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Shows a prompt and waits for its Completed signal. A prompt the service cannot show reports failure; one the user
    // dismissed reports UserDismissed; cancellation dismisses it.
    private async Task<DBusValue> PromptAsync(string prompt, PlatformFailureCode failure, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<(bool Dismissed, DBusValue Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await Connection.SubscribeAsync(new DBusSignalFilter
        {
            Sender = Destination, Path = new DBusObjectPath(prompt), Interface = PromptInterface, Member = "Completed",
        }, cancellationToken).ConfigureAwait(false);
        _ = PortalValues.PumpAsync(subscription, signal =>
        {
            if (signal.Arguments is [{ Signature.Value: "b" } dismissed, { Signature.Value: "v" } result]) completed.TrySetResult((dismissed.GetBoolean(), result.GetVariant()));
            else completed.TrySetException(new InvalidOperationException("Completed carries another type."));
        }, error => completed.TrySetException(error ?? new NativeBackendUnavailableException()));
        try
        {
            // No window to parent the prompt to: the service shows it on its own.
            try { await CallAsync(prompt, PromptInterface, "Prompt", [DBusValue.From("")], cancellationToken).ConfigureAwait(false); }
            catch (DBusException error) when (error.ErrorName != DBusException.Disconnected) { throw new SecretServiceFailure(failure, PortalErrors.Reply(error)); }
            var (dismissed, result) = await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (dismissed) throw new SecretServiceFailure(PlatformFailureCode.UserDismissed);
            return result;
        }
        // Cancelled while the prompt was being shown or answered, or the service was replaced.
        catch (OperationCanceledException)
        {
            await DismissAsync(prompt).ConfigureAwait(false);
            throw;
        }
    }

    private async Task DismissAsync(string prompt)
    {
        try { await CallAsync(prompt, PromptInterface, "Dismiss", [], CancellationToken.None).AsTask().WaitAsync(CleanupTimeout).ConfigureAwait(false); }
        catch (Exception error) when (error is DBusException or TimeoutException) { /* Closing the connection ends the prompt's client. */ }
    }

    private ValueTask<ImmutableArray<DBusValue>> CallAsync(string path, string @interface, string member, ImmutableArray<DBusValue> arguments,
        CancellationToken cancellationToken) =>
        Connection.CallMethodAsync(new DBusMethodCall(Destination, new DBusObjectPath(path), @interface, member) { Arguments = arguments }, cancellationToken);

    // A reply of another shape than the method's is invalid data.
    private static ImmutableArray<DBusValue> Expect(ImmutableArray<DBusValue> reply, string signature) =>
        string.Concat(reply.Select(static value => value.Signature.Value)) == signature ? reply
            : throw new InvalidOperationException($"The Secret Service replied \"{string.Concat(reply.Select(static value => value.Signature.Value))}\", not \"{signature}\".");

    private static DBusValue Attributes(string service, string account, bool schema)
    {
        var attributes = new List<KeyValuePair<DBusValue, DBusValue>>();
        if (schema) attributes.Add(KeyValuePair.Create(DBusValue.From("xdg:schema"), DBusValue.From(SecretServiceStore.GenericSchema)));
        attributes.Add(KeyValuePair.Create(DBusValue.From("service"), DBusValue.From(service)));
        attributes.Add(KeyValuePair.Create(DBusValue.From("account"), DBusValue.From(account)));
        return DBusValue.Dictionary("s", "s", attributes);
    }

    private static string[] Paths(DBusValue paths) => [.. paths.GetObjectPathArray().Select(static path => path.Value)];

    // Clears secret bytes that a D-Bus value holds; the value is not used afterwards.
    private static void Zero(ImmutableArray<byte> bytes)
    {
        if (!bytes.IsDefaultOrEmpty) CryptographicOperations.ZeroMemory(ImmutableCollectionsMarshal.AsArray(bytes));
    }

    // Closes the session. Disconnecting closes it too, so a failed Close is ignored.
    internal async ValueTask CloseAsync()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        if (_sessionPath == "/") return;
        try
        {
            await CallAsync(_sessionPath, "org.freedesktop.Secret.Session", "Close", [], CancellationToken.None).AsTask()
                .WaitAsync(CleanupTimeout).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusException or TimeoutException) { }
    }
}

// The dh-ietf1024-sha256-aes128-cbc-pkcs7 session algorithm of the Secret Service API: Diffie-Hellman over the
// 1024-bit MODP group of RFC 2409 (the Second Oakley Group), HKDF-SHA256 without salt or info to a 128-bit key, and
// AES-128-CBC with PKCS#7 padding and the IV as the secret's parameters. The shared secret is padded to the prime's
// length, as libsecret and GNOME Keyring do.
internal static class SecretServiceDh
{
    internal const int KeyBytes = 128;
    private static readonly BigInteger Prime = BigInteger.Parse(
        "00FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DD" +
        "EF9519B3CD3A431B302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE65381FFFFFFFFFFFFFFFF", System.Globalization.NumberStyles.HexNumber,
        System.Globalization.CultureInfo.InvariantCulture);

    internal static BigInteger PrivateKey(ReadOnlySpan<byte> random) =>
        new BigInteger(random, isUnsigned: true, isBigEndian: true) % (Prime - 3) + 2;

    internal static byte[] PublicKey(BigInteger privateKey) => Pad(BigInteger.ModPow(2, privateKey, Prime));

    internal static byte[] DeriveKey(BigInteger privateKey, ReadOnlySpan<byte> peer)
    {
        var peerKey = new BigInteger(peer, isUnsigned: true, isBigEndian: true);
        if (peer.Length > KeyBytes || peerKey <= 1 || peerKey >= Prime - 1)
            throw new SecretServiceFailure(PlatformFailureCode.InvalidData);
        byte[] shared = Pad(BigInteger.ModPow(peerKey, privateKey, Prime));
        try { return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 16); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    internal static (byte[] Parameters, byte[] Value) Encrypt(byte[] key, ReadOnlySpan<byte> plain)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key;
        return (iv, aes.EncryptCbc(plain, iv, PaddingMode.PKCS7));
    }

    internal static byte[] Decrypt(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> encrypted)
    {
        if (iv.Length != 16) throw new CryptographicException("The secret's IV is not 16 bytes.");
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(encrypted, iv, PaddingMode.PKCS7);
    }

    private static byte[] Pad(BigInteger value)
    {
        var bytes = new byte[KeyBytes];
        int length = value.GetByteCount(isUnsigned: true);
        if (!value.TryWriteBytes(bytes.AsSpan(KeyBytes - length), out _, isUnsigned: true, isBigEndian: true))
            throw new CryptographicException("The key does not fit the group.");
        return bytes;
    }
}
