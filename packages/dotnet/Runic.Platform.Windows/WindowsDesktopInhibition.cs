using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.System.Threading;

namespace Runic.Platform.Windows;

[System.Runtime.Versioning.SupportedOSPlatform("windows8.0")]
internal sealed class WindowsDesktopInhibition : IDesktopInhibition
{
    public DesktopInhibitionEffects SupportedEffects => DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep;
    public unsafe ValueTask<PlatformResult<IDesktopInhibitionLease>> AcquireAsync(DesktopInhibitionEffects effects, string reason, CancellationToken cancellationToken = default)
    {
        InhibitionValidation.Validate(effects, reason);
        cancellationToken.ThrowIfCancellationRequested();
        fixed (char* text = reason)
        {
            // The generated REASON_CONTEXT reserves the union's full detailed-reason size
            // even when SIMPLE_STRING is used (24 bytes x86, 32 bytes x64/ARM64).
            var context = new REASON_CONTEXT { Version = PInvoke.POWER_REQUEST_CONTEXT_VERSION, Flags = POWER_REQUEST_CONTEXT_FLAGS.POWER_REQUEST_CONTEXT_SIMPLE_STRING };
            context.Reason.SimpleReasonString = text;
            // The SafeFileHandle overload closes the request with CloseHandle, as documented.
            var handle = PInvoke.PowerCreateRequest(in context);
            if (handle.IsInvalid)
            {
                var failure = LastFailure();
                handle.Dispose();
                return ValueTask.FromResult(failure);
            }
            var lease = new Lease(handle, effects);
            try
            {
                if (effects.HasFlag(DesktopInhibitionEffects.SystemSleep) && !PInvoke.PowerSetRequest(handle, POWER_REQUEST_TYPE.PowerRequestSystemRequired) ||
                    effects.HasFlag(DesktopInhibitionEffects.DisplaySleep) && !PInvoke.PowerSetRequest(handle, POWER_REQUEST_TYPE.PowerRequestDisplayRequired))
                {
                    var failure = LastFailure();
                    lease.Close();
                    return ValueTask.FromResult(failure);
                }
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Success(lease));
            }
            catch { lease.Close(); throw; }
        }
    }
    private static PlatformResult<IDesktopInhibitionLease> LastFailure()
    {
        int error = Marshal.GetLastPInvokeError();
        return new PlatformResult<IDesktopInhibitionLease>.Failed(
            error == 5 ? PlatformFailureCode.PermissionDenied : PlatformFailureCode.IoError, PlatformDiagnostic.FromWin32Error(error));
    }
    private sealed class Lease(SafeFileHandle handle, DesktopInhibitionEffects effects) : IDesktopInhibitionLease
    {
        private SafeFileHandle? _handle = handle;
        public DesktopInhibitionEffects Effects => effects;
        internal void Close() => Interlocked.Exchange(ref _handle, null)?.Dispose();
        public ValueTask DisposeAsync()
        {
            // Closing the independent power-request handle removes all its counts,
            // including a partial acquisition. It does not affect other leases.
            Close();
            return ValueTask.CompletedTask;
        }
    }
}
