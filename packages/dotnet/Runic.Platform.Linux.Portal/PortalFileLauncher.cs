using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

internal sealed partial class PortalFileLauncher(IPortalWindowOwner owner, PortalApplication? application = null) : IDesktopFileLauncher
{
    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        using var file = new SafeFileHandle(Open(path, 0x80000 | 0x800), ownsHandle: true); // O_RDONLY | O_CLOEXEC | O_NONBLOCK: a special file must not block the caller.
        if (file.IsInvalid)
        {
            int errno = Marshal.GetLastPInvokeError();
            return new PlatformResult<PlatformUnit>.Failed(errno is 1 or 13 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError,
                PlatformDiagnostic.FromErrno(errno));
        }
        try
        {
            var response = await PortalRequest.RunAsync(owner, new PortalTransport(file: file, ask: operation == DesktopFileOperation.ChooseApplication, application: application),
                operation == DesktopFileOperation.Reveal ? "OpenDirectory" : "OpenFile", "", cancellationToken).ConfigureAwait(false);
            return response.Code switch
            {
                0 => new PlatformResult<PlatformUnit>.Success(new PlatformUnit()),
                1 => new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.UserDismissed, response.Diagnostic),
                _ => new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
            };
        }
        catch (OwnerClosedException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed); }
        catch (NativeBackendUnavailableException) { return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.BackendUnavailable); }
    }
    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);
}
