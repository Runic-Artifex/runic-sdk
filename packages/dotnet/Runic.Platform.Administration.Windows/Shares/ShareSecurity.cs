using Windows.Win32;
using Windows.Win32.Security;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;
namespace Runic.Platform.Administration.Windows.Shares;
internal static partial class ShareSecurity
{
    internal static unsafe byte[] ValidateDescriptor(ImmutableArray<byte> descriptor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        if (descriptor.IsDefaultOrEmpty) throw new ArgumentException("Supply a self-relative security descriptor.", nameof(descriptor));
        var bytes = descriptor.ToArray();
        fixed (byte* pointer = bytes)
        {
            // Validate before native calls that inspect descriptor offsets.
            _ = new System.Security.AccessControl.RawSecurityDescriptor(bytes, 0);
            if (!PInvoke.IsValidSecurityDescriptor(new(pointer)) ||
                !PInvoke.GetSecurityDescriptorControl(new(pointer), out var control, out _) || (control & 0x8000) == 0 ||
                PInvoke.GetSecurityDescriptorLength(new(pointer)) != bytes.Length)
                throw new ArgumentException("Supply a complete self-relative security descriptor.", nameof(descriptor));
        }
        return bytes;
    }

    internal static unsafe ImmutableArray<byte>? ReadDescriptor(nint descriptor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        // A missing stored descriptor is a valid share state, not an empty DACL.
        if (descriptor == 0) return null;
        NativeError.CheckWin32(PInvoke.GetSecurityDescriptorControl(new((void*)descriptor), out var control, out _).Value, "Read share security descriptor");
        if ((control & 0x8000) != 0)
        {
            var length = checked((int)PInvoke.GetSecurityDescriptorLength(new((void*)descriptor)));
            return [.. new ReadOnlySpan<byte>((void*)descriptor, length)];
        }
        uint needed = 0;
        var status = PInvoke.MakeSelfRelativeSD(new((void*)descriptor), default, ref needed).Value;
        if (status == 0 && Marshal.GetLastPInvokeError() != 122) throw NativeError.Win32("Size share security descriptor", Marshal.GetLastPInvokeError());
        var buffer = new byte[needed];
        fixed (byte* pointer = buffer)
            NativeError.CheckWin32(PInvoke.MakeSelfRelativeSD(new((void*)descriptor), new(pointer), ref needed).Value, "Copy share security descriptor");
        return [.. buffer];
    }

}
