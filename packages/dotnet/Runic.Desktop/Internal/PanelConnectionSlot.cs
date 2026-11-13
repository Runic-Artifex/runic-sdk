namespace Runic.Desktop.Internal;

// The page connection slot a WebSocket was admitted to (sdk#864, D062.1). A window's surface has one slot for its page
// and, while a side panel is open, one for the panel's page: another page of the same surface, served from the same
// origin with the same profile, that connects to the same window and so to the same ViewModel tree.
internal enum DesktopConnectionSlot
{
    // The window's page: admitted with the surface's session credential, or in region mode with its frame token.
    Page = 0,

    // The side panel's page: admitted only with the panel credential (PanelConnectionSlot). The window's Views producer
    // does not grant it navigation-path/1, so it neither follows nor drives the window's route path, and window-level
    // page operations (NavigateAsync, ExecuteJavaScriptAsync) address the page only. Drops are refused on it for now
    // (D062.6): only the page's view routes drops. It may show regions.
    SidePanel = 1,
}

// The side panel's connection slot of a window: its credential, minted when the panel opens and revoked when it closes.
//
// - The credential is a 256-bit random value of its own, independent of the session credential, and handed only to the
//   panel's view through that view's document-start script (WebUiWindow.CreatePanelDocumentStartScript). The served
//   bootstrap scripts never carry it.
// - The page offers it as the session credential is offered, as the WebSocket subprotocol "runic-desktop.{credential}",
//   so the slot follows from the credential alone: the page cannot choose it. The session credential admits to the
//   page's slot and the panel credential to the panel's; a page that offers both is refused.
// - The slot admits one connection at a time, as the page's slot does, and every other admission rule applies to it
//   too: origin, client cookie and launch binding.
// - Closing revokes the credential; WebUiWindow then closes the panel's connections. A new open mints a new credential,
//   so one revoked stays revoked. A reload of the panel's page reconnects with the same credential while it is open.
// - Region mode (FrameLaunchBinding) admits only its frame: the panel credential admits nothing there.
internal sealed class PanelConnectionSlot
{
    private readonly object _gate = new();
    private string? _credential;
    private string? _protocol;
    // Changes with every open and close, so an admission that raced a close is caught once its session is listed.
    private long _generation;

    internal bool IsOpen
    {
        get { lock (_gate) return _credential is not null; }
    }

    internal long Generation
    {
        get { lock (_gate) return _generation; }
    }

    // The credential while the slot is open, for the panel view's document-start script.
    internal string? Credential
    {
        get { lock (_gate) return _credential; }
    }

    // Mints a new credential, revoking an earlier one. Returns it.
    internal string Open()
    {
        var credential = Convert.ToBase64String(OsRandom.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            _credential = credential;
            _protocol = "runic-desktop." + credential;
            _generation++;
        }
        return credential;
    }

    // Revokes the credential. False when the slot was not open.
    internal bool Close()
    {
        lock (_gate)
        {
            if (_credential is null) return false;
            _credential = null;
            _protocol = null;
            _generation++;
            return true;
        }
    }

    // The subprotocol a panel page offers, when one of offered is this slot's, and the generation it was admitted at.
    internal string? Admit(IEnumerable<string> offered, out long generation)
    {
        string? expected;
        lock (_gate)
        {
            expected = _protocol;
            generation = _generation;
        }
        return expected is not null && WebUiWindow.OffersCredential(offered, expected) ? expected : null;
    }

    // Whether a WebSocket admitted at generation still holds the slot: no close or new open came since.
    internal bool Holds(long generation)
    {
        lock (_gate) return _credential is not null && _generation == generation;
    }
}
