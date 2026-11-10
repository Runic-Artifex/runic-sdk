namespace Runic.Application.Views.Translations;

/// <summary>
/// The titles of Runic Desktop's standard macOS menus in one locale, from <see cref="RunicViewsTranslations.MenuTitles"/>.
/// Each member has the name of the <c>DesktopStandardMenuTitles</c> member it fills; <c>{0}</c> stands for the
/// application's name, which Runic inserts.
/// </summary>
public sealed class RunicViewsMenuTitles
{
    private RunicViewsMenuTitles() { }

    /// <summary>The application menu's About item, such as <c>About {0}</c>.</summary>
    public string About { get; private init; } = "";

    /// <summary>The application menu's Hide item, such as <c>Hide {0}</c>.</summary>
    public string Hide { get; private init; } = "";

    /// <summary>The application menu's Hide Others item.</summary>
    public string HideOthers { get; private init; } = "";

    /// <summary>The application menu's Show All item.</summary>
    public string ShowAll { get; private init; } = "";

    /// <summary>The application menu's Quit item, such as <c>Quit {0}</c>.</summary>
    public string Quit { get; private init; } = "";

    /// <summary>The File menu.</summary>
    public string File { get; private init; } = "";

    /// <summary>The File menu's Close item.</summary>
    public string Close { get; private init; } = "";

    /// <summary>The File menu's Open Recent submenu.</summary>
    public string OpenRecent { get; private init; } = "";

    /// <summary>The Open Recent submenu's Clear Menu item.</summary>
    public string ClearMenu { get; private init; } = "";

    /// <summary>The Edit menu.</summary>
    public string Edit { get; private init; } = "";

    /// <summary>The Edit menu's Undo item.</summary>
    public string Undo { get; private init; } = "";

    /// <summary>The Edit menu's Redo item.</summary>
    public string Redo { get; private init; } = "";

    /// <summary>The Edit menu's Cut item.</summary>
    public string Cut { get; private init; } = "";

    /// <summary>The Edit menu's Copy item.</summary>
    public string Copy { get; private init; } = "";

    /// <summary>The Edit menu's Paste item.</summary>
    public string Paste { get; private init; } = "";

    /// <summary>The Edit menu's Select All item.</summary>
    public string SelectAll { get; private init; } = "";

    /// <summary>The View menu.</summary>
    public string View { get; private init; } = "";

    /// <summary>The View menu's Actual Size item.</summary>
    public string ActualSize { get; private init; } = "";

    /// <summary>The View menu's Zoom In item.</summary>
    public string ZoomIn { get; private init; } = "";

    /// <summary>The View menu's Zoom Out item.</summary>
    public string ZoomOut { get; private init; } = "";

    /// <summary>The Window menu.</summary>
    public string Window { get; private init; } = "";

    /// <summary>The Window menu's Minimize item.</summary>
    public string Minimize { get; private init; } = "";

    /// <summary>The Window menu's Zoom item.</summary>
    public string Zoom { get; private init; } = "";

    /// <summary>The Window menu's Bring All to Front item.</summary>
    public string BringAllToFront { get; private init; } = "";

    internal static RunicViewsMenuTitles From(RunicViewsTextMessages text) => new()
    {
        About = text.menu_about(app: "{0}"), Hide = text.menu_hide(app: "{0}"), HideOthers = text.menu_hide_others,
        ShowAll = text.menu_show_all, Quit = text.menu_quit(app: "{0}"), File = text.menu_file, Close = text.menu_close,
        OpenRecent = text.menu_open_recent, ClearMenu = text.menu_clear_menu, Edit = text.menu_edit, Undo = text.menu_undo,
        Redo = text.menu_redo, Cut = text.menu_cut, Copy = text.menu_copy, Paste = text.menu_paste,
        SelectAll = text.menu_select_all, View = text.menu_view, ActualSize = text.menu_actual_size, ZoomIn = text.menu_zoom_in,
        ZoomOut = text.menu_zoom_out, Window = text.menu_window, Minimize = text.menu_minimize, Zoom = text.menu_zoom,
        BringAllToFront = text.menu_bring_all_to_front,
    };
}
