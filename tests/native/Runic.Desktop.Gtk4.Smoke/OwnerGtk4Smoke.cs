using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;

// --owner (B6): DesktopWindowOptions.Owner and Modality in the GTK 4 window. The owned window is transient for its
// owner; a window-modal one makes the owner insensitive until it closed. Closing the owner closes the windows it owns.
[SupportedOSPlatform("linux")]
internal static partial class OwnerGtk4Smoke
{
    private static string Page(string title) =>
        $"<!doctype html><html><head><meta charset=\"utf-8\"><script src=\"webui.js\"></script><title>{title}</title></head><body>{title}</body></html>";

    internal static int Run() => DesktopEventLoop.Run(new DesktopHostOptions().WithGtk4() with { WaitForConnection = true }, async host =>
    {
        await using var ownerSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("owner")) });
        await using var ownedSurface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page("owned")) });
        var owner = await ownerSurface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 480, Height = 360 });
        try
        {
            await ReviewChecksAsync(host);
            return await RunAsync(ownerSurface, ownedSurface, owner);
        }
        finally
        {
            // A failed check closes the windows, so the event loop ends; the owned ones close with their owner.
            if (owner.IsOpen) await owner.CloseAsync();
        }
    });

    private static async Task<int> RunAsync(DesktopSurface ownerSurface, DesktopSurface ownedSurface, DesktopWindow owner)
    {
        if (!owner.Capabilities.HasFlag(DesktopWindowCapabilities.Ownership))
            throw new InvalidOperationException("The GTK 4 window does not report DesktopWindowCapabilities.Ownership.");
        foreach (var modality in new[] { DesktopWindowModality.Window, DesktopWindowModality.None })
        {
            var owned = await ownedSurface.OpenWindowAsync(new DesktopWindowOptions
            {
                Browser = BrowserKind.Embedded,
                Width = 320,
                Height = 200,
                Owner = DesktopWindowOwner.FromWindow(owner),
                Modality = modality,
            });
            nint parent = 0;
            var sensitive = true;
            await owned.InvokeNativeAsync(handle =>
            {
                parent = GtkWindowGetTransientFor(handle);
                sensitive = GtkWidgetGetSensitive(owner.NativeHandle) != 0;
            });
            if (parent != owner.NativeHandle) throw new InvalidOperationException($"With Modality {modality} the window is transient for 0x{parent:x}.");
            if (sensitive == (modality == DesktopWindowModality.Window))
                throw new InvalidOperationException($"With Modality {modality} the owner is {(sensitive ? "sensitive" : "insensitive")}.");
            await owned.CloseAsync();
            await owner.InvokeNativeAsync(handle => sensitive = GtkWidgetGetSensitive(handle) != 0);
            if (!sensitive) throw new InvalidOperationException($"After the {modality} window closed the owner stays insensitive.");
            Console.WriteLine($"PASS GTK 4 owner: Modality {modality}.");
        }
        var last = await ownedSurface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Owner = DesktopWindowOwner.FromWindow(owner),
            Modality = DesktopWindowModality.Window,
        });
        await owner.CloseAsync();
        await last.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS GTK 4 owner: closing the owner closed its modal window.");
        return 0;
    }

    private static async Task<DesktopWindow> OpenAsync(DesktopHost host, string title, DesktopWindow? owner = null, bool hidden = false)
    {
        var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page(title)) });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions
        {
            Browser = BrowserKind.Embedded,
            Width = 320,
            Height = 200,
            Hidden = hidden,
            Owner = owner is null ? null : DesktopWindowOwner.FromWindow(owner),
            Modality = owner is null ? DesktopWindowModality.None : DesktopWindowModality.Window,
        });
        window.Closed += (_, _) => _ = surface.DisposeAsync().AsTask();
        return window;
    }

    private static async Task ExpectBlockedAsync(DesktopWindow window, bool blocked, string when)
    {
        for (var attempt = 0; ; attempt++)
        {
            var sensitive = true;
            await window.InvokeNativeAsync(handle => sensitive = GtkWidgetGetSensitive(handle) != 0);
            if (sensitive != blocked) return;
            if (attempt == 50) throw new InvalidOperationException($"{when}, the window is {(blocked ? "not blocked" : "still blocked")}.");
            await Task.Delay(20);
        }
    }

    // Review of #814: nested modal windows, two modal windows on one owner, Hidden + Modal, a blocked owner and the window
    // manager's close, and an owner disposed while its modal window is open.
    private static async Task ReviewChecksAsync(DesktopHost host)
    {
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
        Console.WriteLine("PASS GTK 4 owner: nested modal windows.");

        var first = await OpenAsync(host, "first", root);
        var second = await OpenAsync(host, "second", root);
        await first.CloseAsync();
        await ExpectBlockedAsync(root, true, "With one of two modal windows still open, the owner");
        await second.CloseAsync();
        await ExpectBlockedAsync(root, false, "After both modal windows closed, the owner");
        Console.WriteLine("PASS GTK 4 owner: two modal windows on one owner.");

        var hidden = await OpenAsync(host, "hidden", root, hidden: true);
        await ExpectBlockedAsync(root, false, "With a hidden modal window, the owner");
        await hidden.ShowAsync();
        await ExpectBlockedAsync(root, true, "Once the hidden modal window was shown, the owner");
        await hidden.HideAsync();
        await ExpectBlockedAsync(root, false, "With the modal window hidden again, the owner");
        await hidden.ShowAsync();
        await ExpectBlockedAsync(root, true, "Shown again, the owner");
        Console.WriteLine("PASS GTK 4 owner: a Hidden modal window blocks its owner only while it is shown.");

        await root.InvokeNativeAsync(handle => GtkWindowClose(handle));
        await Task.Delay(500);
        if (!root.IsOpen) throw new InvalidOperationException("The window manager closed an owner that a modal window blocks.");
        var free = await OpenAsync(host, "free");
        await free.InvokeNativeAsync(handle => GtkWindowClose(handle));
        await free.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Console.WriteLine("PASS GTK 4 owner: a blocked owner ignores the window manager's close.");

        await root.DisposeAsync();
        await hidden.WaitForCloseAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Console.WriteLine("PASS GTK 4 owner: disposing an owner closes its open modal window.");
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_close")] private static partial void GtkWindowClose(nint window);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_get_transient_for")] private static partial nint GtkWindowGetTransientFor(nint window);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_sensitive")] private static partial int GtkWidgetGetSensitive(nint widget);
}
