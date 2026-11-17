using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Runic.Application.Tool;

/// <summary>
/// Finds programs on PATH as a shell does, and starts Windows batch files (<c>npm.cmd</c>, <c>pnpm.cmd</c>) without a
/// shell interpreting their arguments.
/// </summary>
/// <remarks>
/// On Windows, <see cref="System.Diagnostics.Process"/> with <c>UseShellExecute = false</c> only appends
/// <c>.exe</c> to a bare name, so it cannot start <c>npm</c>, which Node.js installs as <c>npm.cmd</c> (#1081).
/// <see cref="Find"/> searches PATH with PATHEXT in the shell's order instead. Relative PATH entries are skipped: they
/// would resolve against the project directory, so a checked-out file could run as a tool.
/// </remarks>
internal sealed record ExecutableSearch(
    bool IsWindows,
    string? Path,
    string? PathExtensions,
    string CommandInterpreter,
    Func<string, bool> FileExists)
{
    /// <summary>The environment variable that carries a batch file's command line to <c>cmd.exe</c>.</summary>
    internal const string BatchCommandLineVariable = "RUNIC_BATCH_COMMAND_LINE";

    private const string DefaultPathExtensions = ".COM;.EXE;.BAT;.CMD";

    internal static ExecutableSearch System { get; } = new(
        OperatingSystem.IsWindows(),
        null,
        null,
        OperatingSystem.IsWindows() ? global::System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe") : "cmd.exe",
        File.Exists);

    /// <summary>The full path of <paramref name="name"/> as the shell would run it, or null.</summary>
    internal string? Find(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (IsFullyQualified(name))
        {
            return FileExists(name) ? Normalize(name) : null;
        }

        string? path = Path ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;
        IReadOnlyList<string> candidates = CandidateNames(name);
        char separator = IsWindows ? ';' : ':';
        foreach (string directory in path.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static directory => directory.Trim('"'))
            .Where(IsFullyQualified))
        foreach (string candidate in candidates)
        {
            string file = directory.TrimEnd(IsWindows ? ['\\', '/'] : ['/']) + (IsWindows ? '\\' : '/') + candidate;
            if (FileExists(file)) return Normalize(file);
        }

        return null;
    }

    /// <summary>
    /// The program, arguments and environment that start <paramref name="executable"/> with
    /// <paramref name="arguments"/> without a shell. A bare name is resolved on Windows; a batch file starts
    /// through <c>cmd.exe</c> with its command line in <see cref="BatchCommandLineVariable"/>.
    /// </summary>
    internal ProgramStart Prepare(string executable, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!IsWindows) return new(executable, arguments, null, null);

        string program = IsBareName(executable) ? Find(executable) ?? executable : executable;
        if (!IsBatchFile(program)) return new(program, arguments, null, null);

        // cmd.exe expands %VARIABLE% once, before it parses quotes and & | < > ^ ( ). A command line that comes from a
        // variable is therefore not expanded again, so a % in an argument stays literal; quoting each argument keeps
        // cmd.exe's metacharacters literal, both here and where the batch file passes them on with %*. /d skips
        // AutoRun commands, /v:off keeps ! literal, and /s makes cmd.exe strip only the outer quotes.
        var commandLine = new StringBuilder(QuoteForBatch(program));
        foreach (string argument in arguments) commandLine.Append(' ').Append(QuoteForBatch(argument));
        return new(
            CommandInterpreter,
            [],
            $"/d /v:off /s /c \"%{BatchCommandLineVariable}%\"",
            commandLine.ToString());
    }

    /// <summary>Whether Windows starts <paramref name="program"/> through <c>cmd.exe</c>.</summary>
    internal static bool IsBatchFile(string program) =>
        global::System.IO.Path.GetExtension(program) is { } extension &&
        (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Quotes one argument of a batch file's command line. Inside double quotes cmd.exe treats
    /// <c>&amp; | &lt; &gt; ^ ( )</c> and spaces as text. A double quote is written as <c>""</c>, which keeps cmd.exe
    /// inside the quoted text and which the C runtime's parser (Node.js, .NET) reads as one literal quote; backslashes
    /// before a quote, and at the end, are doubled for that parser. cmd.exe ends a command at a line break, so line
    /// breaks and NUL cannot be passed.
    /// </summary>
    internal static string QuoteForBatch(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
        {
            throw new ArgumentException("A batch file cannot receive an argument that contains a line break or NUL.", nameof(argument));
        }

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                quoted.Append('\\', backslashes * 2).Append("\"\"");
            }
            else
            {
                quoted.Append('\\', backslashes).Append(character);
            }
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private IReadOnlyList<string> CandidateNames(string name)
    {
        if (!IsWindows) return [name];
        string[] extensions = (PathExtensions ?? Environment.GetEnvironmentVariable("PATHEXT") ?? DefaultPathExtensions)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (extensions.Length == 0) extensions = DefaultPathExtensions.Split(';');
        // A name that already ends in a PATHEXT extension (powershell.exe) is looked up as is.
        string extension = global::System.IO.Path.GetExtension(name);
        return extension.Length > 0 && extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? [name]
            : [.. extensions.Select(candidate => name + candidate.ToLowerInvariant())];
    }

    private bool IsFullyQualified(string directory) =>
        IsWindows
            ? directory.Length >= 3 && char.IsAsciiLetter(directory[0]) && directory[1] == ':' && directory[2] is '\\' or '/'
                || directory.StartsWith(@"\\", StringComparison.Ordinal)
            : directory.StartsWith('/');

    // GetFullPath follows the host's rules, so a search for the other OS (tests) keeps the path as found.
    private string Normalize(string file) =>
        IsWindows == OperatingSystem.IsWindows() ? global::System.IO.Path.GetFullPath(file) : file;

    private static bool IsBareName(string executable) =>
        executable.IndexOfAny(['\\', '/', ':']) < 0;
}

/// <summary>What <see cref="ExecutableSearch.Prepare"/> starts.</summary>
/// <param name="FileName">The program to start.</param>
/// <param name="Arguments">The arguments, each quoted by .NET, when <paramref name="CommandLine"/> is null.</param>
/// <param name="CommandLine">The raw argument string for <c>cmd.exe</c>, or null.</param>
/// <param name="BatchCommandLine">The value of <see cref="ExecutableSearch.BatchCommandLineVariable"/>, or null.</param>
internal sealed record ProgramStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? CommandLine,
    string? BatchCommandLine);
