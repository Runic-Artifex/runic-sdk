namespace Runic.Desktop.Internal;

// The web view data stores of runic-host/1 hosted windows (specs/desktop/host-protocol.md, Page admission): one for each
// host session, not persisted, shared by that session's windows and by no other window, so a guest's server never
// receives the host's cookies and two guests never share storage. A session names its store with the key Begin returns
// and ends it with End; a hosted window carries the key (DesktopWindowHostOptions.EphemeralDataStore):
// - WebView2: a user data folder of the session in the temporary folder (Folder), deleted after End;
// - WKWebView: a non-persistent WKWebsiteDataStore, WebKitGTK 4.1 an ephemeral WebKitWebContext, WebKitGTK 6
//   (Runic.Desktop.Gtk4) an ephemeral WebKitNetworkSession, each kept by the engine while a window of the key is open
//   (EphemeralDataStoreCache).
internal static class EphemeralDataStores
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> Folders = new(StringComparer.Ordinal);

    internal const string DataStoreEndedCode = "hosted-data-store-ended";

    // A new key for one host session.
    internal static string Begin()
    {
        var key = "runic-hosted-" + Guid.NewGuid().ToString("N");
        lock (Gate) Folders.Add(key, null);
        return key;
    }

    // A window opens only for a session that has not ended.
    internal static void RequireActive(string key)
    {
        lock (Gate)
        {
            if (!Folders.ContainsKey(key))
                throw new DesktopException(DesktopErrorCategory.Unavailable, DataStoreEndedCode,
                    "The hosted window's data store belongs to a host session that has ended.");
        }
    }

    // WebView2: the session's user data folder, a fresh owner-only directory in the temporary folder.
    internal static string Folder(string key)
    {
        lock (Gate)
        {
            RequireActive(key);
            if (Folders[key] is { } existing) return existing;
            var folder = Directory.CreateTempSubdirectory("runic-desktop-hosted-").FullName;
            // WebUiApplication.DeleteAllProfiles removes it too, should the deletion after the session fail.
            WebUiApplication.RegisterGeneratedProfile(folder);
            Folders[key] = folder;
            return folder;
        }
    }

    // The session ended: no window opens for the key any more, and the WebView2 folder is deleted once the browser
    // processes let go of it.
    internal static void End(string key)
    {
        string? folder;
        lock (Gate)
        {
            if (!Folders.Remove(key, out folder)) return;
        }
        if (folder is not null) _ = DeleteFolderAsync(folder);
    }

    // WebView2's browser processes can hold the folder for a while after the last window closed. A folder that still
    // cannot be deleted stays registered, so WebUiApplication.DeleteAllProfiles removes it later.
    private static async Task DeleteFolderAsync(string folder)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (WebUiApplication.TryDeleteGeneratedProfile(folder)) return;
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
    }
}
