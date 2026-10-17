using System.Diagnostics;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;
using TUnit.Core;

// A real Secret Service on a private session bus: CI starts GNOME Keyring with an unlocked login keyring and sets
// RUNIC_TEST_SECRET_SERVICE. The variable keeps the case away from a developer's own keyring.
internal sealed class SecretServiceNativeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Test, Explicit, Category("SecretService"), DisplayName("secret store: GNOME Keyring over an encrypted session, read by secret-tool, then locked")]
    public async Task GnomeKeyringAsync()
    {
        if (Environment.GetEnvironmentVariable("RUNIC_TEST_SECRET_SERVICE") != "gnome-keyring")
            throw new InvalidOperationException("Run under dbus-run-session with an unlocked GNOME Keyring and RUNIC_TEST_SECRET_SERVICE=gnome-keyring.");
        string service = "org.runic.tests." + Guid.NewGuid().ToString("N");
        const string account = "user@example.com";
        var diagnostics = new List<PortalDiagnostic>();
        // Requiring encryption makes a fallback to the plain algorithm fail the case.
        var store = new SecretServiceStore(application: new PortalApplication(null, diagnostics.Add), requireEncryption: true);

        Check(await store.GetAsync(service, account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: null }, "nothing stored yet");
        var stored = await store.SetAsync(service, account, "first-tøken").AsTask().WaitAsync(Wait);
        Check(stored is PlatformResult<PlatformUnit>.Success, $"stored: {stored}; diagnostics: {string.Join(", ", diagnostics.Select(d => d.Code))}");
        Check(store.LastAlgorithm == SecretServiceClient.EncryptedAlgorithm, $"negotiated {store.LastAlgorithm}");
        Check(await store.GetAsync(service, account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "first-tøken" }, "read back");
        Check(await store.SetAsync(service, account, "second").AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success, "replaced");
        Check(await store.GetAsync(service, account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: "second" }, "the replacement is read");
        string largest = new('x', 2560);
        Check(await store.SetAsync(service, "large", largest).AsTask().WaitAsync(Wait) is PlatformResult<PlatformUnit>.Success
            && await store.GetAsync(service, "large").AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: var read } && read == largest,
            "2560 bytes round-trip");

        // libsecret's own client finds the item by its attributes, and its label is readable.
        if (FindOnPath("secret-tool") is { } secretTool)
        {
            Check(Run(secretTool, "lookup", "service", service, "account", account) == "second", "secret-tool reads the secret");
            string search = Run(secretTool, "search", "--all", "service", service, "account", account);
            Check(search.Contains($"label = {service} ({account})", StringComparison.Ordinal)
                && search.Contains("org.freedesktop.Secret.Generic", StringComparison.Ordinal), $"secret-tool search: {search}");
            Console.WriteLine("secret-tool read the item.");
        }
        else Console.WriteLine("secret-tool is not installed; skipped the libsecret check.");

        Check(await store.DeleteAsync(service, account).AsTask().WaitAsync(Wait) is PlatformResult<bool>.Success { Value: true }, "deleted");
        Check(await store.DeleteAsync(service, account).AsTask().WaitAsync(Wait) is PlatformResult<bool>.Success { Value: false }, "deleted once");
        Check(await store.GetAsync(service, account).AsTask().WaitAsync(Wait) is PlatformResult<string?>.Success { Value: null }, "gone");

        // Locking the keyring: without a desktop the unlock prompt cannot be shown, which is a typed failure.
        await LockAllAsync();
        using var deadline = new CancellationTokenSource(Wait);
        var locked = await store.GetAsync(service, "large", deadline.Token);
        Console.WriteLine($"Read from a locked keyring: {locked}");
        Check(locked is PlatformResult<string?>.Failed { Code: PlatformFailureCode.Locked or PlatformFailureCode.UserDismissed }, $"a locked keyring is typed: {locked}");
        Console.WriteLine("PASS secret store: GNOME Keyring round trip over dh-ietf1024-sha256-aes128-cbc-pkcs7, typed locked keyring.");
    }

    private static async Task LockAllAsync()
    {
        using var connection = new DBusConnection(DBusAddress.Session!);
        await connection.ConnectAsync();
        var writer = connection.GetMessageWriter();
        MessageBuffer message;
        try
        {
            writer.WriteMethodCallHeader(destination: "org.freedesktop.secrets", path: "/org/freedesktop/secrets",
                @interface: "org.freedesktop.Secret.Service", member: "Lock", signature: "ao");
            writer.WriteArray(new[] { new ObjectPath("/org/freedesktop/secrets/aliases/default") });
            message = writer.CreateMessage();
        }
        finally { writer.Dispose(); }
        await connection.CallMethodAsync(message);
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(directory => Path.Combine(directory, name)).FirstOrDefault(File.Exists);

    private static string Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
