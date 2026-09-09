using System;
using System.Threading;
using System.Threading.Tasks;
using Spectre.Console;

namespace Runic.CommandLine.Spectre;

/// <summary>Displays catalog help with terminal-aware Spectre styling.</summary>
public sealed class SpectreHelpPresenter : ICommandHelpPresenter
{
    /// <inheritdoc />
    public async ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName, ICommandConsole console, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);
        var presenter = console as SpectreCommandConsole ?? new SpectreCommandConsole(console);
        string text = CommandHelpFormatter.Format(catalog, applicationName, path, outputOptionName);
        // Keep the plain and styled forms textually equivalent, including under redirection.
        string[] lines = text.TrimEnd('\n').Split('\n');
        foreach (string line in lines)
        {
            bool heading = line.StartsWith("Usage:", StringComparison.Ordinal) || (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':'));
            await presenter.WriteAsync(new Text(line + "\n", heading ? new Style(foreground: Color.Cyan, decoration: Decoration.Bold) : Style.Plain), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
