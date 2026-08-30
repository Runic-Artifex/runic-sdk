using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Maps parser-owned errors to application-owned presentation and exit behavior.</summary>
public static class CommandParsePresentation
{
    /// <summary>Gets an application-selected exit code for a non-invocation parse outcome.</summary>
    public static int GetExitCode(ParseOutcome outcome, Func<CommandDiagnostic, int> mapDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(mapDiagnostic);
        if (outcome.Kind != ParseOutcomeKind.Error || outcome.Diagnostics.Count == 0)
        {
            throw new ArgumentException("Only parse errors have an application-mapped exit code.", nameof(outcome));
        }

        return mapDiagnostic(outcome.Diagnostics[0]);
    }

    /// <summary>Writes application-owned human text for parser diagnostics without reparsing arguments.</summary>
    public static async ValueTask WriteHumanAsync(
        ParseOutcome outcome,
        ICommandConsole console,
        Func<CommandDiagnostic, CultureInfo, string> formatDiagnostic,
        CultureInfo culture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(formatDiagnostic);
        ArgumentNullException.ThrowIfNull(culture);
        foreach (CommandDiagnostic diagnostic in outcome.Diagnostics)
        {
            string text = formatDiagnostic(diagnostic, culture) ?? throw new InvalidOperationException("The diagnostic formatter returned null.");
            await console.WriteErrorAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }
}
