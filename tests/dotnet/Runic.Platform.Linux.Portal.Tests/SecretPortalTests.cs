using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;
using TUnit.Core;

// Secrets in a sandbox without Secret Service access: the backend choice, libsecret's file through the store, and the
// Secret portal client against a fake portal on a private bus.
internal sealed class SecretPortalTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string Service = "org.runic.SecretTests";
    private const string Account = "user@example.com";

    [Test, DisplayName("secret portal: Flatpak uses the Secret Service only with the permission; Snap falls back to the portal")]
    public void BackendChoice()
    {
        using var scratch = new Scratch();
        string Info(string text) { string path = Path.Combine(scratch.Root, Guid.NewGuid().ToString("N")); File.WriteAllText(path, text); return path; }
        const string application = "[Application]\nname=org.example.App\n\n";
        Check(!SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Context]\nshared=network;ipc;\nsockets=x11;wayland;\n")), "no permission");
        Check(SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Session Bus Policy]\norg.freedesktop.secrets=talk\n")), "--talk-name");
        Check(SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Session Bus Policy]\norg.freedesktop.secrets=own\n")), "--own-name");
        Check(SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Session Bus Policy]\norg.freedesktop.*=talk\n")), "a prefix pattern");
        Check(!SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Session Bus Policy]\norg.freedesktop.secrets=see\n")), "see is not talk");
        Check(!SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Session Bus Policy]\norg.freedesktop.secretsx=talk\n")), "another name");
        Check(SandboxSecrets.FlatpakAllowsSecretService(Info(application + "[Context]\nsockets=x11;session-bus;\n")), "--socket=session-bus");
        Check(!SandboxSecrets.FlatpakAllowsSecretService(Path.Combine(scratch.Root, "missing")), "no file");

        var app = new PortalApplication();
        Check(SandboxSecrets.Create(app, null, SandboxSecrets.Sandbox.None), r => r is SecretServiceStore, "unsandboxed");
        Check(SandboxSecrets.Create(app, null, SandboxSecrets.Sandbox.Flatpak, Info(application)), r => r is SecretPortalStore, "Flatpak without permission");
        Check(SandboxSecrets.Create(app, new() { RequireEncryption = true }, SandboxSecrets.Sandbox.Flatpak, Info(application + "[Session Bus Policy]\norg.freedesktop.secrets=talk\n")),
            r => r is SecretServiceStore { RequireEncryption: true }, "Flatpak with permission");
        Check(SandboxSecrets.Create(app, null, SandboxSecrets.Sandbox.Snap), r => r is SnapSecretStore { UsesPortal: false }, "Snap starts with the Secret Service");
        Check(new SecretPortalStore().KeyringPath.EndsWith("/keyrings/default.keyring", StringComparison.Ordinal), "libsecret's path");
    }

    [Test, DisplayName("secret portal: the file store round-trips text and bytes, keeps other items and creates a private file")]
    public async Task FileRoundTripAsync()
    {
        using var scratch = new Scratch();
        var store = scratch.Store(Secret(1));
        Check(await store.GetAsync(Service, Account), r => r is PlatformResult<string?>.Success { Value: null }, "nothing stored");
        Check(await store.DeleteAsync(Service, Account), r => r is PlatformResult<bool>.Success { Value: false } && !File.Exists(scratch.Keyring), "nothing deleted or created");
        Check(await store.SetAsync(Service, Account, "first-tøken"), r => r is PlatformResult<PlatformUnit>.Success, "stored");
        Check((File.GetUnixFileMode(scratch.Keyring) & (UnixFileMode)0x3F) == 0 && (File.GetUnixFileMode(Path.GetDirectoryName(scratch.Keyring)!) & (UnixFileMode)0x3F) == 0,
            "the keyring and its directory are private to the user");
        Check(await store.GetAsync(Service, Account), r => r is PlatformResult<string?>.Success { Value: "first-tøken" }, "read back");
        Check(await store.GetBytesAsync(Service, Account), r => r is PlatformResult<byte[]?>.Success { Value: var utf8 } && utf8!.SequenceEqual("first-tøken"u8.ToArray()), "as bytes");
        byte[] secret = new byte[DesktopSecretStore.MaximumBinarySecretBytes];
        Random.Shared.NextBytes(secret);
        Check(await store.SetBytesAsync(Service, "binary", secret), r => r is PlatformResult<PlatformUnit>.Success, "64 KiB stored");
        Check(await store.SetBytesAsync(Service, "binary", new byte[secret.Length + 1]), r => r is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.TooLarge }, "too large");
        Check(await store.SetAsync(Service, Account, new string('x', 2561)), r => r is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.TooLarge }, "the text limit stays");
        var other = scratch.Store(Secret(1));
        Check(await other.GetBytesAsync(Service, "binary"), r => r is PlatformResult<byte[]?>.Success { Value: var read } && read!.SequenceEqual(secret), "another store reads the file");
        Check(await other.GetAsync(Service, "binary"), r => r is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData }, "bytes that are not text");
        Check(await store.SetAsync(Service, Account, "second"), r => r is PlatformResult<PlatformUnit>.Success, "replaced");
        var keyring = LibsecretKeyring.Parse(File.ReadAllBytes(scratch.Keyring));
        Check(keyring.Items.Count == 2 && keyring.UsageCount == 3, $"one item per account: {keyring.Items.Count}, usage {keyring.UsageCount}");
        Check(await store.DeleteAsync(Service, Account), r => r is PlatformResult<bool>.Success { Value: true }, "deleted");
        Check(await store.GetAsync(Service, Account), r => r is PlatformResult<string?>.Success { Value: null }, "the deleted item is gone");
        Check(await store.GetBytesAsync(Service, "binary"), r => r is PlatformResult<byte[]?>.Success { Value.Length: 65_536 }, "only that item is gone");
        Check(Directory.GetFiles(Path.GetDirectoryName(scratch.Keyring)!).Select(Path.GetFileName).Order().SequenceEqual(["default.keyring", "default.keyring.lock"]),
            "no temporary files are left");
    }

    [Test, DisplayName("secret portal: libsecret's own keyring is read and extended; another secret finds nothing and keeps its items")]
    public async Task LibsecretInteropAndWrongKeyAsync()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(Path.GetDirectoryName(scratch.Keyring)!);
        File.WriteAllBytes(scratch.Keyring, Convert.FromBase64String(LibsecretKeyringTests.Fixture));
        var store = scratch.Store(Encoding.UTF8.GetBytes(LibsecretKeyringTests.FixturePassword));
        Check(await store.GetAsync("com.example.Text", "user@example.com"), r => r is PlatformResult<string?>.Success { Value: "text-secret-é" }, "secret-tool's text item");
        Check(await store.GetBytesAsync("com.example.Bin", "bin"), r => r is PlatformResult<byte[]?>.Success { Value: [0, 1, 2, 0xFF, 0xFE] }, "secret-tool's binary item");
        Check(await store.SetAsync(Service, Account, "runic"), r => r is PlatformResult<PlatformUnit>.Success, "added");
        Check(LibsecretKeyring.Parse(File.ReadAllBytes(scratch.Keyring)).Items.Count == 3, "libsecret's items are kept");

        var wrong = scratch.Store(Secret(9));
        Check(await wrong.GetAsync("com.example.Text", "user@example.com"), r => r is PlatformResult<string?>.Success { Value: null }, "another key finds nothing");
        Check(await wrong.SetAsync(Service, "wrong", "x"), r => r is PlatformResult<PlatformUnit>.Success
            && LibsecretKeyring.Parse(File.ReadAllBytes(scratch.Keyring)).Items.Count == 4, "a write keeps the items it cannot read");
        Check(await store.GetAsync(Service, Account), r => r is PlatformResult<string?>.Success { Value: "runic" }, "and the right key still reads them");
    }

    [Test, DisplayName("secret portal: concurrent writers keep every item; a damaged file is InvalidData and never overwritten")]
    public async Task ConcurrencyAndDamageAsync()
    {
        using var scratch = new Scratch();
        var stores = Enumerable.Range(0, 4).Select(_ => scratch.Store(Secret(2))).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => stores[i % stores.Length].SetAsync(Service, "account-" + i, "value-" + i).AsTask()));
        for (int i = 0; i < 24; i++)
            Check(await stores[0].GetAsync(Service, "account-" + i), r => r is PlatformResult<string?>.Success { Value: var value } && value == "value-" + i, $"account-{i} survived");

        var diagnostics = new ConcurrentQueue<PortalDiagnostic>();
        var store = scratch.Store(Secret(2), new PortalApplication(null, diagnostics.Enqueue));
        byte[] file = File.ReadAllBytes(scratch.Keyring);
        var keyring = LibsecretKeyring.Parse(file);
        byte[] key = keyring.DeriveKey(Secret(2));
        var search = new Dictionary<string, string> { ["service"] = Service, ["account"] = "account-23" };
        keyring.Items.Single(item => LibsecretKeyring.Matches(item.Attributes, key, search)).Blob[5] ^= 0x55;
        File.WriteAllBytes(scratch.Keyring, keyring.Serialize());
        Check(await store.GetAsync(Service, "account-23"), r => r is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData }, "a damaged item is InvalidData");
        Check(await store.GetAsync(Service, "account-22"), r => r is PlatformResult<string?>.Success { Value: "value-22" }, "the other items still read");
        File.WriteAllBytes(scratch.Keyring, file[..100]);
        Check(await store.GetAsync(Service, "account-0"), r => r is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData }, "a truncated file is InvalidData");
        Check(await store.SetAsync(Service, "account-0", "x"), r => r is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.InvalidData }
            && File.ReadAllBytes(scratch.Keyring).Length == 100, "a damaged file is not overwritten");
        Check(diagnostics.Any(d => d.Code == "secret-file-invalid") && diagnostics.All(d => !(d.Message + d.Remediation).Contains(Service)), "diagnosed without names");
    }

    // libsecret's own client against a file Runic wrote, and the reverse: secret-tool with libsecret's file backend and
    // a test password, which never touches a keyring daemon (CI installs libsecret-tools).
    [Test, Explicit, Category("SecretService"), DisplayName("secret portal: secret-tool's file backend reads what Runic wrote, and Runic reads what secret-tool wrote")]
    public async Task SecretToolFileBackendAsync()
    {
        string secretTool = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(d => Path.Combine(d, "secret-tool")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("secret-tool is not installed.");
        using var scratch = new Scratch();
        // An ASCII password: the environment variable that carries it to secret-tool is text.
        byte[] password = Encoding.ASCII.GetBytes(new string('k', 32) + Guid.NewGuid().ToString("N"));
        var store = scratch.Store(password);
        Check(await store.SetAsync(Service, Account, "from-runic-é"), r => r is PlatformResult<PlatformUnit>.Success, "stored");
        Check(await store.SetBytesAsync(Service, "binary", "bytes\u0001\u0002"u8.ToArray()), r => r is PlatformResult<PlatformUnit>.Success, "bytes stored");
        Check(SecretTool(secretTool, scratch.Keyring, password, null, "lookup", "service", Service, "account", Account) == "from-runic-é", "secret-tool reads Runic's text");
        Check(SecretTool(secretTool, scratch.Keyring, password, null, "lookup", "service", Service, "account", "binary") == "bytes\u0001\u0002", "secret-tool reads Runic's bytes");
        string search = SecretTool(secretTool, scratch.Keyring, password, null, "search", "--all", "service", Service, "account", Account);
        Check(search.Contains($"label = {Service} ({Account})", StringComparison.Ordinal) && search.Contains("org.freedesktop.Secret.Generic", StringComparison.Ordinal),
            $"secret-tool sees the label and schema: {search}");
        SecretTool(secretTool, scratch.Keyring, password, "from-secret-tool", "store", "--label=x", "service", Service, "account", "tool");
        Check(await store.GetAsync(Service, "tool"), r => r is PlatformResult<string?>.Success { Value: "from-secret-tool" }, "Runic reads secret-tool's item");
        // secret-tool stores without the schema attribute, so libsecret adds a second item next to Runic's (its replace
        // matches the whole attribute set); a read returns the newer one, and Runic's next write replaces both.
        SecretTool(secretTool, scratch.Keyring, password, "newer-from-tool", "store", "--label=x", "service", Service, "account", Account);
        Check(await store.GetAsync(Service, Account), r => r is PlatformResult<string?>.Success { Value: "newer-from-tool" }, "the newer item is read");
        Check(await store.SetAsync(Service, Account, "runic-again"), r => r is PlatformResult<PlatformUnit>.Success
            && SecretTool(secretTool, scratch.Keyring, password, null, "search", "--all", "service", Service, "account", Account).Split("[no path]").Length == 2,
            "a write leaves one item for the account");
        SecretTool(secretTool, scratch.Keyring, password, null, "clear", "service", Service, "account", "binary");
        Check(await store.GetBytesAsync(Service, "binary"), r => r is PlatformResult<byte[]?>.Success { Value: null }, "secret-tool cleared Runic's item");
        Console.WriteLine("PASS libsecret's file backend and Runic share one keyring file.");
    }

    [Test, Explicit, Category("DBus"), DisplayName("secret portal: RetrieveSecret through a fake portal; dismissed, failed and missing portals are typed")]
    public async Task FakePortalAsync()
    {
        using var scratch = new Scratch();
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        var portal = new FakeSecretPortal(connection) { Secret = Secret(4) };
        connection.AddMethodHandler(portal);
        var diagnostics = new ConcurrentQueue<PortalDiagnostic>();
        var application = new PortalApplication(null, diagnostics.Enqueue);
        SecretPortalStore Store() => new(destination: connection.UniqueName!, application: application, path: scratch.Keyring);
        // How long the last read took, the fake's calls and the store's diagnostics, so a failure tells a fixture race from a
        // store result: a read that took less than SecretPortalStore.ActivationRetryDelay did not retry.
        var took = TimeSpan.Zero;
        async Task<PlatformResult<string?>> GetAsync(SecretPortalStore store)
        {
            var clock = Stopwatch.StartNew();
            try { return await store.GetAsync(Service, Account).AsTask().WaitAsync(Wait); }
            finally { took = clock.Elapsed; }
        }
        string State() => $"after {took.TotalMilliseconds:0} ms, {portal.Calls} RetrieveSecret calls, {portal.Closes} Request.Close calls; diagnostics: [" +
            string.Join("; ", diagnostics.Select(d => d.Code + ": " + d.Message)) + "]";

        var store = Store();
        Check(await store.SetAsync(Service, Account, "via-portal").AsTask().WaitAsync(Wait), r => r is PlatformResult<PlatformUnit>.Success, $"stored, {State()}");
        Check(await GetAsync(store), r => r is PlatformResult<string?>.Success { Value: "via-portal" }, $"read back, {State()}");
        Check(portal.Calls == 1 && portal.HandleToken?.StartsWith("runic_", StringComparison.Ordinal) == true, $"the secret is retrieved once per store: {State()}");
        Check(await scratch.Store(Secret(4)).GetAsync(Service, Account), r => r is PlatformResult<string?>.Success { Value: "via-portal" }, "the portal's secret is the password");

        portal.Response = 1;
        var dismissed = Store();
        Check(await GetAsync(dismissed), r => r is PlatformResult<string?>.Failed { Code: PlatformFailureCode.UserDismissed }, $"dismissed, {State()}");
        portal.Response = 2;
        Check(await GetAsync(dismissed), r => r is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Other }, $"failed, {State()}");
        portal.Response = 0;
        portal.Secret = Secret(4)[..32];
        Check(await GetAsync(dismissed), r => r is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData },
            $"a secret of another size than libsecret accepts, {State()}");
        portal.Secret = Secret(4);
        Check(await GetAsync(dismissed), r => r is PlatformResult<string?>.Success { Value: "via-portal" }, $"a failed retrieval is retried, {State()}");

        // The portal that has not exported Secret yet when the activating call arrives: one more attempt succeeds.
        portal.Reset(missingFor: 1);
        Check(await GetAsync(Store()), r => r is PlatformResult<string?>.Success { Value: "via-portal" } && portal.Calls == 2,
            $"retried once after UnknownMethod, {State()}");
        // Still UnknownMethod on the second attempt: reported as before, after exactly two calls.
        portal.Reset(missingFor: 2);
        Check(await GetAsync(Store()), r => r is PlatformResult<string?>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable } && portal.Calls == 2,
            $"one retry only, {State()}");

        portal.Reset(missingFor: 0, missing: true);
        Check(await GetAsync(Store()), r => r is PlatformResult<string?>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable } && portal.Calls == 2,
            $"no Secret portal, {State()}");
        Check(diagnostics.Any(d => d.Code == "secret-portal-unavailable" && d.Remediation?.Contains("--talk-name=org.freedesktop.secrets") == true), $"diagnosed, {State()}");
        var gone = new SecretPortalStore(destination: "org.runic.tests.NoSuchPortal", application: application, path: scratch.Keyring);
        Check(await GetAsync(gone), r => r is PlatformResult<string?>.Unavailable, $"no portal at all, {State()}");
        Console.WriteLine("PASS Secret portal: retrieved through the pipe, dismissed, failed, wrong size and missing.");
    }

    // The real xdg-desktop-portal with GNOME Keyring's org.freedesktop.impl.portal.Secret backend, in the Fedora container
    // (eng/ci/real-portal.sh), which starts an unlocked login keyring on the private bus and sets RUNIC_TEST_REAL_PORTAL.
    // The test runs as a host process: the portal serves it too, so no sandbox is needed to exercise the pipe, the
    // Request and the backend; the sandbox choice itself has its portable tests.
    [Test, Explicit, Category("RealPortalSecret"), DisplayName("secret portal: the real Secret portal and GNOME Keyring's backend key the file store")]
    public async Task RealPortalAsync()
    {
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_REAL_PORTAL") is null)
            throw new InvalidOperationException("Run through eng/ci/real-portal.sh: it starts the portal and an unlocked GNOME Keyring on a private bus.");
        using var scratch = new Scratch();
        var diagnostics = new List<PortalDiagnostic>();
        var store = new SecretPortalStore(application: new PortalApplication(null, diagnostics.Add), path: scratch.Keyring);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var stored = await store.SetAsync(Service, Account, "from-the-real-portal", deadline.Token);
        Console.WriteLine($"Stored through the real Secret portal: {stored}; diagnostics: {string.Join(", ", diagnostics.Select(d => d.Code + " " + d.Message))}");
        Check(stored, r => r is PlatformResult<PlatformUnit>.Success, "stored");
        byte[] secret = new byte[DesktopSecretStore.MaximumBinarySecretBytes];
        Random.Shared.NextBytes(secret);
        Check(await store.SetBytesAsync(Service, "binary", secret, deadline.Token), r => r is PlatformResult<PlatformUnit>.Success, "64 KiB stored");
        // A new store retrieves the secret again: the backend keeps one per application.
        var again = new SecretPortalStore(path: scratch.Keyring);
        Check(await again.GetAsync(Service, Account, deadline.Token), r => r is PlatformResult<string?>.Success { Value: "from-the-real-portal" }, "a second retrieval opens the file");
        Check(await again.GetBytesAsync(Service, "binary", deadline.Token), r => r is PlatformResult<byte[]?>.Success { Value: var read } && read!.SequenceEqual(secret), "64 KiB read back");
        Check(await again.DeleteAsync(Service, Account, deadline.Token), r => r is PlatformResult<bool>.Success { Value: true }, "deleted");
        Console.WriteLine("PASS the real Secret portal (xdg-desktop-portal with GNOME Keyring) keys libsecret's file.");
    }

    private static byte[] Secret(byte seed) => Enumerable.Range(0, SecretPortalStore.PortalSecretSize).Select(i => (byte)(i * 31 + seed)).ToArray();

    private static string SecretTool(string tool, string keyring, byte[] password, string? input, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // The file backend with a test password, and no bus: nothing reaches a keyring daemon.
        start.Environment["SECRET_BACKEND"] = "file";
        start.Environment["SECRET_FILE_TEST_PATH"] = keyring;
        start.Environment["SECRET_FILE_TEST_PASSWORD"] = Encoding.ASCII.GetString(password);
        start.Environment["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/nonexistent/runic-secret-tests";
        using var process = Process.Start(start)!;
        if (input is not null) process.StandardInput.Write(input);
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"secret-tool {arguments[0]} exited with {process.ExitCode}: {error}");
        return output;
    }

    private sealed class Scratch : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "runic-secret-portal-" + Guid.NewGuid().ToString("N")[..8]);
        internal string Keyring => Path.Combine(Root, "data", "keyrings", "default.keyring");
        internal Scratch() => Directory.CreateDirectory(Root);
        internal SecretPortalStore Store(byte[] secret, PortalApplication? application = null) =>
            new(application: application, path: Keyring, secretSource: _ => Task.FromResult(secret.ToArray()));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    // org.freedesktop.portal.Secret on the test's connection: writes the secret into the descriptor, closes it, answers
    // through a Request.Response, and answers and counts Request.Close on its requests. The test thread sets the
    // behaviour while the connection's receive loop reads it, so every field is read and written under the lock.
    private sealed class FakeSecretPortal(DBusConnection connection) : IPathMethodHandler
    {
        private readonly Lock _gate = new();
        private byte[] _secret = [];
        private uint _response;
        private bool _missing;
        private int _missingFor;
        private int _calls;
        private int _closes;
        private string? _handleToken;

        public string Path => "/org/freedesktop/portal/desktop";
        public bool HandlesChildPaths => true;
        public byte[] Secret { get { lock (_gate) return _secret; } set { lock (_gate) _secret = value; } }
        public uint Response { get { lock (_gate) return _response; } set { lock (_gate) _response = value; } }
        // RetrieveSecret calls, including those answered with UnknownMethod.
        public int Calls { get { lock (_gate) return _calls; } }
        public int Closes { get { lock (_gate) return _closes; } }
        public string? HandleToken { get { lock (_gate) return _handleToken; } }

        // Starts counting calls again. missingFor answers this many RetrieveSecret calls with UnknownMethod first, as
        // xdg-desktop-portal 1.22 does for the call that activates it (I-198); missing answers every call so.
        public void Reset(int missingFor, bool missing = false)
        {
            lock (_gate) { _missingFor = missingFor; _missing = missing; _calls = 0; }
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            if (request.PathAsString != Path)
            {
                if (request.InterfaceAsString == "org.freedesktop.portal.Request" && request.MemberAsString == "Close")
                {
                    lock (_gate) _closes++;
                    using var closed = context.CreateReplyWriter(null);
                    context.Reply(closed.CreateMessage());
                }
                else context.ReplyUnknownMethodError();
                return ValueTask.CompletedTask;
            }
            if (request.InterfaceAsString != "org.freedesktop.portal.Secret" || request.MemberAsString != "RetrieveSecret")
            { context.ReplyUnknownMethodError(); return ValueTask.CompletedTask; }
            byte[] secret;
            uint response;
            lock (_gate)
            {
                _calls++;
                if (_missing || _missingFor > 0)
                {
                    if (_missingFor > 0) _missingFor--;
                    context.ReplyUnknownMethodError();
                    return ValueTask.CompletedTask;
                }
                secret = _secret;
                response = _response;
            }
            var reader = request.GetBodyReader();
            string? token = null;
            using (var handle = reader.ReadHandle<SafeFileHandle>())
            {
                var dictionary = reader.ReadDictionaryStart();
                while (reader.HasNext(dictionary))
                {
                    string key = reader.ReadString();
                    var value = reader.ReadVariantValue();
                    if (key == "handle_token") token = value.GetString();
                }
                if (response == 0)
                {
                    using var pipe = new FileStream(handle!, FileAccess.Write, 1);
                    pipe.Write(secret);
                }
            }
            lock (_gate) _handleToken = token;
            string path = Path + "/request/" + request.SenderAsString![1..].Replace('.', '_') + "/" + token;
            using (var reply = context.CreateReplyWriter("o"))
            {
                reply.WriteObjectPath(new ObjectPath(path));
                context.Reply(reply.CreateMessage());
            }
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: path, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
            signal.WriteUInt32(response);
            var results = signal.WriteDictionaryStart();
            signal.WriteDictionaryEnd(results);
            connection.TrySendMessage(signal.CreateMessage());
            return ValueTask.CompletedTask;
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Secret portal check failed: " + message); }

    // A check of a result that reports the result it got.
    private static void Check<T>(T actual, Func<T, bool> condition, string message)
    {
        if (!condition(actual)) throw new InvalidOperationException($"Secret portal check failed: {message}; got {Describe(actual)}");
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        PlatformResult<byte[]?>.Success { Value: { } bytes } => $"Success {{ {bytes.Length} bytes }}",
        _ => value.ToString() ?? value.GetType().Name,
    };
}
