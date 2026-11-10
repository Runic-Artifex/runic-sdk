namespace Runic.Platform;

/// <summary>
/// The text that Runic itself shows in native UI where the platform has no translated text of its own, such as the
/// suggested name of a save dialog or the error pages of a browser window. Every text defaults to English, and
/// <see cref="Current"/> is the set every provider of the process uses.
/// </summary>
/// <remarks>
/// <para>
/// Take the texts from the application's own translations, for example its Runic Translations catalog, and set
/// <see cref="Current"/> again after a language switch, as for <c>DesktopHost.SetMenuBar</c>. The optional
/// <c>Runic.Application.Views.Translations</c> package has them in English and German and keeps them in the application's
/// locale. Dialogs and pages
/// that open later use the new texts; open ones keep theirs.
/// </para>
/// <para>
/// Where the platform has a translated default, Runic uses it and these texts do not apply: the buttons of file dialogs
/// come from the portal backend, GTK or AppKit, a macOS panel shows no title, and GTK and AppKit draw their menus'
/// shortcut labels themselves. The xdg-desktop-portal FileChooser and the GTK 3 dialog need a title, which is the
/// request's <c>Title</c> or one of the dialog titles here.
/// </para>
/// <para>
/// A text has 1 to 1024 characters, no NUL and is not only white space; setting <see cref="Current"/> throws
/// <see cref="ArgumentException"/> otherwise. The shortcut key names may be <see langword="null"/>.
/// </para>
/// </remarks>
/// <example><code>
/// DesktopPlatformTexts.Current = new DesktopPlatformTexts
/// {
///     UntitledFileName = "Unbenannt", ControlKeyName = "Strg", ShiftKeyName = "Umschalt", AltKeyName = "Alt",
/// };
/// </code></example>
public sealed record DesktopPlatformTexts
{
    private const int MaximumLength = 1024;
    private static DesktopPlatformTexts s_current = new();

    /// <summary>
    /// Gets or sets the texts that every provider of the process uses; the English defaults until the application sets
    /// others. Setting <see langword="null"/> returns to them.
    /// </summary>
    /// <exception cref="ArgumentException">A text is empty, white space, longer than 1024 characters or contains NUL.</exception>
    public static DesktopPlatformTexts Current
    {
        get => Volatile.Read(ref s_current);
        set
        {
            var texts = value ?? new DesktopPlatformTexts();
            texts.Validate();
            Volatile.Write(ref s_current, texts);
        }
    }

    /// <summary>Gets the title of a Linux dialog that opens one file without a title of its own; <c>Open file</c>.</summary>
    public string OpenFileTitle { get; init; } = "Open file";

    /// <summary>Gets the title of a Linux dialog that opens several files; <c>Open files</c>.</summary>
    public string OpenFilesTitle { get; init; } = "Open files";

    /// <summary>Gets the title of a Linux dialog that opens one directory; <c>Open directory</c>.</summary>
    public string OpenDirectoryTitle { get; init; } = "Open directory";

    /// <summary>Gets the title of a Linux dialog that opens several directories; <c>Open directories</c>.</summary>
    public string OpenDirectoriesTitle { get; init; } = "Open directories";

    /// <summary>Gets the title of a Linux save dialog without a title of its own; <c>Save file</c>.</summary>
    public string SaveFileTitle { get; init; } = "Save file";

    /// <summary>Gets the name a save dialog suggests when the request names no file; <c>Untitled</c>.</summary>
    public string UntitledFileName { get; init; } = "Untitled";

    /// <summary>Gets the title of the macOS panel that chooses the application to open a file with; <c>Choose an application</c>.</summary>
    public string ChooseApplicationTitle { get; init; } = "Choose an application";

    /// <summary>
    /// Gets the label of the Secret Service collection Runic creates on Linux when the user has no default keyring yet;
    /// <c>Default keyring</c>. The keyring manager shows it.
    /// </summary>
    public string SecretCollectionLabel { get; init; } = "Default keyring";

    /// <summary>
    /// Gets the name of the Control key in shortcut labels that Runic writes itself: the Windows menu bar and context
    /// menus, and the in-page menu bar on Windows and Linux; <c>Ctrl</c> when it is <see langword="null"/> and another
    /// key name is set.
    /// </summary>
    /// <remarks>
    /// All three names come from one source, so a label does not mix two languages. Once the application sets one of
    /// <see cref="ControlKeyName"/>, <see cref="ShiftKeyName"/> and <see cref="AltKeyName"/>, the labels use its names
    /// and English for the others. Without them, Runic Desktop's Windows windows take the names of the keyboard layout
    /// the user types with (<c>Strg</c> and <c>Umschalt</c> on a German layout) and lay their menu bars out again when
    /// the user switches layout; other hosts, such as the CS-WebUI in-page bar, and Linux show English names. Set the
    /// three names to match the application's language whatever the layout. Key names such as <c>Del</c> stay English.
    /// </remarks>
    public string? ControlKeyName { get; init; }

    /// <summary>Gets the name of the Shift key in shortcut labels, as for <see cref="ControlKeyName"/>; else <c>Shift</c>.</summary>
    public string? ShiftKeyName { get; init; }

    /// <summary>Gets the name of the Alt key in shortcut labels, as for <see cref="ControlKeyName"/>; else <c>Alt</c>.</summary>
    public string? AltKeyName { get; init; }

    /// <summary>
    /// Gets the title of the page a Runic Desktop browser window shows when it may not connect; <c>Access Denied</c>.
    /// CS-WebUI windows show WebUI's own English page, which these texts do not reach.
    /// </summary>
    public string AccessDeniedTitle { get; init; } = "Access Denied";

    /// <summary>
    /// Gets the message of that page when another client already shows the window and multi-client mode is off;
    /// <c>This content is already in use and multi-client mode is disabled.</c>
    /// </summary>
    public string ContentInUseMessage { get; init; } = "This content is already in use and multi-client mode is disabled.";

    /// <summary>
    /// Gets the message of that page when a browser opens a launch link that is not valid or was already used.
    /// </summary>
    public string LaunchLinkInvalidMessage { get; init; } =
        "This window's launch link is not valid or was already used by another client. Close this window and open it again from the application.";

    /// <summary>
    /// Gets the title of the page for a resource a Runic Desktop browser window does not serve;
    /// <c>Resource Not Available</c>. CS-WebUI windows show WebUI's own English page.
    /// </summary>
    public string ResourceUnavailableTitle { get; init; } = "Resource Not Available";

    /// <summary>Gets the message of that page; <c>The requested resource is not available.</c></summary>
    public string ResourceUnavailableMessage { get; init; } = "The requested resource is not available.";

    /// <summary>
    /// Gets the title of a Windows window while WebView2 is still starting, until the page has a title; it is also the
    /// loading surface's accessible name, which a screen reader reads. <c>Loading…</c>.
    /// </summary>
    public string LoadingTitle { get; init; } = "Loading…";

    private void Validate()
    {
        Check(OpenFileTitle, nameof(OpenFileTitle));
        Check(OpenFilesTitle, nameof(OpenFilesTitle));
        Check(OpenDirectoryTitle, nameof(OpenDirectoryTitle));
        Check(OpenDirectoriesTitle, nameof(OpenDirectoriesTitle));
        Check(SaveFileTitle, nameof(SaveFileTitle));
        Check(UntitledFileName, nameof(UntitledFileName));
        if (UntitledFileName.IndexOfAny(['/', '\\']) >= 0 || UntitledFileName is "." or "..")
            throw new ArgumentException("The untitled file name must be a file name without a path separator.", nameof(UntitledFileName));
        Check(ChooseApplicationTitle, nameof(ChooseApplicationTitle));
        Check(SecretCollectionLabel, nameof(SecretCollectionLabel));
        if (ControlKeyName is not null) Check(ControlKeyName, nameof(ControlKeyName));
        if (ShiftKeyName is not null) Check(ShiftKeyName, nameof(ShiftKeyName));
        if (AltKeyName is not null) Check(AltKeyName, nameof(AltKeyName));
        Check(AccessDeniedTitle, nameof(AccessDeniedTitle));
        Check(ContentInUseMessage, nameof(ContentInUseMessage));
        Check(LaunchLinkInvalidMessage, nameof(LaunchLinkInvalidMessage));
        Check(ResourceUnavailableTitle, nameof(ResourceUnavailableTitle));
        Check(ResourceUnavailableMessage, nameof(ResourceUnavailableMessage));
        Check(LoadingTitle, nameof(LoadingTitle));
    }

    private static void Check(string? text, string name)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumLength || text.Contains('\0'))
            throw new ArgumentException($"The text {name} must have 1 to {MaximumLength} characters, no NUL and not only white space.", name);
    }
}
