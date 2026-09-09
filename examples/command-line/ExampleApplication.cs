using Runic.CommandLine.Generated;
using Runic.CommandLine.Spectre;

namespace Runic.CommandLine.Examples;

/// <summary>The growing example shares its real application setup with invocation tests.</summary>
public static class ExampleApplication
{
    /// <summary>Creates the real example application with invocation-local output.</summary>
    public static CommandApp Create(ICommandConsole console) => new(GeneratedCommandCatalog.Create(builder => builder
        .GlobalOption("verbose", "--verbose", CommandArity.Zero, new CommandHelp("Show detailed progress."), "-v")
        .Present<TransformResult>("transform", (result, output, _, token) =>
            output.WriteOutAsync($"Wrote {result.Characters} characters to {result.Output}\n".AsMemory(), token))))
    {
        ScopeFactory = HostedExample.CreateCommandScopes(), Name = "hello", Version = "1.0.0",
        HelpPresenter = new SpectreHelpPresenter(), Console = console,
    };
}
