using System.Runtime.InteropServices;
using TUnit.Core;

namespace Runic.Platform.Linux.Tests;

// The NativeMessageBoxes case shows GtkMessageDialogs for a GTK 3 window on the test's own GTK thread under Xvfb and
// answers them as GTK does for the keyboard and the mouse: the default response (Return), gtk_window_close (Esc and
// the close button), a clicked button and an owner window that closes.
internal sealed partial class MessageBoxTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly DesktopMessageBoxButton Save = new("save", "Save") { Role = DesktopMessageBoxButtonRole.Default, AccessKey = 's' };
    private static readonly DesktopMessageBoxButton Discard = new("discard", "Don't Save") { Role = DesktopMessageBoxButtonRole.Destructive, AccessKey = 'n' };
    private static readonly DesktopMessageBoxButton Cancel = new("cancel", "Cancel") { Role = DesktopMessageBoxButtonRole.Cancel };
    private static readonly DesktopMessageBox Close = new("Save changes?")
    {
        Detail = "Your changes are lost otherwise.", Title = "Notes", Kind = DesktopMessageBoxKind.Warning, Buttons = [Discard, Cancel, Save],
    };

    [Test]
    public void ResponsesMapToButtons()
    {
        var buttons = Close.Buttons;
        Check(Gtk3MessageBoxes.Response(buttons, Cancel) == -6 && Gtk3MessageBoxes.Response(buttons, Save) == 2, "GTK_RESPONSE_CANCEL and the index");
        foreach (var button in buttons) Check(Gtk3MessageBoxes.Chosen(buttons, Gtk3MessageBoxes.Response(buttons, button)) == button, button.Id);
        Check(Gtk3MessageBoxes.Chosen(buttons, -4) == Cancel, "Esc and the close button (GTK_RESPONSE_DELETE_EVENT) choose the cancel button");
        Check(Gtk3MessageBoxes.Chosen([Save], -4) is null && Gtk3MessageBoxes.Chosen([], -5) is null && Gtk3MessageBoxes.Chosen(buttons, 9) is null,
            "without a cancel button, or with GTK's own OK, there is no button");
    }

    [Test, Explicit, Category("NativeMessageBoxes")]
    public async Task NativeMessageBoxesAsync()
    {
        await using var owner = new GtkOwner();
        var boxes = (Gtk3MessageBoxes)LinuxPlatformProvider.CreateMessageBoxes(owner);

        var result = await ShowAsync(owner, boxes, Close, dialog =>
        {
            var labels = ButtonLabels(dialog);
            Console.WriteLine($"GTK 3 message dialog buttons: {string.Join(", ", labels)}; title {Marshal.PtrToStringUTF8(Title(dialog))}");
            Check(labels.SequenceEqual(["Do_n't Save!", "Cancel", "_Save*"]), "Don't Save (destructive), Cancel, Save (suggested), with mnemonics");
            Check(Marshal.PtrToStringUTF8(Title(dialog)) == "Notes" && Modal(dialog) != 0 && TransientFor(dialog) != 0, "titled, modal and transient for the window");
            Check(ActivateDefault(dialog) != 0, "a default widget activates");
        });
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var saved) && saved == Save, $"Return chose the default button: {result}");
        result = await ShowAsync(owner, boxes, Close, CloseWindow);
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var cancelled) && cancelled == Cancel, $"Esc chose the cancel button: {result}");
        result = await ShowAsync(owner, boxes, Close, dialog => Click(ButtonFor(dialog, 0)));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var discarded) && discarded == Discard, $"the clicked button: {result}");
        Console.WriteLine("PASS Return, Esc and a click answer the GTK 3 box");

        result = await ShowAsync(owner, boxes, Close with { Buttons = [Save, Discard] }, CloseWindow);
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"Esc without a cancel button answers null: {result}");
        result = await ShowAsync(owner, boxes, new DesktopMessageBox("Exported."), dialog => Respond(dialog, -5));
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(null), $"GTK's own OK answers null: {result}");
        Console.WriteLine("PASS a box without a cancel button or without buttons answers null");

        // Cancellation destroys the dialog and throws.
        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Shown = dialog => shown.TrySetResult(dialog);
        using (var cancel = new CancellationTokenSource())
        {
            var pending = boxes.ShowAsync(Close, cancel.Token).AsTask();
            await shown.Task.WaitAsync(Wait);
            cancel.Cancel();
            try { await pending.WaitAsync(Wait); throw new InvalidOperationException("A cancelled box returned."); }
            catch (OperationCanceledException) { }
            int open = 0;
            await owner.InvokeAsync(_ => open = MessageDialogs());
            Check(open == 0, "cancellation destroyed the dialog");
        }
        Console.WriteLine("PASS cancellation destroys the GTK 3 box");

        // A right-to-left window gives the dialog its direction.
        await owner.InvokeAsync(window => SetDirection(window, 2));
        result = await ShowAsync(owner, boxes, Close, dialog =>
        {
            Check(GetDirection(dialog) == 2, "the dialog is right-to-left as its window");
            CloseWindow(dialog);
        });
        Check(result is PlatformResult<DesktopMessageBoxButton?>.Success(var rtl) && rtl == Cancel, $"the right-to-left box answers: {result}");
        Console.WriteLine("PASS a right-to-left window gets a right-to-left box");

        // Closing the window destroys the dialog with it.
        shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Shown = dialog => shown.TrySetResult(dialog);
        var orphan = boxes.ShowAsync(Close).AsTask();
        await shown.Task.WaitAsync(Wait);
        await owner.CloseWindowAsync();
        var closed = await orphan.WaitAsync(Wait);
        Check(closed is PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed), $"closing the window: {closed}");
        Console.WriteLine("PASS native GTK 3 message boxes");
    }

    // Shows a box and answers it on the GTK thread once it is shown.
    private static async Task<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(GtkOwner owner, Gtk3MessageBoxes boxes, DesktopMessageBox box, Action<nint> answer)
    {
        var shown = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        boxes.Shown = dialog => shown.TrySetResult(dialog);
        var pending = boxes.ShowAsync(box).AsTask();
        var dialog = await shown.Task.WaitAsync(Wait);
        await owner.InvokeAsync(_ => answer(dialog));
        return await pending.WaitAsync(Wait);
    }

    // The action area's buttons in order, with ! for a destructive and * for a suggested one.
    private static List<string> ButtonLabels(nint dialog)
    {
        var labels = new List<string>();
        nint children = Children(Parent(ButtonFor(dialog, -6)));
        for (nint node = children; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
        {
            nint button = Marshal.ReadIntPtr(node);
            nint context = StyleContext(button);
            labels.Add((Marshal.PtrToStringUTF8(Label(button)) ?? "") + (UseUnderline(button) == 0 ? "(no mnemonic)" : "")
                + (HasClass(context, "destructive-action") != 0 ? "!" : "") + (HasClass(context, "suggested-action") != 0 ? "*" : ""));
        }
        FreeList(children);
        return labels;
    }

    private static int MessageDialogs()
    {
        int count = 0;
        nint toplevels = Native.ListToplevels();
        for (nint node = toplevels; node != 0; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            if (Native.IsA(Marshal.ReadIntPtr(node), MessageDialogType()) != 0) count++;
        Native.FreeList(toplevels);
        return count;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_dialog_get_widget_for_response")] private static partial nint ButtonFor(nint dialog, int response);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_dialog_response")] private static partial void Respond(nint dialog, int response);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_activate_default")] private static partial int ActivateDefault(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_close")] private static partial void CloseWindow(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_get_title")] private static partial nint Title(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_get_modal")] private static partial int Modal(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_get_transient_for")] private static partial nint TransientFor(nint window);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_button_clicked")] private static partial void Click(nint button);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_button_get_label")] private static partial nint Label(nint button);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_button_get_use_underline")] private static partial int UseUnderline(nint button);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_parent")] private static partial nint Parent(nint widget);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_container_get_children")] private static partial nint Children(nint container);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_style_context")] private static partial nint StyleContext(nint widget);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_style_context_has_class", StringMarshalling = StringMarshalling.Utf8)] private static partial int HasClass(nint context, string name);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_direction")] private static partial int GetDirection(nint widget);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_set_direction")] private static partial void SetDirection(nint widget, int direction);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_message_dialog_get_type")] private static partial nint MessageDialogType();
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_list_free")] private static partial void FreeList(nint list);
}
