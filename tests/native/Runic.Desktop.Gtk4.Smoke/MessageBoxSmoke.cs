using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Runic.Desktop;
using Runic.Desktop.Gtk4;
using Runic.Platform;
using Runic.Platform.Linux.Gtk4;
using Runic.Platform.Runtime;

// --message-boxes (B17) under Xvfb: GtkAlertDialogs for a GTK 4 Desktop window, answered on the GTK thread by
// activating their buttons (gtk_widget_activate) and closing their window (Esc and the close button). Reports whether the
// buttons take mnemonics and what Esc answers without a cancel button.
[SupportedOSPlatform("linux")]
internal static partial class MessageBoxSmoke
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private const string Page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Runic message boxes</title></head><body>boxes</body></html>";

    internal static int Run() => DesktopEventLoop.Run(new DesktopHostOptions().WithGtk4() with { WaitForConnection = false }, RunAsync);

    private static async Task<int> RunAsync(DesktopHost host)
    {
        Console.WriteLine($"GTK {MajorVersion()}.{MinorVersion()}.{MicroVersion()}; GtkAlertDialog {(Gtk4MessageBoxesSupported() ? "present" : "absent")}");
        await using var surface = await host.CreateSurfaceAsync(new DesktopSurfaceOptions { Content = new DesktopContent.Html(Page) });
        var window = await surface.OpenWindowAsync(new DesktopWindowOptions { Browser = BrowserKind.Embedded, Width = 360, Height = 240 });
        var owner = new WindowOwner(window);
        var boxes = Gtk4PlatformProvider.CreateMessageBoxes(owner);
        if (!Gtk4MessageBoxesSupported())
        {
            Require(await boxes.ShowAsync(new DesktopMessageBox("Hello")) is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
                "GTK before 4.10 reports the backend unavailable");
            Console.WriteLine("PASS GTK 4 message boxes report unavailable before 4.10.");
            return 0;
        }
        var save = new DesktopMessageBoxButton("save", "Save") { Role = DesktopMessageBoxButtonRole.Default, AccessKey = 's' };
        var discard = new DesktopMessageBoxButton("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive };
        var cancel = new DesktopMessageBoxButton("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };
        var close = new DesktopMessageBox("Save changes?") { Detail = "Your changes are lost otherwise.", Buttons = [discard, cancel, save] };

        var result = await ShowAsync(owner, boxes, close, alert =>
        {
            var buttons = Buttons(alert);
            Console.WriteLine($"GTK 4 alert dialog buttons: {string.Join(", ", buttons.Select(static b => $"{b.Label}{(b.Underline ? "" : " (no mnemonic)")}"))}");
            Require(buttons.Select(static b => b.Label).SequenceEqual(["Don't Save", "Cancel", "_Save"]), "Don't Save, Cancel, Save");
            Require(Activate(buttons[2].Widget), "the button activates");
        });
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(var saved) && saved == save, $"the activated default button: {result}");
        result = await ShowAsync(owner, boxes, close, alert => CloseWindow(alert));
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(var cancelled) && cancelled == cancel, $"Esc chose the cancel button: {result}");
        result = await ShowAsync(owner, boxes, close with { Buttons = [save, discard] }, alert => CloseWindow(alert));
        Console.WriteLine($"GTK 4 alert dialog without a cancel button, closed: {result}");
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"Esc without a cancel button answers null: {result}");
        result = await ShowAsync(owner, boxes, new DesktopMessageBox("Exported."), alert => Require(Activate(Buttons(alert)[0].Widget), "GTK's button activates"));
        Require(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"GTK's own button answers null: {result}");
        Console.WriteLine("PASS a button, Esc and GTK's own button answer the GTK 4 box");

        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((Gtk4MessageBoxes)boxes).Shown = alert => shown.TrySetResult(alert);
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = boxes.ShowAsync(close, cancellation.Token).AsTask();
            await shown.Task.WaitScaledAsync(Deadline);
            cancellation.Cancel();
            try { await pending.WaitScaledAsync(Deadline); throw new InvalidOperationException("A cancelled box returned."); }
            catch (OperationCanceledException) { }
            bool open = true;
            for (var deadline = DateTime.UtcNow + TestTimeouts.Scale(Deadline); open && DateTime.UtcNow < deadline; await Task.Delay(50))
                await window.InvokeNativeAsync(parent => open = AlertWindow(parent) != 0);
            Require(!open, "cancellation closed the alert dialog");
        }
        Console.WriteLine("PASS native GTK 4 message boxes");
        return 0;
    }

    private static async Task<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(WindowOwner owner, IDesktopMessageBoxes boxes, DesktopMessageBox box, Action<nint> answer)
    {
        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        ((Gtk4MessageBoxes)boxes).Shown = alert => shown.TrySetResult(alert);
        var pending = boxes.ShowAsync(box).AsTask();
        await shown.Task.WaitScaledAsync(Deadline);
        // The dialog's window appears once GTK has iterated.
        nint alertWindow = 0;
        for (var deadline = DateTime.UtcNow + TestTimeouts.Scale(Deadline); alertWindow == 0; await Task.Delay(50))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The alert dialog's window did not appear.");
            await owner.InvokeAsync(parent => alertWindow = AlertWindow(parent));
        }
        await owner.InvokeAsync(_ => answer(alertWindow));
        return await pending.WaitScaledAsync(Deadline);
    }

    // The toplevel window transient for the Desktop window: the alert dialog's.
    private static nint AlertWindow(nint parent)
    {
        nint list = Toplevels();
        for (uint index = 0, count = ItemCount(list); index < count; index++)
        {
            nint candidate = Item(list, index);
            Unref(candidate);
            if (candidate != parent && TransientFor(candidate) == parent && IsVisible(candidate) != 0) return candidate;
        }
        return 0;
    }

    private static List<(nint Widget, string Label, bool Underline)> Buttons(nint widget)
    {
        var found = new List<(nint, string, bool)>();
        Visit(widget);
        return found;

        void Visit(nint node)
        {
            if (IsA(node, ButtonType()) != 0)
                found.Add((node, Marshal.PtrToStringUTF8(Label(node)) ?? "", UseUnderline(node) != 0));
            for (nint child = FirstChild(node); child != 0; child = NextSibling(child)) Visit(child);
        }
    }

    private static bool Gtk4MessageBoxesSupported() => MinorVersion() >= 10;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class WindowOwner(DesktopWindow window) : INativeWindowOwner
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool IsAvailable => window.IsOpen;
        public async ValueTask InvokeAsync(Action<nint> action, CancellationToken cancellationToken = default)
        {
            if (!IsAvailable) throw new OwnerClosedException();
            await window.InvokeNativeAsync(action, cancellationToken);
        }
    }

    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_get_major_version")] private static partial uint MajorVersion();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_get_minor_version")] private static partial uint MinorVersion();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_get_micro_version")] private static partial uint MicroVersion();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_get_toplevels")] private static partial nint Toplevels();
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_list_model_get_n_items")] private static partial uint ItemCount(nint list);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_list_model_get_item")] private static partial nint Item(nint list, uint index);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static partial void Unref(nint instance);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_type_check_instance_is_a")] private static partial int IsA(nint instance, nint type);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_get_transient_for")] private static partial nint TransientFor(nint window);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_visible")] private static partial int IsVisible(nint widget);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_window_close")] private static partial void CloseWindow(nint window);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_activate")] [return: MarshalAs(UnmanagedType.I4)] private static partial bool Activate(nint widget);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_first_child")] private static partial nint FirstChild(nint widget);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_widget_get_next_sibling")] private static partial nint NextSibling(nint widget);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_button_get_type")] private static partial nint ButtonType();
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_button_get_label")] private static partial nint Label(nint button);
    [LibraryImport("libgtk-4.so.1", EntryPoint = "gtk_button_get_use_underline")] private static partial int UseUnderline(nint button);
}
