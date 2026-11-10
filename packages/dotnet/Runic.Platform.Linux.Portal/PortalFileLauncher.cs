using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;

namespace Runic.Platform.Linux.Portal;

internal sealed partial class PortalFileLauncher(IPortalWindowOwner owner, PortalApplication? application = null,
    IPortalTransport? uriTransport = null, Func<SafeFileHandle, bool, IPortalTransport>? fileTransport = null) : IDesktopFileLauncher
{
    // The OpenURI portal request, which applies the policy before any D-Bus call.
    public ValueTask<PlatformResult<PlatformUnit>> LaunchUriAsync(Uri uri, CancellationToken cancellationToken = default) =>
        PortalPlatformProvider.OpenUriCoreAsync(owner, uri, uriTransport ?? new PortalTransport(application: application), cancellationToken);

    // A file its caller opened and checked for this launch (runic-host/1, Runic.Application.Views.Desktop's hosted
    // windows), with its full path: a launch in this asynchronous flow hands the portal this descriptor instead of opening
    // the path again, so the file launched is the one checked. A launch of any other path in the flow fails without
    // opening it. The caller keeps and disposes it.
    internal static readonly AsyncLocal<(string Path, SafeFileHandle File)?> Preopened = new();

    public async ValueTask<PlatformResult<PlatformUnit>> LaunchAsync(string path, DesktopFileOperation operation = DesktopFileOperation.Open, CancellationToken cancellationToken = default)
    {
        path = DesktopServiceValidation.FilePath(path, operation);
        SafeFileHandle? preopened = null;
        if (Preopened.Value is { } given)
        {
            // The checked file, or nothing: a path spelled otherwise than the checked one is never opened again.
            if (!string.Equals(given.Path, path, StringComparison.Ordinal))
                return new PlatformResult<PlatformUnit>.Failed(PlatformFailureCode.PermissionDenied);
            preopened = given.File;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsAvailable) return new PlatformResult<PlatformUnit>.Unavailable(PlatformUnavailableReason.OwnerClosed);
        // O_RDONLY | O_CLOEXEC | O_NONBLOCK: a special file must not block the caller.
        using var opened = preopened is null ? new SafeFileHandle(Open(path, 0x80000 | 0x800), ownsHandle: true) : null;
        var file = preopened ?? opened!;
        if (file.IsInvalid)
        {
            int errno = Marshal.GetLastPInvokeError();
            return new PlatformResult<PlatformUnit>.Failed(errno is 1 or 13 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.Other,
                PlatformDiagnostic.FromErrno(errno));
        }
        try
        {
            var ask = operation == DesktopFileOperation.ChooseApplication;
            var response = await PortalRequest.RunAsync(owner, fileTransport?.Invoke(file, ask) ?? new PortalTransport(file: file, ask: ask, application: application),
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
