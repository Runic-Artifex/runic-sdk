using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

internal sealed class MacDesktopFileLauncher(INativePickerOwner owner) : IDesktopFileLauncher
{
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        if (operation == DesktopFileOperation.Open && StartsProgram(path)) return Failed(PlatformFailureCode.PermissionDenied);
        nint panel = 0, applicationUrl = 0;
        var generation = owner.Generation;
        Task cancellationTask = Task.CompletedTask;
        CancellationTokenRegistration registration = default;
        var result = new TaskCompletionSource<PlatformResult<PlatformUnit>>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    else result.TrySetResult(BoolArg(workspace, Sel("openURL:"), Url(path)) != 0 ? Success() : Failed(PlatformFailureCode.IoError));
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
                            else result.TrySetResult(Failed(PlatformFailureCode.UserDismissed));
                            return;
                        }
                        if (!owner.IsAvailable || owner.Generation != generation)
                        { result.TrySetResult(new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed)); return; }
                        applicationUrl = Send(panel, Sel("URL")); Send(applicationUrl, Sel("retain"));
                        var completion = MacDesktopBlock.Create((_, error) => result.TrySetResult(error == 0 ? Success()
                            : new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError, ErrorDiagnostic(error))));
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
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        finally
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            await cancellationTask.ConfigureAwait(false);
            if (panel != 0 || applicationUrl != 0) await MacOsMainQueue.InvokeAsync(() => { Release(panel); Release(applicationUrl); }).ConfigureAwait(false);
        }
    }
    // LaunchServices runs applications, Terminal scripts, workflows, installers and
    // extensionless executables, and follows location files. Opening a document must never execute code.
    private static readonly HashSet<string> ProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".app", ".command", ".tool", ".terminal", ".workflow", ".action", ".jar", ".pkg", ".mpkg",
        ".prefpane", ".saver", ".mobileconfig", ".webloc", ".inetloc", ".fileloc",
    };
    internal static bool StartsProgram(string path)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        // LaunchServices opens a symlink's target, so classify both names.
        string target;
        try { target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (IOException) { return true; } // An unresolvable link cannot be classified.
        return IsProgram(path) || IsProgram(target);
        static bool IsProgram(string path)
        {
            if (ProgramExtensions.Contains(Path.GetExtension(path))) return true;
            if (OperatingSystem.IsWindows() || Path.GetExtension(path).Length != 0 || !File.Exists(path)) return false;
            return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
    }
    private static PlatformResult<PlatformUnit> Success() => new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
    private static PlatformResult<PlatformUnit> Failed(PlatformFailureCode code) => new PlatformResult<PlatformUnit>.Failed(code);
}
