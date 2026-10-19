using Runic.Platform.Runtime;
using static Runic.Platform.MacOS.MacDesktopNative;

namespace Runic.Platform.MacOS;

// Opens a web or mail URI with NSWorkspace: the user's default browser or mail client.
internal sealed class MacUriLauncher(INativePickerOwner owner)
{
    // Launch Services has no application for the URL's scheme (kLSApplicationNotFoundErr).
    private const long ApplicationNotFound = -10814;
    // NSUserCancelledError.
    private const long UserCancelled = 3072;

    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(Uri uri, CancellationToken cancellationToken)
    {
        string target = DesktopUriPolicy.Require(uri);
        Task<PlatformResult<PlatformUnit>>? opened = null;
        try
        {
            await owner.InvokeAsync(_ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                opened = Open(target);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OwnerClosedException) { }
        return opened is null ? new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed)
            : await opened.ConfigureAwait(false);
    }

    // Hands an absolute URI the policy has accepted to NSWorkspace openURL:configuration:completionHandler:. The task
    // completes from the completion handler. Tests dispatch a test-only scheme through it.
    internal static Task<PlatformResult<PlatformUnit>> Open(string uri)
    {
        var result = new TaskCompletionSource<PlatformResult<PlatformUnit>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pool = new Pool();
        nint url = Arg(Class("NSURL"), Sel("URLWithString:"), String(uri));
        if (url == 0) return Task.FromResult<PlatformResult<PlatformUnit>>(new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.InvalidData));
        var configuration = Send(Class("NSWorkspaceOpenConfiguration"), Sel("configuration"));
        var completion = MacDesktopBlock.Create((_, error) => result.TrySetResult(Outcome(error)));
        try { Args3(Send(Class("NSWorkspace"), Sel("sharedWorkspace")), Sel("openURL:configuration:completionHandler:"), url, configuration, completion); }
        finally { MacDesktopBlock.Release(completion); }
        return result.Task;
    }

    private static PlatformResult<PlatformUnit> Outcome(nint error)
    {
        if (error == 0) return new PlatformResult<PlatformUnit>.Success(new PlatformUnit());
        var diagnostic = ErrorDiagnostic(error);
        return diagnostic.Code switch
        {
            ApplicationNotFound => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            UserCancelled when diagnostic.Domain == "NSCocoaErrorDomain" => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.UserDismissed, diagnostic),
            _ => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.IoError, diagnostic),
        };
    }
}
