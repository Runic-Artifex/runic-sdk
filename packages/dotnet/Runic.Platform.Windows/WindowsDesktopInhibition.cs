using Microsoft.Win32.SafeHandles;
using Runic.Platform.Runtime;
using System.Runtime.InteropServices;

namespace Runic.Platform.Windows;

internal sealed partial class WindowsDesktopInhibition : IDesktopInhibition
{
    public DesktopInhibitionEffects SupportedEffects => DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep;
    public unsafe ValueTask<PlatformResult<IDesktopInhibitionLease>> AcquireAsync(DesktopInhibitionEffects effects, string reason, CancellationToken cancellationToken = default)
    {
        InhibitionValidation.Validate(effects, reason);
        cancellationToken.ThrowIfCancellationRequested();
        fixed (char* text = reason)
        {
            // REASON_CONTEXT's union must reserve its full detailed-reason size
            // even when SIMPLE_STRING is used (24 bytes x86, 32 bytes x64/ARM64).
            var context = new ReasonContext { Version = 0, Flags = 1, Text = (nint)text };
            var handle = PowerCreateRequest(ref context);
            if (handle.IsInvalid)
            {
                var failure = LastFailure();
                handle.Dispose();
                return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Failed(failure));
            }
            var lease = new Lease(handle, effects);
            try
            {
                if (effects.HasFlag(DesktopInhibitionEffects.SystemSleep) && PowerSetRequest(handle, 1) == 0 ||
                    effects.HasFlag(DesktopInhibitionEffects.DisplaySleep) && PowerSetRequest(handle, 0) == 0)
                {
                    var failure = LastFailure();
                    lease.Close();
                    return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Failed(failure));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Success(lease));
            }
            catch { lease.Close(); throw; }
        }
    }
    private static FailureCode LastFailure() => Marshal.GetLastPInvokeError() == 5 ? FailureCode.PermissionDenied : FailureCode.IoError;
    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext { internal uint Version; internal uint Flags; internal nint Text; internal uint ResourceId; internal uint StringCount; internal nint Strings; }
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
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle PowerCreateRequest(ref ReasonContext context);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int PowerSetRequest(SafeFileHandle request, int requestType);
}
