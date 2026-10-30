using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux;

// Message boxes as GtkMessageDialogs, modal and transient for the GTK 3 window and destroyed with it. The dialog answers
// through its response signal, never gtk_dialog_run, so the window's loop keeps running. GTK runs in the application's
// process, also inside Flatpak: no portal shows message dialogs.
// Unowned boxes (the application's, on GLibApplicationOwner) have no transient parent.
internal sealed partial class Gtk3MessageBoxes(INativeWindowOwner owner, bool unowned = false) : IDesktopMessageBoxes
{
    private const int ResponseDeleteEvent = -4, ResponseOk = -5, ResponseCancel = -6;
    private const int Modal = 1, DestroyWithParent = 2; // GtkDialogFlags
    private const int ButtonsNone = 0, ButtonsOk = 1; // GtkButtonsType

    // Tests answer the dialog on the GTK thread: it receives the dialog once it is shown.
    internal Action<nint>? Shown { get; set; }

    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        DesktopMessageBoxRules.Check(messageBox);
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = DesktopMessageBoxRules.Ordered(messageBox.Buttons, MessageBoxPlatform.Gtk);
        var generation = owner.Generation;
        var state = new DialogState(messageBox.Buttons);
        var handle = GCHandle.Alloc(state);
        bool shown = false;
        CancellationTokenRegistration registration = default;
        try
        {
            await owner.InvokeAsync(parent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parent == 0 && !unowned || !owner.IsAvailable || owner.Generation != generation) throw new OwnerClosedException();
                nint dialog = NewDialog(parent, parent == 0 ? 0 : Modal | DestroyWithParent, MessageType(messageBox.Kind), ordered.IsEmpty ? ButtonsOk : ButtonsNone, 0);
                if (dialog == 0) throw new NativeBackendUnavailableException("GTK could not create a message dialog.");
                state.Dialog = dialog;
                SetString(dialog, "text", messageBox.Message);
                if (messageBox.Detail is { } detail) SetString(dialog, "secondary-text", detail);
                if (messageBox.Title is { } title) SetTitle(dialog, title);
                // The window's direction, such as a right-to-left Runic Desktop window, also for the dialog.
                if (parent != 0) SetDirection(dialog, GetDirection(parent));
                foreach (var button in ordered)
                {
                    int response = Response(messageBox.Buttons, button);
                    nint widget = AddButton(dialog, DesktopMessageBoxRules.Label(button, '_'), response);
                    if (button.Role == DesktopMessageBoxButtonRole.Destructive) AddClass(GetStyleContext(widget), "destructive-action");
                    if (button.Role == DesktopMessageBoxButtonRole.Default)
                    {
                        AddClass(GetStyleContext(widget), "suggested-action");
                        SetDefaultResponse(dialog, response);
                    }
                }
                ConnectSignals(dialog, GCHandle.ToIntPtr(handle));
                ShowWidget(dialog);
                shown = true;
                Shown?.Invoke(dialog);
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() =>
            {
                state.Result.TrySetCanceled(cancellationToken);
                _ = Close(state);
            });
            var result = await state.Result.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.IsAvailable || owner.Generation != generation) return Closed;
            return new PlatformResult<DesktopMessageBoxButton?>.Success(result);
        }
        catch (OwnerClosedException) { return Closed; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or NativeBackendUnavailableException)
        {
            return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await Close(state).ConfigureAwait(false);
            // The destroy handler is the dialog's last callback; without a dialog nothing holds the handle.
            if (!shown || state.IsDestroyed) handle.Free();
            else state.FreeOnDestroy = handle;
        }
    }

    private static readonly PlatformResult<DesktopMessageBoxButton?> Closed =
        new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);

    // Destroys the dialog on the GTK thread; a closed window destroyed it already.
    private async Task Close(DialogState state)
    {
        if (state.Dialog == 0) return;
        try { await owner.InvokeAsync(_ => { if (!state.IsDestroyed && state.Dialog != 0) DestroyWidget(state.Dialog); }, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException) { }
    }

    // GTK_RESPONSE_CANCEL for the cancel button; the others answer with their index in the box.
    internal static int Response(ImmutableArray<DesktopMessageBoxButton> buttons, DesktopMessageBoxButton button) =>
        button.Role == DesktopMessageBoxButtonRole.Cancel ? ResponseCancel : buttons.IndexOf(button);

    // Esc and the window's close button send GTK_RESPONSE_DELETE_EVENT: the cancel button where there is one, else none.
    internal static DesktopMessageBoxButton? Chosen(ImmutableArray<DesktopMessageBoxButton> buttons, int response)
    {
        if (buttons.IsDefaultOrEmpty) return null;
        if (response is ResponseCancel or ResponseDeleteEvent) return buttons.FirstOrDefault(static button => button.Role == DesktopMessageBoxButtonRole.Cancel);
        return response >= 0 && response < buttons.Length ? buttons[response] : null;
    }

    // GtkMessageType: info, warning, error, or other without an icon.
    private static int MessageType(DesktopMessageBoxKind kind) => kind switch
    {
        DesktopMessageBoxKind.Information => 0,
        DesktopMessageBoxKind.Warning => 1,
        DesktopMessageBoxKind.Error => 3,
        _ => 4,
    };

    private sealed class DialogState(ImmutableArray<DesktopMessageBoxButton> buttons)
    {
        internal ImmutableArray<DesktopMessageBoxButton> Buttons => buttons;
        internal nint Dialog;
        internal volatile bool IsDestroyed;
        internal GCHandle FreeOnDestroy;
        internal TaskCompletionSource<DesktopMessageBoxButton?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static unsafe void ConnectSignals(nint dialog, nint data)
    {
        Connect(dialog, "response", (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&Responded, data, 0, 0);
        Connect(dialog, "destroy", (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&DialogDestroyed, data, 0, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Responded(nint dialog, int response, nint data)
    {
        try
        {
            var state = (DialogState)GCHandle.FromIntPtr(data).Target!;
            state.Result.TrySetResult(Chosen(state.Buttons, response));
        }
        catch { /* Never unwind through GTK. */ }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DialogDestroyed(nint dialog, nint data)
    {
        try
        {
            var handle = GCHandle.FromIntPtr(data);
            var state = (DialogState)handle.Target!;
            state.IsDestroyed = true;
            // Destroyed before an answer: the window closed and took its dialog along.
            state.Result.TrySetException(new OwnerClosedException());
            if (state.FreeOnDestroy.IsAllocated) state.FreeOnDestroy.Free();
        }
        catch { /* Never unwind through GTK. */ }
    }

    // A string property through a GValue, since gtk_message_dialog_new and g_object_set are variadic.
    private static unsafe void SetString(nint instance, string property, string value)
    {
        var gvalue = stackalloc byte[24];
        new Span<byte>(gvalue, 24).Clear();
        InitValue((nint)gvalue, 16 << 2); // G_TYPE_STRING
        try
        {
            SetValueString((nint)gvalue, value);
            SetProperty(instance, property, (nint)gvalue);
        }
        finally { UnsetValue((nint)gvalue); }
    }

    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_message_dialog_new")] private static partial nint NewDialog(nint parent, int flags, int type, int buttons, nint format);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_dialog_add_button", StringMarshalling = StringMarshalling.Utf8)] private static partial nint AddButton(nint dialog, string label, int response);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_dialog_set_default_response")] private static partial void SetDefaultResponse(nint dialog, int response);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_window_set_title", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetTitle(nint window, string title);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_direction")] private static partial int GetDirection(nint widget);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_set_direction")] private static partial void SetDirection(nint widget, int direction);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_get_style_context")] private static partial nint GetStyleContext(nint widget);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_style_context_add_class", StringMarshalling = StringMarshalling.Utf8)] private static partial void AddClass(nint context, string name);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_show")] private static partial void ShowWidget(nint widget);
    [LibraryImport("libgtk-3.so.0", EntryPoint = "gtk_widget_destroy")] private static partial void DestroyWidget(nint widget);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_signal_connect_data", StringMarshalling = StringMarshalling.Utf8)] private static partial ulong Connect(nint instance, string signal, nint callback, nint data, nint destroy, int flags);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_init")] private static partial nint InitValue(nint value, nint type);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_set_string", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetValueString(nint value, string text);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_value_unset")] private static partial void UnsetValue(nint value);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_set_property", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetProperty(nint instance, string name, nint value);
}
