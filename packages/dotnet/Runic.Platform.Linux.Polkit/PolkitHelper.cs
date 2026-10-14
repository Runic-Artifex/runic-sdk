using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Runic.Platform.Linux.Polkit;

/// <summary>The privileged side of an action: the program pkexec runs as root.</summary>
/// <remarks>
/// Any local user can run the program through pkexec with any arguments, so the program checks them itself, with
/// <see cref="Match"/> and its own rules. pkexec starts it with a cleared environment (<c>PATH</c> is a fixed
/// list), in root's home directory; use absolute paths and do not read configuration from the caller's environment.
/// </remarks>
public static partial class PolkitHelper
{
    private static readonly Lazy<CancellationToken> s_standardInputClosed = new(WatchStandardInputCore);

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
    /// Finds the action that <paramref name="arguments"/>, this process's arguments without the program name,
    /// select: its verb is the first argument (or it has none), and the remaining arguments are within it.
    /// </summary>
    /// <remarks>
    /// When actions of several programs have the verb, the one whose program is this executable wins; otherwise the
    /// invocation is ambiguous and nothing matches. The program is not required to be this executable, because
    /// packaging often declares a wrapper script, such as the one <c>buildDotnetModule</c> installs, that starts it.
    /// </remarks>
    /// <param name="actions">The declared actions, usually the generated <c>PrivilegedActions.All</c>.</param>
    /// <param name="arguments">The arguments passed to this process.</param>
    /// <param name="actionArguments">The arguments after the verb, when an action matched.</param>
    /// <returns>The matching action, or null; reject the invocation when it is null.</returns>
    public static PrivilegedActionDeclaration? Match(IEnumerable<PrivilegedActionDeclaration> actions, IReadOnlyList<string> arguments, out ImmutableArray<string> actionArguments)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(arguments);
        return MatchCore(actions, arguments, Environment.ProcessPath, out actionArguments);
    }

    internal static PrivilegedActionDeclaration? MatchCore(IEnumerable<PrivilegedActionDeclaration> actions, IReadOnlyList<string> arguments, string? processPath, out ImmutableArray<string> actionArguments)
    {
        actionArguments = default;
        var candidates = actions.Where(a => a.Verb is null || (arguments.Count > 0 && arguments[0] == a.Verb)).ToList();
        // A verb is more specific than none; then this executable's own declaration settles a tie.
        if (candidates.Any(a => a.Verb is not null)) candidates.RemoveAll(a => a.Verb is null);
        if (candidates.Count > 1) candidates.RemoveAll(a => a.Program != processPath);
        if (candidates.Count != 1) return null;
        var action = candidates[0];
        var rest = arguments.Skip(action.Verb is null ? 0 : 1).ToImmutableArray();
        if (!action.AcceptsArguments(rest)) return null;
        actionArguments = rest;
        return action;
    }

    /// <summary>
    /// A token that is cancelled when standard input reaches its end. The application closes it to ask a running
    /// action to stop, because it cannot signal a process that runs as root.
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

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}
