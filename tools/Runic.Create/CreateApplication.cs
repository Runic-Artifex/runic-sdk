using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.CommandLine.Spectre;
using Spectre.Console;

namespace Runic.Create;

internal static partial class CreateApplication
{
    private const string DefaultName = "MyApp";

    // Install guidance for package managers missing from PATH, keyed by template choice.
    private static readonly Dictionary<string, string> PackageManagerInstallLinks = new(StringComparer.Ordinal)
    {
        ["npm"] = "https://nodejs.org/en/download",
        ["pnpm"] = "https://pnpm.io/installation",
        ["bun"] = "https://bun.sh",
    };

    internal static async Task<CommandOutcome<CreateResult>> RunAsync(
        CommandExecutionContext context,
        CreateRequest request,
        string version,
        CancellationToken cancellationToken)
    {
        var console = new SpectreCommandConsole(context.Console);
        bool interactive = context.Console.IsInteractive && !request.UseDefaults &&
            context.OutputMode == CommandOutputMode.Human;
        TemplateOptionModel model = TemplateOptionModel.Load();

        if (!TryResolveExplicit(model, request.Options, out Dictionary<string, TemplateChoice> explicitChoices, out string? usage))
            return Failure(CommandExitCategory.Usage, "RCREATE001", usage!);

        string name = request.Name ?? (interactive ? AskName() : DefaultName);
        if (!IsValidName(name))
        {
            return Failure(CommandExitCategory.Usage, "RCREATE002",
                $"'{name}' is not a valid project name. Start with a letter and use letters, digits, '.', '_' or '-'.");
        }

        if (interactive && explicitChoices.Count < model.Options.Count)
        {
            AnsiConsole.MarkupLine(CultureInfo.InvariantCulture, "[bold]Create a Runic application[/] [grey]· {0}[/]", Markup.Escape(version));
        }
        var selections = new List<OptionSelection>();
        foreach (TemplateOption option in model.Options)
        {
            TemplateChoice choice = explicitChoices.TryGetValue(option.LongName, out TemplateChoice? given)
                ? given
                : interactive ? Ask(option) : option.Default;
            selections.Add(new OptionSelection(option, choice));
        }

        var plan = new CreatePlan(name, request.Directory, version, selections, request.TemplateSource);
        string directory = Path.GetFullPath(plan.Directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return Failure(CommandExitCategory.Usage, "RCREATE003",
                $"The directory '{plan.Directory}' already exists and is not empty. Choose another name or --directory.");
        }

        if (request.DryRun)
        {
            return Success(plan, created: false, DescribePlan(plan));
        }

        await console.WriteAsync(new Markup(
            $"Creating [bold]{Markup.Escape(plan.Name)}[/] [grey]({Markup.Escape(plan.Summary)})[/]\n"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        bool json = context.OutputMode == CommandOutputMode.Json;
        ProcessResult install = await RunDotNetAsync(plan.InstallArguments, capture: true, json, cancellationToken).ConfigureAwait(false);
        bool alreadyInstalled = install.ExitCode == 106 &&
            install.Output.Contains("is already installed", StringComparison.Ordinal);
        if (install.ExitCode != 0 && !alreadyInstalled)
        {
            await context.Console.WriteErrorAsync(install.Output.AsMemory(), cancellationToken).ConfigureAwait(false);
            return Failure(CommandExitCategory.CommandFailure, "RCREATE004",
                $"Installing {TemplateOptionModel.TemplatePackage}@{version} failed with exit code {install.ExitCode}.");
        }

        ProcessResult create = await RunDotNetAsync(plan.CreateArguments, capture: false, json, cancellationToken).ConfigureAwait(false);
        if (create.ExitCode != 0)
        {
            return Failure(CommandExitCategory.CommandFailure, "RCREATE005",
                $"dotnet new {TemplateOptionModel.TemplateShortName} failed with exit code {create.ExitCode}.");
        }

        return Success(plan, created: true, DescribeCreated(plan));
    }

    internal static bool TryResolveExplicit(
        TemplateOptionModel model,
        IReadOnlyDictionary<string, string> values,
        out Dictionary<string, TemplateChoice> choices,
        out string? error)
    {
        choices = new Dictionary<string, TemplateChoice>(StringComparer.Ordinal);
        error = null;
        foreach (TemplateOption option in model.Options)
        {
            if (!values.TryGetValue(option.LongName, out string? value) || string.IsNullOrWhiteSpace(value)) continue;
            TemplateChoice? choice = option.Find(value.Trim());
            if (choice is null)
            {
                error = $"Unknown {option.Flag} '{value}'. Choose {string.Join(", ", option.Choices.Select(item => item.Value))}.";
                return false;
            }
            choices[option.LongName] = choice;
        }
        return true;
    }

    internal static bool IsValidName(string name) => ProjectName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,99}$")]
    private static partial Regex ProjectName();

    private static string AskName() => AnsiConsole.Prompt(
        new TextPrompt<string>("[bold]Project name[/]")
            .DefaultValue(DefaultName)
            .Validate(value => IsValidName(value)
                ? ValidationResult.Success()
                : ValidationResult.Error("Start with a letter and use letters, digits, '.', '_' or '-'.")));

    private static TemplateChoice Ask(TemplateOption option)
    {
        bool packageManager = option.LongName == "package-manager";
        TemplateChoice choice = AnsiConsole.Prompt(
            new SelectionPrompt<TemplateChoice>()
                .Title($"[bold]{Markup.Escape(option.DisplayName)}[/] [grey]{Markup.Escape(option.Description)}[/]")
                .AddChoices(option.Choices)
                .DefaultValue(option.Default)
                .UseConverter(item =>
                {
                    string label = $"{Markup.Escape(item.DisplayName)} [grey]{Markup.Escape(item.Description)}[/]";
                    return packageManager && !IsOnPath(item.Value) ? label + " [yellow](not found on PATH)[/]" : label;
                }));
        AnsiConsole.MarkupLine(CultureInfo.InvariantCulture, "[grey]{0}:[/] {1}", Markup.Escape(option.DisplayName), Markup.Escape(choice.DisplayName));
        return choice;
    }

    private static string DescribePlan(CreatePlan plan) => string.Join('\n',
    [
        $"Would create {plan.Name} ({plan.Summary}) with:",
        $"  {plan.InstallCommand}",
        $"  {plan.CreateCommand}",
        "",
        "Reproduce with the creator:",
        $"  {plan.CreatorCommand}",
    ]);

    private static string DescribeCreated(CreatePlan plan)
    {
        var lines = new List<string> { $"Created {plan.Name} in {plan.Directory}." };
        string manager = plan.Selections.First(selection => selection.Option.LongName == "package-manager").Choice.Value;
        if (!IsOnPath(manager))
        {
            lines.Add(PackageManagerInstallLinks.TryGetValue(manager, out string? link)
                ? $"{manager} was not found on PATH. Install it before the first run: {link}"
                : $"{manager} was not found on PATH. Install it before the first run.");
        }
        lines.Add("");
        lines.Add("Next:");
        lines.AddRange(plan.NextSteps.Select(step => $"  {step}"));
        lines.Add("");
        lines.Add("Create the same project again without questions:");
        lines.Add($"  {plan.CreatorCommand}");
        lines.Add("or with dotnet new:");
        lines.Add($"  {plan.InstallCommand}");
        lines.Add($"  {plan.CreateCommand}");
        return string.Join('\n', lines);
    }

    private static CommandOutcome<CreateResult> Success(CreatePlan plan, bool created, string humanOutput) =>
        CommandOutcome.Success(new CreateResult(
            plan.Name,
            plan.Directory,
            plan.Version,
            plan.Selections.ToDictionary(selection => selection.Option.LongName, selection => selection.Choice.Value, StringComparer.Ordinal),
            [plan.InstallCommand, plan.CreateCommand],
            plan.CreatorCommand,
            created)
        {
            HumanOutput = humanOutput,
        });

    private static CommandOutcome<CreateResult> Failure(CommandExitCategory category, string code, string message) =>
        CommandOutcome.Failure<CreateResult>(category, new CommandFault(code, message), diagnostics: []);

    private sealed record ProcessResult(int ExitCode, string Output);

    // dotnet new writes to the terminal directly. Captured runs and JSON
    // invocations keep the child's output off stdout, which JSON reserves.
    private static async Task<ProcessResult> RunDotNetAsync(
        IReadOnlyList<string> arguments,
        bool capture,
        bool json,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture || json,
            RedirectStandardError = capture,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("The dotnet command could not be started.");
        var output = new StringBuilder();
        Task outputPump = start.RedirectStandardOutput
            ? PumpAsync(process.StandardOutput, capture ? output : null, cancellationToken)
            : Task.CompletedTask;
        Task errorPump = start.RedirectStandardError
            ? PumpAsync(process.StandardError, output, cancellationToken)
            : Task.CompletedTask;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        await Task.WhenAll(outputPump, errorPump).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, output.ToString());
    }

    private static async Task PumpAsync(StreamReader reader, StringBuilder? capture, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (capture is null)
            {
                await Console.Error.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                lock (capture) capture.AppendLine(line);
            }
        }
    }

    internal static bool IsOnPath(string command)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return false;
        string[] extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => extensions.Any(extension => File.Exists(Path.Combine(directory, command + extension))));
    }
}
