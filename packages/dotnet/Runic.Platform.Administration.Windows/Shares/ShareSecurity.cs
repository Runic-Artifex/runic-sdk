using Windows.Win32;
using Windows.Win32.Security;
using System.Runtime.InteropServices;
using Runic.Platform.Administration.Windows.Internal;
namespace Runic.Platform.Administration.Windows.Shares;
internal static partial class ShareSecurity
{
    internal static unsafe byte[] ValidateDescriptor(WindowsSecurityDescriptor descriptor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        ArgumentNullException.ThrowIfNull(descriptor);
        // WindowsSecurityDescriptor parsed the descriptor before the native calls that inspect its offsets.
        var bytes = descriptor.BinaryForm.ToArray();
        fixed (byte* pointer = bytes)
        {
            if (!PInvoke.IsValidSecurityDescriptor(new(pointer)) ||
                !PInvoke.GetSecurityDescriptorControl(new(pointer), out var control, out _) || (control & 0x8000) == 0 ||
                PInvoke.GetSecurityDescriptorLength(new(pointer)) != bytes.Length)
                throw new ArgumentException("Supply a complete self-relative security descriptor.", nameof(descriptor));
        }
        return bytes;
    }

    internal static unsafe WindowsSecurityDescriptor? ReadDescriptor(nint descriptor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException();
        // A missing stored descriptor is a valid share state, not an empty DACL.
        if (descriptor == 0) return null;
        NativeError.CheckWin32(PInvoke.GetSecurityDescriptorControl(new((void*)descriptor), out var control, out _).Value, "Read share security descriptor");
        if ((control & 0x8000) != 0)
        {
            var length = checked((int)PInvoke.GetSecurityDescriptorLength(new((void*)descriptor)));
            return Wrap(new ReadOnlySpan<byte>((void*)descriptor, length));
        }
        uint needed = 0;
        var status = PInvoke.MakeSelfRelativeSD(new((void*)descriptor), default, ref needed).Value;
        if (status == 0 && Marshal.GetLastPInvokeError() != 122) throw NativeError.Win32("Size share security descriptor", Marshal.GetLastPInvokeError());
        var buffer = new byte[needed];
        fixed (byte* pointer = buffer)
            NativeError.CheckWin32(PInvoke.MakeSelfRelativeSD(new((void*)descriptor), new(pointer), ref needed).Value, "Copy share security descriptor");
        return Wrap(buffer);
    }

    private static WindowsSecurityDescriptor Wrap(ReadOnlySpan<byte> bytes)
    {
        try { return WindowsSecurityDescriptor.FromBinaryForm(bytes); }
        catch (ArgumentException) { throw NativeError.Win32("Read share security descriptor", 13); }
    }

}
