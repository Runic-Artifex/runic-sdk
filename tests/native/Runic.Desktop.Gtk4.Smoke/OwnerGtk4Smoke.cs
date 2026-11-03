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

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_get_transient_for")] private static partial nint GtkWindowGetTransientFor(nint window);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_sensitive")] private static partial int GtkWidgetGetSensitive(nint widget);
}
