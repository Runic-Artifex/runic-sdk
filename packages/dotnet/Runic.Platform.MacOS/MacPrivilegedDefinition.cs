using System.Collections.Immutable;

namespace Runic.Platform.MacOS;

/// <summary>
/// The launch daemon that runs an application's privileged actions on macOS, and the Authorization Services right of
/// each action. The build generates it from <c>RunicPrivilegedAction</c> items as <c>PrivilegedActions.MacOSDaemon</c>
/// when <c>RunicMacPrivilegedDaemonLabel</c> is set, together with the daemon's launchd property list.
/// </summary>
/// <remarks>
/// The application and the daemon use the same definition: the application to register the daemon and to check that
/// the XPC service it reaches is the daemon, the daemon to check that its XPC client is the application and that a
/// program it runs as root is signed by the same team.
/// </remarks>
public sealed class MacPrivilegedDaemonDefinition
{
    private readonly string _applicationRequirement;
    private readonly string _daemonRequirement;
    private readonly string _programRequirement;

    /// <summary>Defines the daemon.</summary>
    /// <param name="label">The launchd label, which is also the XPC Mach service name and the property list's name.</param>
    /// <param name="teamIdentifier">The Team ID that signs the application, the daemon and bundled programs.</param>
    /// <param name="applicationIdentifier">The application's code-signing identifier, normally its bundle identifier.</param>
    /// <param name="daemonIdentifier">The daemon executable's code-signing identifier.</param>
    /// <param name="rights">The authorization right of every action the daemon runs.</param>
    /// <exception cref="ArgumentException">A name is not a valid identifier, or two rights name the same action.</exception>
    public MacPrivilegedDaemonDefinition(string label, string teamIdentifier, string applicationIdentifier, string daemonIdentifier, IEnumerable<MacPrivilegedRight> rights)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(teamIdentifier);
        ArgumentNullException.ThrowIfNull(applicationIdentifier);
        ArgumentNullException.ThrowIfNull(daemonIdentifier);
        ArgumentNullException.ThrowIfNull(rights);
        if (!MacPrivilegedNames.IsIdentifier(label)) throw new ArgumentException("The label is a reverse-DNS name of letters, digits, '-' and '.'.", nameof(label));
        if (!MacPrivilegedNames.IsTeamIdentifier(teamIdentifier)) throw new ArgumentException("A Team ID is ten uppercase letters and digits.", nameof(teamIdentifier));
        if (!MacPrivilegedNames.IsIdentifier(applicationIdentifier)) throw new ArgumentException("The application identifier is a reverse-DNS name.", nameof(applicationIdentifier));
        if (!MacPrivilegedNames.IsIdentifier(daemonIdentifier)) throw new ArgumentException("The daemon identifier is a reverse-DNS name.", nameof(daemonIdentifier));
        var all = rights.ToImmutableArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var right in all)
        {
            ArgumentNullException.ThrowIfNull(right, nameof(rights));
            if (!seen.Add(right.ActionId)) throw new ArgumentException($"Action '{right.ActionId}' has two rights.", nameof(rights));
        }
        Label = label;
        TeamIdentifier = teamIdentifier;
        ApplicationIdentifier = applicationIdentifier;
        DaemonIdentifier = daemonIdentifier;
        Rights = all;
        _applicationRequirement = MacPrivilegedNames.Requirement(teamIdentifier, applicationIdentifier);
        _daemonRequirement = MacPrivilegedNames.Requirement(teamIdentifier, daemonIdentifier);
        _programRequirement = MacPrivilegedNames.Requirement(teamIdentifier, null);
    }

    /// <summary>The launchd label and XPC Mach service name.</summary>
    public string Label { get; }

    /// <summary>The name of the daemon's property list in the bundle's <c>Contents/Library/LaunchDaemons</c>.</summary>
    public string PlistName => Label + ".plist";

    /// <summary>The Team ID that signs the application, the daemon and the bundled programs.</summary>
    public string TeamIdentifier { get; }

    /// <summary>The application's code-signing identifier.</summary>
    public string ApplicationIdentifier { get; }

    /// <summary>The daemon's code-signing identifier.</summary>
    public string DaemonIdentifier { get; }

    /// <summary>The right of every action, in declaration order.</summary>
    public ImmutableArray<MacPrivilegedRight> Rights { get; }

    /// <summary>
    /// The code requirement the daemon checks on every XPC client: by default an Apple-issued certificate for
    /// <see cref="TeamIdentifier"/> and the identifier <see cref="ApplicationIdentifier"/>.
    /// </summary>
    public string ApplicationRequirement { get => _applicationRequirement; init => _applicationRequirement = CheckRequirement(value); }

    /// <summary>The code requirement the application checks on the daemon; by default the Team ID and <see cref="DaemonIdentifier"/>.</summary>
    public string DaemonRequirement { get => _daemonRequirement; init => _daemonRequirement = CheckRequirement(value); }

    /// <summary>
    /// The code requirement a declared program must satisfy when its path can be changed by someone other than root:
    /// by default an Apple-issued certificate for <see cref="TeamIdentifier"/>. Programs whose every path component
    /// only root can change, such as those in <c>/usr/bin</c>, need no signature.
    /// </summary>
    public string ProgramRequirement { get => _programRequirement; init => _programRequirement = CheckRequirement(value); }

    /// <summary>The right of <paramref name="actionId"/>, or null.</summary>
    public MacPrivilegedRight? FindRight(string actionId)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        foreach (var right in Rights)
            if (right.ActionId == actionId) return right;
        return null;
    }

    private static string CheckRequirement(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length is 0 or > 4096 || value.Contains('\0')) throw new ArgumentException("A code requirement is a non-empty string without NUL characters.", nameof(value));
        return value;
    }
}

/// <summary>The Authorization Services right of one privileged action on macOS.</summary>
/// <remarks>
/// The daemon defines the right, named after the action ID, as an administrator rule that is not shared and whose
/// credential lasts 30 seconds, so the authorization covers only this action and only this run.
/// </remarks>
public sealed record MacPrivilegedRight
{
    private readonly string? _program;

    /// <summary>Defines the right.</summary>
    /// <param name="actionId">The action ID, which is also the right's name.</param>
    /// <param name="message">The text of the authorization dialog.</param>
    /// <exception cref="ArgumentException">The ID is not a valid action ID or is in a namespace macOS reserves, or the message is empty.</exception>
    public MacPrivilegedRight(string actionId, string message)
    {
        ArgumentNullException.ThrowIfNull(actionId);
        ArgumentNullException.ThrowIfNull(message);
        if (!PrivilegedActionDeclaration.IsValidId(actionId)) throw new ArgumentException("The action ID is not valid.", nameof(actionId));
        if (MacPrivilegedNames.IsReservedRight(actionId)) throw new ArgumentException($"'{actionId}' is in a namespace macOS reserves for its own rights.", nameof(actionId));
        if (message.Length is 0 or > 1024 || message.Contains('\0')) throw new ArgumentException("The message is between 1 and 1024 characters without NUL characters.", nameof(message));
        ActionId = actionId;
        Message = message;
    }

    /// <summary>The action ID and right name.</summary>
    public string ActionId { get; }

    /// <summary>The text of the authorization dialog.</summary>
    public string Message { get; }

    /// <summary>
    /// Where the daemon finds the program on macOS, from the item's <c>MacProgram</c> metadata: a path inside the
    /// application bundle such as <c>Contents/MacOS/helper</c>, or an absolute path. Null runs the declaration's
    /// <see cref="PrivilegedActionDeclaration.Program"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The path is empty, or has empty, <c>.</c> or <c>..</c> segments.</exception>
    public string? Program
    {
        get => _program;
        init
        {
            if (value is not null && !MacPrivilegedNames.IsProgramPath(value))
                throw new ArgumentException("A program is a path inside the bundle or an absolute path, without '.' or '..' segments.", nameof(value));
            _program = value;
        }
    }
}

/// <summary>The state of the privileged daemon's registration with <c>SMAppService</c>.</summary>
public enum MacPrivilegedDaemonStatus
{
    /// <summary>The daemon is not registered; <see cref="MacPrivilegedActions.RegisterDaemon"/> registers it.</summary>
    NotRegistered,
    /// <summary>The daemon is registered and approved; actions can run.</summary>
    Enabled,
    /// <summary>The daemon is registered but waits for the user's approval in System Settings, General, Login Items.</summary>
    RequiresApproval,
    /// <summary>macOS cannot find the daemon's property list in the application bundle.</summary>
    NotFound,
    /// <summary>
    /// The process cannot register a daemon: it is not macOS 14 or later, not an application bundle, or not signed
    /// with the definition's requirement.
    /// </summary>
    Unavailable,
}

internal static class MacPrivilegedNames
{
    // Authorization rights macOS and its components define; an application must not redefine them.
    private static readonly string[] ReservedRightPrefixes = ["system.", "config.", "com.apple.", "authenticate", "admin", "allow", "default"];

    internal static bool IsReservedRight(string name)
    {
        foreach (var prefix in ReservedRightPrefixes)
            if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>A reverse-DNS identifier: ASCII letters, digits and hyphens in two or more dot-separated segments.</summary>
    internal static bool IsIdentifier(string value)
    {
        if (value.Length is 0 or > 255) return false;
        var segments = value.Split('.');
        if (segments.Length < 2) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;
            foreach (var c in segment)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }
        return true;
    }

    internal static bool IsTeamIdentifier(string value) =>
        value.Length == 10 && value.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c));

    /// <summary>A relative path inside the bundle, or an absolute path, without empty, '.' or '..' segments.</summary>
    internal static bool IsProgramPath(string value)
    {
        if (value.Length is 0 or > 1024 || value.Contains('\0')) return false;
        foreach (var segment in (value[0] == '/' ? value[1..] : value).Split('/'))
            if (segment is "" or "." or "..") return false;
        return true;
    }

    // Identifiers and Team IDs are checked above, so they need no quoting beyond the quotes.
    internal static string Requirement(string team, string? identifier) => identifier is null
        ? $"anchor apple generic and certificate leaf[subject.OU] = \"{team}\""
        : $"anchor apple generic and identifier \"{identifier}\" and certificate leaf[subject.OU] = \"{team}\"";
}
