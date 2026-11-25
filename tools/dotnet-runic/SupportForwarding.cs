using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Runic.CommandLine;

namespace Runic.Application.Tool;

/// <summary>
/// <c>dotnet runic support</c> moved to the Translations tool as <c>runic-translations support
/// preview|collect|remove</c>, next to the editor that produces the diagnostic ZIP it reads. This hidden alias keeps
/// the former command line working until 2.0: <c>--mode</c> becomes the subcommand, every other argument, including
/// <c>--format</c>, is passed through, and the Translations tool writes the output and decides the exit code. When the
/// tool is not installed, the hidden <c>support</c> command reports <see cref="MissingToolCode"/>.
/// </summary>
internal sealed class SupportForwarding
{
    internal const string Command = "support";
    internal static string ToolCommand => ToolExtensions.Translations.Command;
    internal static string ToolPackage => ToolExtensions.Translations.Package;
    internal const string MissingToolCode = "RAPPCLI1012";
    internal const string MissingToolMessage =
        "dotnet runic support moved to 'runic-translations support'. Install the dotnet-runic-translations tool, for example with 'dotnet tool install dotnet-runic-translations', and run 'runic-translations support preview|collect|remove'.";
    internal const string MovedNotice =
        "dotnet runic support moved to 'runic-translations support preview|collect|remove'. This alias forwards to it and is removed in 2.0.";

    private SupportForwarding(IReadOnlyList<string> arguments) => Arguments = arguments;

    /// <summary>The Translations tool's arguments, starting with <c>support</c> and the subcommand.</summary>
    internal IReadOnlyList<string> Arguments { get; }

    /// <summary>
    /// Recognizes <c>support</c> as the command, after any leading <c>--format</c>, and maps its arguments. The
    /// selector's hidden alias <c>--output</c> is passed on as <c>--format</c>, except after <c>--</c>.
    /// </summary>
    internal static bool TryParse(IReadOnlyList<string> arguments, out SupportForwarding? forwarding)
    {
        forwarding = null;
        arguments = WithFormatSelector(arguments);
        var leading = new List<string>();
        int index = 0;
        while (index < arguments.Count)
        {
            string argument = arguments[index];
            if (argument == Program.FormatOptionName && index + 1 < arguments.Count)
            {
                leading.Add(argument);
                leading.Add(arguments[index + 1]);
                index += 2;
            }
            else if (argument.StartsWith(Program.FormatOptionName + "=", StringComparison.Ordinal))
            {
                leading.Add(argument);
                index++;
            }
            else break;
        }
        if (index >= arguments.Count || arguments[index] != Command) return false;
        string mode = "preview";
        var rest = new List<string>();
        for (index++; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (argument == "--mode" && index + 1 < arguments.Count) mode = arguments[++index];
            else if (argument.StartsWith("--mode=", StringComparison.Ordinal)) mode = argument["--mode=".Length..];
            else rest.Add(argument);
        }
        forwarding = new SupportForwarding([Command, mode.ToLowerInvariant(), .. leading, .. rest]);
        return true;
    }

    private static string[] WithFormatSelector(IReadOnlyList<string> arguments)
    {
        string[] result = [.. arguments];
        for (int index = 0; index < result.Length && result[index] != "--"; index++)
        {
            string argument = result[index];
            if (argument == Program.FormerFormatOptionName) result[index] = Program.FormatOptionName;
            else if (argument.StartsWith(Program.FormerFormatOptionName + "=", StringComparison.Ordinal))
                result[index] = Program.FormatOptionName + argument[Program.FormerFormatOptionName.Length..];
        }
        return result;
    }

    /// <summary>
    /// The Translations tool's command line, found as <c>dotnet runic translations</c> finds it
    /// (<see cref="ToolExtensions.Resolve"/>): <c>RAX_TRANSLATIONS_TOOL</c>, then <c>dotnet tool run
    /// runic-translations</c> when a local tool manifest in the current directory or above lists the tool, then
    /// <c>node_modules/.bin</c>, then <c>runic-translations</c> on <c>PATH</c>; <see langword="null"/> when none exists.
    /// </summary>
    internal static (string Executable, IReadOnlyList<string> Prefix)? ResolveTool(
        string currentDirectory, Func<string, string?> findExecutable, string dotnetHost) =>
        ResolveTool(currentDirectory, new ExtensionEnvironment(Environment.GetEnvironmentVariable, findExecutable, dotnetHost));

    internal static (string Executable, IReadOnlyList<string> Prefix)? ResolveTool(string currentDirectory, ExtensionEnvironment environment) =>
        ToolExtensions.Resolve(ToolExtensions.Translations, currentDirectory, environment) is { } tool ? (tool.Executable, tool.Prefix) : null;

    /// <summary>Runs the Translations tool with this console's standard streams and returns its exit code.</summary>
    internal async Task<int> RunAsync((string Executable, IReadOnlyList<string> Prefix) tool, ICommandConsole console)
    {
        await console.WriteErrorAsync((MovedNotice + "\n").AsMemory(), default).ConfigureAwait(false);
        ProcessStartInfo start = CommandRunner.CreateInteractiveStartInfo(
            tool.Executable, Environment.CurrentDirectory, [.. tool.Prefix, .. Arguments]);
        // A terminal Ctrl+C reaches the Translations tool too; wait for its exit code instead of ending first.
        Action<PosixSignalContext> wait = static context => context.Cancel = true;
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, wait);
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, wait);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new DevDevelopmentException(MissingToolCode, MissingToolMessage);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }
}
