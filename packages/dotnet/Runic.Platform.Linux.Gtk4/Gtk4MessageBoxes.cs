using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Gtk4;

// Message boxes as GtkAlertDialogs (GTK 4.10 and later), modal for the GTK 4 window and answered through
// gtk_alert_dialog_choose with a GCancellable. An alert dialog has no title, icon or destructive style; its buttons take
// mnemonics. Older GTK 4 reports the boxes unavailable.
internal sealed partial class Gtk4MessageBoxes(INativeWindowOwner owner) : IDesktopMessageBoxes, IDesktopServiceStatus
{
    private const string Gtk = "libgtk-4.so.1";
    private const int DialogErrorCancelled = 1; // GTK_DIALOG_ERROR_CANCELLED; DISMISSED (2) closes without a button

    // Whether this GTK has GtkAlertDialog.
    internal static bool IsSupported { get; } = NativeLibrary.TryLoad(Gtk, typeof(Gtk4MessageBoxes).Assembly, null, out var gtk)
        && NativeLibrary.TryGetExport(gtk, "gtk_alert_dialog_choose", out _);

    // Tests answer the dialog on the GTK thread: it receives the GtkAlertDialog once it is chosen from.
    internal Action<nint>? Shown { get; set; }

    public CapabilityStatus Status { get; } = IsSupported ? new CapabilityStatus.Available()
        : new CapabilityStatus.Unavailable(PlatformUnavailableReason.BackendUnavailable);

    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        DesktopMessageBoxRules.Check(messageBox);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported) return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        var ordered = DesktopMessageBoxRules.Ordered(messageBox.Buttons, MessageBoxPlatform.Gtk);
        var generation = owner.Generation;
        var state = new ChoiceState();
        nint dialog = 0, cancellable = 0;
        CancellationTokenRegistration registration = default;
        try
        {
            await owner.InvokeAsync(parent =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (parent == 0 || !owner.IsAvailable || owner.Generation != generation) throw new OwnerClosedException();
                dialog = NewDialog("");
                if (dialog == 0) throw new NativeBackendUnavailableException("GTK could not create an alert dialog.");
                SetMessage(dialog, messageBox.Message);
                if (messageBox.Detail is { } detail) SetDetail(dialog, detail);
                SetModal(dialog, 1);
                if (!ordered.IsEmpty)
                {
                    SetButtons(dialog, [.. ordered.Select(static button => DesktopMessageBoxRules.Label(button, '_'))]);
                    int cancel = ordered.IndexOf(ordered.FirstOrDefault(static button => button.Role == DesktopMessageBoxButtonRole.Cancel)!);
                    // Without a cancel button Esc dismisses the dialog without a button (-1 is the default).
                    SetCancelButton(dialog, cancel);
                    SetDefaultButton(dialog, ordered.IndexOf(ordered.FirstOrDefault(static button => button.Role == DesktopMessageBoxButtonRole.Default)!));
                }
                cancellable = NewCancellable();
                Choose(dialog, parent, cancellable, GCHandle.ToIntPtr(GCHandle.Alloc(state)));
                Shown?.Invoke(dialog);
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() =>
            {
                state.Result.TrySetCanceled(cancellationToken);
                _ = CancelAsync(cancellable);
            });
            int index = await state.Result.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.IsAvailable || owner.Generation != generation)
                return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            // Without buttons of the application GTK shows its own Close button.
            return new PlatformResult<DesktopMessageBoxButton?>.Success(index >= 0 && index < ordered.Length ? ordered[index] : null);
        }
        catch (OwnerClosedException) { return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or NativeBackendUnavailableException)
        {
            return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            if (dialog != 0 || cancellable != 0)
            {
                try { await owner.InvokeAsync(_ => { if (cancellable != 0) Unref(cancellable); if (dialog != 0) Unref(dialog); }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException) { }
            }
        }
    }

    // Cancelling closes the dialog; the choice then completes with GTK_DIALOG_ERROR_CANCELLED.
    private async Task CancelAsync(nint cancellable)
    {
        if (cancellable == 0) return;
        try { await owner.InvokeAsync(_ => Cancel(cancellable), CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) when (error is OwnerClosedException or ObjectDisposedException) { }
    }

    private sealed class ChoiceState
    {
        internal TaskCompletionSource<int> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static unsafe void Choose(nint dialog, nint parent, nint cancellable, nint data) =>
        ChooseNative(dialog, parent, cancellable, (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&Chosen, data);

    private static unsafe void SetButtons(nint dialog, string[] labels)
    {
        var pointers = new nint[labels.Length + 1];
        try
        {
            for (int index = 0; index < labels.Length; index++) pointers[index] = Marshal.StringToCoTaskMemUTF8(labels[index]);
            fixed (nint* array = pointers) SetButtonsNative(dialog, (nint)array);
        }
        finally { foreach (var pointer in pointers) if (pointer != 0) Marshal.FreeCoTaskMem(pointer); }
    }

    // The choice's index, or -1 when the dialog was dismissed without a button; a cancelled choice was reported already.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Chosen(nint dialog, nint result, nint data)
    {
        var handle = GCHandle.FromIntPtr(data);
        try
        {
            var state = (ChoiceState)handle.Target!;
            int index = ChooseFinish(dialog, result, out nint error);
            if (error != 0)
            {
                int code = Marshal.ReadInt32(error, sizeof(uint));
                FreeError(error);
                if (code == DialogErrorCancelled) state.Result.TrySetCanceled();
                else state.Result.TrySetResult(-1);
            }
            else state.Result.TrySetResult(index);
        }
        catch { /* Never unwind through GTK. */ }
        finally { handle.Free(); }
    }

    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_new", StringMarshalling = StringMarshalling.Utf8)] private static partial nint NewDialog(string format);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_set_message", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetMessage(nint dialog, string message);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_set_detail", StringMarshalling = StringMarshalling.Utf8)] private static partial void SetDetail(nint dialog, string detail);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_set_modal")] private static partial void SetModal(nint dialog, int modal);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_set_buttons")] private static partial void SetButtonsNative(nint dialog, nint labels);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_set_cancel_button")] private static partial void SetCancelButton(nint dialog, int button);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_set_default_button")] private static partial void SetDefaultButton(nint dialog, int button);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_choose")] private static partial void ChooseNative(nint dialog, nint parent, nint cancellable, nint callback, nint data);
    [LibraryImport(Gtk, EntryPoint = "gtk_alert_dialog_choose_finish")] private static partial int ChooseFinish(nint dialog, nint result, out nint error);
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_new")] private static partial nint NewCancellable();
    [LibraryImport("libgio-2.0.so.0", EntryPoint = "g_cancellable_cancel")] private static partial void Cancel(nint cancellable);
    [LibraryImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] private static partial void FreeError(nint error);
    [LibraryImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] private static partial void Unref(nint instance);
}
