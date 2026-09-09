using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

internal sealed partial class PortalFileLauncher(IPortalWindowOwner owner) : IDesktopFileLauncher
{
    public async ValueTask<PlatformResult<Unit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed);
        using var file = new SafeFileHandle(Open(path, 0x80000 | 0x800), ownsHandle: true); // O_RDONLY | O_CLOEXEC | O_NONBLOCK: a special file must not block the caller.
        if (file.IsInvalid) return new PlatformResult<Unit>.Failed(Marshal.GetLastPInvokeError() is 1 or 13 ? FailureCode.PermissionDenied : FailureCode.IoError);
        try
        {
            var response = await PortalRequest.RunAsync(owner, new PortalTransport(file: file, ask: operation == DesktopFileOperation.ChooseApplication),
                operation == DesktopFileOperation.Reveal ? "OpenDirectory" : "OpenFile", "", cancellationToken).ConfigureAwait(false);
            return response.Code switch
            {
                0 => new PlatformResult<Unit>.Success(new Unit()),
                1 => new PlatformResult<Unit>.Failed(FailureCode.UserDismissed),
                _ => new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable),
            };
        }
        catch (OwnerClosedException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.OwnerClosed); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<Unit>.Unavailable(UnavailableReason.BackendUnavailable); }
    }
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
}
