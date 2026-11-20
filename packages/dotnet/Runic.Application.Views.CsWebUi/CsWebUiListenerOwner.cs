using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Runic.Application.Views.CsWebUi;

// DEV-SEC-84: whether the browser process a CS-WebUI window started holds the listening socket on 127.0.0.1:<port>, so the
// host reports a fixed DevTools Protocol port only when a debugger that connects there reaches that browser. Only one
// socket can be bound to 127.0.0.1:<port> (Chromium binds its server socket exclusively on Windows and sets no
// SO_REUSEPORT elsewhere), and a connection reaches it before any wildcard listener, so its process is the one a debugger
// talks to. Each check reads the socket from the process's side:
// - Linux: the LISTEN row of /proc/net/tcp gives the socket's inode, which must be among /proc/<pid>/fd's links. Those are
//   readable only for the user's own processes.
// - Windows: GetExtendedTcpTable lists each IPv4 listener with its owning process.
// - macOS: lsof -a -p <pid> lists the process's listener. Without root, lsof shows only the user's own processes.
// The process must also have started no earlier than the window's Show, so a reused process ID or a process that only
// imitates the browser's command line does not pass. On Windows, reading another user's process start time is denied.
// Unknown counts as not held: the host then reports no endpoint.
internal static unsafe partial class CsWebUiListenerOwner
{
    // Why the listener on 127.0.0.1:<port> is not the browser's, or null when the browser process holds it.
    internal static string? ForeignListener(nuint processId, DateTime shownAfter, int port)
    {
        if (processId == 0 || processId > int.MaxValue) return "WebUI found no browser process for the window";
        var pid = (int)processId;
        if (!StartedSince(pid, shownAfter)) return $"the browser process {pid.ToString(CultureInfo.InvariantCulture)} could not be confirmed as the one the window started";
        var holds = OperatingSystem.IsLinux() ? HoldsOnLinux(pid, port)
            : OperatingSystem.IsWindows() ? HoldsOnWindows(pid, port)
            : OperatingSystem.IsMacOS() && HoldsOnMacOS(pid, port);
        return holds ? null : $"the listener there is not the browser process {pid.ToString(CultureInfo.InvariantCulture)}";
    }

    // Process start times are rounded (clock ticks on Linux), so allow a second.
    private static bool StartedSince(int processId, DateTime shownAfter)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() >= shownAfter.ToUniversalTime() - TimeSpan.FromSeconds(1);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HoldsOnLinux(int processId, int port)
    {
        List<string> sockets;
        try
        {
            using var table = new StreamReader("/proc/net/tcp");
            sockets = [.. LinuxListenerInodes(table, port).Select(static inode => $"socket:[{inode}]")];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        if (sockets.Count == 0) return false;
        try
        {
            foreach (var descriptor in Directory.EnumerateFileSystemEntries($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/fd"))
            {
                try
                {
                    if (new FileInfo(descriptor).LinkTarget is { } target && sockets.Contains(target)) return true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A descriptor closed while the loop ran.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return false;
    }

    // The inodes of the LISTEN rows bound to 127.0.0.1:<port>. The kernel prints the address as one host-order word and
    // the port in hexadecimal: "0100007F:2401" on a little-endian machine.
    internal static IEnumerable<string> LinuxListenerInodes(TextReader table, int port)
    {
        var loopback = BitConverter.ToUInt32([127, 0, 0, 1]);
        _ = table.ReadLine(); // The header.
        while (table.ReadLine() is { } line)
        {
            // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid timeout inode ...
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10 || fields[3] != "0A" || fields[1].Length != 13 || fields[1][8] != ':' ||
                !uint.TryParse(fields[1].AsSpan(0, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address) ||
                !int.TryParse(fields[1].AsSpan(9), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var localPort))
            {
                continue;
            }
            if (address == loopback && localPort == port && fields[9] != "0") yield return fields[9];
        }
    }

    private static bool HoldsOnWindows(int processId, int port)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var table = new byte[Math.Max(size, 4)];
            uint result;
            fixed (byte* pointer = table)
            {
                result = GetExtendedTcpTable(pointer, &size, order: 0, AddressFamilyInet, TcpTableOwnerPidListener, 0);
            }
            if (result == 0) return WindowsTableHolds(table, processId, port);
            if (result != ErrorInsufficientBuffer) return false;
        }
        return false;
    }

    // MIB_TCPTABLE_OWNER_PID: the row count, then MIB_TCPROW_OWNER_PID rows of six 32-bit fields (state, local address,
    // local port, remote address, remote port, owning process). Addresses and ports are in network byte order.
    internal static bool WindowsTableHolds(ReadOnlySpan<byte> table, int processId, int port)
    {
        const int RowSize = 24;
        if (table.Length < 4) return false;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        for (var row = 0; row < count && 4 + (row + 1) * RowSize <= table.Length; row++)
        {
            var fields = table.Slice(4 + row * RowSize, RowSize);
            if (BinaryPrimitives.ReadUInt32LittleEndian(fields) == TcpStateListen
                && fields.Slice(4, 4).SequenceEqual((ReadOnlySpan<byte>)[127, 0, 0, 1])
                && BinaryPrimitives.ReadUInt16BigEndian(fields.Slice(8, 2)) == port
                && BinaryPrimitives.ReadUInt32LittleEndian(fields.Slice(20, 4)) == (uint)processId)
            {
                return true;
            }
        }
        return false;
    }

    private static bool HoldsOnMacOS(int processId, int port)
    {
        var start = new ProcessStartInfo("/usr/sbin/lsof")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (ReadOnlySpan<string>)["-nP", "-a", "-p", processId.ToString(CultureInfo.InvariantCulture),
                     $"-iTCP@127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}", "-sTCP:LISTEN", "-t"])
        {
            start.ArgumentList.Add(argument);
        }
        try
        {
            using var lsof = Process.Start(start);
            if (lsof is null) return false;
            var output = lsof.StandardOutput.ReadToEndAsync();
            _ = lsof.StandardError.ReadToEndAsync();
            if (!lsof.WaitForExit(TimeSpan.FromSeconds(10)))
            {
                lsof.Kill();
                return false;
            }
            return LsofListsProcess(output.GetAwaiter().GetResult(), processId);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    // lsof -t prints one process ID per line.
    internal static bool LsofListsProcess(string output, int processId) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(processId.ToString(CultureInfo.InvariantCulture));

    private const uint AddressFamilyInet = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const uint TcpStateListen = 2;
    private const uint ErrorInsufficientBuffer = 122;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(void* table, uint* size, int order, uint addressFamily, int tableClass, uint reserved);
}
