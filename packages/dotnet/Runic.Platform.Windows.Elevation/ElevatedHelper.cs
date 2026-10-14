using System.Collections.Immutable;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Runic.Platform.Windows.Elevation.ElevationNative;

namespace Runic.Platform.Windows.Elevation;

/// <summary>The elevated side of an action: the helper that <c>runas</c> started.</summary>
/// <remarks>
/// <para>
/// The helper is the security boundary. Any user can start it with <c>runas</c> and any arguments, so it checks
/// them itself, with <see cref="Match"/> and its own rules. It must be a NativeAOT build: an elevated process takes
/// its environment from the user's profile (<c>HKCU\Environment</c>), which every process of the user can write, and
/// a JIT runtime would load <c>DOTNET_STARTUP_HOOKS</c>, <c>DOTNET_ROOT</c> or a <c>CORECLR_PROFILER</c> from it.
/// <see cref="Initialize"/>, which <see cref="Match"/> calls, replaces that environment and the working directory.
/// </para>
/// <para>
/// <see cref="Match"/> connects to the application's pipe, reports the outcome and, for an accepted action, sends
/// this process's standard output and error to the application; tools the helper starts without redirecting their
/// output inherit them. When the application ends or cancels, <see cref="WatchCancellation"/> is cancelled and later
/// output is dropped.
/// </para>
/// </remarks>
public static class ElevatedHelper
{
    private static readonly TimeSpan GoTimeout = TimeSpan.FromMinutes(1);
    private static readonly Lock s_lock = new();
    private static bool? s_initialized;
    private static Session? s_session;
    private static bool s_matched;

    // Stand-in tests run the helper protocol without elevation and under JIT.
    internal static bool RequireElevation = true;
    internal static bool RequireNativeAot = true;

    /// <summary>
    /// Resets the process state the elevation takes from the user. Call it first in <c>Main</c>;
    /// <see cref="Match"/> calls it.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>DLLs load only from the helper's directory and System32 (<c>SetDefaultDllDirectories</c>), never from the
    /// working directory or <c>PATH</c>.</item>
    /// <item>The working directory becomes System32.</item>
    /// <item>The environment is replaced: <c>SystemRoot</c>, <c>windir</c>, <c>SystemDrive</c>, <c>ComSpec</c>,
    /// <c>Path</c>, <c>PATHEXT</c>, <c>OS</c>, <c>NUMBER_OF_PROCESSORS</c>, <c>PROCESSOR_ARCHITECTURE</c> and the
    /// Program Files and ProgramData variables, from the system rather than the user; every other variable is
    /// removed.</item>
    /// <item><c>TEMP</c> and <c>TMP</c> point to a new directory under the Windows directory that only
    /// administrators and SYSTEM can open, removed when the process exits.</item>
    /// </list>
    /// The NativeAOT runtime reads its own <c>DOTNET_</c> garbage collector settings before <c>Main</c>; they can
    /// change performance but load no code.
    /// </remarks>
    /// <returns>False off Windows or when any step fails; do not act then.</returns>
    public static bool Initialize()
    {
        lock (s_lock)
        {
            s_initialized ??= OperatingSystem.IsWindows() && HelperProcess.Initialize();
            return s_initialized.Value;
        }
    }

    /// <summary>
    /// Initializes the process (see <see cref="Initialize"/>), connects to the application that started this helper
    /// and finds the action that <paramref name="arguments"/>, this process's arguments without the program name,
    /// select. For an accepted action it starts sending standard output and error to the application.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The arguments are those the provider passes: a marker, the pipe name, then the action's verb and arguments.
    /// The verb alone selects the action (<c>RUNICPA003</c>), and the remaining arguments must be within it.
    /// </para>
    /// <para>
    /// Before it reports anything, the helper checks that the pipe's server is the process that started it (its
    /// parent, which UAC sets to the requesting process) in the same session. It reports
    /// <see cref="PlatformUnavailableReason.BackendUnavailable"/> to the application when it is not elevated (UAC is
    /// off), and refuses on a JIT runtime. The application cannot be authenticated beyond that: any process of the
    /// user can start the helper and serve the pipe, but the UAC prompt names the helper and its arguments, and the
    /// helper accepts only declared actions.
    /// </para>
    /// </remarks>
    /// <param name="actions">The declared actions, usually the generated <c>PrivilegedActions.All</c>.</param>
    /// <param name="arguments">The arguments passed to this process.</param>
    /// <param name="actionArguments">The arguments after the verb, when an action matched.</param>
    /// <returns>The matching action, or null; exit without acting when it is null. Only the first call can match.</returns>
    public static PrivilegedActionDeclaration? Match(IEnumerable<PrivilegedActionDeclaration> actions, IReadOnlyList<string> arguments, out ImmutableArray<string> actionArguments)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(arguments);
        actionArguments = default;
        lock (s_lock)
        {
            if (s_matched) return null;
            s_matched = true;
        }
        if (!OperatingSystem.IsWindows() || !ElevationProtocol.TryParseHelperArguments(arguments, out var pipeName, out var argumentVector)) return null;
        if (!Initialize()) return null;
        var session = HelperProcess.Connect(pipeName);
        if (session is null) return null;

        PrivilegedActionDeclaration? action = null;
        ImmutableArray<string> rest = default;
        var status = RequireNativeAot && RuntimeFeature.IsDynamicCodeSupported ? HelperStatus.NotNativeAot
            : RequireElevation && IsElevated() != true ? HelperStatus.NotElevated
            : (action = MatchCore(actions, argumentVector, out rest)) is null ? HelperStatus.Rejected
            : HelperStatus.Accepted;
        if (!session.Start(status, GoTimeout))
        {
            session.Dispose();
            return null;
        }
        lock (s_lock) s_session = session;
        actionArguments = rest;
        return action;
    }

    internal static PrivilegedActionDeclaration? MatchCore(IEnumerable<PrivilegedActionDeclaration> actions, IReadOnlyList<string> arguments, out ImmutableArray<string> actionArguments)
    {
        actionArguments = default;
        var all = actions.ToList();
        // An action without a verb matches every invocation, so it is valid only on its own.
        if (all.Any(a => a.Verb is null) && all.Count != 1) return null;
        var candidates = all.Where(a => a.Verb is null || (arguments.Count > 0 && arguments[0] == a.Verb)).ToList();
        if (candidates.Count != 1) return null;
        var action = candidates[0];
        var rest = arguments.Skip(action.Verb is null ? 0 : 1).ToImmutableArray();
        if (!action.AcceptsArguments(rest)) return null;
        actionArguments = rest;
        return action;
    }

    /// <summary>
    /// A token that is cancelled when the application asks the action to stop, closes the pipe or ends. A running
    /// action should stop soon after and exit with a code that says so.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Match"/> did not return an action.</exception>
    public static CancellationToken WatchCancellation() =>
        (s_session ?? throw new InvalidOperationException("Match did not accept an action.")).Cancelled;

    /// <summary>The process ID of the application that started this helper, after <see cref="Match"/> accepted an action.</summary>
    public static int? GetCallerProcessId() => s_session?.CallerProcessId;

    /// <summary>
    /// The image path of the application that started this helper, after <see cref="Match"/> accepted an action;
    /// null when it could not be read. A helper may compare it with the expected application, but any process of the
    /// user can start the helper, so it is not proof of who asked.
    /// </summary>
    public static string? GetCallerImagePath() => s_session?.CallerImagePath;

    /// <summary>A connected helper: its channel, the caller and, once started, its redirected output.</summary>
    internal sealed class Session(HelperChannel channel, int callerProcessId, string? callerImagePath, Func<HelperChannel, bool>? redirect) : IDisposable
    {
        public int CallerProcessId { get; } = callerProcessId;
        public string? CallerImagePath { get; } = callerImagePath;
        public CancellationToken Cancelled => channel.Cancelled;

        /// <summary>Reports <paramref name="status"/> and, for an accepted action, waits for the start signal.</summary>
        public bool Start(HelperStatus status, TimeSpan timeout)
        {
            try { channel.SendHello(status); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { return false; }
            if (status != HelperStatus.Accepted) return false;
            if (!channel.WaitForGoAsync(timeout).GetAwaiter().GetResult()) return false;
            if (redirect is not null && !redirect(channel)) return false;
            channel.StartWatching();
            return true;
        }

        public void Dispose() => channel.Dispose();
    }

    [SupportedOSPlatform("windows")]
    private static bool? IsElevated() => ElevationNative.IsElevated();

    [SupportedOSPlatform("windows")]
    private static unsafe class HelperProcess
    {
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        public static bool Initialize()
        {
            // Never the working directory or PATH: a planted DLL there would load into the elevated process.
            if (!SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs)) return false;
            if (WindowsDirectory() is not { } windows || SystemDirectory() is not { } system) return false;
            try { Environment.CurrentDirectory = system; }
            catch (IOException) { return false; }
            return ResetEnvironment(windows, system);
        }

        private static bool ResetEnvironment(string windows, string system)
        {
            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
                Environment.SetEnvironmentVariable(name, null);
            // The drive first: ProgramData is stored as %SystemDrive%\ProgramData and expanded from this environment.
            Environment.SetEnvironmentVariable("SystemRoot", windows);
            Environment.SetEnvironmentVariable("windir", windows);
            Environment.SetEnvironmentVariable("SystemDrive", windows[..2]);
            Environment.SetEnvironmentVariable("ComSpec", Path.Combine(system, "cmd.exe"));
            Environment.SetEnvironmentVariable("Path", string.Join(';', system, windows, Path.Combine(system, "Wbem"), Path.Combine(system, @"WindowsPowerShell\v1.0\")));
            Environment.SetEnvironmentVariable("PATHEXT", ".COM;.EXE;.BAT;.CMD");
            Environment.SetEnvironmentVariable("OS", "Windows_NT");
            Environment.SetEnvironmentVariable("NUMBER_OF_PROCESSORS", Environment.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Environment.SetEnvironmentVariable("PROCESSOR_ARCHITECTURE", RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 => "AMD64",
                Architecture.Arm64 => "ARM64",
                Architecture.X86 => "x86",
                var other => other.ToString(),
            });
            foreach (var (name, folder) in new[]
            {
                ("ProgramFiles", Environment.SpecialFolder.ProgramFiles),
                ("ProgramW6432", Environment.SpecialFolder.ProgramFiles),
                ("ProgramFiles(x86)", Environment.SpecialFolder.ProgramFilesX86),
                ("CommonProgramFiles", Environment.SpecialFolder.CommonProgramFiles),
                ("CommonProgramW6432", Environment.SpecialFolder.CommonProgramFiles),
                ("CommonProgramFiles(x86)", Environment.SpecialFolder.CommonProgramFilesX86),
                ("ProgramData", Environment.SpecialFolder.CommonApplicationData),
            })
            {
                var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
                if (path.Length > 0) Environment.SetEnvironmentVariable(name, path);
            }
            if (CreatePrivateDirectory(Path.Combine(windows, "Temp")) is not { } temporary) return false;
            // Registered before the output's handler, so it runs first; tools that still run lose their files.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try { Directory.Delete(temporary, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            };
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            return true;
        }

        // The user's TEMP is the user's to change: a tool that writes and then runs or reads a file there could be
        // given another one. A new directory that only administrators and SYSTEM can open cannot be prepared.
        private static string? CreatePrivateDirectory(string parent)
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", 1, out var descriptor, 0)) return null;
            try
            {
                var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), SecurityDescriptor = descriptor };
                var path = Path.Combine(parent, "runic-elevated-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)));
                // Fails if the name exists, so nobody can have prepared it.
                return CreateDirectory(path, &attributes) ? path : null;
            }
            finally { LocalFree(descriptor); }
        }

        public static Session? Connect(string pipeName)
        {
            var output = OutputRedirect.Prepare();
            if (output is null) return null;
            var path = ElevationProtocol.PipePath(pipeName);
            var deadline = DateTime.UtcNow + ConnectTimeout;
            nint handle;
            while (true)
            {
                // Identification only: the application's end of the pipe can learn who the helper is, but cannot act
                // as the elevated helper.
                handle = CreateFile(path, GenericRead | GenericWrite, 0, null, OpenExisting,
                    FileFlagOverlapped | SecuritySqosPresent | SecurityIdentification, 0);
                if (handle != -1) break;
                // Busy: another client holds the single instance until the application disconnects it.
                if (Marshal.GetLastPInvokeError() != ErrorPipeBusy || DateTime.UtcNow > deadline)
                {
                    output.Dispose();
                    return null;
                }
                WaitNamedPipe(path, 1000);
            }
            var safe = new SafePipeHandle(handle, ownsHandle: true);
            var caller = VerifyServer(safe);
            if (caller is null)
            {
                safe.Dispose();
                output.Dispose();
                return null;
            }
            var stream = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, safe);
            return new Session(new HelperChannel(stream), (int)caller.Value, ProcessImagePath(caller.Value), output.Install);
        }

        // The pipe's server must be the process that started this one, in this session. UAC makes the requesting
        // process the parent of the elevated process, as CreateProcess does without UAC.
        private static uint? VerifyServer(SafePipeHandle pipe)
        {
            if (!GetNamedPipeServerProcessId(pipe, out var server) || ParentProcessId() is not { } parent || server != parent) return null;
            if (!GetNamedPipeServerSessionId(pipe, out var serverSession) || !ProcessIdToSessionId(GetCurrentProcessId(), out var session) || serverSession != session)
                return null;
            return server;
        }

        /// <summary>Standard output and error as pipes that pump into the channel.</summary>
        private sealed class OutputRedirect : IDisposable
        {
            private nint _outputRead, _outputWrite, _errorRead, _errorWrite, _input;
            private readonly List<Thread> _pumps = [];

            public static OutputRedirect? Prepare()
            {
                var redirect = new OutputRedirect();
                var attributes = new SecurityAttributes { Length = (uint)sizeof(SecurityAttributes), InheritHandle = 1 };
                // The write ends and NUL are inheritable, so tools the helper starts write into the same pipes; the
                // read ends stay in this process.
                if (!CreatePipe(out redirect._outputRead, out redirect._outputWrite, &attributes, 0)
                    || !CreatePipe(out redirect._errorRead, out redirect._errorWrite, &attributes, 0)
                    || !SetHandleInformation(redirect._outputRead, HandleFlagInherit, 0)
                    || !SetHandleInformation(redirect._errorRead, HandleFlagInherit, 0))
                {
                    redirect.Dispose();
                    return null;
                }
                redirect._input = CreateFile("NUL", GenericRead, 3, &attributes, OpenExisting, 0, 0);
                if (redirect._input == -1)
                {
                    redirect._input = 0;
                    redirect.Dispose();
                    return null;
                }
                return redirect;
            }

            public bool Install(HelperChannel channel)
            {
                if (!SetStdHandle(StdInput, _input) || !SetStdHandle(StdOutput, _outputWrite) || !SetStdHandle(StdError, _errorWrite)) return false;
                Console.SetIn(TextReader.Null);
                Console.SetOut(Writer(_outputWrite));
                Console.SetError(Writer(_errorWrite));
                Pump(_outputRead, channel, ElevationProtocol.StandardOutput);
                Pump(_errorRead, channel, ElevationProtocol.StandardError);
                _outputRead = _errorRead = 0;
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Complete(channel);
                return true;
            }

            private static StreamWriter Writer(nint handle) =>
                new(new FileStream(new SafeFileHandle(handle, ownsHandle: false), FileAccess.Write, 1), new UTF8Encoding(false)) { AutoFlush = true };

            private void Pump(nint read, HelperChannel channel, byte type)
            {
                var thread = new Thread(() =>
                {
                    using var stream = new FileStream(new SafeFileHandle(read, ownsHandle: true), FileAccess.Read, 1);
                    var buffer = new byte[ElevationProtocol.MaximumPayload];
                    try
                    {
                        // Once the application is gone the output is read and dropped, so writers never block.
                        int length;
                        while ((length = stream.Read(buffer)) > 0) channel.Write(type, buffer.AsSpan(0, length));
                    }
                    catch (IOException) { }
                })
                {
                    IsBackground = true,
                    Name = type == ElevationProtocol.StandardOutput ? "Runic elevated output" : "Runic elevated error",
                };
                thread.Start();
                _pumps.Add(thread);
            }

            // At exit: flush, close this process's write ends and forward what is left. Tools still running keep
            // their copies, so the pumps are given a bounded time.
            private void Complete(HelperChannel channel)
            {
                try
                {
                    Console.Out.Flush();
                    Console.Error.Flush();
                }
                catch (IOException) { }
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                Dispose();
                var deadline = DateTime.UtcNow + DrainTimeout;
                foreach (var pump in _pumps) pump.Join(Max(deadline - DateTime.UtcNow));
                channel.Dispose();
            }

            private static TimeSpan Max(TimeSpan value) => value > TimeSpan.Zero ? value : TimeSpan.Zero;

            public void Dispose()
            {
                foreach (ref var handle in new Span<nint>([_outputRead, _outputWrite, _errorRead, _errorWrite, _input]))
                    if (handle is not (0 or -1)) CloseHandle(handle);
                _outputRead = _outputWrite = _errorRead = _errorWrite = _input = 0;
            }
        }
    }
}
