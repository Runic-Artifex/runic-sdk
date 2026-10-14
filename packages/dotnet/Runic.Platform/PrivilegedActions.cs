using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Runic.Platform;

/// <summary>
/// A privileged step the application declared at build time: a fixed program, run as the administrator with the
/// platform's authorization, and the arguments it accepts.
/// </summary>
/// <remarks>
/// Declare actions with <c>RunicPrivilegedAction</c> items, which generate these declarations and the platform
/// artifacts (a Polkit policy on Linux). The privileged program must check its own arguments again, with the
/// provider's helper (on Linux <c>PolkitHelper.Match</c>, which also resets the process state the caller chose)
/// and its own rules: any local user can start it through the platform's tool with any arguments.
/// </remarks>
public sealed class PrivilegedActionDeclaration
{
    /// <summary>The most arguments a declaration can accept after its verb.</summary>
    public const int ArgumentLimit = 64;
    /// <summary>The longest argument, in UTF-16 code units, that a declaration accepts.</summary>
    public const int ArgumentLengthLimit = 4096;

    private readonly string? _verb;
    private readonly int _maximumArguments;
    private readonly string? _argumentPattern;
    private readonly Regex? _argumentRegex;

    /// <summary>Declares an action.</summary>
    /// <param name="id">A lowercase reverse-DNS identifier such as <c>org.example.app.activate</c>; see <see cref="IsValidId"/>.</param>
    /// <param name="program">The absolute, normalized path of the program that runs as the administrator.</param>
    public PrivilegedActionDeclaration(string id, string program)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(program);
        if (!IsValidId(id)) throw new ArgumentException("An action ID is a lowercase reverse-DNS name with at least two segments.", nameof(id));
        if (!IsNormalizedAbsolutePath(program)) throw new ArgumentException("The program must be an absolute, normalized path.", nameof(program));
        Id = id;
        Program = program;
    }

    /// <summary>The action's identifier, the same on every platform.</summary>
    public string Id { get; }

    /// <summary>The absolute path of the program that runs as the administrator.</summary>
    public string Program { get; }

    /// <summary>
    /// The first argument, which selects this action when several actions run the same program; null for none.
    /// On Linux it is the policy's <c>org.freedesktop.policykit.exec.argv1</c>.
    /// </summary>
    public string? Verb
    {
        get => _verb;
        init
        {
            if (value is not null && (value.Length == 0 || value.Length > ArgumentLengthLimit || value.Contains('\0')))
                throw new ArgumentException("A verb is a non-empty argument without NUL characters.", nameof(value));
            _verb = value;
        }
    }

    /// <summary>How many arguments the action accepts after its verb, from 0 to <see cref="ArgumentLimit"/>.</summary>
    public int MaximumArguments
    {
        get => _maximumArguments;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, ArgumentLimit);
            _maximumArguments = value;
        }
    }

    /// <summary>
    /// A .NET regular expression that every argument after the verb must match completely; null accepts any
    /// argument within the length limit. It is evaluated with <see cref="RegexOptions.NonBacktracking"/>, so
    /// backreferences and lookarounds are not available.
    /// </summary>
    /// <remarks>
    /// .NET character classes are Unicode-wide: <c>\d</c> and <c>\w</c> match digits and letters of every script.
    /// A negated class such as <c>[^/]</c> also matches a line break. An inline <c>(?i)</c> makes <c>[a-z]</c> match the
    /// Kelvin sign (U+212A) too, and <c>(?x)</c> turns <c>#</c> into a comment. Spell out the allowed characters, for
    /// example <c>[A-Za-z0-9._+-]</c>, without inline options.
    /// </remarks>
    /// <exception cref="ArgumentException">The pattern is invalid on its own, for example with an unbalanced group.</exception>
    /// <exception cref="NotSupportedException">The pattern needs backtracking (backreferences or lookarounds).</exception>
    public string? ArgumentPattern
    {
        get => _argumentPattern;
        init
        {
            if (value is not null)
            {
                // Compiled alone first, so that "a)|(b" cannot close the anchoring group and match a part.
                _ = new Regex(value, PatternOptions);
                _argumentRegex = new Regex($"^(?:{value})\\z", PatternOptions);
            }
            else _argumentRegex = null;
            _argumentPattern = value;
        }
    }

    /// <summary>The options <see cref="ArgumentPattern"/> is compiled with, at build time and at run time.</summary>
    public const RegexOptions PatternOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>
    /// Whether <paramref name="arguments"/>, the arguments after the verb, are within this declaration: at most
    /// <see cref="MaximumArguments"/>, each without NUL characters, at most <see cref="ArgumentLengthLimit"/> long
    /// and matching <see cref="ArgumentPattern"/>.
    /// </summary>
    public bool AcceptsArguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count > MaximumArguments) return false;
        foreach (var argument in arguments)
        {
            if (argument is null || argument.Length > ArgumentLengthLimit || argument.Contains('\0')) return false;
            if (_argumentRegex is not null && !_argumentRegex.IsMatch(argument)) return false;
        }
        return true;
    }

    /// <summary>The complete argument vector after the program: the verb, when declared, then <paramref name="arguments"/>.</summary>
    /// <exception cref="ArgumentException">The arguments are not within this declaration.</exception>
    public ImmutableArray<string> CreateArgumentVector(IReadOnlyList<string> arguments)
    {
        if (!AcceptsArguments(arguments))
            throw new ArgumentException($"The arguments are not within the declaration of '{Id}'.", nameof(arguments));
        var builder = ImmutableArray.CreateBuilder<string>(arguments.Count + 1);
        if (Verb is not null) builder.Add(Verb);
        builder.AddRange(arguments);
        return builder.ToImmutable();
    }

    /// <summary>
    /// Whether <paramref name="id"/> is a valid action ID: lowercase ASCII letters, digits and hyphens in two or more
    /// dot-separated segments, each starting with a letter or digit, the first with a letter, at most 255 characters.
    /// </summary>
    public static bool IsValidId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (id.Length is 0 or > 255) return false;
        var segments = id.Split('.');
        if (segments.Length < 2 || !char.IsAsciiLetterLower(id[0])) return false;
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment[0] == '-') return false;
            foreach (var c in segment)
                if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return false;
        }
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Id;

    private static bool IsNormalizedAbsolutePath(string path)
    {
        if (path.Length is < 2 or > ArgumentLengthLimit || path[0] != '/' || path[^1] == '/' || path.Contains('\0')) return false;
        foreach (var segment in path[1..].Split('/'))
            if (segment is "" or "." or "..") return false;
        return true;
    }
}

/// <summary>One run of a declared privileged action.</summary>
/// <param name="Action">The declaration to run; the provider must have been created with it.</param>
/// <param name="Arguments">The arguments after the verb. They are passed as an argument vector, never through a shell.</param>
public sealed record PrivilegedActionRequest(PrivilegedActionDeclaration Action, ImmutableArray<string> Arguments)
{
    /// <summary>
    /// Receives the program's output lines while it runs, one call at a time on a worker thread. The program blocks
    /// while the callback runs, so a slow callback slows the program rather than buffering its output.
    /// </summary>
    public Action<PrivilegedActionOutput>? Output { get; init; }
}

/// <summary>The output stream a line came from.</summary>
public enum PrivilegedOutputChannel
{
    /// <summary>Standard output.</summary>
    StandardOutput,
    /// <summary>Standard error.</summary>
    StandardError
}

/// <summary>
/// A line of output without its line terminator. Lines are decoded as UTF-8; a very long line arrives in several
/// parts. Lines from different streams are not ordered relative to each other.
/// </summary>
/// <param name="Channel">The stream it came from.</param>
/// <param name="Text">The text of the line.</param>
public readonly record struct PrivilegedActionOutput(PrivilegedOutputChannel Channel, string Text);

/// <summary>The authorized program ran and exited.</summary>
/// <param name="ExitCode">Its exit code; 128 plus the signal number when a signal ended it on Unix.</param>
public sealed record PrivilegedActionCompletion(int ExitCode)
{
    /// <summary>Whether the program reported success with exit code 0.</summary>
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs declared privileged actions with the platform's authorization.</summary>
public interface IDesktopPrivilegedActions
{
    /// <summary>Asks for authorization and runs the action's program as the administrator.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="PlatformResult{T}.Success"/> means the program ran; check
    /// <see cref="PrivilegedActionCompletion.ExitCode"/> for its own outcome. Authorization refused by policy is
    /// <see cref="PlatformResult{T}.Failed"/> with <see cref="PlatformFailureCode.PermissionDenied"/>; the user
    /// dismissing the authentication dialog is <see cref="PlatformFailureCode.UserDismissed"/>. No authorization
    /// service or authentication agent is <see cref="PlatformResult{T}.Unavailable"/>. A program that is missing or
    /// can be replaced by other users is <see cref="PlatformFailureCode.InvalidData"/>, and is never started.
    /// </para>
    /// <para>
    /// Cancellation before the program starts stops the authorization and throws
    /// <see cref="OperationCanceledException"/>. Once the program runs as the administrator it cannot be stopped from
    /// the application: cancellation closes its standard input, which a cooperating program treats as a request to
    /// stop, and the result reports how it actually ended.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">The action is not one this provider declares, or the arguments are not within it.</exception>
    ValueTask<PlatformResult<PrivilegedActionCompletion>> RunAsync(PrivilegedActionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Privileged actions for hosts and platforms that cannot provide them.</summary>
public static class DesktopPrivilegedActions
{
    /// <summary>Creates privileged actions that report every request unavailable for <paramref name="reason"/>.</summary>
    /// <remarks>Arguments are still checked against the declaration. Nothing else throws except caller cancellation.</remarks>
    public static IDesktopPrivilegedActions Unavailable(PlatformUnavailableReason reason)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        return new UnavailablePrivilegedActions(reason);
    }

    private sealed class UnavailablePrivilegedActions(PlatformUnavailableReason reason) : IDesktopPrivilegedActions
    {
        public ValueTask<PlatformResult<PrivilegedActionCompletion>> RunAsync(PrivilegedActionRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Action);
            if (request.Arguments.IsDefault) throw new ArgumentException("Arguments must be initialized.", nameof(request));
            request.Action.CreateArgumentVector(request.Arguments);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<PlatformResult<PrivilegedActionCompletion>>(new PlatformResult<PrivilegedActionCompletion>.Unavailable(reason));
        }
    }
}
