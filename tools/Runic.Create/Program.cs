using System;
using System.Collections.Generic;
using System.Linq;
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
    internal const int Cancelled = 130;

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

    // Command Line's default exit codes are those of every Runic tool: 0, 1, 2, 3 and 130.
    internal static CommandApp CreateApp(ICommandConsole console, Func<string, string?> getEnvironmentVariable)
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        return new(GeneratedCommandCatalog.Create(builder =>
        {
            builder.OutputSelector(FormatOptionName);
            // The template's text parameters, such as --application-id, are options generated from its model, so a
            // new text parameter needs no change here. The handler reads their values from TemplateTextValues.
            foreach (TemplateText text in model.Texts)
            {
                builder.GlobalOption(TextOptionId(text), text.Flag, CommandArity.ExactlyOne,
                    new CommandHelp(text.Description, valueName: "VALUE"));
            }
        }))
        {
            Name = "runic-create",
            Version = Version,
            HelpPresenter = new SpectreHelpPresenter(),
            Console = console,
            ParseSettings = new ParseSettings(getEnvironmentVariable(CommandOutputClassifier.EnvironmentVariableName))
            {
                GetEnvironmentVariable = getEnvironmentVariable,
            },
            CreateScopeFactory = invocation => CommandScopes.FromServices(
                CommandServices.Empty.With(TemplateTextValues.From(model, invocation))),
        };
    }

    internal static string TextOptionId(TemplateText text) => "template-text-" + text.LongName;

    [Command("create",
        Description = "Create a Runic application. Asks for each option you do not pass.",
        Examples =
        [
            "dnx Runic.Create",
            "dnx Runic.Create -- MyApp --frontend svelte --package-manager bun",
            "dnx Runic.Create -- MyApp --host cswebui --view-models toolkit",
            "dnx Runic.Create -- MyApp --yes --dry-run",
            "dnx Runic.Create -- MyApp -o apps/MyApp --yes",
            "dnx Runic.Create -- MyApp --application-id org.example.MyApp --yes",
            "dnx Runic.Create -- MyApp --no-install --force --yes",
        ])]
    [RootCommand]
    [CommandResult("runic.create/2", typeof(CreateJsonContext))]
    internal static Task<CommandOutcome<CreateResult>> Create(
        CommandExecutionContext context,
        [FromServices] TemplateTextValues texts,
        CancellationToken cancellationToken,
        [Argument(Description = "Project name and default directory.")] string? name = null,
        [Option("--output", "-o", ValueName = "PATH", Description = "Create the project in this directory instead, as dotnet new --output does.")] string? output = null,
        [Option("--frontend", Description = "Frontend framework.")] string? frontend = null,
        [Option("--package-manager", Description = "JavaScript package manager.")] string? packageManager = null,
        [Option("--host", Description = "Window host: desktop opens a native window, cswebui opens a browser window.")] string? host = null,
        [Option("--linux-gtk", Description = "GTK version of Runic Desktop on Linux: 3 or 4.")] string? linuxGtk = null,
        [Option("--view-models", Description = "MVVM library for the C# ViewModels.")] string? viewModels = null,
        [Option("--tests", Description = "Add a TUnit test project for the Window.")] bool tests = false,
        [Option("--nix", Description = "Add a Nix flake that builds the application with the Runic SDK's builder.")] bool nix = false,
        [Option("--packaging", Description = "Add the packaging/ manifests that dotnet runic package fills, to edit them: MSIX, macOS bundle, desktop entry, AppStream metadata and Flatpak manifest.")] bool packaging = false,
        [Option("--restore", Description = "Restore the project's NuGet packages after creating it, for an IDE that opens it next.")] bool restore = false,
        [Option("--force", Description = "Create the project in a directory that is not empty, as dotnet new --force does. Files the template writes are replaced.")] bool force = false,
        [Option("--no-install", Description = "Use the installed Runic.Application.Templates instead of installing this version, for example offline.")] bool noInstall = false,
        [Option("--yes", "-y", Description = "Use the default for every option you do not pass instead of asking.")] bool yes = false,
        [Option("--dry-run", Description = "Print the commands without running them.")] bool dryRun = false,
        [Option("--template-source", ValueName = "SOURCE", Description = "An additional NuGet source for the template package.")] string? templateSource = null)
    {
        var request = new CreateRequest(
            Optional(name),
            Optional(output),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["frontend"] = frontend ?? "",
                ["package-manager"] = packageManager ?? "",
                ["host"] = host ?? "",
                ["linux-gtk"] = linuxGtk ?? "",
                ["view-models"] = viewModels ?? "",
            },
            yes,
            dryRun,
            Optional(templateSource),
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["tests"] = tests, ["nix"] = nix, ["packaging"] = packaging, ["restore"] = restore })
        {
            Texts = texts.Values,
            Force = force,
            NoInstall = noInstall,
        };
        return CreateApplication.RunAsync(context, request, Version, cancellationToken);
    }

    /// <summary>The template options the creator exposes as flags, by long name.</summary>
    internal static IReadOnlyList<string> OptionFlags { get; } = ["frontend", "package-manager", "host", "linux-gtk", "view-models"];

    /// <summary>The template's Boolean parameters the creator exposes as presence flags, by long name.</summary>
    internal static IReadOnlyList<string> ToggleFlags { get; } = ["tests", "nix", "packaging", "restore"];

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
    string? TemplateSource,
    IReadOnlyDictionary<string, bool> Toggles)
{
    /// <summary>The text parameters given, such as <c>application-id</c>, keyed by long name.</summary>
    public IReadOnlyDictionary<string, string> Texts { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates the project in a directory that is not empty (<c>dotnet new --force</c>).</summary>
    public bool Force { get; init; }

    /// <summary>Uses the installed template package instead of installing the creator's version.</summary>
    public bool NoInstall { get; init; }
}

/// <summary>The values of the template's text options in one invocation, keyed by long name.</summary>
internal sealed class TemplateTextValues(IReadOnlyDictionary<string, string> values)
{
    public IReadOnlyDictionary<string, string> Values { get; } = values;

    public static TemplateTextValues From(TemplateOptionModel model, ParsedInvocation invocation)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (TemplateText text in model.Texts)
        {
            string id = Program.TextOptionId(text);
            CommandValueBinding? binding = invocation.Options.LastOrDefault(option => option.Id == id);
            if (binding is { Values.Count: > 0 } && !string.IsNullOrWhiteSpace(binding.Values[^1]))
                values[text.LongName] = binding.Values[^1].Trim();
        }
        return new TemplateTextValues(values);
    }
}

// The runic.create/2 payload names its properties in camelCase. They are spelled out
// so that the JSON context's options cannot rename them: since Runic.CommandLine
// 0.6.0-preview.3, the generated codec serializes through CreateJsonContext.Default.
// Options holds the template's choice parameters, Toggles its Boolean parameters and
// TextOptions the text parameters that were given, all keyed by their long option names.
internal sealed record CreateResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("fullPath")] string FullPath,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, string> Options,
    [property: JsonPropertyName("toggles")] IReadOnlyDictionary<string, bool> Toggles,
    [property: JsonPropertyName("textOptions")] IReadOnlyDictionary<string, string> TextOptions,
    [property: JsonPropertyName("commands")] IReadOnlyList<string> Commands,
    [property: JsonPropertyName("reproduceCommand")] string ReproduceCommand,
    [property: JsonPropertyName("created")] bool Created)
{
    [JsonIgnore]
    public string HumanOutput { get; init; } = "";

    public override string ToString() => HumanOutput;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CreateResult))]
internal sealed partial class CreateJsonContext : JsonSerializerContext;
