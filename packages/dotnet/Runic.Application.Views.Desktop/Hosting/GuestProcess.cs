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
// processes when the host's handle closes, also when the host crashes. Elsewhere it leads a process group of its own,
// which KillTree ends; a host that exits normally ends the groups of the guests still running. A host that crashes or is
// killed cannot: its guests see their standard input end and exit within 10 seconds (host-protocol.md, Host gone), and
// what a guest started itself may outlive it. PR_SET_PDEATHSIG is not used: Linux sends it when the thread that started
// the child ends, and a .NET thread-pool thread can end at any time.
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

// CreateProcessW: the guest starts suspended, joins a job object whose processes end when its last handle closes, also
// when the host crashes, and only then runs, so nothing it starts escapes the job. A job that cannot be created or joined
// fails the start. The guest inherits exactly its three pipe ends (PROC_THREAD_ATTRIBUTE_HANDLE_LIST), none of the host's
// other inheritable handles, such as the pipes of a process the host starts at the same time or what the host inherited
// itself. The ends are inheritable from their creation until they are closed after the start, under one lock, so no other
// guest's start sees them; a process that System.Diagnostics.Process starts meanwhile, which inherits every inheritable
// handle of the host, can (its lock is its own).
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsGuestProcess : GuestProcess
{
    private readonly SafeProcessHandle _process;
    private readonly SafeJobHandle _job;
    private readonly int _id;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly FileStream _errors;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly RegisteredWaitHandle _wait;
    private readonly ProcessWaitHandle _waitHandle;

    private WindowsGuestProcess(SafeProcessHandle process, int id, SafeJobHandle job, SafeFileHandle input, SafeFileHandle output, SafeFileHandle errors)
    {
        _process = process;
        _id = id;
        _job = job;
        _input = new FileStream(input, FileAccess.Write, bufferSize: 0);
        _output = new FileStream(output, FileAccess.Read, bufferSize: 0);
        _errors = new FileStream(errors, FileAccess.Read, bufferSize: 0);
        _waitHandle = new ProcessWaitHandle(process);
        _wait = ThreadPool.RegisterWaitForSingleObject(_waitHandle, static (state, _) => ((WindowsGuestProcess)state!).OnExited(), this,
            Timeout.Infinite, executeOnlyOnce: true);
    }

    internal static GuestProcess Start(RunicGuestStartInfo startInfo, Dictionary<string, string> environment)
    {
        var path = Resolve(startInfo.FileName, environment);
        // CreateProcess runs a batch file through cmd.exe, whose own parsing of the command line no quoting of the
        // arguments protects against (the BatBadBut class): such a guest is refused.
        var extension = Path.GetExtension(path.TrimEnd('.', ' '));
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"The guest '{startInfo.FileName}' is a batch file, which only cmd.exe runs; start an executable.", nameof(startInfo));
        var directory = startInfo.WorkingDirectory is { } workingDirectory ? Path.GetFullPath(workingDirectory) : null;
        var commandLine = CommandLine(path, startInfo.Arguments);

        using var job = CreateJob();
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, errorsRead = null, errorsWrite = null;
        ProcessInformation information;
        lock (StartGate)
        {
            try
            {
                CreatePipe(out inputWrite, out inputRead, childReads: true);
                CreatePipe(out outputRead, out outputWrite, childReads: false);
                CreatePipe(out errorsRead, out errorsWrite, childReads: false);
                information = Spawn(path, commandLine, directory, Block(environment), inputRead, outputWrite, errorsWrite, startInfo.FileName);
            }
            catch
            {
                inputWrite?.Dispose();
                outputRead?.Dispose();
                errorsRead?.Dispose();
                throw;
            }
            finally
            {
                // The child's ends are the child's now; closed under the lock, no later start inherits them.
                inputRead?.Dispose();
                outputWrite?.Dispose();
                errorsWrite?.Dispose();
            }
        }
        var process = new SafeProcessHandle(information.Process, ownsHandle: true);
        using var thread = new SafeProcessHandle(information.Thread, ownsHandle: true);
        try
        {
            if (!AssignProcessToJobObject(job, process))
            {
                var error = Marshal.GetLastPInvokeError();
                _ = TerminateProcess(process, 1);
                throw new Win32Exception(error, $"The guest '{startInfo.FileName}' could not join its job object.");
            }
            if (ResumeThread(thread) == uint.MaxValue)
            {
                var error = Marshal.GetLastPInvokeError();
                _ = TerminateJobObject(job, 1);
                throw new Win32Exception(error, $"The guest '{startInfo.FileName}' could not be resumed.");
            }
            return new WindowsGuestProcess(process, information.ProcessId, job.Transfer(), inputWrite, outputRead, errorsRead);
        }
        catch
        {
            process.Dispose();
            inputWrite.Dispose();
            outputRead.Dispose();
            errorsRead.Dispose();
            throw;
        }
    }

    private static readonly object StartGate = new();

    // As System.Diagnostics.Process resolves a file name: a path is relative to the current directory; a name without
    // a directory is looked up in the host's directory, the current directory, the system directories and PATH. A name
    // without an extension gets .exe.
    internal static string Resolve(string fileName, Dictionary<string, string> environment)
    {
        if (fileName.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            var full = Path.GetFullPath(fileName);
            if (File.Exists(full)) return full;
            if (!Path.HasExtension(full) && File.Exists(full + ".exe")) return full + ".exe";
            throw new Win32Exception(2, $"The guest '{fileName}' was not found.");
        }
        var name = Path.HasExtension(fileName) ? fileName : fileName + ".exe";
        var directories = new List<string>();
        if (System.Environment.ProcessPath is { } host && Path.GetDirectoryName(host) is { } hostDirectory) directories.Add(hostDirectory);
        directories.Add(System.Environment.CurrentDirectory);
        directories.Add(System.Environment.SystemDirectory);
        directories.Add(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows));
        var search = environment.GetValueOrDefault("PATH") ?? System.Environment.GetEnvironmentVariable("PATH") ?? "";
        directories.AddRange(search.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static d => d.Trim('"')));
        foreach (var directory in directories)
        {
            if (directory.Length == 0) continue;
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(directory, name)); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (File.Exists(candidate)) return candidate;
        }
        throw new Win32Exception(2, $"The guest '{fileName}' was not found in PATH.");
    }

    // The command line that the C runtime's and CommandLineToArgvW's parsing turn back into the program and the arguments.
    internal static string CommandLine(string path, IReadOnlyList<string> arguments)
    {
        var line = new System.Text.StringBuilder();
        line.Append('"').Append(path).Append('"');
        foreach (var argument in arguments)
        {
            line.Append(' ');
            if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            {
                line.Append(argument);
                continue;
            }
            line.Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    line.Append('\\', backslashes * 2 + 1);
                }
                else
                {
                    line.Append('\\', backslashes);
                }
                backslashes = 0;
                line.Append(character);
            }
            line.Append('\\', backslashes * 2).Append('"');
        }
        return line.ToString();
    }

    // CREATE_UNICODE_ENVIRONMENT's block: name=value strings, sorted without regard to case, each ending in a null
    // character, and a null character at the end.
    private static string Block(Dictionary<string, string> environment)
    {
        var block = new System.Text.StringBuilder();
        foreach (var (name, value) in environment.OrderBy(static variable => variable.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(name).Append('=').Append(value).Append('\0');
        if (block.Length == 0) block.Append('\0');
        return block.Append('\0').ToString();
    }

    private static SafeJobHandle CreateJob()
    {
        var job = CreateJobObjectW(0, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "The guest's job object could not be created.");
        }
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new() { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error, "The guest's job object could not be limited.");
        }
        return job;
    }

    // A pipe whose child end is inheritable; the parent's end is not.
    private static unsafe void CreatePipe(out SafeFileHandle parent, out SafeFileHandle child, bool childReads)
    {
        var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "A pipe for the guest could not be created.");
        parent = childReads ? write : read;
        child = childReads ? read : write;
        if (!SetHandleInformation(parent, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error, "A pipe for the guest could not be prepared.");
        }
    }

    // The handle list names the three ends: CreateProcessW's bInheritHandles then passes only those, and they must be
    // inheritable. UpdateProcThreadAttribute keeps a pointer to the list, not a copy, so it lives on this method's stack
    // until CreateProcessW returned; the attribute list is deleted and freed after it.
    private static unsafe ProcessInformation Spawn(string path, string commandLine, string? directory, string environment,
        SafeFileHandle input, SafeFileHandle output, SafeFileHandle errors, string fileName)
    {
        bool addedInput = false, addedOutput = false, addedErrors = false;
        nint attributeList = 0;
        var attributeListReady = false;
        try
        {
            input.DangerousAddRef(ref addedInput);
            output.DangerousAddRef(ref addedOutput);
            errors.DangerousAddRef(ref addedErrors);
            var inherited = stackalloc nint[3];
            inherited[0] = input.DangerousGetHandle();
            inherited[1] = output.DangerousGetHandle();
            inherited[2] = errors.DangerousGetHandle();

            // The first call reports the size the list needs and fails with ERROR_INSUFFICIENT_BUFFER.
            nint size = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "The guest's inherited handles could not be listed.");
            attributeList = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The guest's inherited handles could not be listed.");
            attributeListReady = true;
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, inherited, 3 * sizeof(nint), 0, 0))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The guest's inherited handles could not be listed.");

            var startup = new StartupInformationEx
            {
                StartupInfo = new StartupInformation
                {
                    Size = (uint)sizeof(StartupInformationEx),
                    Flags = StartfUseStdHandles,
                    StandardInput = inherited[0],
                    StandardOutput = inherited[1],
                    StandardError = inherited[2],
                },
                AttributeList = attributeList,
            };
            // CreateProcessW may write to the command line.
            var line = (commandLine + "\0").ToCharArray();
            fixed (char* applicationName = path)
            fixed (char* commandLineBuffer = line)
            fixed (char* environmentBlock = environment)
            fixed (char* currentDirectory = directory)
            {
                if (!CreateProcessW(applicationName, commandLineBuffer, 0, 0, true,
                        CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                        environmentBlock, currentDirectory, &startup, out var information))
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"The guest '{fileName}' could not be started.");
                return information;
            }
        }
        finally
        {
            if (attributeListReady) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != 0) Marshal.FreeHGlobal(attributeList);
            if (addedInput) input.DangerousRelease();
            if (addedOutput) output.DangerousRelease();
            if (addedErrors) errors.DangerousRelease();
        }
    }

    private void OnExited()
    {
        int code;
        try { code = GetExitCodeProcess(_process, out var exitCode) ? (int)exitCode : -1; }
        catch (ObjectDisposedException) { code = -1; }
        _exited.TrySetResult(code);
    }

    internal override int Id => _id;

    internal override Stream StandardInput => _input;

    internal override Stream StandardOutput => _output;

    internal override Stream StandardError => _errors;

    internal override Task<int> Exited => _exited.Task;

    // The job holds the guest and what it started; SafeHandle keeps the handle valid while TerminateJobObject runs, also
    // when Dispose closes it meanwhile.
    internal override void KillTree()
    {
        try
        {
            if (TerminateJobObject(_job, 1)) return;
        }
        catch (ObjectDisposedException) { return; }
        try { _ = TerminateProcess(_process, 1); }
        catch (ObjectDisposedException) { }
    }

    // Closing the job's handle kills what still runs in it.
    public override void Dispose()
    {
        _wait.Unregister(null);
        _input.Dispose();
        _output.Dispose();
        _errors.Dispose();
        _job.Dispose();
        _waitHandle.Dispose();
        _process.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(SafeProcessHandle process)
        {
            bool added = false;
            process.DangerousAddRef(ref added);
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
            _process = process;
            _added = added;
        }

        private readonly SafeProcessHandle _process;
        private readonly bool _added;

        protected override void Dispose(bool explicitDisposing)
        {
            base.Dispose(explicitDisposing);
            if (_added) _process.DangerousRelease();
        }
    }

    // A job object handle (JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE).
    internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }

        // The handle moves to a new owner; this one no longer closes it.
        internal SafeJobHandle Transfer()
        {
            var job = new SafeJobHandle();
            job.SetHandle(handle);
            SetHandleAsInvalid();
            return job;
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformationClass = 9;
    private const uint HandleFlagInherit = 0x1;
    private const uint StartfUseStdHandles = 0x100;
    private const uint CreateSuspended = 0x4;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint ExtendedStartupInfoPresent = 0x80000;
    private const nint ProcThreadAttributeHandleList = 0x20002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformation
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    // STARTUPINFOEXW: 112 bytes on 64-bit Windows, 72 on 32-bit.
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInformationEx
    {
        public StartupInformation StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

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
    private static partial SafeJobHandle CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeJobHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32", SetLastError = true)]
    private static partial uint ResumeThread(SafeProcessHandle thread);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes attributes, uint size);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(char* applicationName, char* commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment, char* currentDirectory,
        StartupInformationEx* startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nint size);

    [LibraryImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nint attribute, void* value, nint size,
        nint previousValue, nint returnSize);

    [LibraryImport("kernel32")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
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
    private bool _reaped;

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
        int[]? input = null, output = null, errors = null;
        var started = false;
        try
        {
            input = Pipe();
            output = Pipe();
            errors = Pipe();
            var pid = Spawn(path, startInfo, environment, input[0], output[1], errors[1]);
            started = true;
            return new UnixGuestProcess(pid, new SafeFileHandle(input[1], ownsHandle: true),
                new SafeFileHandle(output[0], ownsHandle: true), new SafeFileHandle(errors[0], ownsHandle: true));
        }
        finally
        {
            // The child's ends belong to the child now; the parent's are closed only when the start failed.
            if (input is not null) _ = close(input[0]);
            if (output is not null) _ = close(output[1]);
            if (errors is not null) _ = close(errors[1]);
            if (!started)
            {
                if (input is not null) _ = close(input[1]);
                if (output is not null) _ = close(output[0]);
                if (errors is not null) _ = close(errors[0]);
            }
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

    // A pipe whose ends are close-on-exec. Linux creates them so (pipe2). macOS has no pipe2: pipe, then fcntl, so a
    // process the host forks between the two, such as one System.Diagnostics.Process starts on another thread, can
    // inherit the host's ends. The guest itself cannot: POSIX_SPAWN_CLOEXEC_DEFAULT passes it only the descriptors the
    // file actions name, its standard input, output and error.
    private static int[] Pipe()
    {
        var ends = new int[2];
        if (OperatingSystem.IsLinux())
        {
            if (pipe2(ends, OCloseOnExecLinux) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "A pipe for the guest could not be created.");
            return ends;
        }
        if (pipe(ends) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "A pipe for the guest could not be created.");
        if (fcntl(ends[0], FSetFd, FdCloseOnExec) == 0 && fcntl(ends[1], FSetFd, FdCloseOnExec) == 0) return ends;
        var error = Marshal.GetLastPInvokeError();
        _ = close(ends[0]);
        _ = close(ends[1]);
        throw new Win32Exception(error, "A pipe for the guest could not be made close-on-exec.");
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
            // Linux: every descriptor above standard error closes before the guest runs, also one a native library of
            // the host opened without O_CLOEXEC (glibc 2.34). macOS has POSIX_SPAWN_CLOEXEC_DEFAULT; an older glibc or
            // musl relies on the host's descriptors being close-on-exec, as .NET's are.
            if (OperatingSystem.IsLinux() && CloseFrom.Value is { } closeFrom)
                Check(((delegate* unmanaged<nint, int, int>)closeFrom)(fileActions, 3), "posix_spawn_file_actions_addclosefrom_np");

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

    // The guest exited: it stays a zombie (waitid with WNOWAIT) while what it started in its group is killed, so the
    // group's id cannot belong to another process group yet; then it is reaped. A group id that the system reused would
    // otherwise receive the SIGKILL of KillTree.
    private void Wait()
    {
        var info = new byte[128]; // siginfo_t
        while (true)
        {
            if (waitid(PIdentifierProcess, _pid, info, WaitExited | WaitNoWait) == 0) break;
            if (Marshal.GetLastPInvokeError() == ErrorInterrupted) continue;
            // Another part of the process reaped it, such as a SIGCHLD disposition of SIG_IGN; its group may be gone.
            lock (RunningGate)
            {
                Running.Remove(_pid);
                _reaped = true;
            }
            _exited.TrySetResult(-1);
            return;
        }
        int code;
        lock (RunningGate)
        {
            // The guest's own processes go with it (host-protocol.md, Guest gone): none outlives the session.
            _ = kill(-_pid, SignalKill);
            Running.Remove(_pid);
            code = Reap();
            _reaped = true;
        }
        _exited.TrySetResult(code);
    }

    private int Reap()
    {
        while (true)
        {
            var waited = waitpid(_pid, out var status, 0);
            // WIFEXITED: the exit status; WIFSIGNALED: 128 plus the signal, as System.Diagnostics.Process reports it.
            if (waited == _pid) return (status & 0x7f) == 0 ? (status >> 8) & 0xff : 128 + (status & 0x7f);
            if (waited == -1 && Marshal.GetLastPInvokeError() == ErrorInterrupted) continue;
            return -1;
        }
    }

    internal override int Id => _pid;

    internal override Stream StandardInput => _input;

    internal override Stream StandardOutput => _output;

    internal override Stream StandardError => _errors;

    internal override Task<int> Exited => _exited.Task;

    // Only while the guest is not reaped: its id, and its group's, are its own until then. Its group is killed when it
    // exits (Wait), so nothing is left to signal afterwards.
    internal override void KillTree()
    {
        lock (RunningGate)
        {
            if (_reaped) return;
            _ = kill(-_pid, SignalKill);
            _ = kill(_pid, SignalKill);
        }
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
            // Running holds only guests that are not reaped, under the same lock as the reaping.
            lock (RunningGate)
            {
                groups = [.. Running];
                foreach (var group in groups) _ = kill(-group, SignalKill);
            }
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
    private const int PIdentifierProcess = 1; // P_PID
    private const int WaitExited = 0x04; // WEXITED, on Linux and macOS
    private static int WaitNoWait => OperatingSystem.IsMacOS() ? 0x20 : 0x01000000; // WNOWAIT

    // posix_spawn_file_actions_addclosefrom_np, from glibc 2.34; null where libc does not export it.
    private static readonly Lazy<nint?> CloseFrom = new(static () =>
        NativeLibrary.TryLoad("libc.so.6", out var libc) && NativeLibrary.TryGetExport(libc, "posix_spawn_file_actions_addclosefrom_np", out var export)
            ? export
            : null);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitid(int idType, int id, [Out] byte[] info, int options);

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
