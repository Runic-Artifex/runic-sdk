using System.Threading.Tasks;
using Runic.Assets.Packer;
using Runic.CommandLine;
using Runic.CommandLine.Generated;

return await PackerApplication.RunAsync(args).ConfigureAwait(false);

internal static class PackerApplication
{
    /// <summary>Selects human or JSON output, as for every Runic tool.</summary>
    private const string FormatOptionName = "--format";

    /// <summary>The selector's former spelling, a hidden alias until 2.0.</summary>
    private const string FormerFormatOptionName = "--output";

    public static Task<int> RunAsync(string[] args) => CreateApp(new Runic.CommandLine.Spectre.SpectreCommandConsole()).RunAsync(args);

    // No ParseSettings: CommandApp's defaults read RUNIC_COMMANDLINE_OUTPUT, RUNIC_COMMANDLINE_NO_INPUT and option
    // environment fallbacks from the process environment. Its default exit codes are those of every Runic tool.
    // The presenter writes the human output; JSON output keeps the standard envelope. Tests pass a presenter that
    // observes the invocation's context and then delegates to AssetPackCommand.PresentAsync.
    internal static CommandApp CreateApp(ICommandConsole console, CommandOutcomePresenter<PackerResult>? presenter = null) =>
        new(CreateCatalog(presenter))
        {
            Name = "Runic.Assets.Packer",
            Version = "0.2",
            HelpPresenter = new Runic.CommandLine.Spectre.SpectreHelpPresenter(),
            Console = console,
        };

    // The pack command is AssetPackCommand.PackAsync, which dotnet runic assets pack shares.
    internal static CommandCatalog CreateCatalog(CommandOutcomePresenter<PackerResult>? presenter = null) =>
        GeneratedCommandCatalog.Create(builder => builder
            .OutputSelector(FormatOptionName, FormerFormatOptionName)
            .PresentOutcome(AssetPackCommand.CommandPath, presenter ?? AssetPackCommand.PresentAsync));
}
