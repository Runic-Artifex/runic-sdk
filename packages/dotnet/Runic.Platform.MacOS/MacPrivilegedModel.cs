using System.Collections.Immutable;
using System.Globalization;

namespace Runic.Platform.MacOS;

// The XPC protocol between the application and the daemon. Every message is a dictionary with "op" and "v".
//   application -> daemon  prepare                    reply: status=ready | failed(code)
//   application -> daemon  run(id, args, auth, env)   reply: status=ran(exit) | denied(code) | untrusted(code)
//                                                            | rejected | cancelled | failed(code)
//   application -> daemon  cancel                     no reply
//   daemon -> application  output(channel, data)      reply: ack; the daemon reads no further output until then
internal static class MacPrivilegedProtocol
{
    internal const long ProtocolVersion = 1;
    internal const string Op = "op", V = "v", Id = "id", Arguments = "args", Authorization = "auth", EnvironmentKey = "env",
        Channel = "channel", Data = "data", Status = "status", Exit = "exit", Code = "code";
    internal const string Prepare = "prepare", Run = "run", Cancel = "cancel", Output = "output", Ack = "ack";

    // Locale variables the application passes to the program, as pkexec keeps them on Linux.
    internal static readonly ImmutableArray<string> PassedVariables =
        ["LANG", "LANGUAGE", "LC_ALL", "LC_COLLATE", "LC_CTYPE", "LC_MESSAGES", "LC_MONETARY", "LC_NUMERIC", "LC_TIME"];

    /// <summary>The variable that carries the user ID of the application's process to the program.</summary>
    internal const string CallerUserIdVariable = "RUNIC_CALLER_UID";

    internal const string ProgramPath = "/usr/bin:/bin:/usr/sbin:/sbin";

    internal static bool IsPassedValue(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '@' or '-');

    /// <summary>The environment of the program: fixed variables, the caller's user ID and the accepted locale variables.</summary>
    internal static List<string> ProgramEnvironment(uint callerUserId, IReadOnlyDictionary<string, string> passed)
    {
        var environment = new List<string>
        {
            "PATH=" + ProgramPath,
            "HOME=/var/root",
            "USER=root",
            "LOGNAME=root",
            CallerUserIdVariable + "=" + callerUserId.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var name in PassedVariables)
            if (passed.TryGetValue(name, out var value) && IsPassedValue(value)) environment.Add(name + "=" + value);
        return environment;
    }
}

internal enum MacReplyStatus
{
    Ready,
    Ran,
    /// <summary>The daemon's check of the authorization failed.</summary>
    Denied,
    /// <summary>The program is missing or neither root-owned nor signed as required (code 1 missing, 3 untrusted).</summary>
    Untrusted,
    /// <summary>The daemon does not declare the action or the arguments, or the client is not the application.</summary>
    Rejected,
    Cancelled,
    /// <summary>The daemon failed: defining the rights (OSStatus) or starting the program (errno).</summary>
    Failed,
    /// <summary>No daemon answered: not registered, not approved, or it refused the connection.</summary>
    Unreachable,
    /// <summary>The service that answered does not satisfy the daemon's code requirement.</summary>
    UntrustedDaemon,
    /// <summary>The connection broke after the request was sent; the program may have run.</summary>
    Interrupted,
    /// <summary>A reply this version does not understand.</summary>
    Invalid,
}

internal readonly record struct MacReply(MacReplyStatus Status, long Code = 0)
{
    internal static MacReplyStatus? Parse(string? status) => status switch
    {
        "ready" => MacReplyStatus.Ready,
        "ran" => MacReplyStatus.Ran,
        "denied" => MacReplyStatus.Denied,
        "untrusted" => MacReplyStatus.Untrusted,
        "rejected" => MacReplyStatus.Rejected,
        "cancelled" => MacReplyStatus.Cancelled,
        "failed" => MacReplyStatus.Failed,
        _ => null,
    };

    internal static string Name(MacReplyStatus status) => status switch
    {
        MacReplyStatus.Ready => "ready",
        MacReplyStatus.Ran => "ran",
        MacReplyStatus.Denied => "denied",
        MacReplyStatus.Untrusted => "untrusted",
        MacReplyStatus.Rejected => "rejected",
        MacReplyStatus.Cancelled => "cancelled",
        MacReplyStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

/// <summary>The outcomes of a run, without native calls, so every platform can test them.</summary>
internal static class MacPrivilegedOutcome
{
    // Authorization Services results (Authorization.h).
    internal const int AuthorizationDenied = -60005, AuthorizationCanceled = -60006, AuthorizationInteractionNotAllowed = -60007;
    internal const string AuthorizationDomain = "AuthorizationServices", DaemonDomain = "Runic.PrivilegedAction";

    /// <summary>The result of the application's <c>AuthorizationCopyRights</c> with interaction; null when it succeeded.</summary>
    internal static PlatformResult<PrivilegedActionCompletion>? FromAuthorization(int status) => status switch
    {
        0 => null,
        AuthorizationCanceled => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.UserDismissed, new(AuthorizationDomain, status)),
        AuthorizationDenied => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied, new(AuthorizationDomain, status)),
        // No authorization dialog can be shown, as in a session without a user interface.
        AuthorizationInteractionNotAllowed => new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        _ => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new(AuthorizationDomain, status)),
    };

    /// <summary>The result of the daemon's reply to a run; <see cref="MacReplyStatus.Cancelled"/> is the caller's to throw.</summary>
    internal static PlatformResult<PrivilegedActionCompletion> FromReply(MacReply reply) => reply.Status switch
    {
        MacReplyStatus.Ran => new PlatformResult<PrivilegedActionCompletion>.Success(new PrivilegedActionCompletion(checked((int)reply.Code))),
        MacReplyStatus.Denied => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.PermissionDenied,
            new(AuthorizationDomain, reply.Code, "The privileged daemon did not accept the authorization.")),
        MacReplyStatus.Untrusted => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData, ProgramDiagnostic(reply.Code)),
        MacReplyStatus.Rejected => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData,
            new(DaemonDomain, 4, "The privileged daemon does not declare this action or these arguments.")),
        MacReplyStatus.UntrustedDaemon => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.InvalidData,
            new(DaemonDomain, 5, "The XPC service does not satisfy the privileged daemon's code requirement.")),
        MacReplyStatus.Unreachable => new PlatformResult<PrivilegedActionCompletion>.Unavailable(PlatformUnavailableReason.BackendUnavailable),
        MacReplyStatus.Interrupted => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError,
            new(DaemonDomain, 6, "The connection to the privileged daemon broke; the program may have run.")),
        MacReplyStatus.Failed => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new(DaemonDomain, reply.Code)),
        MacReplyStatus.Cancelled => throw new ArgumentException("A cancelled run has no result.", nameof(reply)),
        _ => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IoError, new(DaemonDomain, 7, "The privileged daemon sent a reply this version does not understand.")),
    };

    internal static PlatformDiagnostic ProgramDiagnostic(long code) => code switch
    {
        1 => new(DaemonDomain, 1, "The declared program does not exist."),
        _ => new(DaemonDomain, 3, "The declared program can be changed by users other than root and is not signed with the required code signature."),
    };

    // SMAppServiceStatus.
    internal static MacPrivilegedDaemonStatus FromServiceStatus(long status) => status switch
    {
        0 => MacPrivilegedDaemonStatus.NotRegistered,
        1 => MacPrivilegedDaemonStatus.Enabled,
        2 => MacPrivilegedDaemonStatus.RequiresApproval,
        3 => MacPrivilegedDaemonStatus.NotFound,
        _ => MacPrivilegedDaemonStatus.Unavailable,
    };

    /// <summary>The exit code of a <c>waitpid</c> status: the code, or 128 plus the signal.</summary>
    internal static int ExitCode(int status) => (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F);
}

/// <summary>The Authorization Services rule of every action's right.</summary>
/// <remarks>
/// <para>
/// Not shared: authd stores the administrator's credential in the application's authorization only, not in the
/// session, so it satisfies no other right and no other process. The application creates a new authorization for each
/// run and destroys its rights when the run ends.
/// </para>
/// <para>
/// A 30-second credential, like macOS's own <c>authenticate-admin-nonshared</c> rule, because a timeout of 0 breaks the
/// daemon's check. authd compares the credential's age with the timeout when it evaluates a right again
/// (<c>engine->now - credential_get_creation_time(cred) > rule_get_timeout(rule)</c> in authd's engine): with 0, the
/// credential is already too old when the daemon checks it without interaction, and the check fails. Only
/// authorizations in the private least-privileged mode skip that comparison.
/// </para>
/// </remarks>
internal static class MacAuthorizationRule
{
    internal const long TimeoutSeconds = 30;

    internal static string Comment(string actionId) =>
        $"Runic privileged action {actionId}: an administrator authorizes each run; the credential is not shared.";

    /// <summary>The rule's keys and values: strings, booleans and integers.</summary>
    internal static ImmutableArray<KeyValuePair<string, object>> Definition(string actionId) =>
    [
        new("class", "user"),
        new("group", "admin"),
        new("shared", false),
        new("timeout", TimeoutSeconds),
        new("authenticate-user", true),
        new("allow-root", false),
        new("session-owner", false),
        new("comment", Comment(actionId)),
    ];
}

internal enum MacProgramCheck
{
    /// <summary>Every path component is a real directory or file that only root can change.</summary>
    RootOwned,
    /// <summary>The program exists, but its path is not root's alone; it runs only with the required code signature.</summary>
    NeedsSignature,
    Missing,
}

internal static class MacProgramTrust
{
    private const ushort TypeMask = 0xF000, Directory = 0x4000, Regular = 0x8000, Link = 0xA000;
    private const ushort GroupWrite = 0x10, OtherWrite = 0x02, Sticky = 0x200, AnyExecute = 0x49;

    /// <summary>
    /// Checks <paramref name="path"/> without following symbolic links: every directory owned by root and not writable
    /// by others (nor by its group unless sticky), and the program a root-owned executable file writable only by root.
    /// </summary>
    internal static MacProgramCheck Check(string path, Func<string, MacProcess.FileStatus?> lstat)
    {
        if (path.Length < 2 || path[0] != '/') return MacProgramCheck.Missing;
        var rootOwned = RootDirectory(lstat("/"));
        var segments = path[1..].Split('/');
        var current = "";
        for (var i = 0; i < segments.Length; i++)
        {
            current += "/" + segments[i];
            if (lstat(current) is not { } status) return MacProgramCheck.Missing;
            var type = (ushort)(status.Mode & TypeMask);
            if (i < segments.Length - 1)
            {
                // A symbolic link in the middle, such as /var or /tmp: the path is not real, so only a signature proves the program.
                if (type == Link) rootOwned = false;
                else if (type != Directory) return MacProgramCheck.Missing;
                else rootOwned &= RootDirectory(status);
            }
            else
            {
                if (type == Link) return MacProgramCheck.NeedsSignature;
                if (type != Regular || (status.Mode & AnyExecute) == 0) return MacProgramCheck.Missing;
                rootOwned &= status.Owner == 0 && (status.Mode & (GroupWrite | OtherWrite)) == 0;
            }
        }
        return rootOwned ? MacProgramCheck.RootOwned : MacProgramCheck.NeedsSignature;

        static bool RootDirectory(MacProcess.FileStatus? status) => status is { } s && (s.Mode & TypeMask) == Directory && s.Owner == 0
            && (s.Mode & OtherWrite) == 0 && ((s.Mode & GroupWrite) == 0 || (s.Mode & Sticky) != 0);
    }
}
