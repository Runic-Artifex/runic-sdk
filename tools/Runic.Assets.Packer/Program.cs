using System.Threading.Tasks;
using Runic.Assets.Packer;
using Runic.CommandLine;
using Runic.CommandLine.Generated;

// --format selects human or JSON output, as for every Runic tool; --output, its former spelling, stays a hidden alias
// until 2.0. The generated catalog declares the selector from this attribute (Command Line 0.11).
[assembly: OutputSelector("--format", "--output")]

return await PackerApplication.RunAsync(args).ConfigureAwait(false);

internal static class PackerApplication
{
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
            .PresentOutcome(AssetPackCommand.CommandPath, presenter ?? AssetPackCommand.PresentAsync)
            .PresentOutcome<VerifierResult>(AssetVerifyCommand.CommandPath, AssetVerifyCommand.PresentAsync));
}
