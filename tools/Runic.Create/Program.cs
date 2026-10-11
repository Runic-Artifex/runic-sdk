using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Generated;
using Runic.CommandLine.Spectre;

namespace Runic.Create;

internal static class Program
{
    internal const int Success = 0;
    internal const int CreateFailure = 1;
    internal const int UsageFailure = 2;
    internal const int InternalFailure = 3;

    /// <summary>
    /// Selects human or JSON output. <c>--output</c> and <c>-o</c> choose the
    /// destination directory, as they do for <c>dotnet new</c>.
    /// </summary>
    internal const string FormatOptionName = "--format";

    internal static Task<int> Main(string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return CreateApp(new SpectreCommandConsole(), Environment.GetEnvironmentVariable).RunAsync(arguments);
    }

    internal static CommandApp CreateApp(ICommandConsole console, Func<string, string?> getEnvironmentVariable) =>
        new(GeneratedCommandCatalog.Create())
        {
            Name = "runic-create",
            Version = Version,
            HelpPresenter = new SpectreHelpPresenter(),
            Console = console,
            ExitCodePolicy = CreateExitCodePolicy.Instance,
            ParseSettings = new ParseSettings(
                getEnvironmentVariable(CommandOutputClassifier.EnvironmentVariableName),
                transportOutputOptionName: FormatOptionName)
            {
                GetEnvironmentVariable = getEnvironmentVariable,
            },
        };

    [Command("create",
        Description = "Create a Runic application. Asks for each option you do not pass.",
        Examples =
        [
            "dnx Runic.Create",
            "dnx Runic.Create -- MyApp --frontend svelte --package-manager bun",
            "dnx Runic.Create -- MyApp --yes --dry-run",
            "dnx Runic.Create -- MyApp -o apps/MyApp --yes",
        ])]
    [DefaultCommand]
    [CommandResult("runic.create/1", typeof(CreateJsonContext))]
    internal static Task<CommandOutcome<CreateResult>> Create(
        CommandExecutionContext context,
        CancellationToken cancellationToken,
        [Argument(Description = "Project name and default directory.")] string? name = null,
        [Option("--output", "-o", ValueName = "PATH", Description = "Create the project in this directory instead, as dotnet new --output does.")] string? output = null,
        [Option("--directory", ValueName = "PATH", Hidden = true, Description = "Former name of --output.")] string? directory = null,
        [Option("--frontend", Description = "Frontend framework.")] string? frontend = null,
        [Option("--package-manager", Description = "JavaScript package manager.")] string? packageManager = null,
        [Option("--host", Description = "Window host.")] string? host = null,
        [Option("--view-models", Description = "MVVM library for the C# ViewModels.")] string? viewModels = null,
        [Option("--yes", "-y", Description = "Use the default for every option you do not pass instead of asking.")] bool yes = false,
        [Option("--dry-run", Description = "Print the commands without running them.")] bool dryRun = false,
        [Option("--template-source", ValueName = "SOURCE", Description = "An additional NuGet source for the template package.")] string? templateSource = null)
    {
        string? destination = Optional(output);
        string? formerDestination = Optional(directory);
        if (destination is not null && formerDestination is not null &&
            !string.Equals(destination, formerDestination, StringComparison.Ordinal))
        {
            return Task.FromResult(CreateApplication.Failure(CommandExitCategory.Usage, "RCREATE006",
                "Pass the destination once with --output (-o). --directory is its former name."));
        }
        var request = new CreateRequest(
            Optional(name),
            destination ?? formerDestination,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["frontend"] = frontend ?? "",
                ["package-manager"] = packageManager ?? "",
                ["host"] = host ?? "",
                ["view-models"] = viewModels ?? "",
            },
            yes,
            dryRun,
            Optional(templateSource));
        return CreateApplication.RunAsync(context, request, Version, cancellationToken);
    }

    /// <summary>The template options the creator exposes as flags, by long name.</summary>
    internal static IReadOnlyList<string> OptionFlags { get; } = ["frontend", "package-manager", "host", "view-models"];

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string Version
    {
        get
        {
            string value = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion ?? "unknown";
            int metadata = value.IndexOf('+', StringComparison.Ordinal);
            return metadata < 0 ? value : value[..metadata];
        }
    }
}

internal sealed record CreateRequest(
    string? Name,
    string? Directory,
    IReadOnlyDictionary<string, string> Options,
    bool UseDefaults,
    bool DryRun,
    string? TemplateSource);

internal sealed record CreateResult(
    string Name,
    string Directory,
    string Version,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyList<string> Commands,
    string ReproduceCommand,
    bool Created)
{
    [JsonIgnore]
    public string HumanOutput { get; init; } = "";

    public override string ToString() => HumanOutput;
}

internal sealed class CreateExitCodePolicy : IExitCodePolicy
{
    internal static CreateExitCodePolicy Instance { get; } = new();

    public int GetExitCode(CommandExitCategory category) => category switch
    {
        CommandExitCategory.Success => Program.Success,
        CommandExitCategory.Usage or CommandExitCategory.Validation => Program.UsageFailure,
        CommandExitCategory.CommandFailure or CommandExitCategory.Unavailable => Program.CreateFailure,
        _ => Program.InternalFailure,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateResult))]
internal sealed partial class CreateJsonContext : JsonSerializerContext;
