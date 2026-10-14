using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static Runic.Platform.MacOS.MacPrivilegedProtocol;

namespace Runic.Platform.MacOS;

/// <summary>The privileged side of actions on macOS: the launch daemon that runs declared programs as root.</summary>
/// <remarks>
/// <para>
/// The daemon is an executable in the application bundle, registered with <see cref="MacPrivilegedActions.RegisterDaemon"/>
/// and started by launchd from the generated property list, whose launch constraint lets launchd run it only with the
/// application's Team ID. Its <c>Main</c> calls <see cref="Run"/> and returns its result.
/// </para>
/// <para>
/// For every XPC connection the daemon requires the client's code signature to satisfy
/// <see cref="MacPrivilegedDaemonDefinition.ApplicationRequirement"/>, checked by libxpc against the connection's audit
/// token and again for each run request. A request must name a declared action with arguments within its declaration,
/// and carry an authorization that grants the action's right without interaction. The daemon then runs the program
/// with <c>posix_spawn</c>, with its argument vector, a fixed environment, <c>/</c> as working directory, default
/// signal dispositions, an empty signal mask and no other open files. A program whose path only root can change runs
/// directly; any other program must satisfy <see cref="MacPrivilegedDaemonDefinition.ProgramRequirement"/>, checked on
/// disk and again in the started, still suspended process before it runs.
/// </para>
/// <para>
/// The program receives <c>RUNIC_CALLER_UID</c>, the user ID of the application's process. Standard input reaches its
/// end when the application cancels the run or disconnects; standard output and error go to the application line by
/// line, and the daemon reads no further output until the application took a line.
/// </para>
/// </remarks>
public static class MacPrivilegedDaemon
{
    /// <summary>
    /// Serves the actions until launchd stops the daemon. Returns only when the daemon cannot start: not macOS 14,
    /// not root, not started by launchd, not signed as <see cref="MacPrivilegedDaemonDefinition.DaemonRequirement"/>,
    /// or the rights cannot be defined.
    /// </summary>
    /// <param name="actions">The declared actions, usually the generated <c>PrivilegedActions.All</c>.</param>
    /// <param name="daemon">The definition, usually the generated <c>PrivilegedActions.MacOSDaemon</c>.</param>
    /// <returns>A non-zero exit code for the daemon's process.</returns>
    /// <exception cref="ArgumentException">An action has no right in the definition, or a right has no action.</exception>
    public static int Run(IEnumerable<PrivilegedActionDeclaration> actions, MacPrivilegedDaemonDefinition daemon) =>
        RunWith(actions, daemon, MacSystemAuthority.Instance);

    internal static int RunWith(IEnumerable<PrivilegedActionDeclaration> actions, MacPrivilegedDaemonDefinition daemon, IMacDaemonAuthority authority)
    {
        var server = new MacPrivilegedDaemonServer(actions, daemon, authority, BundleRoot());
        if (!OperatingSystem.IsMacOSVersionAtLeast(14)) return 2;
        // launchd (process 1) starts the daemon as root with a clean environment; refuse anything else.
        if (MacProcess.GetEffectiveUserId() != 0 || MacProcess.GetParentProcessId() != 1) return 3;
        _ = MacProcess.Umask(0x12); // 022, inherited by every program
        Environment.CurrentDirectory = "/";
        if (!MacSecurity.SelfSatisfies(daemon.DaemonRequirement)) return 4;
        if (server.Prepare().Status != MacReplyStatus.Ready) return 5;
        var queue = MacXpc.CreateQueue(daemon.Label, 0);
        var listener = MacXpc.CreateMachService(daemon.Label, queue, MacXpc.MachServiceListener);
        if (listener == 0) return 6;
        server.Listen(listener);
        server.Stopped.Wait();
        return 7;
    }

    // <bundle>.app/Contents/MacOS/<daemon>; null when the daemon is not in an application bundle.
    private static string? BundleRoot()
    {
        if (Environment.ProcessPath is not { } path) return null;
        var macOS = Path.GetDirectoryName(path);
        var contents = Path.GetDirectoryName(macOS);
        var bundle = Path.GetDirectoryName(contents);
        return Path.GetFileName(macOS) == "MacOS" && Path.GetFileName(contents) == "Contents" && bundle is not null
            && bundle.EndsWith(".app", StringComparison.Ordinal) ? bundle : null;
    }
}

/// <summary>Defines the rights in the authorization database and checks an authorization against them.</summary>
internal interface IMacDaemonAuthority
{
    /// <summary>Defines <paramref name="right"/> as <see cref="MacAuthorizationRule"/> unless it already is; the OSStatus.</summary>
    int EnsureRight(MacPrivilegedRight right);

    /// <summary>Checks without interaction that <paramref name="externalForm"/> grants the right <paramref name="name"/>; the OSStatus.</summary>
    int Check(string name, byte[] externalForm);
}

internal sealed unsafe class MacSystemAuthority : IMacDaemonAuthority
{
    internal static readonly MacSystemAuthority Instance = new();

    public int EnsureRight(MacPrivilegedRight right)
    {
        var existing = MacSecurity.CopyRight(right.ActionId);
        if (existing != 0)
        {
            try
            {
                if (Matches(existing, right)) return 0;
            }
            finally { MacSecurity.Release(existing); }
        }
        // Anyone may add a right that does not exist yet, so a definition that differs is replaced: root may modify any.
        var status = MacSecurity.AuthorizationCreate(null, null, 0, out var authorization);
        if (status != 0) return status;
        var definition = CreateDefinition(right.ActionId);
        var description = MacSecurity.CreateString(right.Message);
        try { return MacSecurity.AuthorizationRightSet(authorization, right.ActionId, definition, description, 0, 0); }
        finally
        {
            MacSecurity.Release(description);
            MacSecurity.Release(definition);
            _ = MacSecurity.AuthorizationFree(authorization, 0);
        }
    }

    public int Check(string name, byte[] externalForm)
    {
        if (externalForm.Length != MacSecurity.ExternalFormLength) return MacPrivilegedOutcome.AuthorizationDenied;
        nint authorization;
        int status;
        fixed (byte* form = externalForm) status = MacSecurity.AuthorizationCreateFromExternalForm(form, out authorization);
        if (status != 0) return status;
        var text = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var item = new MacSecurity.AuthorizationItem { Name = text };
            var rights = new MacSecurity.AuthorizationItemSet { Count = 1, Items = &item };
            // Extend the rights, but never interact: the application asked the user already.
            return MacSecurity.AuthorizationCopyRights(authorization, &rights, null, MacSecurity.FlagExtendRights, 0);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
            _ = MacSecurity.AuthorizationFree(authorization, 0);
        }
    }

    internal static nint CreateDefinition(string actionId)
    {
        var dictionary = MacSecurity.CreateDictionary();
        foreach (var (key, value) in MacAuthorizationRule.Definition(actionId))
        {
            var cfValue = value switch
            {
                string text => MacSecurity.CreateString(text),
                bool flag => Retained(flag ? MacSecurity.True : MacSecurity.False),
                long number => MacSecurity.CreateNumber(number),
                _ => throw new InvalidOperationException(),
            };
            MacSecurity.Set(dictionary, key, cfValue);
        }
        return dictionary;
    }

    // Set releases its value, so a CFBoolean constant is retained once for it.
    private static nint Retained(nint value) => MacSecurity.Retain(value);

    /// <summary>Whether a right's stored definition is the rule this daemon defines, with this message.</summary>
    internal static bool Matches(nint existing, MacPrivilegedRight right)
    {
        if (!MacSecurity.IsDictionary(existing)) return false;
        foreach (var (key, value) in MacAuthorizationRule.Definition(right.ActionId))
        {
            var stored = MacSecurity.Get(existing, key);
            var same = value switch
            {
                string text => MacSecurity.ToText(stored) == text,
                bool flag => MacSecurity.ToBoolean(stored) == flag,
                long number => MacSecurity.ToNumber(stored) == number,
                _ => false,
            };
            if (!same) return false;
        }
        var prompts = MacSecurity.Get(existing, "default-prompt");
        return MacSecurity.IsDictionary(prompts) && MacSecurity.ToText(MacSecurity.Get(prompts, "")) == right.Message;
    }
}

internal sealed record MacRunRequest(string ActionId, ImmutableArray<string> Arguments, byte[] Authorization, IReadOnlyDictionary<string, string> Environment);

/// <summary>The daemon's listener and the checks of every run, independent of how the listener was created.</summary>
internal sealed class MacPrivilegedDaemonServer
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(5);
    private readonly FrozenDictionary<string, PrivilegedActionDeclaration> _actions;
    private readonly IMacDaemonAuthority _authority;
    private readonly string? _bundleRoot;

    internal MacPrivilegedDaemonServer(IEnumerable<PrivilegedActionDeclaration> actions, MacPrivilegedDaemonDefinition definition, IMacDaemonAuthority authority, string? bundleRoot)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(definition);
        var byId = new Dictionary<string, PrivilegedActionDeclaration>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action, nameof(actions));
            if (!byId.TryAdd(action.Id, action)) throw new ArgumentException($"Action '{action.Id}' is declared twice.", nameof(actions));
            if (definition.FindRight(action.Id) is null) throw new ArgumentException($"Action '{action.Id}' has no right in the daemon definition.", nameof(definition));
        }
        foreach (var right in definition.Rights)
            if (!byId.ContainsKey(right.ActionId)) throw new ArgumentException($"Right '{right.ActionId}' has no declared action.", nameof(definition));
        _actions = byId.ToFrozenDictionary(StringComparer.Ordinal);
        Definition = definition;
        _authority = authority;
        _bundleRoot = bundleRoot;
    }

    internal MacPrivilegedDaemonDefinition Definition { get; }

    /// <summary>Set when the listener was cancelled or became invalid.</summary>
    internal ManualResetEventSlim Stopped { get; } = new();

    /// <summary>Whether each run request is checked against the sender's code signature again (always, except in tests without a signature).</summary>
    internal bool CheckSender { get; init; } = true;

    internal void Listen(nint listener)
    {
        var handler = MacDesktopBlock.Create(peer =>
        {
            if (MacXpc.GetType(peer) == MacXpc.TypeConnection) Accept(peer);
            else if (MacXpc.IsError(peer)) Stopped.Set();
        });
        try { MacXpc.SetEventHandler(listener, handler); }
        finally { MacDesktopBlock.Release(handler); }
        MacXpc.Resume(listener);
    }

    private void Accept(nint peer)
    {
        // libxpc checks the peer's code signature against its audit token for every message it delivers.
        if (MacXpc.SetPeerCodeSigningRequirement(peer, Definition.ApplicationRequirement) != 0)
        {
            MacXpc.Cancel(peer);
            return;
        }
        var session = new MacDaemonSession(this, MacXpc.Retain(peer));
        var handler = MacDesktopBlock.Create(session.OnEvent);
        try { MacXpc.SetEventHandler(peer, handler); }
        finally { MacDesktopBlock.Release(handler); }
        MacXpc.Resume(peer);
    }

    internal MacReply Prepare()
    {
        foreach (var right in Definition.Rights)
            if (_authority.EnsureRight(right) is var status and not 0) return new(MacReplyStatus.Failed, status);
        return new(MacReplyStatus.Ready);
    }

    /// <summary>Checks and runs one request. Never throws: every failure is a reply.</summary>
    internal MacReply Execute(MacRunRequest request, uint callerUserId, MacDaemonSession session)
    {
        try { return ExecuteCore(request, callerUserId, session); }
        catch (Exception e) when (e is not OutOfMemoryException) { return new(MacReplyStatus.Failed, e.HResult); }
    }

    internal MacReply Validate(MacRunRequest request, out PrivilegedActionDeclaration? action, out ImmutableArray<string> argv)
    {
        argv = default;
        if (!_actions.TryGetValue(request.ActionId, out action) || !action.AcceptsArguments(request.Arguments)) return new(MacReplyStatus.Rejected);
        argv = action.CreateArgumentVector(request.Arguments);
        return new(MacReplyStatus.Ready);
    }

    private MacReply ExecuteCore(MacRunRequest request, uint callerUserId, MacDaemonSession session)
    {
        if (Validate(request, out var action, out var argv) is { Status: not MacReplyStatus.Ready } invalid) return invalid;
        var right = Definition.FindRight(action!.Id)!;
        if (session.IsCancelled) return new(MacReplyStatus.Cancelled);
        if (_authority.EnsureRight(right) is var defined and not 0) return new(MacReplyStatus.Failed, defined);
        if (_authority.Check(right.ActionId, request.Authorization) is var authorized and not 0) return new(MacReplyStatus.Denied, authorized);
        if (session.IsCancelled) return new(MacReplyStatus.Cancelled);

        string program;
        if (right.Program is null) program = action.Program;
        else if (right.Program[0] == '/') program = right.Program;
        else if (_bundleRoot is null) return new(MacReplyStatus.Untrusted, 1);
        else program = _bundleRoot + "/" + right.Program;
        var check = MacProgramTrust.Check(program, path => MacProcess.Lstat(path, out _));
        if (check == MacProgramCheck.Missing) return new(MacReplyStatus.Untrusted, 1);
        if (check == MacProgramCheck.NeedsSignature && MacSecurity.CheckFile(program, Definition.ProgramRequirement) != 0) return new(MacReplyStatus.Untrusted, 3);

        var environment = ProgramEnvironment(callerUserId, request.Environment);
        if (MacSpawnedProgram.Start(program, argv, environment, out var error) is not { } spawned) return new(MacReplyStatus.Failed, error);
        using (spawned)
        {
            // The file was checked before the start; the code the kernel loaded is checked before it runs.
            if (check == MacProgramCheck.NeedsSignature && MacSecurity.CheckProcess(spawned.Pid, Definition.ProgramRequirement) != 0)
            {
                spawned.Kill();
                return new(MacReplyStatus.Untrusted, 3);
            }
            if (!session.Started(spawned))
            {
                spawned.Kill();
                return new(MacReplyStatus.Cancelled);
            }
            spawned.Resume();
            var stdout = Pump(spawned.Output, PrivilegedOutputChannel.StandardOutput, session);
            var stderr = Pump(spawned.Error, PrivilegedOutputChannel.StandardError, session);
            var exitCode = spawned.WaitForExit();
            // A process the program started may hold its output open; stop reading after a while.
            if (!Task.WhenAll(stdout, stderr).Wait(OutputDrainTimeout)) spawned.CloseOutput();
            session.Finished();
            return new(MacReplyStatus.Ran, exitCode);
        }
    }

    private static Task Pump(Stream stream, PrivilegedOutputChannel channel, MacDaemonSession session) =>
        Task.Factory.StartNew(() =>
        {
            var delivering = true;
            MacLineReader.Read(stream, line =>
            {
                // After the application left, the rest is read and dropped, so the program does not block.
                if (delivering) delivering = session.Deliver(channel, line);
            });
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

/// <summary>One XPC connection from the application: at most one run, its cancellation and its output.</summary>
internal sealed class MacDaemonSession(MacPrivilegedDaemonServer server, nint peer)
{
    private readonly Lock _lock = new();
    // Serializes output across both streams; held while the daemon waits for the application's acknowledgement.
    private readonly Lock _delivery = new();
    private bool _running, _cancelled, _disconnected, _released;
    private MacSpawnedProgram? _program;

    internal bool IsCancelled
    {
        get { lock (_lock) return _cancelled || _disconnected; }
    }

    internal void OnEvent(nint message)
    {
        if (MacXpc.IsError(message))
        {
            Disconnect();
            return;
        }
        if (!MacXpc.IsDictionary(message) || MacXpc.GetInt64(message, V) != ProtocolVersion) return;
        switch (MacXpc.GetString(message, Op))
        {
            case Prepare:
                Reply(message, server.Prepare());
                break;
            case MacPrivilegedProtocol.Run:
                StartRun(message);
                break;
            case MacPrivilegedProtocol.Cancel:
                RequestCancel();
                break;
        }
    }

    private void StartRun(nint message)
    {
        lock (_lock)
        {
            if (_running)
            {
                Reply(message, new(MacReplyStatus.Rejected));
                return;
            }
            _running = true;
        }
        // The audit token of this very message, in addition to the connection's requirement.
        if (server.CheckSender && !MacSecurity.SenderSatisfies(message, server.Definition.ApplicationRequirement))
        {
            Reply(message, new(MacReplyStatus.Rejected));
            MacXpc.Cancel(peer);
            Finish();
            return;
        }
        if (Parse(message) is not { } request)
        {
            Reply(message, new(MacReplyStatus.Rejected));
            Finish();
            return;
        }
        var callerUserId = MacXpc.GetEffectiveUserId(peer);
        var reply = MacXpc.CreateReply(message);
        var thread = new Thread(() =>
        {
            var result = server.Execute(request, callerUserId, this);
            if (reply != 0)
            {
                Fill(reply, result);
                MacXpc.Send(peer, reply);
                MacXpc.Release(reply);
            }
            Finish();
        })
        {
            IsBackground = true,
            Name = "Runic privileged run",
        };
        thread.Start();
    }

    internal static MacRunRequest? Parse(nint message)
    {
        if (MacXpc.GetString(message, Id) is not { } id) return null;
        if (MacXpc.GetStringArray(MacXpc.GetValue(message, Arguments), PrivilegedActionDeclaration.ArgumentLimit) is not { } arguments) return null;
        if (MacXpc.GetData(message, Authorization) is not { Length: MacSecurity.ExternalFormLength } authorization) return null;
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (MacXpc.GetValue(message, EnvironmentKey) is var passed and not 0 && MacXpc.IsDictionary(passed))
            foreach (var name in PassedVariables)
                if (MacXpc.GetString(passed, name) is { } value) environment[name] = value;
        return new MacRunRequest(id, [.. arguments], authorization, environment);
    }

    /// <summary>Records the started program; false when the run was cancelled first, and the program must not run.</summary>
    internal bool Started(MacSpawnedProgram program)
    {
        lock (_lock)
        {
            if (_cancelled || _disconnected) return false;
            _program = program;
            return true;
        }
    }

    internal void Finished()
    {
        lock (_lock) _program = null;
    }

    /// <summary>Sends a line and waits until the application took it; false once the application is gone.</summary>
    internal bool Deliver(PrivilegedOutputChannel channel, byte[] line)
    {
        lock (_delivery)
        {
            lock (_lock)
                if (_disconnected) return false;
            var message = MacXpc.CreateDictionary(0, 0, 0);
            MacXpc.SetInt64(message, V, ProtocolVersion);
            MacXpc.SetString(message, Op, Output);
            MacXpc.SetInt64(message, Channel, channel == PrivilegedOutputChannel.StandardOutput ? 1 : 2);
            MacXpc.SetData(message, Data, line);
            var reply = MacXpc.SendWithReplySync(peer, message);
            MacXpc.Release(message);
            var delivered = MacXpc.IsDictionary(reply);
            if (reply != 0) MacXpc.Release(reply);
            if (!delivered)
                lock (_lock) _disconnected = true;
            return delivered;
        }
    }

    private void RequestCancel()
    {
        MacSpawnedProgram? program;
        lock (_lock)
        {
            _cancelled = true;
            program = _program;
        }
        // As on Linux, a running program is asked to stop by the end of its standard input.
        program?.CloseInput();
    }

    private void Disconnect()
    {
        MacSpawnedProgram? program;
        bool release;
        lock (_lock)
        {
            _disconnected = true;
            program = _program;
            release = !_running && !_released;
            _released |= release;
        }
        program?.CloseInput();
        // A peer that failed the code requirement is cancelled, so the client's pending messages fail instead of waiting.
        MacXpc.Cancel(peer);
        if (release) MacXpc.Release(peer);
    }

    private void Finish()
    {
        bool release;
        lock (_lock)
        {
            _running = false;
            release = _disconnected && !_released;
            _released |= release;
        }
        if (release) MacXpc.Release(peer);
    }

    private void Reply(nint message, MacReply result)
    {
        var reply = MacXpc.CreateReply(message);
        if (reply == 0) return;
        Fill(reply, result);
        MacXpc.Send(peer, reply);
        MacXpc.Release(reply);
    }

    private static void Fill(nint reply, MacReply result)
    {
        MacXpc.SetInt64(reply, V, ProtocolVersion);
        MacXpc.SetString(reply, Status, MacReply.Name(result.Status));
        MacXpc.SetInt64(reply, result.Status == MacReplyStatus.Ran ? Exit : Code, result.Code);
    }
}

/// <summary>A program started suspended with <c>posix_spawn</c>, with pipes for its standard streams.</summary>
internal sealed unsafe class MacSpawnedProgram : IDisposable
{
    private readonly SafeFileHandle _input;
    private bool _exited;

    private MacSpawnedProgram(int pid, SafeFileHandle input, SafeFileHandle output, SafeFileHandle error)
    {
        Pid = pid;
        _input = input;
        Output = new FileStream(output, FileAccess.Read, 1);
        Error = new FileStream(error, FileAccess.Read, 1);
    }

    internal int Pid { get; }
    internal Stream Output { get; }
    internal Stream Error { get; }

    internal static MacSpawnedProgram? Start(string program, IReadOnlyList<string> argv, IReadOnlyList<string> environment, out int error)
    {
        var input = stackalloc int[2];
        var output = stackalloc int[2];
        var errorPipe = stackalloc int[2];
        input[0] = input[1] = output[0] = output[1] = errorPipe[0] = errorPipe[1] = -1;
        if (MacProcess.Pipe(input) != 0 || MacProcess.Pipe(output) != 0 || MacProcess.Pipe(errorPipe) != 0)
        {
            error = Marshal.GetLastPInvokeError();
            CloseAll(input, output, errorPipe);
            return null;
        }
        // The application's ends are closed on exec; the program's ends are inherited only through the dup2 actions.
        foreach (var descriptor in new[] { input[1], output[0], errorPipe[0] }) _ = MacProcess.Fcntl(descriptor, 2 /* F_SETFD */, 1 /* FD_CLOEXEC */);

        nint actions, attributes;
        _ = MacProcess.FileActionsInit(&actions);
        _ = MacProcess.AttributesInit(&attributes);
        var strings = new List<nint>();
        try
        {
            _ = MacProcess.FileActionsDup2(&actions, input[0], 0);
            _ = MacProcess.FileActionsDup2(&actions, output[1], 1);
            _ = MacProcess.FileActionsDup2(&actions, errorPipe[1], 2);
            _ = MacProcess.FileActionsChdir(&actions, "/");
            _ = MacProcess.AttributesSetFlags(&attributes,
                MacProcess.SpawnSetSignalDefault | MacProcess.SpawnSetSignalMask | MacProcess.SpawnStartSuspended | MacProcess.SpawnCloseOnExecDefault);
            uint none = 0, all = uint.MaxValue;
            _ = MacProcess.AttributesSetSignalMask(&attributes, &none);
            _ = MacProcess.AttributesSetSignalDefault(&attributes, &all);

            var arguments = stackalloc nint[argv.Count + 2];
            arguments[0] = Allocate(program, strings);
            for (var i = 0; i < argv.Count; i++) arguments[i + 1] = Allocate(argv[i], strings);
            arguments[argv.Count + 1] = 0;
            var variables = stackalloc nint[environment.Count + 1];
            for (var i = 0; i < environment.Count; i++) variables[i] = Allocate(environment[i], strings);
            variables[environment.Count] = 0;

            error = MacProcess.Spawn(out var pid, program, &actions, &attributes, arguments, variables);
            _ = MacProcess.Close(input[0]);
            _ = MacProcess.Close(output[1]);
            _ = MacProcess.Close(errorPipe[1]);
            if (error != 0)
            {
                _ = MacProcess.Close(input[1]);
                _ = MacProcess.Close(output[0]);
                _ = MacProcess.Close(errorPipe[0]);
                return null;
            }
            return new MacSpawnedProgram(pid, new SafeFileHandle(input[1], true), new SafeFileHandle(output[0], true), new SafeFileHandle(errorPipe[0], true));
        }
        finally
        {
            foreach (var pointer in strings) Marshal.FreeCoTaskMem(pointer);
            _ = MacProcess.FileActionsDestroy(&actions);
            _ = MacProcess.AttributesDestroy(&attributes);
        }
    }

    private static nint Allocate(string value, List<nint> strings)
    {
        var pointer = Marshal.StringToCoTaskMemUTF8(value);
        strings.Add(pointer);
        return pointer;
    }

    private static void CloseAll(int* a, int* b, int* c)
    {
        foreach (var descriptor in new[] { a[0], a[1], b[0], b[1], c[0], c[1] })
            if (descriptor >= 0) _ = MacProcess.Close(descriptor);
    }

    /// <summary>Lets the suspended program run.</summary>
    internal void Resume() => _ = MacProcess.Kill(Pid, MacProcess.SigContinue);

    /// <summary>Kills the program, which has not run yet, and reaps it.</summary>
    internal void Kill()
    {
        _ = MacProcess.Kill(Pid, MacProcess.SigKill);
        WaitForExit();
    }

    internal void CloseInput()
    {
        try { _input.Dispose(); }
        catch (ObjectDisposedException) { }
    }

    internal void CloseOutput()
    {
        Output.Dispose();
        Error.Dispose();
    }

    /// <summary>Waits for the program and reaps it; its exit code, or 128 plus the signal that ended it.</summary>
    internal int WaitForExit()
    {
        int status;
        while (MacProcess.WaitPid(Pid, &status, 0) < 0)
        {
            if (Marshal.GetLastPInvokeError() != MacProcess.Interrupted)
            {
                _exited = true;
                return 128 + MacProcess.SigKill;
            }
        }
        _exited = true;
        return MacPrivilegedOutcome.ExitCode(status);
    }

    public void Dispose()
    {
        CloseInput();
        CloseOutput();
        if (!_exited)
        {
            _ = MacProcess.Kill(Pid, MacProcess.SigKill);
            WaitForExit();
        }
    }
}

/// <summary>Splits a stream into lines of bytes, at most 16 KiB each, without cutting a UTF-8 sequence.</summary>
internal static class MacLineReader
{
    internal const int MaximumLineBytes = 16 * 1024;

    internal static void Read(Stream stream, Action<byte[]> deliver)
    {
        var buffer = new byte[MaximumLineBytes];
        var length = 0;
        while (true)
        {
            int read;
            try { read = stream.Read(buffer, length, buffer.Length - length); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { read = 0; }
            if (read == 0) break;
            length += read;
            var start = 0;
            for (var i = length - read; i < length; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                deliver(Line(buffer.AsSpan(start, i - start)));
                start = i + 1;
            }
            if (start > 0)
            {
                buffer.AsSpan(start, length - start).CopyTo(buffer);
                length -= start;
            }
            if (length == buffer.Length)
            {
                var cut = CompleteUtf8Prefix(buffer.AsSpan(0, length));
                deliver(buffer.AsSpan(0, cut).ToArray());
                buffer.AsSpan(cut, length - cut).CopyTo(buffer);
                length -= cut;
            }
        }
        if (length > 0) deliver(Line(buffer.AsSpan(0, length)));
    }

    private static byte[] Line(ReadOnlySpan<byte> line) => (line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line).ToArray();

    internal static int CompleteUtf8Prefix(ReadOnlySpan<byte> bytes)
    {
        var i = bytes.Length - 1;
        var continuation = 0;
        while (i >= 0 && continuation < 3 && (bytes[i] & 0xC0) == 0x80)
        {
            i--;
            continuation++;
        }
        if (i < 0) return bytes.Length;
        var lead = bytes[i];
        var needed = lead >= 0xF0 ? 3 : lead >= 0xE0 ? 2 : lead >= 0xC0 ? 1 : 0;
        return needed > continuation ? i : bytes.Length;
    }
}
