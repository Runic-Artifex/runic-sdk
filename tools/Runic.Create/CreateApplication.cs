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
        // The questions are Spectre prompts on standard output. Runic.CommandLine 0.7 counts a
        // console as interactive when standard input and standard error are terminals, so also
        // require standard output to be one: redirected output keeps using the defaults.
        bool interactive = context.Console.IsInteractive && !context.Console.IsOutputRedirected &&
            !request.UseDefaults && context.OutputMode == CommandOutputMode.Human;
        TemplateOptionModel model = TemplateOptionModel.Load();

        if (!TryResolveExplicit(model, request.Options, out Dictionary<string, TemplateChoice> explicitChoices, out string? usage))
            return Failure(CommandExitCategory.Usage, "RCREATE001", usage!);

        string name = request.Name ?? (interactive ? AskName() : DefaultName);
        if (!IsValidName(name))
        {
            return Failure(CommandExitCategory.Usage, "RCREATE002",
                $"'{name}' is not a valid project name. Start with a letter and use letters, digits, '.', '_' or '-'.");
        }

        bool Given(TemplateToggle toggle) => request.Toggles.TryGetValue(toggle.LongName, out bool on) && on;
        if (interactive && (explicitChoices.Count < model.Options.Count || !model.Toggles.All(Given)))
        {
            AnsiConsole.MarkupLine(CultureInfo.InvariantCulture, "[bold]Create a Runic application[/] [grey]· {0}[/]", Markup.Escape(version));
        }
        var selections = new List<OptionSelection>();
        foreach (TemplateOption option in model.Options)
        {
            // --linux-gtk selects Runic Desktop's GTK version. A CS-WebUI project does not use it, so the creator
            // neither asks for it nor prints it there unless it was passed.
            if (option.LongName == "linux-gtk" && !explicitChoices.ContainsKey(option.LongName) &&
                selections.Find(selection => selection.Option.LongName == "host")?.Choice.Value is not (null or "desktop"))
            {
                continue;
            }
            TemplateChoice choice = explicitChoices.TryGetValue(option.LongName, out TemplateChoice? given)
                ? given
                : interactive ? Ask(option) : option.Default;
            selections.Add(new OptionSelection(option, choice));
        }

        // A toggle the user did not pass is asked for, or stays off.
        TemplateToggle[] enabled = model.Toggles.Where(toggle => Given(toggle) || (interactive && Ask(toggle))).ToArray();

        var plan = new CreatePlan(name, request.Directory, version, selections, request.TemplateSource, enabled);
        string directory = Path.GetFullPath(plan.Directory);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
        {
            return Failure(CommandExitCategory.Usage, "RCREATE003",
                $"The directory '{plan.Directory}' already exists and is not empty. Choose another name or --output.");
        }

        if (request.DryRun)
        {
            return Success(model, plan, directory, created: false, DescribePlan(plan));
        }

        await console.WriteAsync(new Markup(
            $"Creating [bold]{Markup.Escape(plan.Name)}[/] [grey]({Markup.Escape(plan.Summary)})[/]\n"),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        bool json = context.OutputMode == CommandOutputMode.Json;
        ProcessResult install = await RunDotNetAsync(plan.InstallArguments, capture: true, json, cancellationToken).ConfigureAwait(false);
        if (install.ExitCode != 0 && !IsAlreadyInstalled(install.ExitCode, install.Output))
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

        return Success(model, plan, directory, created: true, DescribeCreated(plan));
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

    private static bool Ask(TemplateToggle toggle)
    {
        bool on = AnsiConsole.Prompt(
            new ConfirmationPrompt($"[bold]{Markup.Escape(toggle.DisplayName)}[/] [grey]{Markup.Escape(toggle.Description)}[/]")
            {
                DefaultValue = false,
            });
        AnsiConsole.MarkupLine(CultureInfo.InvariantCulture, "[grey]{0}:[/] {1}", Markup.Escape(toggle.DisplayName), on ? "Yes" : "No");
        return on;
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

    private static CommandOutcome<CreateResult> Success(
        TemplateOptionModel model, CreatePlan plan, string fullPath, bool created, string humanOutput) =>
        CommandOutcome.Success(ResultOf(model, plan, fullPath, created) with { HumanOutput = humanOutput });

    internal static CreateResult ResultOf(TemplateOptionModel model, CreatePlan plan, string fullPath, bool created) => new(
        plan.Name,
        plan.Directory,
        fullPath,
        plan.Version,
        plan.Selections.ToDictionary(selection => selection.Option.LongName, selection => selection.Choice.Value, StringComparer.Ordinal),
        model.Toggles.ToDictionary(toggle => toggle.LongName, toggle => plan.IsEnabled(toggle.LongName), StringComparer.Ordinal),
        [plan.InstallCommand, plan.CreateCommand],
        plan.CreatorCommand,
        created);

    // Human output ends with the code's catalog entry at this release's tag;
    // Runic.CommandLine writes it before the fault line and omits it from JSON.
    internal static CommandOutcome<CreateResult> Failure(CommandExitCategory category, string code, string message) =>
        CommandOutcome.Failure<CreateResult>(category, new CommandFault(code, message), diagnostics: [],
            humanOutput: $"Help for {code}: {RunicReleaseLinks.Diagnostic(code)}\n");

    private sealed record ProcessResult(int ExitCode, string Output);

    /// <summary>
    /// Whether <c>dotnet new install</c> failed only because this version is installed already. Its exit code 106
    /// covers other install failures too, so the message decides; the captured install runs with English SDK
    /// messages (see <see cref="CreateDotNetStartInfo"/>), so a localized SDK does not turn the no-op into RCREATE004.
    /// </summary>
    internal static bool IsAlreadyInstalled(int exitCode, string output) =>
        exitCode == 106 && output.Contains("is already installed", StringComparison.Ordinal);

    internal static ProcessStartInfo CreateDotNetStartInfo(IReadOnlyList<string> arguments, bool capture, bool json)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = capture || json,
            RedirectStandardError = capture,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        // Captured output is parsed, so it must not depend on the user's SDK language. dotnet new itself writes
        // to the terminal in the user's language.
        if (capture) start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        return start;
    }

    // dotnet new writes to the terminal directly. Captured runs and JSON
    // invocations keep the child's output off stdout, which JSON reserves.
    private static async Task<ProcessResult> RunDotNetAsync(
        IReadOnlyList<string> arguments,
        bool capture,
        bool json,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = CreateDotNetStartInfo(arguments, capture, json);
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
