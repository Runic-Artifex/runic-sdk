namespace Runic.Desktop.Internal;

// The main menu of the macOS WKWebView host: the standard application, Edit and Window menus, and the View menu's
// zoom items (MacOsPageZoom). Edit and Window items send AppKit's standard actions along the responder chain, so the
// focused WKWebView cuts, copies and pastes, and the window minimizes. The host completes an empty main menu, such as
// the one AppKit gives an unbundled process on macOS 26, and leaves an application's own main menu alone, adding only
// the zoom items to it.
internal static class MacOsMainMenu
{
    internal const ulong Command = 1UL << 20, Option = 1UL << 19, Shift = 1UL << 17;
    // Modifiers a key equivalent can use; others, such as the keypad flag, do not take part in matching.
    internal const ulong KeyModifiers = Command | Option | Shift | (1UL << 18);

    /// <summary>A menu item. A null action is a separator; a null target sends the action along the responder chain.</summary>
    internal readonly record struct Item(string Title, string? Action, string KeyEquivalent = "", ulong Modifiers = Command, bool OwnTarget = false);

    internal static readonly Item Separator = new("", null);

    // The English titles, which DesktopHostOptions.StandardMenuTitles replaces.
    internal static readonly DesktopStandardMenuTitles English = new();

    // {0} is the application's name.
    internal static readonly Item[] Application = ApplicationItems(English);

    internal static Item[] ApplicationItems(DesktopStandardMenuTitles titles) =>
    [
        new(titles.About, "orderFrontStandardAboutPanel:", "", 0),
        Separator,
        new(titles.Hide, "hide:", "h"),
        new(titles.HideOthers, "hideOtherApplications:", "h", Command | Option),
        new(titles.ShowAll, "unhideAllApplications:", "", 0),
        Separator,
        // Under the Desktop event loop, AppKit's terminate:, which takes Runic's one quit path
        // (DesktopHostOptions.ConfirmQuitAsync, then each close guard); without it, asks each window to close.
        new(titles.Quit, QuitAction, "q", Command, OwnTarget: true),
    ];

    internal static readonly Item[] Edit = EditItems(English);

    internal static Item[] EditItems(DesktopStandardMenuTitles titles) =>
    [
        new(titles.Undo, "undo:", "z"),
        new(titles.Redo, "redo:", "z", Command | Shift),
        Separator,
        new(titles.Cut, "cut:", "x"),
        new(titles.Copy, "copy:", "c"),
        new(titles.Paste, "paste:", "v"),
        new(titles.SelectAll, "selectAll:", "a"),
    ];

    // The Cocoa template's File menu. Close sends performClose:, which asks the window's delegate as its close button does.
    internal static readonly Item[] File = FileItems(English);

    internal static Item[] FileItems(DesktopStandardMenuTitles titles) =>
    [
        new(titles.Close, "performClose:", "w"),
    ];

    internal static readonly string[] FileActions = ["performClose:"];

    // Open Recent (MacOSDesktopOptions.OpenRecentMenu), first in the standard File menu: a submenu that Runic fills from
    // NSDocumentController's recent documents as it opens, above Clear Menu, which sends clearRecentDocuments: to the
    // document controller. A chosen document is delivered as an open-documents event (MacOsApplication.OpenFileUrls).
    internal const string OpenRecentIdentifier = "runic.menu.file.openRecent";
    internal const string OpenRecentAction = "runicOpenRecentDocument:";
    internal const string ClearRecentAction = "clearRecentDocuments:";

    /// <summary>
    /// The title of a recent document's item: its display name, and, as AppKit's own menu does, the name of its folder
    /// after an en dash when another recent document has the same display name.
    /// </summary>
    internal static string RecentDocumentTitle(string name, string path, IEnumerable<(string Path, string Name)> documents)
    {
        if (!documents.Any(other => other.Name == name && other.Path != path)) return name;
        var folder = Path.GetFileName(Path.GetDirectoryName(path.TrimEnd('/')) ?? "");
        return folder.Length == 0 ? name : $"{name} – {folder}";
    }

    internal static readonly Item[] Window = WindowItems(English);

    internal static Item[] WindowItems(DesktopStandardMenuTitles titles) =>
    [
        new(titles.Minimize, "performMiniaturize:", "m"),
        new(titles.Zoom, "performZoom:", "", 0),
        Separator,
        new(titles.BringAllToFront, "arrangeInFront:", "", 0),
    ];

    internal const string QuitAction = "runicQuit:";

    // Identifiers of the menus the host creates, which it finds again by them rather than by their English titles.
    internal const string EditIdentifier = "runic.menu.edit", ViewIdentifier = "runic.menu.view", WindowIdentifier = "runic.menu.window";

    // Actions that identify a menu that an application or AppKit made: the Edit menu copies; the View menu toggles
    // full screen or the sidebar; the Window menu minimizes.
    internal static readonly string[] EditActions = ["copy:", "paste:"];
    internal static readonly string[] ViewActions = ["toggleFullScreen:", "toggleSidebar:", "toggleToolbarShown:"];
    internal static readonly string[] WindowActions = ["performMiniaturize:", "arrangeInFront:"];

    /// <summary>Whether a key equivalent with these modifiers is Command and the key, as the zoom items are.</summary>
    internal static bool IsCommandKey(string keyEquivalent, ulong modifiers, string key) =>
        keyEquivalent == key && (modifiers & KeyModifiers) == Command;

    /// <summary>
    /// Whether the host completes the main menu: there is none, or it has no key equivalent at all, as AppKit's
    /// default menu. An application's own main menu has key equivalents.
    /// </summary>
    internal static bool Completes(bool hasMainMenu, bool hasKeyEquivalent) => !hasMainMenu || !hasKeyEquivalent;
}
