using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

// Which secret store a process uses (D037.6): outside a sandbox the Secret Service. Inside Flatpak the Secret Service
// when /.flatpak-info grants talking to org.freedesktop.secrets (or the whole session bus), so applications that hold
// that permission keep finding their keyring items; otherwise the Secret portal with libsecret's file. Inside Snap the
// Secret Service while the password-manager-service plug answers, otherwise the portal.
internal static class SandboxSecrets
{
    internal enum Sandbox { None, Flatpak, Snap }

    internal const string FlatpakInfo = "/.flatpak-info";

    internal static Sandbox Detect() =>
        File.Exists(FlatpakInfo) ? Sandbox.Flatpak
        : Environment.GetEnvironmentVariable("SNAP_NAME") is not null || Environment.GetEnvironmentVariable("SNAP") is not null ? Sandbox.Snap
        : Sandbox.None;

    internal static IDesktopSecretStore Create(PortalApplication application, DesktopSecretStoreOptions? options,
        Sandbox sandbox, string flatpakInfo = FlatpakInfo)
    {
        var service = new SecretServiceStore(application: application, requireEncryption: options?.RequireEncryption ?? false,
            interactive: options?.AllowUserInteraction ?? true);
        return sandbox switch
        {
            Sandbox.Flatpak => FlatpakAllowsSecretService(flatpakInfo) ? service : new SecretPortalStore(application: application),
            Sandbox.Snap => new SnapSecretStore(service, new SecretPortalStore(application: application)),
            _ => service,
        };
    }

    // [Context] sockets=session-bus, or a [Session Bus Policy] entry that lets the application talk to or own
    // org.freedesktop.secrets, possibly through a "prefix.*" pattern.
    internal static bool FlatpakAllowsSecretService(string path)
    {
        if (DesktopKeyFile.Load(path) is not { } info) return false;
        if (info.List("Context", "sockets").Contains("session-bus")) return true;
        const string name = SecretServiceStore.ServiceName;
        foreach (string key in info.Keys("Session Bus Policy"))
        {
            bool matches = key == name || (key.EndsWith(".*", StringComparison.Ordinal) && name.StartsWith(key[..^1], StringComparison.Ordinal));
            if (matches && info.Raw("Session Bus Policy", key)?.Trim() is "talk" or "own") return true;
        }
        return false;
    }
}

// Snap: the Secret Service until it reports no service, then the Secret portal for the store's lifetime.
internal sealed class SnapSecretStore(SecretServiceStore secretService, SecretPortalStore secretPortal) : IDesktopSecretStore
{
    private volatile bool _portal;

    internal bool UsesPortal => _portal;

    public ValueTask<PlatformResult<string?>> GetAsync(string service, string account, CancellationToken cancellationToken = default) =>
        RunAsync(store => store.GetAsync(service, account, cancellationToken));

    public ValueTask<PlatformResult<byte[]?>> GetBytesAsync(string service, string account, CancellationToken cancellationToken = default) =>
        RunAsync(store => store.GetBytesAsync(service, account, cancellationToken));

    public ValueTask<PlatformResult<PlatformUnit>> SetAsync(string service, string account, string secret, CancellationToken cancellationToken = default) =>
        RunAsync(store => store.SetAsync(service, account, secret, cancellationToken));

    public ValueTask<PlatformResult<PlatformUnit>> SetBytesAsync(string service, string account, ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken = default) =>
        RunAsync(store => store.SetBytesAsync(service, account, secret, cancellationToken));

    public ValueTask<PlatformResult<bool>> DeleteAsync(string service, string account, CancellationToken cancellationToken = default) =>
        RunAsync(store => store.DeleteAsync(service, account, cancellationToken));

    private async ValueTask<PlatformResult<T>> RunAsync<T>(Func<IDesktopSecretStore, ValueTask<PlatformResult<T>>> request)
    {
        if (_portal) return await request(secretPortal).ConfigureAwait(false);
        var result = await request(secretService).ConfigureAwait(false);
        if (result is not PlatformResult<T>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }) return result;
        _portal = true;
        return await request(secretPortal).ConfigureAwait(false);
    }
}
