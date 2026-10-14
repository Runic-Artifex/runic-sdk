using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static Runic.Platform.Windows.Elevation.ElevationNative;

namespace Runic.Platform.Windows.Elevation;

[SupportedOSPlatform("windows")]
internal sealed class WindowsElevationHost : IElevationHost
{
    public static readonly WindowsElevationHost Instance = new();

    private const uint PipeBufferSize = 4096;

    public ProgramCheck CheckProgram(string path)
    {
        // path is drive-absolute and normalized (HelperLocation). Mapped network drives and SUBST drives do not count
        // as local: who can change them is decided elsewhere.
        var root = path[..3];
        if (GetDriveType(root) != DriveFixed) return ProgramCheck.Replaceable;
        var segments = path[3..].Split('\\');
        var components = new List<PathComponent>(segments.Length + 1);
        var current = root;
        for (var i = -1; i < segments.Length; i++)
        {
            if (i >= 0) current = i == 0 ? root + segments[0] : current + "\\" + segments[i];
            var role = i == segments.Length - 1 ? ComponentRole.Program : i == segments.Length - 2 ? ComponentRole.Directory : ComponentRole.Ancestor;
            var attributes = GetFileAttributes(current);
            if (attributes == InvalidFileAttributes)
                return Marshal.GetLastPInvokeError() is 2 or 3 ? ProgramCheck.Missing : ProgramCheck.Replaceable;
            if ((attributes & FileAttributeReparsePoint) != 0) return ProgramCheck.NotRealPath;
            var isDirectory = (attributes & FileAttributeDirectory) != 0;
            if (role == ComponentRole.Program ? isDirectory : !isDirectory) return ProgramCheck.Missing;
            if (Read(current, role) is not { } component) return ProgramCheck.Replaceable;
            components.Add(component);
        }
        return ProgramTrust.Evaluate(components);
    }

    private static unsafe PathComponent? Read(string path, ComponentRole role)
    {
        // GetNamedSecurityInfo wants the root as "C:\".
        if (GetNamedSecurityInfo(path, SeFileObject, OwnerSecurityInformation | DaclSecurityInformation,
                out var owner, out _, out var dacl, out _, out var descriptor) != 0)
            return null;
        try
        {
            if (SidToString(owner) is not { } ownerSid) return null;
            return new PathComponent(path, role, ownerSid, dacl == 0 ? null : ReadAcl((byte*)dacl));
        }
        finally { LocalFree(descriptor); }
    }

    private static unsafe List<AccessEntry> ReadAcl(byte* acl)
    {
        // ACL: revision, padding, size (16 bits), entry count (16 bits), padding; the entries follow.
        var count = *(ushort*)(acl + 4);
        var entries = new List<AccessEntry>(count);
        var entry = acl + 8;
        for (var i = 0; i < count; i++)
        {
            var type = entry[0];
            var flags = entry[1];
            var size = *(ushort*)(entry + 2);
            var mask = *(uint*)(entry + 4);
            var inheritOnly = (flags & 0x8) != 0;
            byte* sid;
            bool allow;
            switch (type)
            {
                case 0 or 9: // ACCESS_ALLOWED, ACCESS_ALLOWED_CALLBACK
                    allow = true;
                    sid = entry + 8;
                    break;
                case 1 or 10: // ACCESS_DENIED, ACCESS_DENIED_CALLBACK
                    allow = false;
                    sid = entry + 8;
                    break;
                case 5 or 6 or 11 or 12: // the object variants: flags, then up to two GUIDs, then the SID
                    allow = type is 5 or 11;
                    var objectFlags = *(uint*)(entry + 8);
                    sid = entry + 12 + ((objectFlags & 1) != 0 ? 16 : 0) + ((objectFlags & 2) != 0 ? 16 : 0);
                    break;
                default:
                    // An entry this check does not know could grant anything: count it as a write by an unknown SID.
                    entries.Add(new AccessEntry(true, inheritOnly, uint.MaxValue, "unknown"));
                    entry += size;
                    continue;
            }
            entries.Add(new AccessEntry(allow, inheritOnly, mask, SidToString((nint)sid) ?? "unknown"));
            entry += size;
        }
        return entries;
    }

    public unsafe IElevationPipe CreatePipe(string name)
    {
        // Only the calling user, SYSTEM and elevated administrators may open the pipe. Administrators is needed for
        // an over-the-shoulder elevation, where the helper runs as another (administrator) user; a filtered token,
        // as every non-elevated process has, holds Administrators for deny only. FILE_FLAG_FIRST_PIPE_INSTANCE fails if
        // anyone created the name first, and one instance means no second client can connect while the helper is.
        var user = CurrentUserSid() ?? throw new IOException("The current user's SID could not be read.");
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{user})(A;;GA;;;SY)(A;;GA;;;BA)", 1, out var descriptor, 0))
            throw new IOException("The pipe's security descriptor could not be built.", Marshal.GetLastPInvokeError());
        try
        {
            var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), SecurityDescriptor = descriptor };
            var handle = CreateNamedPipe(ElevationProtocol.PipePath(name), PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeRejectRemoteClients, 1, PipeBufferSize, PipeBufferSize, 0, &attributes);
            if (handle == -1)
                throw new IOException($"The pipe could not be created: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}", Marshal.GetLastPInvokeError());
            var safe = new SafePipeHandle(handle, ownsHandle: true);
            return new Pipe(new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: false, safe), safe);
        }
        finally { LocalFree(descriptor); }
    }

    public Task<LaunchResult> LaunchAsync(string program, string commandLine, nint ownerWindow)
    {
        var result = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // ShellExecuteExW blocks until the UAC prompt is answered, and wants COM on the calling thread.
        var thread = new Thread(() =>
        {
            try { result.SetResult(Launch(program, commandLine, ownerWindow)); }
            catch (Exception e) { result.SetException(e); }
        })
        {
            IsBackground = true,
            Name = "Runic elevation prompt",
        };
        thread.Start();
        return result.Task;
    }

    private static unsafe LaunchResult Launch(string program, string commandLine, nint ownerWindow)
    {
        var com = CoInitializeEx(0, CoinitApartmentThreaded | CoinitDisableOle1Dde);
        try
        {
            var directory = SystemDirectory();
            fixed (char* verb = "runas")
            fixed (char* file = program)
            fixed (char* parameters = commandLine)
            fixed (char* workingDirectory = directory)
            {
                var info = new ShellExecuteInfo
                {
                    Size = (uint)sizeof(ShellExecuteInfo),
                    Mask = SeeMaskNoCloseProcess | SeeMaskNoAsync | SeeMaskFlagNoUi,
                    Window = ownerWindow,
                    Verb = verb,
                    File = file,
                    Parameters = parameters,
                    Directory = workingDirectory,
                    Show = SwHide,
                };
                if (!ShellExecuteEx(&info)) return new LaunchResult(null, Marshal.GetLastPInvokeError());
                // Without a process handle (an elevation broker that reused a process) nothing can be followed.
                if (info.Process == 0) return new LaunchResult(null, 0);
                return new LaunchResult(new HelperProcess(new SafeProcessHandle(info.Process, ownsHandle: true)), 0);
            }
        }
        finally
        {
            if (com >= 0) CoUninitialize();
        }
    }

    private sealed class Pipe : IElevationPipe
    {
        private readonly NamedPipeServerStream _server;
        private readonly SafePipeHandle _handle;
        private Task _connect;

        public Pipe(NamedPipeServerStream server, SafePipeHandle handle)
        {
            _server = server;
            _handle = handle;
            // Listening before the helper starts: a helper that connects, reports and exits at once must still be
            // seen as connected, with its report readable.
            _connect = Listen();
        }

        private Task Listen()
        {
            var connect = _server.WaitForConnectionAsync();
            // Disposing the pipe ends a pending wait; observe its exception.
            _ = connect.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return connect;
        }

        public async Task<Stream?> AcceptAsync(int clientProcessId, Task processExited, CancellationToken cancellationToken)
        {
            while (true)
            {
                await Task.WhenAny(_connect, processExited).WaitAsync(cancellationToken).ConfigureAwait(false);
                // A helper that connected and exited at once completes both; its connection wins.
                if (!_connect.IsCompleted) return null;
                try { await _connect.ConfigureAwait(false); }
                catch (IOException)
                {
                    // The client went away before the connection completed.
                    if (processExited.IsCompleted) return null;
                    _connect = Listen();
                    continue;
                }
                // Anyone the DACL allows can connect first, such as another process of the same user that listed the
                // pipe names. Only the helper this run started is accepted; anyone else is disconnected.
                if (GetNamedPipeClientProcessId(_handle, out var client) && client == (uint)clientProcessId) return _server;
                _server.Disconnect();
                _connect = Listen();
            }
        }

        public void Dispose() => _server.Dispose();
    }

    private sealed class HelperProcess : IHelperProcess
    {
        private readonly SafeProcessHandle _handle;
        private readonly ProcessWaitHandle _event;
        private readonly RegisteredWaitHandle _wait;
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HelperProcess(SafeProcessHandle handle)
        {
            _handle = handle;
            Id = (int)GetProcessId(handle);
            _event = new ProcessWaitHandle(handle);
            _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) =>
                _exited.TrySetResult(GetExitCodeProcess(_handle, out var code) ? unchecked((int)code) : -1), null, Timeout.Infinite, executeOnlyOnce: true);
        }

        public int Id { get; }
        public Task<int> Exited => _exited.Task;

        public bool TryTerminate(int exitCode) => !_exited.Task.IsCompleted && TerminateProcess(_handle, unchecked((uint)exitCode));

        public void Dispose()
        {
            _wait.Unregister(null);
            _event.Dispose();
            _handle.Dispose();
        }
    }

    /// <summary>Waits on a process handle it does not own.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) => SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
