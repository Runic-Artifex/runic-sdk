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
    /// <summary>
    /// The program is missing (code 1), not signed as required (3), a .NET program that is not NativeAOT (8), without
    /// a <c>MacProgramIdentifier</c> (9), or without the hardened runtime or library validation (10).
    /// </summary>
    Untrusted,
    /// <summary>The client, or all clients together, already run as many actions as the daemon allows.</summary>
    Busy,
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
        "busy" => MacReplyStatus.Busy,
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
        MacReplyStatus.Busy => "busy",
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
        _ => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.Other, new(AuthorizationDomain, status)),
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
        MacReplyStatus.Busy => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.ResourceBusy,
            new(DaemonDomain, 11, "The privileged daemon runs as many actions as it allows; try again when one finished.")),
        MacReplyStatus.Interrupted => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.IOError,
            new(DaemonDomain, 6, "The connection to the privileged daemon broke; the program may have run.")),
        MacReplyStatus.Failed => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.Other, new(DaemonDomain, reply.Code)),
        MacReplyStatus.Cancelled => throw new ArgumentException("A cancelled run has no result.", nameof(reply)),
        _ => new PlatformResult<PrivilegedActionCompletion>.Failed(PlatformFailureCode.Other, new(DaemonDomain, 7, "The privileged daemon sent a reply this version does not understand.")),
    };

    internal static PlatformDiagnostic ProgramDiagnostic(long code) => code switch
    {
        1 => new(DaemonDomain, 1, "The declared program does not exist."),
        8 => new(DaemonDomain, 8, "The declared program is a .NET application that is not NativeAOT; its assemblies and configuration are not code-signed."),
        9 => new(DaemonDomain, 9, "The declared program needs a signature, but its action has no MacProgramIdentifier."),
        10 => new(DaemonDomain, 10, "The declared program does not run with the hardened runtime, or has an entitlement that lets it load unsigned libraries or generate code."),
        _ => new(DaemonDomain, 3, "The declared program can be changed by users other than root and is not signed with the required code signature."),
    };

    /// <summary>
    /// Whether a client's kernel code-signing status allows it: the hardened runtime, not being debugged, and no
    /// <c>get-task-allow</c>, which lets other processes of the user take control of it, unless that is allowed.
    /// </summary>
    internal static bool IsHardenedClient(long status, IReadOnlySet<string> entitlements, bool developmentSigning) =>
        (status & MacSecurity.StatusRuntime) != 0 && (status & MacSecurity.StatusDebugged) == 0
        && (developmentSigning || ((status & MacSecurity.StatusGetTaskAllow) == 0 && !entitlements.Overlaps(ClientRefusedEntitlements)));

    /// <summary>
    /// Entitlements that let a hardened process load unsigned libraries, take <c>DYLD_</c> variables from whoever
    /// starts it, or generate code (a JIT runtime needs one of the last two).
    /// </summary>
    internal static readonly ImmutableArray<string> ClientRefusedEntitlements =
    [
        "com.apple.security.cs.allow-dyld-environment-variables",
        "com.apple.security.cs.disable-library-validation",
        "com.apple.security.cs.allow-jit",
        "com.apple.security.cs.allow-unsigned-executable-memory",
    ];

    /// <summary>Entitlements a program that needs a signature must not have: it would load unsigned libraries or generate code.</summary>
    internal static readonly ImmutableArray<string> ProgramRefusedEntitlements =
    [
        "com.apple.security.cs.disable-library-validation",
        "com.apple.security.cs.allow-jit",
        "com.apple.security.cs.allow-unsigned-executable-memory",
        "com.apple.security.cs.allow-dyld-environment-variables",
    ];

    /// <summary>Whether a program that needs a signature runs with the hardened runtime and without the refused entitlements.</summary>
    internal static bool IsHardenedProgram(long status, IReadOnlySet<string> entitlements) =>
        (status & MacSecurity.StatusRuntime) != 0 && !entitlements.Overlaps(ProgramRefusedEntitlements);

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
        new("tries", 10000L), // authd's default; it reports the value on every rule
        new("comment", Comment(actionId)),
    ];

    /// <summary>Keys authd adds to a stored right that do not change how it is evaluated.</summary>
    internal static readonly ImmutableArray<string> MetadataKeys = ["version", "created", "modified", "identifier", "requirement"];
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
    /// by others (nor by its group unless sticky), the program a root-owned executable file writable only by root, and
    /// no extended ACL on any of them that lets anyone but root change them.
    /// </summary>
    internal static MacProgramCheck Check(string path, Func<string, MacProcess.FileStatus?> lstat, Func<string, bool> aclAllowsChange)
    {
        if (path.Length < 2 || path[0] != '/') return MacProgramCheck.Missing;
        var rootOwned = RootDirectory(lstat("/")) && !aclAllowsChange("/");
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
                else rootOwned &= RootDirectory(status) && !aclAllowsChange(current);
            }
            else
            {
                if (type == Link) return MacProgramCheck.NeedsSignature;
                if (type != Regular || (status.Mode & AnyExecute) == 0) return MacProgramCheck.Missing;
                rootOwned &= status.Owner == 0 && (status.Mode & (GroupWrite | OtherWrite)) == 0 && !aclAllowsChange(current);
            }
        }
        return rootOwned ? MacProgramCheck.RootOwned : MacProgramCheck.NeedsSignature;

        static bool RootDirectory(MacProcess.FileStatus? status) => status is { } s && (s.Mode & TypeMask) == Directory && s.Owner == 0
            && (s.Mode & OtherWrite) == 0 && ((s.Mode & GroupWrite) == 0 || (s.Mode & Sticky) != 0);
    }
}

/// <summary>A .NET program that is not NativeAOT loads assemblies and configuration that code signing does not cover.</summary>
internal static class MacManagedProgram
{
    /// <summary>Whether <paramref name="program"/> has the files of a .NET application host beside it.</summary>
    internal static bool IsManaged(string program, Func<string, bool> exists)
    {
        var directory = program[..program.LastIndexOf('/')];
        var name = program[(program.LastIndexOf('/') + 1)..];
        return exists($"{directory}/{name}.runtimeconfig.json") || exists($"{directory}/{name}.deps.json") || exists($"{directory}/{name}.dll");
    }

    // The 32-byte bundle marker of dotnet/runtime src/native/corehost/apphost/bundle_marker.c, which every apphost and
    // single-file host carries (the same bytes Runic.Platform.Windows.Elevation looks for), and the name of the
    // library that hosts the runtime. Stored complemented and
    // decoded on each use, so the NativeAOT daemon, which scans its own image, never carries them.
    private static readonly byte[] ComplementedBundleMarker =
    [
        0x74, 0xed, 0xfd, 0x46, 0x95, 0x9e, 0xdf, 0xc7, 0x8d, 0x84, 0x6c, 0xfd, 0xeb, 0x28, 0x5f, 0xcd,
        0xec, 0x0a, 0x46, 0x19, 0x10, 0x51, 0xcc, 0xe7, 0x11, 0xc4, 0xd2, 0x31, 0xdb, 0x4c, 0x95, 0x51,
    ];
    private static readonly byte[] ComplementedHostLibrary = [.. "libhostfxr"u8.ToArray().Select(b => (byte)~b)];

    private static byte[] Decode(byte[] complemented) => [.. complemented.Select(b => (byte)~b)];

    /// <summary>
    /// Whether the file carries the .NET host's bundle marker or names <c>libhostfxr</c>: an apphost, or a
    /// single-file bundle, whatever its name. A best-effort check of the file; the entitlements of the loaded code
    /// (a JIT runtime needs <c>allow-jit</c> or <c>allow-unsigned-executable-memory</c>) are what bind.
    /// </summary>
    internal static bool HasHostMarker(Stream file, bool hostLibrary = true)
    {
        byte[][] markers = hostLibrary ? [Decode(ComplementedBundleMarker), Decode(ComplementedHostLibrary)] : [Decode(ComplementedBundleMarker)];
        var longest = markers.Max(m => m.Length);
        var buffer = new byte[64 * 1024];
        var kept = 0;
        while (true)
        {
            var read = file.Read(buffer, kept, buffer.Length - kept);
            if (read == 0) return false;
            var length = kept + read;
            foreach (var marker in markers)
                if (buffer.AsSpan(0, length).IndexOf(marker.AsSpan()) >= 0) return true;
            // Keep the end, so a marker across two reads is found.
            kept = Math.Min(length, longest - 1);
            buffer.AsSpan(length - kept, kept).CopyTo(buffer);
        }
    }

    /// <summary>
    /// Whether the program is a .NET host by its sidecar files or by the markers in its file; true when it cannot be
    /// read. <paramref name="hostLibrary"/> also looks for <c>libhostfxr</c>, which only a host names; the daemon's
    /// check of its own image uses the bundle marker alone.
    /// </summary>
    internal static bool IsDotnetHost(string program, bool hostLibrary = true)
    {
        if (IsManaged(program, File.Exists)) return true;
        try
        {
            using var file = new FileStream(program, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            return HasHostMarker(file, hostLibrary);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }
}
