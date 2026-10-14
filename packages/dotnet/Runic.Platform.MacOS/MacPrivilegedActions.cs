using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using static Runic.Platform.MacOS.MacDesktopNative;
using static Runic.Platform.MacOS.MacPrivilegedProtocol;

namespace Runic.Platform.MacOS;

/// <summary>
/// Runs declared privileged actions on macOS through the application's launch daemon, authorized with Authorization
/// Services. See <see cref="MacPrivilegedDaemon"/> for the daemon's side.
/// </summary>
/// <remarks>
/// <para>
/// Each run asks for the action's right with <c>AuthorizationCopyRights</c>, which shows the authorization dialog, and
/// sends the authorization's external form and the argument vector to the daemon over XPC. The daemon checks the right
/// again without interaction and runs the program as root. The application checks that the service it reaches satisfies
/// <see cref="MacPrivilegedDaemonDefinition.DaemonRequirement"/>.
/// </para>
/// <para>
/// Actions are unavailable until the daemon is registered (<see cref="RegisterDaemon"/>) and the user approved it in
/// System Settings, General, Login Items; on macOS before 14; and in a process that is not an application bundle
/// signed as <see cref="MacPrivilegedDaemonDefinition.ApplicationRequirement"/>.
/// </para>
/// </remarks>
public static class MacPrivilegedActions
{
    /// <summary>Creates the provider for <paramref name="actions"/>, usually the generated <c>PrivilegedActions.All</c>.</summary>
    /// <param name="actions">The declared actions.</param>
    /// <param name="daemon">The daemon's definition, usually the generated <c>PrivilegedActions.MacOSDaemon</c>.</param>
    /// <remarks>Off macOS the result reports every request unavailable with <see cref="PlatformUnavailableReason.BackendUnavailable"/>.</remarks>
    /// <exception cref="ArgumentException">An action is declared twice or has no right in the definition.</exception>
    public static IDesktopPrivilegedActions Create(IEnumerable<PrivilegedActionDeclaration> actions, MacPrivilegedDaemonDefinition daemon)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(daemon);
        var provider = new MacPrivilegedProvider(actions, daemon, MacPrivilegedSystemHost.Instance);
        return OperatingSystem.IsMacOS() ? provider : DesktopPrivilegedActions.Unavailable(PlatformUnavailableReason.BackendUnavailable);
    }

    /// <summary>The daemon's registration state.</summary>
    public static MacPrivilegedDaemonStatus GetDaemonStatus(MacPrivilegedDaemonDefinition daemon)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        return MacPrivilegedSystemHost.GetStatus(daemon);
    }

    /// <summary>
    /// Registers the daemon with <c>SMAppService.daemon(plistName:)</c>. macOS then asks the user to approve it in
    /// System Settings, General, Login Items, and the result is usually <see cref="MacPrivilegedDaemonStatus.RequiresApproval"/>.
    /// </summary>
    /// <returns>The status after registering, or the failure with ServiceManagement's error domain and code.</returns>
    public static PlatformResult<MacPrivilegedDaemonStatus> RegisterDaemon(MacPrivilegedDaemonDefinition daemon)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        return MacPrivilegedSystemHost.Register(daemon, register: true);
    }

    /// <summary>Unregisters the daemon; launchd stops it.</summary>
    public static PlatformResult<MacPrivilegedDaemonStatus> UnregisterDaemon(MacPrivilegedDaemonDefinition daemon)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        return MacPrivilegedSystemHost.Register(daemon, register: false);
    }

    /// <summary>Opens System Settings at General, Login Items, where the user approves the daemon.</summary>
    /// <returns>False when this is not macOS 13 or later.</returns>
    public static bool OpenLoginItemsSettings()
    {
        if (!MacLoginStartup.IsServiceAvailable) return false;
        using var pool = new Pool();
        Send(Class("SMAppService"), Sel("openSystemSettingsLoginItems"));
        return true;
    }
}

/// <summary>An authorization the application obtained; disposing it destroys its rights.</summary>
internal sealed class MacAuthorizationGrant(int status, byte[]? externalForm, Action? destroy) : IDisposable
{
    private Action? _destroy = destroy;
    internal int Status { get; } = status;
    internal byte[]? ExternalForm { get; } = externalForm;
    public void Dispose() => Interlocked.Exchange(ref _destroy, null)?.Invoke();
}

/// <summary>A connection to the daemon for one run.</summary>
internal interface IMacPrivilegedChannel : IDisposable
{
    Task<MacReply> PrepareAsync();

    /// <summary>Sends the run; <paramref name="output"/> is called for each line, one at a time, before the daemon reads further.</summary>
    Task<MacReply> RunAsync(MacRunRequest request, Action<PrivilegedOutputChannel, byte[]> output);

    /// <summary>Asks the daemon to cancel the run: before the program starts it never starts, afterwards its input closes.</summary>
    void RequestCancel();
}

/// <summary>The native services the provider uses, replaced in tests.</summary>
internal abstract class MacPrivilegedHost
{
    /// <summary>Null when actions can run; otherwise why not.</summary>
    internal abstract PlatformUnavailableReason? CheckAvailable(MacPrivilegedDaemonDefinition daemon);
    internal abstract IMacPrivilegedChannel Connect(MacPrivilegedDaemonDefinition daemon);

    /// <summary>Asks for the right with interaction; blocks until the user answers.</summary>
    internal abstract MacAuthorizationGrant Authorize(string right);

    internal virtual IReadOnlyDictionary<string, string> PassedEnvironment()
    {
        var passed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in PassedVariables)
            if (Environment.GetEnvironmentVariable(name) is { } value && IsPassedValue(value)) passed[name] = value;
        return passed;
    }
}

internal sealed class MacPrivilegedProvider : IDesktopPrivilegedActions
{
    // launchd starts the daemon on demand; one that does not answer within this time is treated as absent.
    private static readonly TimeSpan PrepareTimeout = TimeSpan.FromSeconds(30);
    private readonly FrozenDictionary<string, PrivilegedActionDeclaration> _actions;
    private readonly MacPrivilegedDaemonDefinition _daemon;
    private readonly MacPrivilegedHost _host;

    internal MacPrivilegedProvider(IEnumerable<PrivilegedActionDeclaration> actions, MacPrivilegedDaemonDefinition daemon, MacPrivilegedHost host)
    {
        var byId = new Dictionary<string, PrivilegedActionDeclaration>(StringComparer.Ordinal);
        foreach (var action in actions)
        {
            ArgumentNullException.ThrowIfNull(action, nameof(actions));
            if (!byId.TryAdd(action.Id, action)) throw new ArgumentException($"Action '{action.Id}' is declared twice.", nameof(actions));
            if (daemon.FindRight(action.Id) is null) throw new ArgumentException($"Action '{action.Id}' has no right in the daemon definition.", nameof(daemon));
        }
        _actions = byId.ToFrozenDictionary(StringComparer.Ordinal);
        _daemon = daemon;
        _host = host;
    }

    public async ValueTask<PlatformResult<PrivilegedActionCompletion>> RunAsync(PrivilegedActionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Action);
        if (request.Arguments.IsDefault) throw new ArgumentException("Arguments must be initialized.", nameof(request));
        if (!_actions.TryGetValue(request.Action.Id, out var action) || !SameDeclaration(action, request.Action))
            throw new ArgumentException($"'{request.Action.Id}' is not an action this provider declares.", nameof(request));
        _ = action.CreateArgumentVector(request.Arguments);
        cancellationToken.ThrowIfCancellationRequested();

        if (_host.CheckAvailable(_daemon) is { } reason) return new PlatformResult<PrivilegedActionCompletion>.Unavailable(reason);
        using var channel = _host.Connect(_daemon);
        // The daemon defines the rights before the application asks for one, so the dialog shows the action's message
        // and the right is the daemon's rule rather than the database's default.
        MacReply prepared;
        try { prepared = await channel.PrepareAsync().WaitAsync(PrepareTimeout, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { prepared = new MacReply(MacReplyStatus.Unreachable); }
        if (prepared.Status != MacReplyStatus.Ready) return MacPrivilegedOutcome.FromReply(prepared);

        // AuthorizationCopyRights blocks while the dialog is open and cannot be withdrawn. A cancelled run stops
        // waiting for it, and its rights are destroyed as soon as the user answers.
        var authorizing = Task.Factory.StartNew(() => _host.Authorize(action.Id), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        MacAuthorizationGrant grant;
        try { grant = await authorizing.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = authorizing.ContinueWith(static t => t.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            throw;
        }
        using (grant)
        {
            if (MacPrivilegedOutcome.FromAuthorization(grant.Status) is { } refused) return refused;
            cancellationToken.ThrowIfCancellationRequested();

            var output = new OutputSink(request.Output);
            var run = new MacRunRequest(action.Id, request.Arguments, grant.ExternalForm!, _host.PassedEnvironment());
            MacReply reply;
            using (cancellationToken.Register(channel.RequestCancel))
                reply = await channel.RunAsync(run, output.Deliver).ConfigureAwait(false);
            output.Close();
            output.ThrowIfFailed();
            if (reply.Status == MacReplyStatus.Cancelled) throw new OperationCanceledException(cancellationToken);
            return MacPrivilegedOutcome.FromReply(reply);
        }
    }

    private static bool SameDeclaration(PrivilegedActionDeclaration a, PrivilegedActionDeclaration b) =>
        ReferenceEquals(a, b) || (a.Program == b.Program && a.Verb == b.Verb && a.MaximumArguments == b.MaximumArguments && a.ArgumentPattern == b.ArgumentPattern);

    /// <summary>Decodes lines, delivers them one at a time and keeps the first exception a callback threw.</summary>
    private sealed class OutputSink(Action<PrivilegedActionOutput>? callback)
    {
        private readonly Lock _lock = new();
        private Exception? _failure;
        private bool _closed;

        public void Close()
        {
            lock (_lock) _closed = true;
        }

        public void Deliver(PrivilegedOutputChannel channel, byte[] line)
        {
            if (callback is null) return;
            lock (_lock)
            {
                if (_failure is not null || _closed) return;
                try { callback(new PrivilegedActionOutput(channel, Encoding.UTF8.GetString(line))); }
                catch (Exception e) { _failure = e; }
            }
        }

        public void ThrowIfFailed()
        {
            if (_failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        }
    }
}

/// <summary>An XPC connection to the daemon, through the C XPC API.</summary>
internal sealed class MacXpcChannel : IMacPrivilegedChannel
{
    private readonly nint _connection;
    private readonly nint _queue;
    private readonly bool _requirementSet;
    private Action<PrivilegedOutputChannel, byte[]>? _output;
    private TaskCompletionSource<MacReply>? _pending;
    private volatile bool _peerRejected;
    private int _disposed;

    private MacXpcChannel(Func<nint, nint> connect, string daemonRequirement)
    {
        _queue = MacXpc.CreateQueue("eu.runic.privileged.client", 0);
        _connection = connect(_queue);
        // libxpc checks the daemon's code signature against its audit token; a mismatch fails every message.
        _requirementSet = _connection != 0 && MacXpc.SetPeerCodeSigningRequirement(_connection, daemonRequirement) == 0;
        if (_connection == 0) return;
        var handler = MacDesktopBlock.Create(OnEvent);
        try { MacXpc.SetEventHandler(_connection, handler); }
        finally { MacDesktopBlock.Release(handler); }
        MacXpc.Resume(_connection);
    }

    /// <summary>Connects to the daemon's Mach service in the system domain, as <c>NSXPCConnection</c>'s <c>.privileged</c> option does.</summary>
    internal static MacXpcChannel ConnectService(string serviceName, string daemonRequirement) =>
        new(queue => MacXpc.CreateMachService(serviceName, queue, MacXpc.MachServicePrivileged), daemonRequirement);

    /// <summary>Connects to an anonymous listener's endpoint, for tests that run the daemon in the process.</summary>
    internal static MacXpcChannel ConnectEndpoint(nint endpoint, string daemonRequirement) => new(queue =>
    {
        var connection = MacXpc.CreateFromEndpoint(endpoint);
        if (connection != 0) MacXpc.SetTargetQueue(connection, queue);
        return connection;
    }, daemonRequirement);

    public Task<MacReply> PrepareAsync()
    {
        var message = Message(Prepare);
        return SendAsync(message);
    }

    public Task<MacReply> RunAsync(MacRunRequest request, Action<PrivilegedOutputChannel, byte[]> output)
    {
        _output = output;
        var message = Message(MacPrivilegedProtocol.Run);
        MacXpc.SetString(message, Id, request.ActionId);
        var arguments = MacXpc.CreateStringArray(request.Arguments);
        MacXpc.SetValue(message, Arguments, arguments);
        MacXpc.Release(arguments);
        MacXpc.SetData(message, Authorization, request.Authorization);
        var environment = MacXpc.CreateDictionary(0, 0, 0);
        foreach (var (name, value) in request.Environment) MacXpc.SetString(environment, name, value);
        MacXpc.SetValue(message, EnvironmentKey, environment);
        MacXpc.Release(environment);
        return SendAsync(message);
    }

    public void RequestCancel()
    {
        if (_connection == 0 || Volatile.Read(ref _disposed) != 0) return;
        var message = Message(MacPrivilegedProtocol.Cancel);
        MacXpc.Send(_connection, message);
        MacXpc.Release(message);
    }

    private static nint Message(string op)
    {
        var message = MacXpc.CreateDictionary(0, 0, 0);
        MacXpc.SetInt64(message, V, ProtocolVersion);
        MacXpc.SetString(message, Op, op);
        return message;
    }

    private Task<MacReply> SendAsync(nint message)
    {
        if (_connection == 0 || !_requirementSet)
        {
            MacXpc.Release(message);
            return Task.FromResult(new MacReply(_connection == 0 ? MacReplyStatus.Unreachable : MacReplyStatus.UntrustedDaemon));
        }
        var completion = new TaskCompletionSource<MacReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        var handler = MacDesktopBlock.Create(reply =>
        {
            var parsed = ParseReply(reply);
            // libxpc may report a daemon that fails the requirement to the event handler and only invalidate the reply.
            completion.TrySetResult(_peerRejected && parsed.Status == MacReplyStatus.Unreachable ? new(MacReplyStatus.UntrustedDaemon) : parsed);
        });
        try { MacXpc.SendWithReply(_connection, message, _queue, handler); }
        finally
        {
            MacDesktopBlock.Release(handler);
            MacXpc.Release(message);
        }
        return completion.Task;
    }

    internal static MacReply ParseReply(nint reply)
    {
        if (reply == MacXpc.ErrorPeerCodeSigningRequirement && reply != 0) return new(MacReplyStatus.UntrustedDaemon);
        if (reply == MacXpc.ErrorConnectionInterrupted) return new(MacReplyStatus.Interrupted);
        if (!MacXpc.IsDictionary(reply)) return new(MacReplyStatus.Unreachable);
        if (MacXpc.GetInt64(reply, V) != ProtocolVersion || MacReply.Parse(MacXpc.GetString(reply, Status)) is not { } status) return new(MacReplyStatus.Invalid);
        return new(status, MacXpc.GetInt64(reply, status == MacReplyStatus.Ran ? Exit : Code));
    }

    // Messages the daemon sends on this connection: output lines, each acknowledged after the callback returned.
    private void OnEvent(nint message)
    {
        if (message != 0 && message == MacXpc.ErrorPeerCodeSigningRequirement)
        {
            _peerRejected = true;
            _pending?.TrySetResult(new(MacReplyStatus.UntrustedDaemon));
            return;
        }
        if (!MacXpc.IsDictionary(message) || MacXpc.GetString(message, Op) != Output) return;
        var channel = MacXpc.GetInt64(message, Channel) == 2 ? PrivilegedOutputChannel.StandardError : PrivilegedOutputChannel.StandardOutput;
        if (MacXpc.GetData(message, Data) is { } line) _output?.Invoke(channel, line);
        var reply = MacXpc.CreateReply(message);
        if (reply == 0) return;
        MacXpc.SetInt64(reply, V, ProtocolVersion);
        MacXpc.SetString(reply, Op, Ack);
        MacXpc.Send(_connection, reply);
        MacXpc.Release(reply);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_connection != 0)
        {
            MacXpc.Cancel(_connection);
            MacXpc.Release(_connection);
        }
        MacXpc.ReleaseQueue(_queue);
    }
}

/// <summary>ServiceManagement, Authorization Services and XPC for a bundled, signed application.</summary>
internal sealed unsafe class MacPrivilegedSystemHost : MacPrivilegedHost
{
    internal static readonly MacPrivilegedSystemHost Instance = new();

    internal override PlatformUnavailableReason? CheckAvailable(MacPrivilegedDaemonDefinition daemon) =>
        GetStatus(daemon) == MacPrivilegedDaemonStatus.Enabled ? null : PlatformUnavailableReason.BackendUnavailable;

    internal static MacPrivilegedDaemonStatus GetStatus(MacPrivilegedDaemonDefinition daemon)
    {
        if (Service(daemon) is not { } service) return MacPrivilegedDaemonStatus.Unavailable;
        using var pool = new Pool();
        return MacPrivilegedOutcome.FromServiceStatus(Send(service, Sel("status")));
    }

    internal static PlatformResult<MacPrivilegedDaemonStatus> Register(MacPrivilegedDaemonDefinition daemon, bool register)
    {
        if (Service(daemon) is not { } service) return new PlatformResult<MacPrivilegedDaemonStatus>.Unavailable(PlatformUnavailableReason.BackendUnavailable);
        using var pool = new Pool();
        if (BoolError(service, Sel(register ? "registerAndReturnError:" : "unregisterAndReturnError:"), out var error) == 0 && error != 0)
        {
            var diagnostic = ErrorDiagnostic(error);
            // Already registered, or not registered when unregistering: the status says what it is.
            if (!(diagnostic.Code is MacLoginStartupModel.ErrorAlreadyRegistered or MacLoginStartupModel.ErrorJobNotFound))
                return new PlatformResult<MacPrivilegedDaemonStatus>.Failed(
                    diagnostic.Code == MacLoginStartupModel.ErrorInvalidSignature ? PlatformFailureCode.InvalidData : PlatformFailureCode.IoError, diagnostic);
        }
        return new PlatformResult<MacPrivilegedDaemonStatus>.Success(MacPrivilegedOutcome.FromServiceStatus(Send(service, Sel("status"))));
    }

    // SMAppService.daemon(plistName:) for a bundled application, signed as the definition requires, on macOS 14 or later.
    private static nint? Service(MacPrivilegedDaemonDefinition daemon)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14) || !MacLoginStartup.IsBundled || !MacSecurity.SelfSatisfies(daemon.ApplicationRequirement)) return null;
        return Arg(Class("SMAppService"), Sel("daemonServiceWithPlistName:"), MacDesktopNative.String(daemon.PlistName)) is var service and not 0 ? service : null;
    }

    internal override IMacPrivilegedChannel Connect(MacPrivilegedDaemonDefinition daemon) => MacXpcChannel.ConnectService(daemon.Label, daemon.DaemonRequirement);

    internal override MacAuthorizationGrant Authorize(string right) => AuthorizeWithFlags(right, MacSecurity.FlagInteractionAllowed | MacSecurity.FlagExtendRights);

    /// <summary>A new authorization for <paramref name="right"/> and its external form.</summary>
    internal static MacAuthorizationGrant AuthorizeWithFlags(string right, int flags)
    {
        var status = MacSecurity.AuthorizationCreate(null, null, 0, out var authorization);
        if (status != 0) return new MacAuthorizationGrant(status, null, null);
        // Destroying the rights removes the credential from authd, so the external form grants nothing afterwards.
        var grant = () => { _ = MacSecurity.AuthorizationFree(authorization, MacSecurity.FlagDestroyRights); };
        var name = Marshal.StringToCoTaskMemUTF8(right);
        try
        {
            var item = new MacSecurity.AuthorizationItem { Name = name };
            var rights = new MacSecurity.AuthorizationItemSet { Count = 1, Items = &item };
            status = MacSecurity.AuthorizationCopyRights(authorization, &rights, null, flags, 0);
        }
        finally { Marshal.FreeCoTaskMem(name); }
        if (status != 0) return new MacAuthorizationGrant(status, null, grant);
        var form = new byte[MacSecurity.ExternalFormLength];
        fixed (byte* bytes = form) status = MacSecurity.AuthorizationMakeExternalForm(authorization, bytes);
        return new MacAuthorizationGrant(status, status == 0 ? form : null, grant);
    }
}
