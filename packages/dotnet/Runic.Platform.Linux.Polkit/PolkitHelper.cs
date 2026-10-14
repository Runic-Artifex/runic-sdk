using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Runic.Platform.Linux.Polkit;

/// <summary>The privileged side of an action: the program pkexec runs as root.</summary>
/// <remarks>
/// <para>
/// Any local user can run the program through pkexec with any arguments, so the program checks them itself, with
/// <see cref="Match"/> and its own rules. pkexec clears the environment (<c>PATH</c> is a fixed list), but the caller
/// chooses the working directory (<c>pkexec --keep-cwd</c>), the umask, ignored and blocked signals and resource
/// limits. <see cref="Initialize"/>, which <see cref="Match"/> calls, resets them. Call tools by absolute path and
/// read no configuration from the caller's environment or working directory.
/// </para>
/// <para>
/// When the application ends while the program runs, nothing signals the program: pkexec's parent-death signal is
/// cleared when it switches to root. Standard input reaches its end instead (see <see cref="WatchStandardInput"/>),
/// and nothing reads standard output and error any more: .NET's console drops writes silently, while the tools the
/// program starts get write errors (EPIPE) or, when they handle SIGPIPE by default, are killed. A step that must
/// finish should not depend on either: redirect the output of the tools it starts, or run them in their own systemd
/// unit, as nixos-rebuild runs <c>switch-to-configuration</c>.
/// </para>
/// </remarks>
public static partial class PolkitHelper
{
    private static readonly Lazy<CancellationToken> s_standardInputClosed = new(WatchStandardInputCore);
    private static readonly Lock s_initializeLock = new();
    private static bool? s_initialized;

    /// <summary>
    /// The user ID of the user who started this process through pkexec, from <c>PKEXEC_UID</c>, when the process
    /// runs as root; null otherwise.
    /// </summary>
    public static int? GetCallerUserId()
    {
        if (!OperatingSystem.IsLinux() || GetEffectiveUserId() != 0) return null;
        var value = Environment.GetEnvironmentVariable("PKEXEC_UID");
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ? uid : null;
    }

    /// <summary>
    /// Checks that this process runs as root for a pkexec caller and resets the process state pkexec inherits from
    /// the caller. Call it first in <c>Main</c>, before starting threads or processes; <see cref="Match"/> calls it.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>The working directory becomes <c>/</c>, the umask <c>022</c> and the nice value 0.</item>
    /// <item>Signals the caller ignored are handled by default again, except <c>SIGPIPE</c>, which .NET ignores
    /// itself. The signal mask is per thread: the calling thread's becomes empty, and threads and processes it starts
    /// later inherit that, but threads the runtime started earlier keep the caller's.</item>
    /// <item>Core dumps are off (<c>RLIMIT_CORE</c> 0); the CPU time, address space, data and file size limits are
    /// lifted (<c>RLIMIT_CPU</c>, <c>RLIMIT_AS</c>, <c>RLIMIT_DATA</c>, <c>RLIMIT_FSIZE</c>); at least 1024 open
    /// files are allowed (<c>RLIMIT_NOFILE</c>); and <c>oom_score_adj</c> is 0.</item>
    /// </list>
    /// The scheduling policy, CPU affinity and <c>RLIMIT_STACK</c> stay as the caller set them.
    /// </remarks>
    /// <returns>
    /// False when the effective user is not root (as when pkexec ran with <c>--user</c> and a user other than root),
    /// <c>PKEXEC_UID</c> is missing, the process is 32-bit, or any reset fails; do not act then. Some state may
    /// already be reset when a later step fails.
    /// </returns>
    public static bool Initialize()
    {
        lock (s_initializeLock)
        {
            s_initialized ??= GetCallerUserId() is not null && Reset();
            return s_initialized.Value;
        }
    }

    private static bool Reset()
    {
        // rlim_t is 64 bits on 64-bit Linux with glibc and on every musl target; 32-bit glibc differs, so only
        // 64-bit processes are supported.
        if (!Environment.Is64BitProcess) return false;
        try { Environment.CurrentDirectory = "/"; }
        catch (IOException) { return false; }
        _ = Umask(0x12); // 022; umask cannot fail
        if (SetPriority(PrioProcess, 0, 0) != 0 || !ResetSignals()) return false;
        if (!SetLimit(RlimitCore, 0, 0)) return false;
        foreach (var resource in new[] { RlimitCpu, RlimitFsize, RlimitData, RlimitAs })
            if (!SetLimit(resource, ulong.MaxValue, ulong.MaxValue)) return false;
        if (!GetLimit(RlimitNofile, out var soft, out var hard)) return false;
        if ((soft < 1024 || hard < 1024) && !SetLimit(RlimitNofile, Math.Max(soft, 1024), Math.Max(hard, 1024))) return false;
        try { File.WriteAllText("/proc/self/oom_score_adj", "0"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
        return true;
    }

    /// <summary>
    /// Initializes the process (see <see cref="Initialize"/>) and finds the action that <paramref name="arguments"/>,
    /// this process's arguments without the program name, select: its verb is the first argument (or it is the only
    /// action and has none), and the remaining arguments are within it.
    /// </summary>
    /// <remarks>
    /// The verb alone decides. pkexec authorized the action by the program's real path and the verb, but one
    /// executable can be installed under several paths, such as behind the wrapper script <c>buildDotnetModule</c>
    /// installs, and this process cannot see which one pkexec matched. Declarations therefore give every action its
    /// own verb (<c>RUNICPA003</c>).
    /// </remarks>
    /// <param name="actions">The declared actions, usually the generated <c>PrivilegedActions.All</c>.</param>
    /// <param name="arguments">The arguments passed to this process.</param>
    /// <param name="actionArguments">The arguments after the verb, when an action matched.</param>
    /// <returns>
    /// The matching action, or null; reject the invocation when it is null. It is also null when
    /// <see cref="Initialize"/> fails.
    /// </returns>
    public static PrivilegedActionDeclaration? Match(IEnumerable<PrivilegedActionDeclaration> actions, IReadOnlyList<string> arguments, out ImmutableArray<string> actionArguments)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(arguments);
        actionArguments = default;
        return Initialize() ? MatchCore(actions, arguments, out actionArguments) : null;
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
    /// A token that is cancelled when standard input reaches its end. The application closes it to ask a running
    /// action to stop, because it cannot signal a process that runs as root, and it also ends when the application
    /// dies.
    /// </summary>
    /// <remarks>The first call starts a background thread that reads and discards standard input.</remarks>
    public static CancellationToken WatchStandardInput() => s_standardInputClosed.Value;

    private static CancellationToken WatchStandardInputCore()
    {
        var source = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                using var input = Console.OpenStandardInput();
                var buffer = new byte[256];
                while (input.Read(buffer) > 0) { }
            }
            catch (IOException) { }
            source.Cancel();
        })
        {
            IsBackground = true,
            Name = "Runic privileged input",
        };
        thread.Start();
        return source.Token;
    }

    private const int SigKill = 9, SigPipe = 13, SigStop = 19, SigSetMask = 2;
    private const int RlimitCpu = 0, RlimitFsize = 1, RlimitData = 2, RlimitCore = 4, RlimitNofile = 7, RlimitAs = 9;
    private const int PrioProcess = 0;

    private static unsafe bool ResetSignals()
    {
        // Ignored dispositions survive exec; handlers do not, so a handler here is the runtime's own.
        var action = stackalloc byte[256]; // struct sigaction, 152 bytes on glibc and musl; the handler comes first
        for (var signal = 1; signal < 32; signal++)
        {
            if (signal is SigKill or SigStop or SigPipe) continue;
            if (SigAction(signal, null, action) != 0) return false;
            if (*(nint*)action == 1 /* SIG_IGN */ && Signal(signal, 0 /* SIG_DFL */) == -1 /* SIG_ERR */) return false;
        }
        var empty = stackalloc byte[128]; // sigset_t
        new Span<byte>(empty, 128).Clear();
        return SigProcMask(SigSetMask, empty, null) == 0;
    }

    private static unsafe bool GetLimit(int resource, out ulong soft, out ulong hard)
    {
        var limit = stackalloc ulong[2];
        var ok = GetRLimit(resource, limit) == 0;
        soft = limit[0];
        hard = limit[1];
        return ok;
    }

    private static unsafe bool SetLimit(int resource, ulong soft, ulong hard)
    {
        var limit = stackalloc ulong[2];
        limit[0] = soft;
        limit[1] = hard;
        return SetRLimit(resource, limit) == 0;
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();

    [LibraryImport("libc", EntryPoint = "umask")]
    private static partial uint Umask(uint mask);

    [LibraryImport("libc", EntryPoint = "sigaction")]
    private static unsafe partial int SigAction(int signal, byte* action, byte* previous);

    [LibraryImport("libc", EntryPoint = "signal")]
    private static partial nint Signal(int signal, nint handler);

    [LibraryImport("libc", EntryPoint = "sigprocmask")]
    private static unsafe partial int SigProcMask(int how, byte* set, byte* previous);

    // struct rlimit: two 64-bit rlim_t; Reset runs only in 64-bit processes.
    [LibraryImport("libc", EntryPoint = "getrlimit")]
    private static unsafe partial int GetRLimit(int resource, ulong* limit);

    [LibraryImport("libc", EntryPoint = "setrlimit")]
    private static unsafe partial int SetRLimit(int resource, ulong* limit);

    [LibraryImport("libc", EntryPoint = "setpriority")]
    private static partial int SetPriority(int which, uint who, int priority);
}
