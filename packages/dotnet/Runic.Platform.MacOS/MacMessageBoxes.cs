using System.Collections.Immutable;
using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Message boxes as NSAlert sheets on the owner window, as the file panels are. The sheet is window-modal: the rest of
// the application stays usable. Unowned boxes (the application's, MacApplicationOwner) run as application-modal alerts
// (runModal) on the main thread instead.
internal sealed class MacMessageBoxes(INativeWindowOwner owner, bool unowned = false) : IDesktopMessageBoxes
{
    private const nint AbortResponse = -1001; // NSModalResponseAbort

    private const nint FirstButtonReturn = 1000; // NSAlertFirstButtonReturn
    private const nint WarningStyle = 0, InformationalStyle = 1, CriticalStyle = 2; // NSAlertStyle

    // Tests answer the sheet in process: it receives the NSAlert on the main thread once the sheet began.
    internal Action<nint>? Shown { get; set; }

    public async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken = default)
    {
        DesktopMessageBoxRules.Check(messageBox);
        cancellationToken.ThrowIfCancellationRequested();
        if (unowned) return await ShowUnownedAsync(messageBox, cancellationToken).ConfigureAwait(false);
        var ordered = DesktopMessageBoxRules.Ordered(messageBox.Buttons, MessageBoxPlatform.MacOS);
        var generation = owner.Generation;
        var result = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
        nint alert = 0, window = 0;
        // Set by the sheet's completion on the main thread; the cancellation reports before the sheet ends.
        bool answered = false;
        Task ending = Task.CompletedTask;
        CancellationTokenRegistration registration = default;
        try
        {
            await owner.InvokeAsync(handle =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (handle == 0 || !owner.IsAvailable || owner.Generation != generation) throw new OwnerClosedException();
                using var pool = new Pool();
                alert = CreateAlert(messageBox, ordered);
                window = handle;
                Send(window, Sel("retain"));
                var completion = MacDesktopBlock.Response(response => { answered = true; result.TrySetResult(response); });
                try { Args(alert, Sel("beginSheetModalForWindow:completionHandler:"), window, completion); }
                finally { MacDesktopBlock.Release(completion); }
                Shown?.Invoke(alert);
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() =>
            {
                ending = MacOsMainQueue.InvokeAsync(() =>
                {
                    using var pool = new Pool();
                    if (!answered) Args(window, Sel("endSheet:returnCode:"), Send(alert, Sel("window")), 0);
                }).AsTask();
                result.TrySetCanceled(cancellationToken);
            });
            nint response = await result.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.IsAvailable || owner.Generation != generation)
                return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed);
            nint index = response - FirstButtonReturn;
            return new PlatformResult<DesktopMessageBoxButton?>.Success(index >= 0 && index < ordered.Length ? ordered[(int)index] : null);
        }
        catch (OwnerClosedException) { return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await ending.ConfigureAwait(false);
            if (alert != 0 || window != 0) await MacOsMainQueue.InvokeAsync(() => { Release(alert); Release(window); }).ConfigureAwait(false);
        }
    }

    // An NSAlert with the box's text, style and buttons; the caller releases it.
    private static nint CreateAlert(DesktopMessageBox messageBox, ImmutableArray<DesktopMessageBoxButton> ordered)
    {
        var alert = Send(Send(Class("NSAlert"), Sel("alloc")), Sel("init"));
        Arg(alert, Sel("setMessageText:"), String(messageBox.Message));
        Arg(alert, Sel("setInformativeText:"), String(messageBox.Detail ?? ""));
        Arg(alert, Sel("setAlertStyle:"), messageBox.Kind switch
        {
            DesktopMessageBoxKind.Error => CriticalStyle,
            DesktopMessageBoxKind.Warning => WarningStyle,
            _ => InformationalStyle,
        });
        // Without buttons NSAlert adds its own localized OK.
        foreach (var entry in ordered)
        {
            var button = Arg(alert, Sel("addButtonWithTitle:"), String(entry.Label));
            // Return chooses only the default button and Esc the cancel button; NSAlert would give the first button Return.
            Arg(button, Sel("setKeyEquivalent:"), String(entry.Role switch
            {
                DesktopMessageBoxButtonRole.Default => "\r",
                DesktopMessageBoxButtonRole.Cancel => "\u001b",
                _ => "",
            }));
            if (entry.Role == DesktopMessageBoxButtonRole.Destructive && BoolArg(button, Sel("respondsToSelector:"), Sel("setHasDestructiveAction:")) != 0)
                Arg(button, Sel("setHasDestructiveAction:"), 1);
        }
        return alert;
    }

    // The application's box: an application-modal alert (runModal) on the main thread, in front of every window. The
    // main queue keeps running during the modal loop, so cancellation aborts it from there.
    private async ValueTask<PlatformResult<DesktopMessageBoxButton?>> ShowUnownedAsync(DesktopMessageBox messageBox, CancellationToken cancellationToken)
    {
        var ordered = DesktopMessageBoxRules.Ordered(messageBox.Buttons, MessageBoxPlatform.MacOS);
        nint response = 0;
        bool running = false, answered = false;
        Task aborting = Task.CompletedTask;
        using var registration = cancellationToken.Register(() => aborting = MacOsMainQueue.InvokeAsync(() =>
        {
            if (running && !answered) Send(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("abortModal"));
        }).AsTask());
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pool = new Pool();
                var alert = CreateAlert(messageBox, ordered);
                try
                {
                    BoolArg(Send(Class("NSApplication"), Sel("sharedApplication")), Sel("activateIgnoringOtherApps:"), 1);
                    running = true;
                    Shown?.Invoke(alert);
                    response = Send(alert, Sel("runModal"));
                }
                finally
                {
                    answered = true;
                    Release(alert);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NativeBackendUnavailableException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new PlatformResult<DesktopMessageBoxButton?>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        }
        finally { await aborting.ConfigureAwait(false); }
        cancellationToken.ThrowIfCancellationRequested();
        if (response == AbortResponse) throw new OperationCanceledException(cancellationToken);
        nint index = response - FirstButtonReturn;
        return new PlatformResult<DesktopMessageBoxButton?>.Success(index >= 0 && index < ordered.Length ? ordered[(int)index] : null);
    }
}
