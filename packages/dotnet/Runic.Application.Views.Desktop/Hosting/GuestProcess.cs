using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Runic.Application.Views.Desktop.Hosting;

// A guest application's process (host-protocol.md, Starting the guest): started directly, without a shell, with its
// standard input and output piped and its standard error read as diagnostic text, without RUNIC_APPLICATION_SERVE_ONLY
// and RUNIC_APPLICATION_CLOSE_AFTER_OPEN, and tied to the host. On Windows it runs in a job object that kills its
// processes when the host's handle closes, also when the host crashes; elsewhere it leads a process group of its own,
// which KillTree ends, and the host's exit ends the groups of the guests still running.
internal abstract class GuestProcess : IDisposable
{
    internal static readonly string[] RemovedVariables = ["RUNIC_APPLICATION_SERVE_ONLY", "RUNIC_APPLICATION_CLOSE_AFTER_OPEN"];

    internal static GuestProcess Start(RunicGuestStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var environment = Environment(startInfo.Environment);
        if (OperatingSystem.IsWindows()) return WindowsGuestProcess.Start(startInfo, environment);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return UnixGuestProcess.Start(startInfo, environment);
        throw new PlatformNotSupportedException("Hosted applications run on Windows, Linux and macOS.");
    }

    // The host's environment with the start information's changes, without the serve-only switches.
    internal static Dictionary<string, string> Environment(IReadOnlyDictionary<string, string?>? changes)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var environment = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry variable in System.Environment.GetEnvironmentVariables())
            if (variable.Key is string name && variable.Value is string value) environment[name] = value;
        foreach (var (name, value) in changes ?? new Dictionary<string, string?>())
        {
            if (value is null) environment.Remove(name);
            else environment[name] = value;
        }
        foreach (var name in RemovedVariables) environment.Remove(name);
        return environment;
    }

    internal abstract int Id { get; }

    internal abstract Stream StandardInput { get; }

    internal abstract Stream StandardOutput { get; }

    internal abstract Stream StandardError { get; }

    // Completes with the exit code once the process exited: 128 plus the signal for one a signal ended on Linux and macOS,
    // -1 when it cannot be known.
    internal abstract Task<int> Exited { get; }

    // Ends the guest and every process it started that is still in its job object or process group.
    internal abstract void KillTree();

    public abstract void Dispose();
}

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsGuestProcess : GuestProcess
{
    private readonly Process _process;
    private readonly WindowsJob? _job;
    private readonly Task<int> _exited;

    private WindowsGuestProcess(Process process, WindowsJob? job)
    {
        _process = process;
        _job = job;
        _exited = WaitAsync(process);
    }

    internal static GuestProcess Start(RunicGuestStartInfo startInfo, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(startInfo.FileName)
        {
            UseShellExecute = false,
            // A console guest started by a windowed host would otherwise get a console window of its own.
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (startInfo.WorkingDirectory is { } directory) start.WorkingDirectory = directory;
        foreach (var argument in startInfo.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var (name, value) in environment) start.Environment[name] = value;
        var job = WindowsJob.TryCreate();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException($"The guest '{startInfo.FileName}' did not start.");
        }
        catch
        {
            job?.Dispose();
            throw;
        }
        // The guest's own children inherit the job; one it started before this call escapes it.
        job?.TryAssign(process);
        return new WindowsGuestProcess(process, job);
    }

    private static async Task<int> WaitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return -1; }
    }

    internal override int Id => _process.Id;

    internal override Stream StandardInput => _process.StandardInput.BaseStream;

    internal override Stream StandardOutput => _process.StandardOutput.BaseStream;

    internal override Stream StandardError => _process.StandardError.BaseStream;

    internal override Task<int> Exited => _exited;

    internal override void KillTree()
    {
        if (_job is not null && _job.Terminate()) return;
        try { _process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or AggregateException or Win32Exception or NotSupportedException) { }
    }

    // Closing the job's handle kills what still runs in it.
    public override void Dispose()
    {
        _job?.Dispose();
        _process.Dispose();
    }

    // A job object whose processes end when its last handle closes (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).
    private sealed partial class WindowsJob : IDisposable
    {
        private nint _handle;

        private WindowsJob(nint handle) => _handle = handle;

        internal static WindowsJob? TryCreate()
        {
            var handle = CreateJobObjectW(0, null);
            if (handle == 0) return null;
            var limits = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = KillOnJobClose } };
            if (SetInformationJobObject(handle, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                return new WindowsJob(handle);
            CloseHandle(handle);
            return null;
        }

        internal void TryAssign(Process process)
        {
            try { _ = AssignProcessToJobObject(_handle, process.Handle); }
            catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
        }

        internal bool Terminate()
        {
            var handle = Volatile.Read(ref _handle);
            return handle != 0 && TerminateJobObject(handle, 1);
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, 0);
            if (handle != 0) CloseHandle(handle);
        }

        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformationClass = 9;

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
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }

        [LibraryImport("kernel32", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint CreateJobObjectW(nint attributes, string? name);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool AssignProcessToJobObject(nint job, nint process);

        [LibraryImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TerminateJobObject(nint job, uint exitCode);

        [LibraryImport("kernel32")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}

// posix_spawn with POSIX_SPAWN_SETPGROUP: the guest leads a new process group, so KillTree reaches the processes it
// started also after their parent exited. System.Diagnostics.Process cannot start a process in a group of its own on
// Linux and macOS (ProcessStartInfo.CreateNewProcessGroup is Windows-only), so the pipes and the wait are this class's.
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed partial class UnixGuestProcess : GuestProcess
{
    private static readonly object RunningGate = new();
    private static readonly HashSet<int> Running = [];
    private static int s_exitHooked;

    private readonly int _pid;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly FileStream _errors;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private UnixGuestProcess(int pid, SafeFileHandle input, SafeFileHandle output, SafeFileHandle errors)
    {
        _pid = pid;
        _input = new FileStream(input, FileAccess.Write, bufferSize: 0);
        _output = new FileStream(output, FileAccess.Read, bufferSize: 0);
        _errors = new FileStream(errors, FileAccess.Read, bufferSize: 0);
        lock (RunningGate) Running.Add(pid);
        new Thread(Wait) { IsBackground = true, Name = "Runic hosted application wait" }.Start();
    }

    internal static GuestProcess Start(RunicGuestStartInfo startInfo, Dictionary<string, string> environment)
    {
        HookProcessExit();
        var path = Resolve(startInfo.FileName, environment);
        int[] input = Pipe(), output = Pipe(), errors = Pipe();
        var parentEnds = new[] { input[1], output[0], errors[0] };
        var started = false;
        try
        {
            var pid = Spawn(path, startInfo, environment, input[0], output[1], errors[1]);
            started = true;
            return new UnixGuestProcess(pid, new SafeFileHandle(input[1], ownsHandle: true),
                new SafeFileHandle(output[0], ownsHandle: true), new SafeFileHandle(errors[0], ownsHandle: true));
        }
        finally
        {
            // The child's ends belong to the child now; the parent's are closed only when the start failed.
            _ = close(input[0]);
            _ = close(output[1]);
            _ = close(errors[1]);
            if (!started)
                foreach (var end in parentEnds) _ = close(end);
        }
    }

    // A file name without a slash is looked up in PATH, as a shell would; a relative path is relative to the host's
    // current directory.
    private static string Resolve(string fileName, Dictionary<string, string> environment)
    {
        if (fileName.Contains('/')) return Path.GetFullPath(fileName);
        var search = environment.GetValueOrDefault("PATH") ?? System.Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in search.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        throw new Win32Exception(2, $"The guest '{fileName}' was not found in PATH.");
    }

    private static int[] Pipe()
    {
        var ends = new int[2];
        if (OperatingSystem.IsLinux())
        {
            if (pipe2(ends, OCloseOnExecLinux) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "A pipe for the guest could not be created.");
            return ends;
        }
        if (pipe(ends) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "A pipe for the guest could not be created.");
        // macOS has no pipe2; posix_spawn's POSIX_SPAWN_CLOEXEC_DEFAULT keeps the ends from the guest itself.
        _ = fcntl(ends[0], FSetFd, FdCloseOnExec);
        _ = fcntl(ends[1], FSetFd, FdCloseOnExec);
        return ends;
    }

    private static unsafe int Spawn(string path, RunicGuestStartInfo startInfo, Dictionary<string, string> environment,
        int input, int output, int errors)
    {
        var strings = new List<nint>();
        nint Utf8(string text)
        {
            var pointer = Marshal.StringToCoTaskMemUTF8(text);
            strings.Add(pointer);
            return pointer;
        }
        // posix_spawn_file_actions_t and posix_spawnattr_t are structures on Linux and pointers on macOS; sigset_t is
        // 128 bytes on Linux and 4 on macOS. Each buffer has room for the larger.
        var fileActions = Marshal.AllocHGlobal(1024);
        var attributes = Marshal.AllocHGlobal(1024);
        var signals = Marshal.AllocHGlobal(256);
        var fileActionsReady = false;
        var attributesReady = false;
        try
        {
            Check(posix_spawn_file_actions_init(fileActions), "posix_spawn_file_actions_init");
            fileActionsReady = true;
            Check(posix_spawn_file_actions_adddup2(fileActions, input, 0), "posix_spawn_file_actions_adddup2");
            Check(posix_spawn_file_actions_adddup2(fileActions, output, 1), "posix_spawn_file_actions_adddup2");
            Check(posix_spawn_file_actions_adddup2(fileActions, errors, 2), "posix_spawn_file_actions_adddup2");
            if (startInfo.WorkingDirectory is { } directory)
                Check(posix_spawn_file_actions_addchdir_np(fileActions, Utf8(Path.GetFullPath(directory))), "posix_spawn_file_actions_addchdir_np");

            Check(posix_spawnattr_init(attributes), "posix_spawnattr_init");
            attributesReady = true;
            var flags = SpawnSetProcessGroup | SpawnSetSignalDefault | SpawnSetSignalMask;
            if (OperatingSystem.IsMacOS()) flags |= SpawnCloseOnExecDefault;
            Check(posix_spawnattr_setflags(attributes, (short)flags), "posix_spawnattr_setflags");
            Check(posix_spawnattr_setpgroup(attributes, 0), "posix_spawnattr_setpgroup");
            // .NET ignores SIGPIPE; the guest starts with its default action, and with no signal blocked.
            _ = sigemptyset(signals);
            _ = sigaddset(signals, SignalPipe);
            Check(posix_spawnattr_setsigdefault(attributes, signals), "posix_spawnattr_setsigdefault");
            _ = sigemptyset(signals);
            Check(posix_spawnattr_setsigmask(attributes, signals), "posix_spawnattr_setsigmask");

            var arguments = new nint[startInfo.Arguments.Count + 2];
            arguments[0] = Utf8(path);
            for (var index = 0; index < startInfo.Arguments.Count; index++) arguments[index + 1] = Utf8(startInfo.Arguments[index]);
            var variables = new nint[environment.Count + 1];
            var position = 0;
            foreach (var (name, value) in environment) variables[position++] = Utf8(name + "=" + value);

            int pid;
            int result;
            fixed (nint* argv = arguments)
            fixed (nint* envp = variables)
            {
                result = posix_spawn(out pid, Utf8(path), fileActions, attributes, argv, envp);
            }
            if (result != 0) throw new Win32Exception(result, $"The guest '{startInfo.FileName}' could not be started.");
            return pid;
        }
        finally
        {
            if (fileActionsReady) _ = posix_spawn_file_actions_destroy(fileActions);
            if (attributesReady) _ = posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(fileActions);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(signals);
            foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
        }

        static void Check(int result, string function)
        {
            if (result != 0) throw new Win32Exception(result, $"{function} failed while starting the guest.");
        }
    }

    private void Wait()
    {
        int code;
        while (true)
        {
            var waited = waitpid(_pid, out var status, 0);
            if (waited == _pid)
            {
                // WIFEXITED: the exit status; WIFSIGNALED: 128 plus the signal, as System.Diagnostics.Process reports it.
                code = (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
                break;
            }
            if (waited == -1 && Marshal.GetLastPInvokeError() == ErrorInterrupted) continue;
            // Another part of the process reaped it, such as a SIGCHLD disposition of SIG_IGN.
            code = -1;
            break;
        }
        lock (RunningGate) Running.Remove(_pid);
        _exited.TrySetResult(code);
    }

    internal override int Id => _pid;

    internal override Stream StandardInput => _input;

    internal override Stream StandardOutput => _output;

    internal override Stream StandardError => _errors;

    internal override Task<int> Exited => _exited.Task;

    // The group outlives its leader while a member runs, and its id is not reused until then, so the group can be
    // signalled after the guest itself exited.
    internal override void KillTree()
    {
        _ = kill(-_pid, SignalKill);
        if (!_exited.Task.IsCompleted) _ = kill(_pid, SignalKill);
    }

    public override void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
        _errors.Dispose();
    }

    // A host that exits without stopping its guests ends their groups; standard input's end would make them exit too,
    // within 10 seconds (host-protocol.md, Host gone).
    private static void HookProcessExit()
    {
        if (Interlocked.Exchange(ref s_exitHooked, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
        {
            int[] groups;
            lock (RunningGate) groups = [.. Running];
            foreach (var group in groups) _ = kill(-group, SignalKill);
        };
    }

    private const int OCloseOnExecLinux = 0x80000;
    private const int FSetFd = 2;
    private const int FdCloseOnExec = 1;
    private const int SpawnSetProcessGroup = 0x02;
    private const int SpawnSetSignalDefault = 0x04;
    private const int SpawnSetSignalMask = 0x08;
    private const int SpawnCloseOnExecDefault = 0x4000;
    private const int SignalPipe = 13;
    private const int SignalKill = 9;
    private const int ErrorInterrupted = 4;

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pipe2([Out] int[] fds, int flags);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int pipe([Out] int[] fds);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int command, int argument);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc")]
    private static unsafe partial int posix_spawn(out int pid, nint path, nint fileActions, nint attributes, nint* argv, nint* envp);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(nint fileActions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(nint fileActions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_adddup2(nint fileActions, int fd, int newFd);

    // glibc 2.29 and macOS 10.15.
    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_addchdir_np(nint fileActions, nint path);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_init(nint attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_destroy(nint attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setflags(nint attributes, short flags);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setpgroup(nint attributes, int processGroup);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigdefault(nint attributes, nint signals);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigmask(nint attributes, nint signals);

    [LibraryImport("libc")]
    private static partial int sigemptyset(nint signals);

    [LibraryImport("libc")]
    private static partial int sigaddset(nint signals, int signal);
}
