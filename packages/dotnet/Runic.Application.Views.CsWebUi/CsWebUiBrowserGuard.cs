using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Runic.Application.Views.CsWebUi;

// sdk#1263: the browser a CS-WebUI window started with the run's own profile (CsWebUiRemoteDebugging, DEV-SEC-85) ends with
// the application, also when the application is ended without running any code of its own: a debugger's Stop, Task
// Manager, kill -9. WebUI starts the browser detached (cmd /c START on Windows, "<browser> &" through sh elsewhere) and ends
// it only in its own close path (webui_close, webui_exit, webui_clean), so after such an end the browser kept running and
// kept the DevTools Protocol port, which Runic DevKit's port-owner check then refused (editors#60).
// - Windows: every browser process WebUI reports for an opener goes in a job object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
//   that only the application holds (as dotnet runic dev does for the frontend server, #1163). Windows closes the handle
//   when the process ends, however it ends, and the job ends the browser and whatever it started since. The process must
//   have started no earlier than the window's Show (as CsWebUiListenerOwner checks), so a reused process ID is not taken.
//   The profile stays behind; the next run deletes it (CsWebUiRemoteDebugging.Session.SweepStaleProfiles).
// - Linux and macOS: no process can ask the kernel to end a process it did not start itself (PR_SET_PDEATHSIG is set by the
//   child, and WebUI's shell starts the browser), so a watcher does it: a /bin/sh that leaves the application's process tree
//   (so a tree kill does not reach it), ignores the terminal's signals, and reads a pipe whose only writer is the
//   application. The pipe ends when the application does; unless the application released the watcher first, it then ends
//   every process whose command line names this profile (--user-data-dir=<profile>): only the browser of this run can, as
//   the profile is a new 0700 directory. Then it deletes the profile.
// A browser the user started, or WebUI's shared .WebUI profile, is never touched: only the per-run profile's browser is.
internal static partial class CsWebUiBrowserGuard
{
    // The watcher. $1 is the profile. Its standard input is the application's pipe; it keeps a copy as descriptor 3 for the
    // background part, whose standard input would otherwise be /dev/null.
    internal const string WatcherScript = """
        trap '' HUP INT QUIT TERM PIPE
        exec 3<&0 </dev/null >/dev/null 2>&1
        profile=$1
        (
          trap '' HUP INT QUIT TERM PIPE
          line=
          IFS= read -r line <&3
          exec 3<&-
          [ "$line" = release ] && exit 0
          browsers() {
            if command -v ps >/dev/null 2>&1; then
              ps -A -ww -o pid= -o args= 2>/dev/null | while read -r pid args; do
                case "$args " in *"--user-data-dir=$profile "*) echo "$pid" ;; esac
              done
            else
              for entry in /proc/[0-9]*; do
                args=$(tr '\000' ' ' < "$entry/cmdline" 2>/dev/null) || continue
                case "$args " in *"--user-data-dir=$profile "*) echo "${entry#/proc/}" ;; esac
              done
            fi
          }
          pids=$(browsers)
          if [ -n "$pids" ]; then
            kill -TERM $pids 2>/dev/null
            tries=0
            while [ $tries -lt 50 ] && [ -n "$(browsers)" ]; do sleep 0.2; tries=$((tries + 1)); done
            pids=$(browsers)
            [ -n "$pids" ] && kill -KILL $pids 2>/dev/null
          fi
          tries=0
          while [ $tries -lt 20 ]; do
            rm -rf -- "$profile" 2>/dev/null
            [ -e "$profile" ] || exit 0
            sleep 0.25
            tries=$((tries + 1))
          done
        ) &
        """;

    // Linux and macOS: the watcher of a per-run profile, or null where there is none (Windows) or it cannot start. Dispose
    // releases it: the profile's browser exited and the application deleted the profile.
    internal static Watcher? WatchProfile(string profile)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;
        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            ArgumentList = { "-c", WatcherScript, "runic-cswebui-browser-guard", profile },
        };
        try
        {
            using var shell = Process.Start(start);
            if (shell is null) return null;
            // The shell exits at once, its watcher running on in the background with a copy of the pipe.
            shell.WaitForExit();
            return new Watcher(shell.StandardInput);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    internal sealed class Watcher(StreamWriter pipe) : IDisposable
    {
        private StreamWriter? _pipe = pipe;

        public void Dispose() => Close(release: true);

        // Closes the pipe as the application's end does: the watcher ends the profile's browser and deletes the profile.
        internal void Abandon() => Close(release: false);

        private void Close(bool release)
        {
            if (Interlocked.Exchange(ref _pipe, null) is not { } pipe) return;
            try
            {
                if (release)
                {
                    pipe.WriteLine("release");
                    pipe.Flush();
                }
            }
            catch (IOException)
            {
                // The watcher is gone.
            }
            finally
            {
                try { pipe.Dispose(); }
                catch (IOException) { }
            }
        }
    }

    // Windows: the application's kill-on-close job, created for the first browser it holds.
    [SupportedOSPlatform("windows")]
    internal sealed partial class Job
    {
        private readonly Lock _gate = new();
        private nint _handle;

        // Puts the browser process in the job, if it started no earlier than shownAfter (less a second, as process start
        // times are rounded). False when the process is gone, is not confirmed, or Windows refuses.
        internal bool Hold(nuint processId, DateTime shownAfter)
        {
            if (processId == 0 || processId > int.MaxValue) return false;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                // The handle the start time is read through is the one the job takes, so the process cannot change between.
                var handle = process.SafeHandle;
                if (process.StartTime.ToUniversalTime() < shownAfter.ToUniversalTime() - TimeSpan.FromSeconds(1)) return false;
                lock (_gate)
                {
                    if (_handle == 0 && !Create()) return false;
                    return AssignProcessToJobObject(_handle, handle.DangerousGetHandle());
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                or System.ComponentModel.Win32Exception or NotSupportedException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        // Under _gate. The handle is not inheritable, so only this process holds the job; it is never closed, as closing it
        // ends the browsers in it, which Windows does when the process ends.
        private bool Create()
        {
            var job = CreateJobObjectW(0, 0);
            if (job == 0) return false;
            // BREAKAWAY_OK: a browser that starts a process outside its job, as Chromium may, can still do so.
            var limits = new ExtendedLimitInformation { BasicLimits = { LimitFlags = KillOnJobClose | BreakawayOk } };
            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
            {
                _ = CloseHandle(job);
                return false;
            }
            _handle = job;
            return true;
        }

        private const int ExtendedLimitInformationClass = 9;
        private const uint BreakawayOk = 0x800;
        private const uint KillOnJobClose = 0x2000;

        // JOBOBJECT_BASIC_LIMIT_INFORMATION.
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        // IO_COUNTERS.
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION.
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimits;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint CreateJobObjectW(nint attributes, nint name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(nint job, nint process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
