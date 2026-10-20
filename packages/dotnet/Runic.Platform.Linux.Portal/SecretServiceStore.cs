using System.Numerics;
using System.Security.Cryptography;
using Runic.Platform.Runtime;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

// The freedesktop.org Secret Service API (org.freedesktop.secrets), served by GNOME Keyring, KeePassXC, KWallet and
// others. Every request opens its own connection and session and closes both, so a restarted service needs no
// recovery. Items carry libsecret's generic schema attributes, which secret-tool and keytar also use.
internal sealed class SecretServiceStore(string? address = null, string destination = SecretServiceStore.ServiceName,
    PortalApplication? application = null, bool requireEncryption = false) : IDesktopSecretStore
{
    internal const string ServiceName = "org.freedesktop.secrets";
    internal const string GenericSchema = "org.freedesktop.Secret.Generic";

    // The algorithm the last request negotiated, for tests that check a real service encrypts.
    internal string? LastAlgorithm { get; private set; }

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
        catch (DBusErrorReplyException error)
        {
            var diagnostic = PortalErrors.Reply(error);
            switch (error.ErrorName)
            {
                case "org.freedesktop.Secret.Error.IsLocked":
                    return new PlatformResult<T>.Failed(PlatformFailureCode.Locked, diagnostic);
                case "org.freedesktop.DBus.Error.AccessDenied":
                    return new PlatformResult<T>.Failed(PlatformFailureCode.PermissionDenied, diagnostic);
                case "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner":
                case var name when name.StartsWith("org.freedesktop.DBus.Error.Spawn.", StringComparison.Ordinal):
                    Unavailable();
                    return new PlatformResult<T>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
                default:
                    return new PlatformResult<T>.Failed(PlatformFailureCode.IoError, diagnostic);
            }
        }
        catch (Exception error) when (error is NativeBackendUnavailableException or DBusExceptionBase or TimeoutException)
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

internal sealed class SecretServiceClient : IDisposable
{
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string CollectionInterface = "org.freedesktop.Secret.Collection";
    private const string ItemInterface = "org.freedesktop.Secret.Item";
    private const string PromptInterface = "org.freedesktop.Secret.Prompt";
    internal const string EncryptedAlgorithm = "dh-ietf1024-sha256-aes128-cbc-pkcs7";
    internal const string PlainAlgorithm = "plain";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private delegate void BodyWriter(ref MessageWriter writer);

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
        var client = new SecretServiceClient(await PortalConnection.OpenAsync(address, destination, null, cancellationToken).ConfigureAwait(false));
        try
        {
            await client.OpenSessionAsync(requireEncryption, application, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private async Task OpenSessionAsync(bool requireEncryption, PortalApplication? application, CancellationToken cancellationToken)
    {
        byte[] privateBytes = RandomNumberGenerator.GetBytes(SecretServiceDh.KeyBytes);
        try
        {
            var privateKey = SecretServiceDh.PrivateKey(privateBytes);
            byte[] publicKey = SecretServiceDh.PublicKey(privateKey);
            (byte[] peer, string path) = await Connection.CallMethodAsync(Call(ServicePath, ServiceInterface, "OpenSession", "sv",
                (ref MessageWriter writer) =>
                {
                    writer.WriteString(EncryptedAlgorithm);
                    writer.WriteSignature("ay");
                    writer.WriteArray(publicKey);
                }), static (message, _) =>
            {
                var reader = message.GetBodyReader();
                var output = reader.ReadVariantValue();
                return (output.GetArray<byte>(), reader.ReadObjectPathAsString());
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            _sessionPath = path;
            _key = SecretServiceDh.DeriveKey(privateKey, peer);
            Algorithm = EncryptedAlgorithm;
        }
        catch (DBusErrorReplyException error) when (error.ErrorName == "org.freedesktop.DBus.Error.NotSupported" && !requireEncryption)
        {
            _sessionPath = await Connection.CallMethodAsync(Call(ServicePath, ServiceInterface, "OpenSession", "sv",
                static (ref MessageWriter writer) =>
                {
                    writer.WriteString(PlainAlgorithm);
                    writer.WriteVariantString("");
                }), static (message, _) =>
            {
                var reader = message.GetBodyReader();
                _ = reader.ReadVariantValue();
                return reader.ReadObjectPathAsString();
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            Algorithm = PlainAlgorithm;
            application?.Diagnose("secret-service-plain-session",
                "The Secret Service does not support encrypted sessions, so secrets cross the session bus unencrypted.",
                "Use a Secret Service provider that supports the dh-ietf1024-sha256-aes128-cbc-pkcs7 algorithm, as GNOME Keyring and KeePassXC do.");
        }
        finally { CryptographicOperations.ZeroMemory(privateBytes); }
    }

    internal Task<(string[] Unlocked, string[] Locked)> SearchAsync(string service, string account, CancellationToken cancellationToken) =>
        Connection.CallMethodAsync(Call(ServicePath, ServiceInterface, "SearchItems", "a{ss}",
            (ref MessageWriter writer) => WriteAttributes(ref writer, service, account, schema: false)), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return (Paths(reader.ReadArrayOfObjectPath()), Paths(reader.ReadArrayOfObjectPath()));
        }).WaitAsync(cancellationToken);

    // Unlocks one item or collection, asking the user when the service needs to.
    internal async Task UnlockAsync(string path, CancellationToken cancellationToken)
    {
        (string[] unlocked, string prompt) = await Connection.CallMethodAsync(Call(ServicePath, ServiceInterface, "Unlock", "ao",
            (ref MessageWriter writer) => writer.WriteArray(new[] { new ObjectPath(path) })), static (reply, _) =>
        {
            var reader = reply.GetBodyReader();
            return (Paths(reader.ReadArrayOfObjectPath()), reader.ReadObjectPathAsString());
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (unlocked.Contains(path) || prompt == "/") return;
        // A service that cannot show its unlock prompt leaves the store locked.
        await PromptAsync(prompt, PlatformFailureCode.Locked, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<byte[]> GetSecretAsync(string item, CancellationToken cancellationToken)
    {
        string session = _sessionPath;
        (byte[] parameters, byte[] value) = await Connection.CallMethodAsync(Call(item, ItemInterface, "GetSecret", "o",
            (ref MessageWriter writer) => writer.WriteObjectPath(session)), static (reply, _) =>
        {
            var reader = reply.GetBodyReader();
            reader.AlignStruct();
            _ = reader.ReadObjectPathAsString();
            byte[] parameters = reader.ReadArrayOfByte();
            byte[] value = reader.ReadArrayOfByte();
            _ = reader.ReadString();
            return (parameters, value);
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (_key is null) return value;
        try { return SecretServiceDh.Decrypt(_key, parameters, value); }
        catch (CryptographicException) { throw new SecretServiceFailure(PlatformFailureCode.InvalidData); }
        finally { CryptographicOperations.ZeroMemory(value); }
    }

    // The collection behind the "default" alias, created as libsecret does when the service has none.
    internal async Task<string> DefaultCollectionAsync(CancellationToken cancellationToken)
    {
        string collection = await Connection.CallMethodAsync(Call(ServicePath, ServiceInterface, "ReadAlias", "s",
            static (ref MessageWriter writer) => writer.WriteString("default")),
            static (reply, _) => reply.GetBodyReader().ReadObjectPathAsString()).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (collection != "/") return collection;
        (collection, string prompt) = await Connection.CallMethodAsync(Call(ServicePath, ServiceInterface, "CreateCollection", "a{sv}s",
            static (ref MessageWriter writer) =>
            {
                var properties = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString(CollectionInterface + ".Label");
                writer.WriteVariantString("Default keyring");
                writer.WriteDictionaryEnd(properties);
                writer.WriteString("default");
            }), static (reply, _) =>
        {
            var reader = reply.GetBodyReader();
            return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (prompt != "/") collection = (await PromptAsync(prompt, PlatformFailureCode.IoError, cancellationToken).ConfigureAwait(false)).GetObjectPathAsString();
        if (collection == "/") throw new SecretServiceFailure(PlatformFailureCode.IoError);
        return collection;
    }

    internal async Task CreateItemAsync(string collection, string service, string account, byte[] secret, CancellationToken cancellationToken)
    {
        byte[] parameters = [], value = secret;
        if (_key is not null) (parameters, value) = SecretServiceDh.Encrypt(_key, secret);
        string session = _sessionPath;
        MessageBuffer message;
        try
        {
            message = Call(collection, CollectionInterface, "CreateItem", "a{sv}(oayays)b", (ref MessageWriter writer) =>
            {
                var properties = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart();
                writer.WriteString(ItemInterface + ".Label");
                // A label people recognise in Seahorse, KeePassXC or KWallet.
                writer.WriteVariantString(service + " (" + account + ")");
                writer.WriteDictionaryEntryStart();
                writer.WriteString(ItemInterface + ".Attributes");
                writer.WriteSignature("a{ss}");
                WriteAttributes(ref writer, service, account, schema: true);
                writer.WriteDictionaryEnd(properties);
                writer.WriteStructureStart();
                writer.WriteObjectPath(session);
                writer.WriteArray(parameters);
                writer.WriteArray(value);
                writer.WriteString("text/plain");
                writer.WriteBool(true);
            });
        }
        finally { if (!ReferenceEquals(value, secret)) CryptographicOperations.ZeroMemory(value); }
        (_, string prompt) = await Connection.CallMethodAsync(message, static (reply, _) =>
        {
            var reader = reply.GetBodyReader();
            return (reader.ReadObjectPathAsString(), reader.ReadObjectPathAsString());
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (prompt != "/") await PromptAsync(prompt, PlatformFailureCode.IoError, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> DeleteItemAsync(string item, CancellationToken cancellationToken)
    {
        string prompt;
        try
        {
            prompt = await Connection.CallMethodAsync(Call(item, ItemInterface, "Delete", null, null),
                static (reply, _) => reply.GetBodyReader().ReadObjectPathAsString()).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        // Another client removed it first.
        catch (DBusErrorReplyException error) when (error.ErrorName is "org.freedesktop.Secret.Error.NoSuchObject" or "org.freedesktop.DBus.Error.UnknownObject")
        { return false; }
        if (prompt != "/") await PromptAsync(prompt, PlatformFailureCode.IoError, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Shows a prompt and waits for its Completed signal. A prompt the service cannot show reports failure; one the user
    // dismissed reports UserDismissed; cancellation dismisses it.
    private async Task<VariantValue> PromptAsync(string prompt, PlatformFailureCode failure, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<(bool Dismissed, VariantValue Result)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await Connection.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal, Sender = Destination, Path = prompt, Interface = PromptInterface, Member = "Completed",
        }, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            return (reader.ReadBool(), reader.ReadVariantValue());
        }, notification =>
        {
            if (notification.HasValue) completed.TrySetResult(notification.Value);
            else if (notification.IsCompletion) completed.TrySetException(notification.Exception ?? new NativeBackendUnavailableException());
        }, emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No window to parent the prompt to: the service shows it on its own.
            try
            {
                await Connection.CallMethodAsync(Call(prompt, PromptInterface, "Prompt", "s",
                    static (ref MessageWriter writer) => writer.WriteString(""))).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DBusErrorReplyException error) { throw new SecretServiceFailure(failure, PortalErrors.Reply(error)); }
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
        try { await Connection.CallMethodAsync(Call(prompt, PromptInterface, "Dismiss", null, null)).WaitAsync(CleanupTimeout).ConfigureAwait(false); }
        catch (Exception error) when (error is DBusExceptionBase or TimeoutException or ObjectDisposedException) { /* Closing the connection ends the prompt's client. */ }
    }

    private MessageBuffer Call(string path, string @interface, string member, string? signature, BodyWriter? body)
    {
        var writer = Connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: Destination, path: path, @interface: @interface, member: member, signature: signature);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }

    private static void WriteAttributes(ref MessageWriter writer, string service, string account, bool schema)
    {
        var attributes = writer.WriteDictionaryStart();
        if (schema)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("xdg:schema");
            writer.WriteString(SecretServiceStore.GenericSchema);
        }
        writer.WriteDictionaryEntryStart();
        writer.WriteString("service");
        writer.WriteString(service);
        writer.WriteDictionaryEntryStart();
        writer.WriteString("account");
        writer.WriteString(account);
        writer.WriteDictionaryEnd(attributes);
    }

    private static string[] Paths(ObjectPath[] paths) => Array.ConvertAll(paths, static path => path.ToString());

    // Closes the session, then the connection. Disconnecting closes the session too, so a failed Close is ignored.
    internal async ValueTask CloseAsync()
    {
        if (_sessionPath != "/")
        {
            try
            {
                await Connection.CallMethodAsync(Call(_sessionPath, "org.freedesktop.Secret.Session", "Close", null, null))
                    .WaitAsync(CleanupTimeout).ConfigureAwait(false);
            }
            catch (Exception error) when (error is DBusExceptionBase or TimeoutException or ObjectDisposedException or InvalidOperationException) { }
        }
        Dispose();
    }

    public void Dispose()
    {
        if (_key is not null) CryptographicOperations.ZeroMemory(_key);
        _session.Dispose();
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
