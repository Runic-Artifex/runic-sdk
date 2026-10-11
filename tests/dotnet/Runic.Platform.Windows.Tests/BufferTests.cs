using Runic.Platform.Runtime;
using TUnit.Core;

namespace Runic.Platform.Windows.Tests;

// A portable check: DecodeText only reads the given buffer.
internal sealed class BufferTests
{
    [Test]
    public unsafe void DecodeTextBufferBounds()
    {
        char* empty = stackalloc char[] { '\0' };
        char* valid = stackalloc char[] { 'a', 'b', '\0' };
        char* malformed = stackalloc char[] { 'a', 'b' };
        Assert(Win32Clipboard.DecodeText(empty, 1, 0) is PlatformResult<string?>.Success { Value: "" });
        Assert(Win32Clipboard.DecodeText(valid, 3, 2) is PlatformResult<string?>.Success { Value: "ab" });
        Assert(Win32Clipboard.DecodeText(valid, 3, 1) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.TooLarge });
        Assert(Win32Clipboard.DecodeText(malformed, 2, 2) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData });
        Assert(Win32Clipboard.DecodeText(malformed, 2, int.MaxValue) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData });
        Assert(Win32Clipboard.DecodeText(null, 0, 0) is PlatformResult<string?>.Failed { Code: PlatformFailureCode.InvalidData });
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Native text buffer bound assertion failed"); }
}
