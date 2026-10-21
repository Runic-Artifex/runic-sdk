using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Runic.Application.Testing;

/// <summary>
/// A descendant process of a <see cref="RunicServeOnlyApplication"/>, as
/// <see cref="RunicServeOnlyApplication.GetDescendantProcesses"/> listed it. It holds no operating-system handle, so
/// there is nothing to dispose.
/// </summary>
public sealed class RunicDescendantProcess
{
    // Linux and macOS compute a start time from the boot time on each read, which can move slightly between reads.
    private static readonly TimeSpan s_startTimeTolerance = TimeSpan.FromSeconds(1);

    internal RunicDescendantProcess(int id, DateTime? startTime)
    {
        Id = id;
        StartTime = startTime;
    }

    /// <summary>The process id.</summary>
    public int Id { get; }

    /// <summary>When the process started, in local time; <see langword="null"/> when it could not be read.</summary>
    public DateTime? StartTime { get; }

    /// <summary>
    /// Looks the process up again and tells whether it has exited: no process has its <see cref="Id"/> any more, or
    /// the process with that id started at another time, so the id was reused.
    /// </summary>
    public bool HasExited()
    {
        Process process;
        try { process = Process.GetProcessById(Id); }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        using (process)
        {
            try
            {
                if (process.HasExited) return true;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Not accessible; fall back to the start time.
            }
            if (StartTime is not { } started) return false;
            try { return (process.StartTime - started).Duration() > s_startTimeTolerance; }
            catch (InvalidOperationException) { return true; } // It has exited meanwhile.
            catch (Exception error) when (error is Win32Exception or NotSupportedException)
            {
                return false; // Not accessible, so the id cannot be checked for reuse.
            }
        }
    }

    /// <summary>The process id.</summary>
    public override string ToString() => Id.ToString(CultureInfo.InvariantCulture);
}
