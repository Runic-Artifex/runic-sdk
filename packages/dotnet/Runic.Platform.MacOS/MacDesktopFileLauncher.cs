using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

internal sealed class MacDesktopFileLauncher(INativePickerOwner owner) : IDesktopFileLauncher
{
    public async ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        nint panel = 0, applicationUrl = 0;
        var generation = owner.Generation;
        Task cancellationTask = Task.CompletedTask;
        CancellationTokenRegistration registration = default;
        var result = new TaskCompletionSource<PlatformResult<Unit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await owner.InvokeAsync(window =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var pool = new Pool();
                if (operation != DesktopFileOperation.ChooseApplication)
                {
                    var workspace = Send(Class("NSWorkspace"), Sel("sharedWorkspace"));
                    if (operation == DesktopFileOperation.Reveal)
                    { Arg(workspace, Sel("activateFileViewerSelectingURLs:"), Array(Url(path))); result.TrySetResult(Success()); }
                    else result.TrySetResult(BoolArg(workspace, Sel("openURL:"), Url(path)) != 0 ? Success() : Failed(FailureCode.IoError));
                    return;
                }
                panel = Send(Class("NSOpenPanel"), Sel("openPanel")); Send(panel, Sel("retain"));
                Arg(panel, Sel("setTitle:"), String("Choose an application"));
                Arg(panel, Sel("setDirectoryURL:"), Url("/Applications"));
                Arg(panel, Sel("setAllowedFileTypes:"), Array(String("app")));
                Arg(panel, Sel("setAllowsMultipleSelection:"), 0);
                var block = MacDesktopBlock.Response(response =>
                {
                    try
                    {
                        using var callbackPool = new Pool();
                        if (response != 1 || cancellationToken.IsCancellationRequested)
                        {
                            if (cancellationToken.IsCancellationRequested) result.TrySetCanceled(cancellationToken);
                            else result.TrySetResult(Failed(FailureCode.UserDismissed));
                            return;
                        }
                        if (!owner.IsAvailable || owner.Generation != generation)
                        { result.TrySetResult(new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed)); return; }
                        applicationUrl = Send(panel, Sel("URL")); Send(applicationUrl, Sel("retain"));
                        var completion = MacDesktopBlock.Create((_, error) => result.TrySetResult(error == 0 ? Success() : Failed(FailureCode.IoError)));
                        try
                        {
                            var configuration = Send(Class("NSWorkspaceOpenConfiguration"), Sel("configuration"));
                            Args4(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("openURLs:withApplicationAtURL:configuration:completionHandler:"),
                                Array(Url(path)), applicationUrl, configuration, completion);
                        }
                        finally { MacDesktopBlock.Release(completion); }
                    }
                    catch (Exception error) { result.TrySetException(error); }
                });
                try { Args(panel, Sel("beginSheetModalForWindow:completionHandler:"), window, block); }
                finally { MacDesktopBlock.Release(block); }
            }, cancellationToken).ConfigureAwait(false);
            registration = cancellationToken.Register(() => cancellationTask = MacOsMainQueue.InvokeAsync(() =>
            { using var pool = new Pool(); if (!result.Task.IsCompleted && panel != 0 && applicationUrl == 0) Arg(panel, Sel("cancel:"), 0); }).AsTask());
            var outcome = await result.Task.ConfigureAwait(false);
            return outcome;
        }
        catch (OwnerClosedException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellationTask.ConfigureAwait(false);
            if (panel != 0 || applicationUrl != 0) await MacOsMainQueue.InvokeAsync(() => { Release(panel); Release(applicationUrl); }).ConfigureAwait(false);
        }
    }
    private static PlatformResult<Unit> Success() => new PlatformResult<Unit>.Success(new Unit());
    private static PlatformResult<Unit> Failed(FailureCode code) => new PlatformResult<Unit>.Failed(code);
}
