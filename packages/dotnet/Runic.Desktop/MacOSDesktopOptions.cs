using Runic.Platform;

namespace Runic.Desktop;

/// <summary>macOS application configuration: the Dock menu and an application delegate of the application's own.</summary>
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
    /// open-documents and get-URL Apple events go to the open requests of Runic.Platform.MacOS, not to
    /// <c>application:openURLs:</c>.
    /// </para>
    /// <para>Runic retains the object for the process lifetime. The first host started with one supplies it.</para>
    /// </remarks>
    public nint ForwardingApplicationDelegate { get; init; }
}
