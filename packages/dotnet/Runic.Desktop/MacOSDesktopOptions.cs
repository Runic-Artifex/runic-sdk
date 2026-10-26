using Runic.Platform;

namespace Runic.Desktop;

/// <summary>macOS application configuration: the Dock menu, Open Recent and an application delegate of the application's own.</summary>
/// <remarks>Other platforms ignore these options.</remarks>
public sealed record MacOSDesktopOptions
{
    /// <summary>Gets the items that the application's Dock menu shows above the system's; empty, the default, for none.</summary>
    /// <remarks>
    /// <para>
    /// Items, separators and submenus follow the rules of a context menu; shortcuts are not shown. Choosing an item runs
    /// its command through <see cref="DesktopHost.MenuCommands"/>, as a menu bar item does while no window is key, and an
    /// item is enabled when its <see cref="DesktopMenuItem.IsEnabled"/> is <see langword="true"/> and that target's
    /// <see cref="IDesktopMenuCommandTarget.CanExecute"/> answers <see langword="true"/> as the menu opens.
    /// </para>
    /// <para>
    /// The menu exists while <see cref="DesktopEventLoop"/> runs. With several hosts, the first host started with a Dock
    /// menu supplies it.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DesktopMenuEntry> DockMenu { get; init; } = [];

    /// <summary>
    /// Gets whether the standard File menu starts with Open Recent, the submenu of the documents noted with
    /// <see cref="DesktopHost.NoteRecentDocumentAsync"/>; <see langword="false"/>, the default, for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// AppKit fills the submenu from the application's recent documents, the list the Dock menu also shows, above a
    /// Clear Menu item that empties it. Choosing a document delivers it as an open request
    /// (<c>IDesktopOpenRequests.Opened</c> of Runic.Platform.MacOS with <c>DesktopOpenOptions.IncludeFiles</c>), as
    /// choosing it in the Dock or opening it in the Finder does; the open requests keep it until they are created.
    /// Declared items of a <see cref="DesktopMenuRole.File"/> menu, such as Open…, come before it.
    /// </para>
    /// <para>
    /// Runic adds Open Recent to the main menu that it completes, as the other standard menus, not to a main menu of
    /// the application's own. <see cref="DesktopStandardMenuTitles.OpenRecent"/> and
    /// <see cref="DesktopStandardMenuTitles.ClearMenu"/> give the titles. The first host started with this option
    /// adds it; the menu stays for the process.
    /// </para>
    /// </remarks>
    public bool OpenRecentMenu { get; init; }

    /// <summary>
    /// Gets an Objective-C object that implements <c>NSApplicationDelegate</c> methods of the application's own, or 0,
    /// the default, for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="DesktopEventLoop"/> sets Runic's own application delegate before the application's code runs; an
    /// application that sets <c>NSApp.delegate</c> itself replaces it and loses quit handling, reopen, the Dock menu, open
    /// requests and universal links. Runic's delegate forwards every message it does not handle to this object, such as
    /// <c>applicationDidFinishLaunching:</c>, <c>applicationDidBecomeActive:</c> or
    /// <c>applicationShouldTerminateAfterLastWindowClosed:</c>, and also <c>applicationWillFinishLaunching:</c>
    /// after its own handling. It asks this object when Runic does not answer: a Dock menu while
    /// <see cref="DockMenu"/> is empty, a continued user activity that is not one of the application's universal links,
    /// and reopen, whose answer it uses after raising <see cref="DesktopHost.ReopenRequested"/>.
    /// <c>applicationShouldTerminate:</c> is Runic's: use <see cref="DesktopHostOptions.ConfirmQuitAsync"/>. The
    /// open-documents and get-URL Apple events, and the documents chosen in Open Recent, go to the open requests of
    /// Runic.Platform.MacOS, not to <c>application:openURLs:</c> or <c>application:openFile:</c>.
    /// </para>
    /// <para>Runic retains the object for the process lifetime. The first host started with one supplies it.</para>
    /// </remarks>
    public nint ForwardingApplicationDelegate { get; init; }
}
