using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Runic.Platform.Administration.Windows.Internal;

namespace Runic.Platform.Administration.Windows.Processes;

/// <summary>How <see cref="WindowsProcessClient.CloseOrKillAsync"/> ended a process.</summary>
public enum ProcessCloseOutcome
{
    /// <summary>The process was not running when the call started.</summary>
    NotRunning = 1,
    /// <summary>The process exited after its windows were asked to close.</summary>
    Closed = 2,
    /// <summary>The process was terminated because it did not exit in time or has no windows to close.</summary>
    Killed = 3
}

/// <summary>A program to start and wait for.</summary>
/// <param name="FileName">The program, a full path or a name found on <c>PATH</c>.</param>
public sealed record ProcessStartSpecification(string FileName)
{
    /// <summary>The command-line arguments as one string, quoted by the caller as the program expects (for example
    /// <c>/i "C:\\Setup Files\\app.msi" /qn</c> for msiexec).</summary>
    public string Arguments { get; init; } = "";
    /// <summary>The working directory; null uses the current directory.</summary>
    public string? WorkingDirectory { get; init; }
    /// <summary>Starts the program elevated through the shell (the <c>runas</c> verb), which shows the UAC prompt
    /// when the caller is not elevated. A declined prompt fails with native error 1223.</summary>
    public bool Elevated { get; init; }
}

public sealed partial class WindowsProcessClient
{
    private const uint StillRunning = 258; // WAIT_TIMEOUT
    private static readonly TimeSpan TerminationWait = TimeSpan.FromSeconds(10);

    /// <summary>Asks a process to close by posting <c>WM_CLOSE</c> to its top-level windows, as <c>taskkill</c> without
    /// <c>/F</c> does, waits up to <paramref name="timeout"/> for it to exit, and terminates it otherwise.</summary>
    /// <remarks>A process without top-level windows, such as a console program or a service, cannot be asked and is
    /// terminated at once. Termination uses exit code 1. A process that does not exit within 10 seconds of termination
    /// throws <see cref="TimeoutException"/>. Cancellation stops waiting; it never kills the process.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="processId"/> is negative, or
    /// <paramref name="timeout"/> is negative or longer than a day.</exception>
    public Task<ProcessCloseOutcome> CloseOrKillAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        return NativeWork.RunAsync(() => CloseOrKill(processId, timeout, cancellationToken), cancellationToken);
    }

    /// <summary>Terminates a process (<c>TerminateProcess</c>, as <c>taskkill /F /PID</c>) and waits for it to exit.
    /// Returns false when it was not running.</summary>
    /// <remarks>A process that does not exit within 10 seconds throws <see cref="TimeoutException"/>.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="processId"/> is negative.</exception>
    public Task<bool> TerminateAsync(int processId, int exitCode = 1, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(processId);
        return NativeWork.RunAsync(() =>
        {
            using var process = OpenForControl(processId);
            if (process is null || Exited(process, TimeSpan.Zero, cancellationToken)) return false;
            Terminate(process, unchecked((uint)exitCode), cancellationToken);
            return true;
        }, cancellationToken);
    }

    /// <summary>Starts a program, waits for it to exit and returns its exit code.</summary>
    /// <remarks>A program that cannot be started fails with its native error, for example 2
    /// (<see cref="AdministrationErrorCategory.NotFound"/>) or 1223 when the user declines elevation. Cancellation stops
    /// waiting; the program keeps running.</remarks>
    public async Task<int> StartAndWaitAsync(ProcessStartSpecification specification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(specification);
        NativeError.Text(specification.FileName, nameof(specification));
        NativeError.Text(specification.Arguments, nameof(specification), allowEmpty: true);
        if (specification.WorkingDirectory is not null) NativeError.Text(specification.WorkingDirectory, nameof(specification));
        NativeError.Windows();
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(specification.FileName, specification.Arguments)
        {
            UseShellExecute = specification.Elevated,
            WorkingDirectory = specification.WorkingDirectory ?? "",
            Verb = specification.Elevated ? "runas" : ""
        };
        Process? process;
        try { process = Process.Start(start); }
        catch (Win32Exception error) { throw new WindowsAdministrationException("Start process", NativeError.Categorize(error.NativeErrorCode), NativeErrorDomain.Win32, error.NativeErrorCode, $"Start process failed (Win32: 0x{error.NativeErrorCode:X8}).", error); }
        using (process)
        {
            if (process is null) throw NativeError.Win32("Start process", 1155); // ERROR_NO_ASSOCIATION: the shell started no process.
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
    }

    private static unsafe string? ReadImagePath(uint processId)
    {
        if (processId == 0 || !OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return null;
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsInvalid) return null;
        Span<char> buffer = stackalloc char[1024];
        var length = (uint)buffer.Length;
        if (PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)) return new string(buffer[..(int)length]);
        if (Marshal.GetLastPInvokeError() != 122) return null;
        var large = new char[32768];
        length = (uint)large.Length;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, large, ref length) ? new string(large, 0, (int)length) : null;
    }

    // Null when no process has this ID (ERROR_INVALID_PARAMETER).
    private static SafeFileHandle? OpenForControl(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_TERMINATE | PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE
            | PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, unchecked((uint)processId));
        if (!process.IsInvalid) return process;
        var error = Marshal.GetLastPInvokeError();
        process.Dispose();
        if (error == 87) return null;
        throw NativeError.Win32("Open process", error);
    }

    // Waits in short steps so cancellation is observed; true once the process has exited.
    private static bool Exited(SafeFileHandle process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var clock = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeout - clock.Elapsed;
            var step = remaining <= TimeSpan.Zero ? 0u : (uint)Math.Min(100, Math.Ceiling(remaining.TotalMilliseconds));
            var result = (uint)PInvoke.WaitForSingleObject(process, step);
            if (result == 0) return true;
            if (result != StillRunning) throw NativeError.Win32("Wait for process", Marshal.GetLastPInvokeError());
            if (remaining <= TimeSpan.Zero) return false;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void Terminate(SafeFileHandle process, uint exitCode, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        if (!PInvoke.TerminateProcess(process, exitCode))
        {
            var error = Marshal.GetLastPInvokeError();
            // A process that is already exiting refuses termination with access denied.
            if (!Exited(process, TimeSpan.Zero, cancellationToken)) throw NativeError.Win32("Terminate process", error);
            return;
        }
        if (!Exited(process, TerminationWait, cancellationToken)) throw new TimeoutException("The process did not exit after it was terminated.");
    }

    private static ProcessCloseOutcome CloseOrKill(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = OpenForControl(processId);
        if (process is null || Exited(process, TimeSpan.Zero, cancellationToken)) return ProcessCloseOutcome.NotRunning;
        if (PostClose(processId) > 0 && Exited(process, timeout, cancellationToken)) return ProcessCloseOutcome.Closed;
        cancellationToken.ThrowIfCancellationRequested();
        Terminate(process, 1, cancellationToken);
        return ProcessCloseOutcome.Killed;
    }

    private struct CloseRequest
    {
        internal uint ProcessId;
        internal int Posted;
    }

    // Posts WM_CLOSE to every top-level window of the process; returns how many were asked.
    private static unsafe int PostClose(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) throw new PlatformNotSupportedException("Windows 7 or later is required.");
        var request = new CloseRequest { ProcessId = unchecked((uint)processId) };
        // EnumWindows fails only when the callback stops it or the desktop cannot be enumerated (as in a service
        // without one); either way the process has no window to ask.
        _ = PInvoke.EnumWindows(&PostCloseToWindow, new LPARAM((nint)(&request)));
        return request.Posted;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe BOOL PostCloseToWindow(HWND window, LPARAM context)
    {
        var request = (CloseRequest*)context.Value;
        uint owner;
        if (OperatingSystem.IsWindowsVersionAtLeast(5) && PInvoke.GetWindowThreadProcessId(window, &owner) != 0 && owner == request->ProcessId
            && PInvoke.PostMessage(window, 0x0010, default, default))
            request->Posted++;
        return true;
    }
}
