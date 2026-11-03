using System.Runtime.InteropServices;
using Runic.Desktop;

// B6: DesktopWindowOptions.Owner and Modality in the built-in embedded windows. A window-modal window is owned by its
// owner natively (GTK transient_for, the Win32 owner HWND, an AppKit sheet), and the owner takes no input until it closed
// (GTK insensitive, EnableWindow, the sheet); an owned window without modality leaves the owner usable (a child window on
// macOS). Run with --owner on any platform.
internal static partial class OwnerSmoke
{
    internal static int Run(DesktopHostOptions hostOptions) => DesktopEventLoop.Run(hostOptions, RunAsync);

    private static async Task<int> RunAsync(DesktopHost host)
    {
        await using var ownerSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("owner")) });
        await using var ownedSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("owned")) });
        await using var owner = await ownerSurface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 520, Height = 400 });
        try
        {
            return await RunAsync(ownedSurface, owner);
        }
        finally
        {
            // A failed check closes the windows, so the event loop ends; the owned ones close with their owner.
            if (owner.IsOpen) await owner.CloseAsync();
        }
    }

    private static async Task<int> RunAsync(DesktopSurface ownedSurface, DesktopWindow owner)
    {
        var ownerHandle = owner.NativeHandle;

        foreach (var modality in new[] { DesktopWindowModality.Window, DesktopWindowModality.None })
        {
            var owned = await ownedSurface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 360,
                Height = 240,
                Owner = DesktopWindowOwner.FromWindow(owner),
                Modality = modality,
            });
            var (parent, blocked) = await ReadAsync(owned, ownerHandle, modality);
            if (parent != ownerHandle)
                throw new InvalidOperationException($"With Modality {modality} the owned window's native owner is 0x{parent:x}, expected 0x{ownerHandle:x}.");
            if (blocked != (modality == DesktopWindowModality.Window))
                throw new InvalidOperationException($"With Modality {modality} the owner {(blocked ? "is" : "is not")} blocked.");
            if (owner.OwnedWindows.Count != 1 || owned.Owner?.Window != owner)
                throw new InvalidOperationException("DesktopWindow.OwnedWindows and Owner do not name the owned window and its owner.");
            Console.WriteLine($"PASS owner: Modality {modality}: owned by the owner (0x{parent:x}); the owner is {(blocked ? "blocked" : "usable")}.");
            await owned.CloseAsync();
            if (await OwnerBlockedAsync(owner))
                throw new InvalidOperationException($"After the {modality} window closed the owner is still blocked.");
        }
        Console.WriteLine("PASS owner: the owner takes input again once its modal window closed.");

        // Closing the owner closes the windows it owns.
        var last = await ownedSurface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Owner = DesktopWindowOwner.FromWindow(owner),
            Modality = DesktopWindowModality.Window,
        });
        await owner.CloseAsync();
        await last.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS owner: closing the owner closed its modal window.");
        Console.WriteLine("OWNER_OK");
        return 0;
    }

    // On the owned window's native thread: its native owner, and whether the owner is blocked.
    private static async Task<(nint Parent, bool Blocked)> ReadAsync(DesktopWindow owned, nint owner, DesktopWindowModality modality)
    {
        nint parent = 0;
        var blocked = false;
        await owned.InvokeNativeAsync(handle =>
        {
            if (OperatingSystem.IsLinux())
            {
                parent = GtkWindowGetTransientFor(handle);
                blocked = GtkWidgetGetSensitive(owner) == 0;
            }
            else if (OperatingSystem.IsWindows())
            {
                parent = GetWindow(handle, 4);
                blocked = !IsWindowEnabled(owner);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // A window-modal window is the owner's sheet; another owned window is its child window.
                parent = Send(handle, Sel(modality == DesktopWindowModality.Window ? "sheetParent" : "parentWindow"));
                blocked = Send(owner, Sel("attachedSheet")) == handle;
            }
        });
        return (parent, blocked);
    }

    private static async Task<bool> OwnerBlockedAsync(DesktopWindow owner)
    {
        var blocked = false;
        await owner.InvokeNativeAsync(handle =>
        {
            if (OperatingSystem.IsLinux()) blocked = GtkWidgetGetSensitive(handle) == 0;
            else if (OperatingSystem.IsWindows()) blocked = !IsWindowEnabled(handle);
            else if (OperatingSystem.IsMacOS()) blocked = Send(handle, Sel("attachedSheet")) != 0;
        });
        return blocked;
    }

    private static string Page(string title) =>
        $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><script src=\"webui.js\"></script><title>{title}</title></head><body><main><h1>{title}</h1></main></body></html>";

    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_get_transient_for")] private static partial nint GtkWindowGetTransientFor(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_sensitive")] private static partial int GtkWidgetGetSensitive(nint widget);
    [LibraryImport("user32.dll", EntryPoint = "GetWindow")] private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(nint window);
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
}
