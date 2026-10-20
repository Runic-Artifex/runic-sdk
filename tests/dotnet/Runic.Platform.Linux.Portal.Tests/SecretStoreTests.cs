using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;
using TUnit.Core;

internal sealed class SecretStoreTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string Service = "org.runic.SecretTests";
    private const string Account = "user@example.com";

    [Test, DisplayName("secret store: Diffie-Hellman agreement derives the same key on both sides and AES round-trips")]
    public void KeyAgreement()
    {
        for (int round = 0; round < 8; round++)
        {
            var client = SecretServiceDh.PrivateKey(RandomNumberGenerator.GetBytes(SecretServiceDh.KeyBytes));
            var server = SecretServiceDh.PrivateKey(RandomNumberGenerator.GetBytes(SecretServiceDh.KeyBytes));
            byte[] clientPublic = SecretServiceDh.PublicKey(client), serverPublic = SecretServiceDh.PublicKey(server);
            Check(clientPublic.Length == 128 && serverPublic.Length == 128, "public keys are padded to the prime's length");
            byte[] clientKey = SecretServiceDh.DeriveKey(client, serverPublic), serverKey = SecretServiceDh.DeriveKey(server, clientPublic);
            Check(clientKey.Length == 16 && clientKey.AsSpan().SequenceEqual(serverKey), "both sides derive one 128-bit key");
            var (iv, encrypted) = SecretServiceDh.Encrypt(clientKey, "tøken"u8);
            Check(iv.Length == 16 && encrypted.Length % 16 == 0 && !encrypted.AsSpan().SequenceEqual("tøken"u8), "AES-CBC with a 16-byte IV");
            Check(SecretServiceDh.Decrypt(serverKey, iv, encrypted).AsSpan().SequenceEqual("tøken"u8), "the peer decrypts");
        }
        // A degenerate peer key would fix the shared secret.
        foreach (var peer in new byte[][] { [1], [0], new byte[129] })
        {
            try { SecretServiceDh.DeriveKey(BigInteger.One + 1, peer); throw new InvalidOperationException("A degenerate peer key was accepted."); }
            catch (SecretServiceFailure failure) when (failure.Code == PlatformFailureCode.InvalidData) { }
        }
    }

    [Test, DisplayName("secret store: arguments are checked before connecting, and an oversized secret is TooLarge")]
    public async Task ArgumentsAsync()
    {
        // No session bus at this address: a request that connected would report unavailable instead.
        var store = new SecretServiceStore(address: "unix:path=/nonexistent/runic-secret-tests");
        foreach (var (service, account) in new[] { ("", "a"), ("s", ""), ("s\0x", "a"), (new string('s', 257), "a"), ("s", "\ud800") })
        {
            try { await store.GetAsync(service, account); throw new InvalidOperationException($"'{service}' and '{account}' were accepted."); }
            catch (ArgumentException) { }
        }
        try { await store.SetAsync("s", "a", null!); throw new InvalidOperationException("A null secret was accepted."); }
        catch (ArgumentNullException) { }
        Check(await store.SetAsync("s", "a", new string('x', 2561)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.TooLarge },
            "2561 bytes are too large");
        Check(await store.SetAsync("s", "a", new string('é', 1281)) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.TooLarge },
            "the limit counts UTF-8 bytes, not characters");
        Check(await store.SetAsync("s", "a", new string('x', 2560)) is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "2560 bytes are accepted and reach the (missing) bus");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await store.DeleteAsync("s", "a", cancelled.Token); throw new InvalidOperationException("A cancelled request ran."); }
        catch (OperationCanceledException) { }
    }

    [Test, Explicit, Category("DBus"), DisplayName("secret store: set, get, replace and delete over an encrypted session, with libsecret's attributes and a readable label")]
    public async Task RoundTripAsync()
    {
        await using var fake = await FakeSecretService.StartAsync();
        var store = new SecretServiceStore(destination: fake.Name, requireEncryption: true);

        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: null }, "nothing stored yet");
        Check(await store.DeleteAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<bool>.Success { Value: false }, "nothing to delete");
        Check(await store.SetAsync(Service, Account, "first-tøken").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "stored");
        Check(store.LastAlgorithm == SecretServiceClient.EncryptedAlgorithm, "the session is encrypted");
        var item = fake.Service.Items.Values.Single();
        Check(Encoding.UTF8.GetString(item.Secret) == "first-tøken" && item.ContentType == "text/plain", "the service decrypted UTF-8 text");
        Check(item.Label == $"{Service} ({Account})", $"readable label, found '{item.Label}'");
        Check(item.Attributes.Count == 3 && item.Attributes["xdg:schema"] == "org.freedesktop.Secret.Generic"
            && item.Attributes["service"] == Service && item.Attributes["account"] == Account, "libsecret generic schema attributes");
        Check(fake.Service.LastSearch is { Count: 2 } search && search["service"] == Service && search["account"] == Account,
            "searches match items stored without the schema attribute, as secret-tool stores them");
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "first-tøken" }, "read back");

        Check(await store.SetAsync(Service, Account, "").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "replaced with an empty secret");
        Check(fake.Service.Items.Count == 1 && fake.Service.LastReplace, "CreateItem replaces the item");
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "" }, "an empty secret is not missing");
        Check(await store.GetAsync(Service, "other").AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: null }, "accounts are distinct");

        Check(await store.DeleteAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<bool>.Success { Value: true }, "deleted");
        Check(fake.Service.Items.Count == 0, "the item is gone");
        // Close is sent without waiting for a reply; let the fake service read it.
        for (int i = 0; i < 100 && fake.Service.OpenSessions != 0; i++) await Task.Delay(20);
        Check(fake.Service.OpenSessions == 0, $"every request closed its session, {fake.Service.OpenSessions} open");

        fake.Service.Items["/org/freedesktop/secrets/collection/login/raw"] = new("/org/freedesktop/secrets/collection/login", "raw",
            new() { ["service"] = Service, ["account"] = Account }, [0xff, 0xfe], "application/octet-stream");
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData },
            "a stored value that is not UTF-8 is invalid data");
        Console.WriteLine("PASS secret store: encrypted round trip.");
    }

    [Test, Explicit, Category("DBus"), DisplayName("secret store: a service without encrypted sessions falls back to plain and says so")]
    public async Task PlainSessionAsync()
    {
        await using var fake = await FakeSecretService.StartAsync();
        fake.Service.SupportsEncryption = false;
        var diagnostics = new List<PortalDiagnostic>();
        var store = new SecretServiceStore(destination: fake.Name, application: new PortalApplication(null, diagnostics.Add));
        Check(await store.SetAsync(Service, Account, "plain").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "stored");
        Check(store.LastAlgorithm == SecretServiceClient.PlainAlgorithm && diagnostics.Any(d => d.Code == "secret-service-plain-session"), "plain, diagnosed");
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "plain" }, "read back");
        Check(diagnostics.All(d => !(d.Message + d.Remediation).Contains(Account) && !(d.Message + d.Remediation).Contains(Service)), "diagnostics carry no names");
        var strict = new SecretServiceStore(destination: fake.Name, requireEncryption: true);
        Check(await strict.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Other },
            "a store that requires encryption does not fall back");
    }

    [Test, Explicit, Category("DBus"), DisplayName("secret store: a locked item is unlocked through a prompt; dismissal, an unshowable prompt and a refused unlock are typed")]
    public async Task LockedAsync()
    {
        await using var fake = await FakeSecretService.StartAsync();
        var store = new SecretServiceStore(destination: fake.Name);
        Check(await store.SetAsync(Service, Account, "locked-secret").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "stored");

        fake.Service.LockAll();
        fake.Service.Prompts = PromptBehaviour.Complete;
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "locked-secret" }, "unlocked by the prompt");
        Check(fake.Service.PromptsShown == 1 && fake.Service.LastWindowId == "", "one unparented prompt");

        fake.Service.LockAll();
        fake.Service.Prompts = PromptBehaviour.Dismiss;
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.UserDismissed },
            "a dismissed unlock prompt");
        Check(await store.SetAsync(Service, Account, "x").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed },
            "a locked collection is unlocked before storing");
        Check(await store.DeleteAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<bool>.Failed { Code: PlatformFailureCode.UserDismissed },
            "a locked item is unlocked before deleting");

        fake.Service.Prompts = PromptBehaviour.Fail;
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Locked, Diagnostic.Domain: "org.freedesktop.Secret.Error.NoPrompter" },
            "a service that cannot prompt leaves the store locked");

        fake.Service.Prompts = PromptBehaviour.None;
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Locked, Diagnostic.Domain: "org.freedesktop.Secret.Error.IsLocked" },
            "a refused unlock without a prompt is locked");

        fake.Service.Prompts = PromptBehaviour.Hold;
        using var cancellation = new CancellationTokenSource();
        fake.Service.PromptShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = store.GetAsync(Service, Account, cancellation.Token).AsTask();
        await fake.Service.PromptShown.Task.WaitAsync(Wait);
        cancellation.Cancel();
        try { await pending.WaitAsync(Wait); throw new InvalidOperationException("A cancelled prompt completed."); }
        catch (OperationCanceledException) { }
        Check(fake.Service.Dismissed == 1, "cancellation dismissed the prompt");
        Console.WriteLine("PASS secret store: locked keyring.");
    }

    [Test, Explicit, Category("DBus"), DisplayName("secret store: without a default collection, reads find nothing and a write creates it through a prompt")]
    public async Task MissingCollectionAsync()
    {
        await using var fake = await FakeSecretService.StartAsync();
        fake.Service.DefaultCollection = "/";
        var store = new SecretServiceStore(destination: fake.Name);
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: null }, "nothing to read");
        Check(await store.DeleteAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<bool>.Success { Value: false }, "nothing to delete");
        fake.Service.Prompts = PromptBehaviour.Dismiss;
        Check(await store.SetAsync(Service, Account, "s").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.UserDismissed },
            "the user declined to create a keyring");
        fake.Service.Prompts = PromptBehaviour.Complete;
        Check(await store.SetAsync(Service, Account, "s").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "stored in the new keyring");
        Check(fake.Service.DefaultCollection == "/org/freedesktop/secrets/collection/created"
            && fake.Service.Items.Values.Single().Collection == fake.Service.DefaultCollection, "created under the default alias");
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "s" }, "read back");
    }

    [Test, Explicit, Category("DBus"), DisplayName("secret store: no Secret Service on the bus is unavailable with a diagnostic")]
    public async Task MissingServiceAsync()
    {
        var diagnostics = new List<PortalDiagnostic>();
        var store = new SecretServiceStore(destination: "org.runic.NoSuchSecretService", application: new PortalApplication(null, diagnostics.Add));
        Check(await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable },
            "unavailable");
        Check(diagnostics.Any(d => d.Code == "secret-service-unavailable" && d.Remediation?.Contains("--talk-name=org.freedesktop.secrets") == true),
            "the diagnostic names the sandbox permission");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal enum PromptBehaviour { Complete, Dismiss, Fail, Hold, None }

    internal sealed record FakeItem(string Collection, string Label, Dictionary<string, string> Attributes, byte[] Secret, string ContentType)
    {
        public bool Locked { get; set; }
    }

    // The service on its own connection, owning a well-known name as a real service does, so requests take the same
    // name-owner path as for org.freedesktop.secrets.
    private sealed class FakeSecretService : IAsyncDisposable
    {
        private readonly DBusConnection _connection;
        internal FakeService Service { get; }
        internal string Name { get; } = "org.runic.tests.FakeSecretService";

        private FakeSecretService(DBusConnection connection) { _connection = connection; Service = new FakeService(connection); }

        internal static async Task<FakeSecretService> StartAsync()
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            await connection.ConnectAsync();
            var fake = new FakeSecretService(connection);
            connection.AddMethodHandler(fake.Service);
            if (!await connection.TryRequestNameAsync(fake.Name)) throw new InvalidOperationException("Cannot own the fake Secret Service name.");
            return fake;
        }

        public ValueTask DisposeAsync() { _connection.Dispose(); return ValueTask.CompletedTask; }
    }

    internal sealed class FakeService(DBusConnection connection) : IPathMethodHandler
    {
        private const string Root = "/org/freedesktop/secrets";
        public string Path => Root;
        public bool HandlesChildPaths => true;
        internal readonly Dictionary<string, FakeItem> Items = [];
        internal readonly Dictionary<string, bool> CollectionLocked = new() { [Root + "/collection/login"] = false };
        internal string DefaultCollection = Root + "/collection/login";
        internal bool SupportsEncryption = true;
        internal PromptBehaviour Prompts = PromptBehaviour.Complete;
        internal Dictionary<string, string>? LastSearch;
        internal bool LastReplace;
        internal string? LastWindowId;
        internal int PromptsShown, Dismissed, OpenSessions;
        internal volatile TaskCompletionSource PromptShown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, byte[]?> _sessions = [];
        private readonly Dictionary<string, Action<DBusConnection, string>> _prompts = [];
        private int _next;

        internal void LockAll()
        {
            foreach (var item in Items.Values) item.Locked = true;
            foreach (var collection in CollectionLocked.Keys.ToArray()) CollectionLocked[collection] = true;
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            string path = context.Request.PathAsString!, member = context.Request.MemberAsString!;
            var reader = context.Request.GetBodyReader();
            lock (this)
            {
                switch (member)
                {
                    case "OpenSession": OpenSession(context, ref reader); break;
                    case "Close": _sessions.Remove(path); OpenSessions--; Reply(context, null, static (ref MessageWriter _) => { }); break;
                    case "SearchItems":
                        LastSearch = ReadAttributes(ref reader);
                        var matches = Items.Where(item => LastSearch.All(pair => item.Value.Attributes.TryGetValue(pair.Key, out var value) && value == pair.Value)).ToArray();
                        var unlocked = matches.Where(m => !m.Value.Locked).Select(m => new ObjectPath(m.Key)).ToArray();
                        var locked = matches.Where(m => m.Value.Locked).Select(m => new ObjectPath(m.Key)).ToArray();
                        Reply(context, "aoao", (ref MessageWriter writer) => { writer.WriteArray(unlocked); writer.WriteArray(locked); });
                        break;
                    case "Unlock":
                        var objects = reader.ReadArrayOfObjectPath().Select(o => o.ToString()).ToArray();
                        if (Prompts == PromptBehaviour.None) { Reply(context, "aoo", static (ref MessageWriter writer) => { writer.WriteArray(Array.Empty<ObjectPath>()); writer.WriteObjectPath("/"); }); break; }
                        string unlockPrompt = NewPrompt((bus, prompt) => { foreach (var o in objects) SetLocked(o, false); Completed(bus, prompt, false, (ref MessageWriter w) => { w.WriteSignature("ao"); w.WriteArray(objects.Select(o => new ObjectPath(o)).ToArray()); }); });
                        var already = objects.Where(o => !IsLocked(o)).Select(o => new ObjectPath(o)).ToArray();
                        bool all = already.Length == objects.Length;
                        Reply(context, "aoo", (ref MessageWriter writer) => { writer.WriteArray(already); writer.WriteObjectPath(all ? "/" : unlockPrompt); });
                        break;
                    case "ReadAlias": Reply(context, "o", (ref MessageWriter writer) => writer.WriteObjectPath(DefaultCollection)); break;
                    case "CreateCollection":
                        string createPrompt = NewPrompt((bus, prompt) =>
                        {
                            string created = Root + "/collection/created";
                            CollectionLocked[created] = false; DefaultCollection = created;
                            Completed(bus, prompt, false, (ref MessageWriter w) => w.WriteVariantObjectPath(created));
                        });
                        Reply(context, "oo", (ref MessageWriter writer) => { writer.WriteObjectPath("/"); writer.WriteObjectPath(createPrompt); });
                        break;
                    case "CreateItem":
                        if (CollectionLocked[path]) { context.ReplyError("org.freedesktop.Secret.Error.IsLocked", "locked"); break; }
                        var properties = ReadProperties(ref reader);
                        reader.AlignStruct();
                        string session = reader.ReadObjectPathAsString();
                        byte[] parameters = reader.ReadArrayOfByte(), value = reader.ReadArrayOfByte();
                        string contentType = reader.ReadString();
                        LastReplace = reader.ReadBool();
                        var attributes = properties["org.freedesktop.Secret.Item.Attributes"].GetDictionary<string, string>();
                        foreach (var existing in Items.Where(i => i.Value.Collection == path && Same(i.Value.Attributes, attributes)).Select(i => i.Key).ToArray())
                            Items.Remove(existing);
                        string itemPath = path + "/" + ++_next;
                        Items[itemPath] = new(path, properties["org.freedesktop.Secret.Item.Label"].GetString(), attributes, Decrypt(session, parameters, value), contentType);
                        Reply(context, "oo", (ref MessageWriter writer) => { writer.WriteObjectPath(itemPath); writer.WriteObjectPath("/"); });
                        break;
                    case "GetSecret":
                        var item = Items[path];
                        if (item.Locked) { context.ReplyError("org.freedesktop.Secret.Error.IsLocked", "locked"); break; }
                        string sessionPath = reader.ReadObjectPathAsString();
                        var (iv, encrypted) = Encrypt(sessionPath, item.Secret);
                        Reply(context, "(oayays)", (ref MessageWriter writer) =>
                        {
                            writer.WriteStructureStart(); writer.WriteObjectPath(sessionPath); writer.WriteArray(iv); writer.WriteArray(encrypted); writer.WriteString(item.ContentType);
                        });
                        break;
                    case "Delete":
                        if (!Items.TryGetValue(path, out var deleted)) { context.ReplyError("org.freedesktop.Secret.Error.NoSuchObject", "gone"); break; }
                        if (deleted.Locked) { context.ReplyError("org.freedesktop.Secret.Error.IsLocked", "locked"); break; }
                        Items.Remove(path);
                        Reply(context, "o", static (ref MessageWriter writer) => writer.WriteObjectPath("/"));
                        break;
                    case "Prompt":
                        LastWindowId = reader.ReadString();
                        PromptsShown++;
                        if (Prompts == PromptBehaviour.Fail) { context.ReplyError("org.freedesktop.Secret.Error.NoPrompter", "no prompter"); break; }
                        Reply(context, null, static (ref MessageWriter _) => { });
                        PromptShown.TrySetResult();
                        if (Prompts == PromptBehaviour.Dismiss) Completed(connection, path, true, static (ref MessageWriter w) => w.WriteVariantString(""));
                        else if (Prompts == PromptBehaviour.Complete) _prompts[path](connection, path);
                        break;
                    case "Dismiss":
                        Dismissed++;
                        Reply(context, null, static (ref MessageWriter _) => { });
                        Completed(connection, path, true, static (ref MessageWriter w) => w.WriteVariantString(""));
                        break;
                    default: context.ReplyUnknownMethodError(); break;
                }
            }
            return ValueTask.CompletedTask;
        }

        private void OpenSession(MethodContext context, ref Reader reader)
        {
            string algorithm = reader.ReadString();
            var input = reader.ReadVariantValue();
            string session = Root + "/session/" + ++_next;
            if (algorithm == "plain")
            {
                _sessions[session] = null; OpenSessions++;
                Reply(context, "vo", (ref MessageWriter writer) => { writer.WriteVariantString(""); writer.WriteObjectPath(session); });
                return;
            }
            if (!SupportsEncryption || algorithm != SecretServiceClient.EncryptedAlgorithm)
            { context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "unsupported"); return; }
            var own = SecretServiceDh.PrivateKey(RandomNumberGenerator.GetBytes(SecretServiceDh.KeyBytes));
            byte[] ownPublic = SecretServiceDh.PublicKey(own);
            _sessions[session] = SecretServiceDh.DeriveKey(own, input.GetArray<byte>()); OpenSessions++;
            Reply(context, "vo", (ref MessageWriter writer) => { writer.WriteSignature("ay"); writer.WriteArray(ownPublic); writer.WriteObjectPath(session); });
        }

        private byte[] Decrypt(string session, byte[] parameters, byte[] value) =>
            _sessions[session] is { } key ? SecretServiceDh.Decrypt(key, parameters, value) : value;

        private (byte[], byte[]) Encrypt(string session, byte[] value) =>
            _sessions[session] is { } key ? SecretServiceDh.Encrypt(key, value) : ([], value);

        private bool IsLocked(string path) => Items.TryGetValue(path, out var item) ? item.Locked : CollectionLocked.GetValueOrDefault(path);

        private void SetLocked(string path, bool locked)
        {
            if (Items.TryGetValue(path, out var item)) item.Locked = locked;
            else if (CollectionLocked.ContainsKey(path)) CollectionLocked[path] = locked;
        }

        private string NewPrompt(Action<DBusConnection, string> complete)
        {
            string prompt = Root + "/prompt/" + ++_next;
            _prompts[prompt] = complete;
            return prompt;
        }

        private static bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
            left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

        private static Dictionary<string, string> ReadAttributes(ref Reader reader)
        {
            var result = new Dictionary<string, string>();
            var end = reader.ReadDictionaryStart();
            while (reader.HasNext(end)) result[reader.ReadString()] = reader.ReadString();
            return result;
        }

        private static Dictionary<string, VariantValue> ReadProperties(ref Reader reader) => reader.ReadDictionaryOfStringToVariantValue();

        private delegate void Body(ref MessageWriter writer);

        private static void Reply(MethodContext context, string? signature, Body body)
        {
            if (context.NoReplyExpected) return;
            var writer = context.CreateReplyWriter(signature);
            try { body(ref writer); context.Reply(writer.CreateMessage()); }
            finally { writer.Dispose(); }
        }

        private static void Completed(DBusConnection bus, string prompt, bool dismissed, Body result)
        {
            var writer = bus.GetMessageWriter();
            try
            {
                writer.WriteSignalHeader(path: prompt, @interface: "org.freedesktop.Secret.Prompt", member: "Completed", signature: "bv");
                writer.WriteBool(dismissed);
                result(ref writer);
                bus.TrySendMessage(writer.CreateMessage());
            }
            finally { writer.Dispose(); }
        }
    }
}
