using Runic.Platform.Runtime;
using System.Runtime.InteropServices;

namespace Runic.Platform.MacOS;

internal sealed partial class MacDesktopInhibition : IDesktopInhibition
{
    public DesktopInhibitionEffects SupportedEffects => DesktopInhibitionEffects.SystemSleep | DesktopInhibitionEffects.DisplaySleep;
    public ValueTask<PlatformResult<IDesktopInhibitionLease>> AcquireAsync(DesktopInhibitionEffects effects, string reason, CancellationToken cancellationToken = default)
    {
        InhibitionValidation.Validate(effects, reason);
        cancellationToken.ThrowIfCancellationRequested();
        var assertions = new List<uint>();
        nint label = 0;
        try
        {
            label = StringCreate(0, reason, 0x08000100);
            if (label == 0) return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Failed(FailureCode.IoError));
            foreach (var (effect, name) in new[] { (DesktopInhibitionEffects.SystemSleep, "PreventUserIdleSystemSleep"), (DesktopInhibitionEffects.DisplaySleep, "PreventUserIdleDisplaySleep") })
            {
                if (!effects.HasFlag(effect)) continue;
                var type = StringCreate(0, name, 0x08000100);
                if (type == 0) return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Failed(FailureCode.IoError));
                try
                {
                    if (CreateAssertion(type, 255, label, out var id) != 0)
                        return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Failed(FailureCode.IoError));
                    assertions.Add(id);
                }
                finally { Release(type); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var lease = new Lease(assertions.ToArray(), effects);
            assertions.Clear();
            return ValueTask.FromResult<PlatformResult<IDesktopInhibitionLease>>(new PlatformResult<IDesktopInhibitionLease>.Success(lease));
        }
        finally
        {
            foreach (var id in assertions) _ = ReleaseAssertion(id);
            if (label != 0) Release(label);
        }
    }
    private sealed class Lease(uint[] ids, DesktopInhibitionEffects effects) : IDesktopInhibitionLease
    {
        private uint[]? _ids = ids;
        public DesktopInhibitionEffects Effects => effects;
        public ValueTask DisposeAsync()
        {
            foreach (var id in Interlocked.Exchange(ref _ids, null) ?? []) _ = ReleaseAssertion(id);
            return ValueTask.CompletedTask;
        }
    }
    [LibraryImport("/System/Library/Frameworks/IOKit.framework/IOKit", EntryPoint = "IOPMAssertionCreateWithName")]
    private static partial int CreateAssertion(nint type, uint level, nint reason, out uint id);
    [LibraryImport("/System/Library/Frameworks/IOKit.framework/IOKit", EntryPoint = "IOPMAssertionRelease")]
    private static partial int ReleaseAssertion(uint id);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFStringCreateWithCString", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint StringCreate(nint allocator, string text, uint encoding);
    [LibraryImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    private static partial void Release(nint value);
}
