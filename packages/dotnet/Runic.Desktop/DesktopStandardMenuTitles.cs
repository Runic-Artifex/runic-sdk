using System.Diagnostics.CodeAnalysis;

namespace Runic.Desktop;

/// <summary>
/// The titles of the standard macOS menus and their items, which Runic adds to the application's main menu
/// (<see cref="DesktopHostOptions.StandardMenuTitles"/>). Every title defaults to the English one.
/// </summary>
/// <remarks>
/// <para>
/// Supply them in the user's language from the application's translations; Runic ships none. <c>{0}</c> in
/// <see cref="About"/>, <see cref="Hide"/> and <see cref="Quit"/> is replaced by the application's name, as in the
/// English titles; a translation can leave it out or place it elsewhere.
/// </para>
/// <para>
/// A title has 1 to 256 characters, no NUL character and is not only white space; <c>{0}</c> may appear at most once and
/// only in <see cref="About"/>, <see cref="Hide"/> and <see cref="Quit"/>, and no other brace may appear in them. Building
/// the host throws <see cref="ArgumentException"/> otherwise, and <see cref="DesktopHost.Validate"/> reports
/// <c>menu-title-invalid</c>.
/// </para>
/// </remarks>
/// <example><code>
/// StandardMenuTitles = new DesktopStandardMenuTitles { Edit = "Bearbeiten", Quit = "{0} beenden", Copy = "Kopieren" },
/// </code></example>
public sealed record DesktopStandardMenuTitles
{
    /// <summary>Gets the application menu's About item; <c>{0}</c> is the application's name.</summary>
    public string About { get; init; } = "About {0}";

    /// <summary>Gets the application menu's Hide item; <c>{0}</c> is the application's name.</summary>
    public string Hide { get; init; } = "Hide {0}";

    /// <summary>Gets the application menu's Hide Others item.</summary>
    public string HideOthers { get; init; } = "Hide Others";

    /// <summary>Gets the application menu's Show All item.</summary>
    public string ShowAll { get; init; } = "Show All";

    /// <summary>Gets the application menu's Quit item; <c>{0}</c> is the application's name.</summary>
    public string Quit { get; init; } = "Quit {0}";

    /// <summary>Gets the File menu's title; a declared File menu's title takes its place.</summary>
    public string File { get; init; } = "File";

    /// <summary>Gets the File menu's Close item.</summary>
    public string Close { get; init; } = "Close";

    /// <summary>Gets the File menu's Open Recent submenu (<see cref="MacOSDesktopOptions.OpenRecentMenu"/>).</summary>
    public string OpenRecent { get; init; } = "Open Recent";

    /// <summary>Gets the Open Recent submenu's Clear Menu item.</summary>
    public string ClearMenu { get; init; } = "Clear Menu";

    /// <summary>Gets the Edit menu's title.</summary>
    public string Edit { get; init; } = "Edit";

    /// <summary>Gets the Edit menu's Undo item.</summary>
    public string Undo { get; init; } = "Undo";

    /// <summary>Gets the Edit menu's Redo item.</summary>
    public string Redo { get; init; } = "Redo";

    /// <summary>Gets the Edit menu's Cut item.</summary>
    public string Cut { get; init; } = "Cut";

    /// <summary>Gets the Edit menu's Copy item.</summary>
    public string Copy { get; init; } = "Copy";

    /// <summary>Gets the Edit menu's Paste item.</summary>
    public string Paste { get; init; } = "Paste";

    /// <summary>Gets the Edit menu's Select All item.</summary>
    public string SelectAll { get; init; } = "Select All";

    /// <summary>Gets the View menu's title.</summary>
    public string View { get; init; } = "View";

    /// <summary>Gets the View menu's Actual Size item.</summary>
    public string ActualSize { get; init; } = "Actual Size";

    /// <summary>Gets the View menu's Zoom In item.</summary>
    public string ZoomIn { get; init; } = "Zoom In";

    /// <summary>Gets the View menu's Zoom Out item.</summary>
    public string ZoomOut { get; init; } = "Zoom Out";

    /// <summary>Gets the Window menu's title.</summary>
    public string Window { get; init; } = "Window";

    /// <summary>Gets the Window menu's Minimize item.</summary>
    public string Minimize { get; init; } = "Minimize";

    /// <summary>Gets the Window menu's Zoom item.</summary>
    public string Zoom { get; init; } = "Zoom";

    /// <summary>Gets the Window menu's Bring All to Front item.</summary>
    public string BringAllToFront { get; init; } = "Bring All to Front";

    internal const int MaximumLength = 256;
    internal const string InvalidCode = "menu-title-invalid";

    // Every title with its property name, and whether it may contain {0}.
    internal IEnumerable<(string Name, string? Title, bool Named)> Titles()
    {
        yield return (nameof(About), About, true);
        yield return (nameof(Hide), Hide, true);
        yield return (nameof(HideOthers), HideOthers, false);
        yield return (nameof(ShowAll), ShowAll, false);
        yield return (nameof(Quit), Quit, true);
        yield return (nameof(File), File, false);
        yield return (nameof(Close), Close, false);
        yield return (nameof(OpenRecent), OpenRecent, false);
        yield return (nameof(ClearMenu), ClearMenu, false);
        yield return (nameof(Edit), Edit, false);
        yield return (nameof(Undo), Undo, false);
        yield return (nameof(Redo), Redo, false);
        yield return (nameof(Cut), Cut, false);
        yield return (nameof(Copy), Copy, false);
        yield return (nameof(Paste), Paste, false);
        yield return (nameof(SelectAll), SelectAll, false);
        yield return (nameof(View), View, false);
        yield return (nameof(ActualSize), ActualSize, false);
        yield return (nameof(ZoomIn), ZoomIn, false);
        yield return (nameof(ZoomOut), ZoomOut, false);
        yield return (nameof(Window), Window, false);
        yield return (nameof(Minimize), Minimize, false);
        yield return (nameof(Zoom), Zoom, false);
        yield return (nameof(BringAllToFront), BringAllToFront, false);
    }

    // The diagnostics of invalid titles: one per title, naming its property.
    internal IReadOnlyList<DesktopDiagnostic> Check()
    {
        List<DesktopDiagnostic> diagnostics = [];
        foreach (var (name, title, named) in Titles())
        {
            if (Problem(title, named) is { } problem)
            {
                diagnostics.Add(new DesktopDiagnostic(DesktopErrorCategory.InvalidArgument, InvalidCode,
                    $"The standard menu title {name} {problem}.", Retryable: false,
                    Remediation: "Give every standard menu title 1 to 256 characters; use {0} at most once, in About, Hide and Quit only.")
                {
                    Option = $"{nameof(DesktopHostOptions)}.{nameof(DesktopHostOptions.StandardMenuTitles)}.{name}",
                });
            }
        }
        return diagnostics;
    }

    // Throws for the first invalid title, as building the host does.
    internal void Validate()
    {
        if (Check() is [var first, ..])
            throw new ArgumentException(first.Message, nameof(DesktopHostOptions.StandardMenuTitles));
    }

    private static string? Problem([NotNullWhen(false)] string? title, bool named)
    {
        if (title is null) return "is null";
        if (string.IsNullOrWhiteSpace(title)) return "is empty";
        if (title.Length > MaximumLength) return $"is longer than {MaximumLength} characters";
        if (title.Contains('\0')) return "contains a NUL character";
        var rest = title;
        var placeholder = rest.IndexOf("{0}", StringComparison.Ordinal);
        if (placeholder >= 0)
        {
            if (!named) return "contains {0}, which only About, Hide and Quit replace";
            rest = rest.Remove(placeholder, 3);
            if (rest.Contains("{0}", StringComparison.Ordinal)) return "contains {0} more than once";
        }
        if (named && (rest.Contains('{') || rest.Contains('}'))) return "contains a brace other than {0}";
        return null;
    }

    // The title with {0} replaced by the application's name.
    internal static string Format(string title, string applicationName) =>
        title.Replace("{0}", applicationName, StringComparison.Ordinal);
}
