using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Runic.Desktop.Internal;

// Cryptographically strong random bytes for the window tokens, session credentials, client cookies and browser launch
// codes of a surface. On Linux, RandomNumberGenerator loads OpenSSL (libssl and libcrypto, ≈1.7 ms and ≈4.7 MiB in a
// NativeAOT process) just to read the kernel's random source, before the first window opens (S1, D053.25). getrandom(2)
// reads that source directly: with no flags it blocks only until the kernel's pool is initialized at boot and then
// returns the requested bytes (at most 256 per call here) unless a signal interrupts it. A kernel or C library without
// getrandom (Linux before 3.17, a seccomp filter) uses RandomNumberGenerator. Windows (BCryptGenRandom) and macOS
// (CCRandomGenerateBytes) load no extra library for RandomNumberGenerator, so it is used there unchanged.
internal static class OsRandom
{
    private const int MaxRequest = 256;
    private const int Interrupted = 4; // EINTR
    private static bool s_getrandomUnavailable;

    internal static void Fill(Span<byte> data)
    {
        if (OperatingSystem.IsLinux() && !Volatile.Read(ref s_getrandomUnavailable) && TryGetRandom(data)) return;
        RandomNumberGenerator.Fill(data);
    }

    internal static byte[] GetBytes(int count)
    {
        var data = new byte[count];
        Fill(data);
        return data;
    }

    private static unsafe bool TryGetRandom(Span<byte> data)
    {
        var getRandom = GetRandom;
        if (getRandom == null) return false;
        while (!data.IsEmpty)
        {
            var request = Math.Min(data.Length, MaxRequest);
            nint written;
            fixed (byte* buffer = data)
            {
                written = getRandom(buffer, (nuint)request, 0);
            }
            if (written < 0)
            {
                // errno; a value other than EINTR, or one the runtime replaced on the way back, falls back.
                if (Marshal.GetLastSystemError() == Interrupted) continue;
                Volatile.Write(ref s_getrandomUnavailable, true);
                return false;
            }
            data = data[(int)written..];
        }
        return true;
    }

    // getrandom from the C library the process already has, looked up in its global scope. A [LibraryImport("libc")]
    // would be resolved through NativeLibrary's name probing, whose case-insensitive comparisons load ICU in a
    // NativeAOT process, the library S1 keeps off the startup path too.
    private static unsafe delegate* unmanaged<byte*, nuint, uint, nint> GetRandom
    {
        get
        {
            var getRandom = s_getRandom;
            if (getRandom == null) s_getRandom = getRandom = LookUpGetRandom();
            return getRandom;
        }
    }

    private static unsafe delegate* unmanaged<byte*, nuint, uint, nint> s_getRandom;

    private static unsafe delegate* unmanaged<byte*, nuint, uint, nint> LookUpGetRandom()
    {
        if (NativeLibrary.TryGetExport(NativeLibrary.GetMainProgramHandle(), "getrandom", out var address))
            return (delegate* unmanaged<byte*, nuint, uint, nint>)address;
        Volatile.Write(ref s_getrandomUnavailable, true);
        return null;
    }
}
