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
            await ReviewChecksAsync(host);
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

    // A window on a surface of its own.
    private static async Task<DesktopWindow> OpenAsync(DesktopHost host, string title, DesktopWindow? owner = null, bool hidden = false)
    {
        var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page(title)) });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 360,
            Height = 240,
            Hidden = hidden,
            Owner = owner is null ? null : DesktopWindowOwner.FromWindow(owner),
            Modality = owner is null ? DesktopWindowModality.None : DesktopWindowModality.Window,
        });
        // The surface goes with its window.
        window.Closed += (_, _) => _ = surface.DisposeAsync().AsTask();
        return window;
    }

    private static async Task ExpectBlockedAsync(DesktopWindow window, bool blocked, string when)
    {
        // GTK and Win32 apply the change on their own threads; AppKit within the call.
        for (var attempt = 0; attempt < 50 && await OwnerBlockedAsync(window) != blocked; attempt++) await Task.Delay(20);
        if (await OwnerBlockedAsync(window) != blocked)
            throw new InvalidOperationException($"{when}, the window is {(blocked ? "not blocked" : "still blocked")}.");
    }

    // Review of #814: nested modal windows, two modal windows on one owner, Hidden + Modal, a blocked owner and the window
    // manager's close, and an owner disposed while its modal window is open.
    private static async Task ReviewChecksAsync(DesktopHost host)
    {
        // A dialog that opens its own dialog: each blocks its owner, and closing the inner one unblocks only the middle.
        var root = await OpenAsync(host, "root");
        var middle = await OpenAsync(host, "middle", root);
        var inner = await OpenAsync(host, "inner", middle);
        await ExpectBlockedAsync(root, true, "With a nested modal window open, the root");
        await ExpectBlockedAsync(middle, true, "With its own modal window open, the middle window");
        await inner.CloseAsync();
        await ExpectBlockedAsync(middle, false, "After the inner modal window closed, the middle window");
        await ExpectBlockedAsync(root, true, "After the inner modal window closed, the root");
        await middle.CloseAsync();
        await ExpectBlockedAsync(root, false, "After both modal windows closed, the root");
        Console.WriteLine("PASS owner: nested modal windows block and unblock their own owners.");

        // Two modal windows on one owner: the owner takes input again when the last one closed. AppKit queues the second
        // sheet behind the first.
        var first = await OpenAsync(host, "first", root);
        var second = await OpenAsync(host, "second", root);
        if (OperatingSystem.IsMacOS())
        {
            nint shown = 0;
            await root.InvokeNativeAsync(handle => shown = Send(handle, Sel("attachedSheet")));
            if (shown != first.NativeHandle) throw new InvalidOperationException("The first sheet is not the one AppKit shows.");
        }
        await first.CloseAsync();
        await ExpectBlockedAsync(root, true, "With one of two modal windows still open, the owner");
        await second.CloseAsync();
        await ExpectBlockedAsync(root, false, "After both modal windows closed, the owner");
        Console.WriteLine("PASS owner: two modal windows on one owner; it takes input again after the last one.");

        // Hidden + Modal: the owner is blocked from the first show, and again usable while the modal window is hidden.
        var hidden = await OpenAsync(host, "hidden", root, hidden: true);
        await ExpectBlockedAsync(root, false, "With a hidden modal window, the owner");
        await hidden.ShowAsync();
        await ExpectBlockedAsync(root, true, "Once the hidden modal window was shown, the owner");
        await hidden.HideAsync();
        await ExpectBlockedAsync(root, false, "With the modal window hidden again, the owner");
        await hidden.FocusAsync();
        await ExpectBlockedAsync(root, true, "With the modal window focused, the owner");
        Console.WriteLine("PASS owner: a Hidden modal window blocks its owner only while it is shown.");

        // GTK: the window manager's close of a blocked owner is ignored, as on Windows and macOS.
        if (OperatingSystem.IsLinux())
        {
            await root.InvokeNativeAsync(handle => GtkWindowClose(handle));
            await Task.Delay(500);
            if (!root.IsOpen) throw new InvalidOperationException("The window manager closed an owner that a modal window blocks.");
            // The same close does close a window that nothing blocks.
            var free = await OpenAsync(host, "free");
            await free.InvokeNativeAsync(handle => GtkWindowClose(handle));
            await free.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("PASS owner: a blocked GTK owner ignores the window manager's close.");
        }
        await hidden.CloseAsync();

        // An owner disposed while its modal window (on macOS its sheet) is open: the modal window closes with it.
        var orphan = await OpenAsync(host, "orphan", root);
        await root.DisposeAsync();
        await orphan.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS owner: disposing an owner closes its open modal window.");
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
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_close")] private static partial void GtkWindowClose(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_sensitive")] private static partial int GtkWidgetGetSensitive(nint widget);
    [LibraryImport("user32.dll", EntryPoint = "GetWindow")] private static partial nint GetWindow(nint window, uint command);
    [LibraryImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowEnabled(nint window);
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)] private static partial nint Sel(string name);
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")] private static partial nint Send(nint target, nint selector);
}
