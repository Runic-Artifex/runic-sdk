using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Runic.CommandLine;
using Runic.Create;
using TUnit.Core;

namespace Runic.Create.Tests;

internal sealed class CreateTests
{
    [Test, DisplayName("faults link to their catalog entry at the release tag")]
    public void FaultsLinkToCatalog()
    {
        var outcome = CreateApplication.Failure(Runic.CommandLine.CommandExitCategory.Usage, "RCREATE002", "Invalid name.");
        Equal("RCREATE002", outcome.Fault!.Code);
        string help = outcome.HumanOutput ?? string.Empty;
        True(help.StartsWith("Help for RCREATE002: https://github.com/Runic-Artifex/runic-sdk/blob/v", StringComparison.Ordinal)
            && help.EndsWith("/docs/diagnostics.md#rcreate002\n", StringComparison.Ordinal), help);
    }

    [Test, DisplayName("the option model mirrors the template's visible choices")]
    public void ModelMirrorsTemplate()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        Equal("frontend,package-manager,host,view-models", string.Join(',', model.Options.Select(option => option.LongName)));
        // Runic Desktop and ReactiveUI are the defaults and are listed first (D018.1, D018.9).
        Equal("react,npm,desktop,reactiveui", string.Join(',', model.Options.Select(option => option.Default.Value)));
        Equal("react,vue,svelte,angular", string.Join(',', model.Options[0].Choices.Select(choice => choice.Value)));
        Equal("npm,pnpm,bun", string.Join(',', model.Options[1].Choices.Select(choice => choice.Value)));
        Equal("desktop,desktop-gtk4,cswebui", string.Join(',', model.Options[2].Choices.Select(choice => choice.Value)));
        Equal("reactiveui,toolkit", string.Join(',', model.Options[3].Choices.Select(choice => choice.Value)));
        Equal("Runic Desktop", model.Options[2].Find("desktop")!.DisplayName);
        True(model.Options[2].Find("desktop")!.Description.StartsWith("Opens a native window", StringComparison.Ordinal), "Runic Desktop says it opens a native window");
        True(model.Options[2].Find("cswebui")!.Description.StartsWith("Opens a browser window", StringComparison.Ordinal), "CS-WebUI says it opens a browser window");
        Equal("CommunityToolkit.Mvvm", model.Options[3].Find("toolkit")!.DisplayName);
        True(model.Options.All(option => option.Choices.All(choice => choice.Description.Length > 0)), "every choice is described");
        Equal("tests,nix", string.Join(',', model.Toggles.Select(toggle => toggle.LongName)));
        Equal("Test project", model.Toggles[0].DisplayName);
        Equal("Nix flake", model.Toggles[1].DisplayName);
        True(model.Toggles.All(toggle => toggle.Description.Length > 0), "every toggle is described");
        Equal(string.Join(',', model.Toggles.Select(toggle => toggle.LongName)), string.Join(',', Runic.Create.Program.ToggleFlags));
    }

    [Test, DisplayName("the creator exposes a flag for every template option")]
    public void CreatorExposesEveryOption()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        Equal(string.Join(',', model.Options.Select(option => option.LongName)), string.Join(',', Runic.Create.Program.OptionFlags));
    }

    [Test, DisplayName("explicit choices match case-insensitively and reject unknown values")]
    public void ExplicitChoicesAreValidated()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        True(CreateApplication.TryResolveExplicit(model,
            new Dictionary<string, string> { ["frontend"] = "Svelte", ["host"] = "", ["view-models"] = "reactiveui" },
            out Dictionary<string, TemplateChoice> choices, out _), "valid choices resolve");
        Equal("svelte", choices["frontend"].Value);
        True(!choices.ContainsKey("host"), "an empty value is left for the prompt or default");
        True(!CreateApplication.TryResolveExplicit(model,
            new Dictionary<string, string> { ["package-manager"] = "yarn" }, out _, out string? error), "unknown choice fails");
        Equal("Unknown --package-manager 'yarn'. Choose npm, pnpm, bun.", error);
    }

    [Test, DisplayName("project names start with a letter")]
    public void ProjectNamesAreValidated()
    {
        foreach (string name in new[] { "MyApp", "my-app", "Company.Product", "a" })
            True(CreateApplication.IsValidName(name), name);
        foreach (string name in new[] { "", "9lives", "-app", "my app", "../escape", "app/child" })
            True(!CreateApplication.IsValidName(name), name);
    }

    [Test, DisplayName("plans print the same choices for dotnet new and the creator")]
    public void PlansAreReproducible()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        CreatePlan plan = new("MyApp", null, "1.2.3-preview.4",
            model.Options.Select(option => new OptionSelection(option, option.Choices[^1])).ToArray(), null);
        Equal("dotnet new install Runic.Application.Templates@1.2.3-preview.4", plan.InstallCommand);
        Equal("dotnet new runic-app --name MyApp --frontend angular --package-manager bun --host cswebui --view-models toolkit", plan.CreateCommand);
        Equal("dnx Runic.Create@1.2.3-preview.4 -- MyApp --frontend angular --package-manager bun --host cswebui --view-models toolkit", plan.CreatorCommand);
        Equal("Angular · Bun · CS-WebUI · CommunityToolkit.Mvvm", plan.Summary);
        Equal("cd MyApp|dotnet tool restore|dotnet runic dev", string.Join('|', plan.NextSteps));
    }

    [Test, DisplayName("plans quote values a shell would split")]
    public void PlansQuoteShellValues()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        CreatePlan plan = new("MyApp", "My Projects/it's", "1.0.0",
            model.Options.Select(option => new OptionSelection(option, option.Default)).ToArray(), "/feeds/local nuget");
        Equal("dotnet new install Runic.Application.Templates@1.0.0 --nuget-source '/feeds/local nuget'", plan.InstallCommand);
        True(plan.CreateCommand.Contains("--output 'My Projects/it'\"'\"'s'", StringComparison.Ordinal), plan.CreateCommand);
        True(plan.CreatorCommand.Contains("--output 'My Projects/it'\"'\"'s'", StringComparison.Ordinal), plan.CreatorCommand);
        Equal("cd 'My Projects/it'\"'\"'s'", plan.NextSteps[0]);
    }

    [Test, DisplayName("-o and --output choose the destination as they do for dotnet new")]
    public void OutputChoosesTheDestination()
    {
        string destination = Path.Combine(Path.GetTempPath(), "runic-create-" + Guid.NewGuid().ToString("N"), "apps", "MyApp");
        foreach (string flag in new[] { "-o", "--output" })
        {
            (int exitCode, string output, string error) = Run("MyApp", flag, destination, "--yes", "--dry-run", "--format", "json");
            Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
            Equal(destination, ResultProperty(output, "Directory"));
            True(ResultProperty(output, "ReproduceCommand").Contains("--output ", StringComparison.Ordinal), output);
            True(!Directory.Exists(destination), destination);
        }
    }

    [Test, DisplayName("--directory remains a hidden alias of --output")]
    public void DirectoryIsAFormerAlias()
    {
        string destination = Path.Combine(Path.GetTempPath(), "runic-create-" + Guid.NewGuid().ToString("N"));
        (int exitCode, string output, _) = Run("MyApp", "--directory", destination, "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Equal(destination, ResultProperty(output, "Directory"));

        (exitCode, _, string error) = Run("MyApp", "--directory", destination, "-o", destination + "-other", "--yes", "--dry-run");
        Equal("2", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        True(error.Contains("RCREATE006", StringComparison.Ordinal), error);

        (_, string help, _) = Run("create", "--help");
        True(help.Contains("--output", StringComparison.Ordinal), help);
        True(!help.Contains("--directory", StringComparison.Ordinal), help);
    }

    [Test, DisplayName("--format selects JSON output")]
    public void FormatSelectsJson()
    {
        (int exitCode, string output, _) = Run("MyApp", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Equal("MyApp", ResultProperty(output, "Directory"));
    }

    private static (int ExitCode, string Output, string Error) Run(params string[] arguments)
    {
        var console = new CaptureConsole();
        int exitCode = Runic.Create.Program.CreateApp(console, _ => null).RunAsync(arguments).GetAwaiter().GetResult();
        return (exitCode, console.Output.ToString(), console.Error.ToString());
    }

    private static string ResultProperty(string json, string name)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("payload").GetProperty(name).GetString() ?? "";
    }

    private sealed class CaptureConsole : ICommandConsole
    {
        public StringBuilder Output { get; } = new();
        public StringBuilder Error { get; } = new();
        public bool IsErrorRedirected => true;
        public bool IsInputRedirected => true;
        public bool IsInteractive => false;
        public bool IsOutputRedirected => true;
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ValueTask.FromResult<string?>(null);

        public ValueTask WriteErrorAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            Error.Append(value.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutAsync(ReadOnlyMemory<char> value, CancellationToken cancellationToken)
        {
            Output.Append(value.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteOutBytesAsync(ReadOnlyMemory<byte> value, CancellationToken cancellationToken)
        {
            Output.Append(Encoding.UTF8.GetString(value.Span));
            return ValueTask.CompletedTask;
        }
    }

    [Test, DisplayName("the test project toggle is a presence flag in every command")]
    public void TestsTogglePassesThrough()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        OptionSelection[] defaults = model.Options.Select(option => new OptionSelection(option, option.Default)).ToArray();
        CreatePlan plan = new("MyApp", null, "1.0.0", defaults, null, [model.Toggles[0]]);
        Equal("dotnet new runic-app --name MyApp --frontend react --package-manager npm --host desktop --view-models reactiveui --tests", plan.CreateCommand);
        Equal("dnx Runic.Create@1.0.0 -- MyApp --frontend react --package-manager npm --host desktop --view-models reactiveui --tests", plan.CreatorCommand);
        True(plan.Summary.EndsWith(" · Test project", StringComparison.Ordinal), plan.Summary);
        Equal("dotnet test --project MyApp.Tests", plan.NextSteps[^1]);

        CreatePlan without = new("MyApp", null, "1.0.0", defaults, null);
        True(!without.CreateCommand.Contains("--tests", StringComparison.Ordinal), without.CreateCommand);
        Equal("cd MyApp|dotnet tool restore|dotnet runic dev", string.Join('|', without.NextSteps));

        True(Throws(() => TemplateOptionModel.Parse(
            """{ "symbols": { "tests": { "type": "parameter", "datatype": "bool", "defaultValue": "true" } } }""", "{}")),
            "a Boolean parameter that defaults to true is rejected");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (System.IO.InvalidDataException) { return true; }
    }

    private static void Equal(string? expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected '{expected}' but found '{actual}'.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
