using System.Threading;
using System.Threading.Tasks;

namespace Runic.CommandLine;

/// <summary>Renders catalog help for a person. Machine help uses the shared framework presentation layer.</summary>
public interface ICommandHelpPresenter
{
    /// <summary>Writes help to an invocation-local console.</summary>
    ValueTask WriteAsync(CommandCatalog catalog, string applicationName, CommandPath path, string outputOptionName, ICommandConsole console, CancellationToken cancellationToken);
}
