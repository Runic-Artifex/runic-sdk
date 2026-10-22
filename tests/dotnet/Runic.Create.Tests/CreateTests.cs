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
        Equal("frontend,package-manager,host,linux-gtk,view-models", string.Join(',', model.Options.Select(option => option.LongName)));
        // Runic Desktop and ReactiveUI are the defaults and are listed first (D018.1, D018.9); GTK 3 is the Linux default.
        Equal("react,npm,desktop,3,reactiveui", string.Join(',', model.Options.Select(option => option.Default.Value)));
        Equal("react,vue,svelte,angular", string.Join(',', model.Options[0].Choices.Select(choice => choice.Value)));
        Equal("npm,pnpm,bun", string.Join(',', model.Options[1].Choices.Select(choice => choice.Value)));
        // W319-003 D19 tmpl-03: the host and the Linux GTK version are separate choices; desktop-gtk4 is gone.
        Equal("desktop,cswebui", string.Join(',', model.Options[2].Choices.Select(choice => choice.Value)));
        Equal("3,4", string.Join(',', model.Options[3].Choices.Select(choice => choice.Value)));
        Equal("reactiveui,toolkit", string.Join(',', model.Options[4].Choices.Select(choice => choice.Value)));
        Equal("Runic Desktop", model.Options[2].Find("desktop")!.DisplayName);
        True(model.Options[2].Find("desktop")!.Description.StartsWith("Opens a native window", StringComparison.Ordinal), "Runic Desktop says it opens a native window");
        True(model.Options[2].Find("cswebui")!.Description.StartsWith("Opens a browser window", StringComparison.Ordinal), "CS-WebUI says it opens a browser window");
        Equal("GTK 4", model.Options[3].Find("4")!.DisplayName);
        Equal("CommunityToolkit.Mvvm", model.Options[4].Find("toolkit")!.DisplayName);
        True(model.Options.All(option => option.Choices.All(choice => choice.Description.Length > 0)), "every choice is described");
        Equal("tests,nix,packaging", string.Join(',', model.Toggles.Select(toggle => toggle.LongName)));
        Equal("Test project", model.Toggles[0].DisplayName);
        Equal("Nix flake", model.Toggles[1].DisplayName);
        Equal("Packaging recipes", model.Toggles[2].DisplayName);
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

    [Test, DisplayName("--linux-gtk passes the GTK version through to dotnet new")]
    public void LinuxGtkPassesThrough()
    {
        (int exitCode, string output, string error) = Run("MyApp", "--host", "desktop", "--linux-gtk", "4", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        True(ResultProperty(output, "reproduceCommand").Contains("--host desktop --linux-gtk 4 --view-models", StringComparison.Ordinal), output);
        using (JsonDocument document = JsonDocument.Parse(output))
            Equal("4", document.RootElement.GetProperty("payload").GetProperty("options").GetProperty("linux-gtk").GetString());

        // A CS-WebUI project does not use Runic Desktop's GTK, so the creator leaves --linux-gtk out unless it is passed.
        (exitCode, output, error) = Run("MyApp", "--host", "cswebui", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        True(!ResultProperty(output, "reproduceCommand").Contains("--linux-gtk", StringComparison.Ordinal), output);
        using (JsonDocument document = JsonDocument.Parse(output))
            True(!document.RootElement.GetProperty("payload").GetProperty("options").TryGetProperty("linux-gtk", out _), output);
        (exitCode, output, error) = Run("MyApp", "--host", "cswebui", "--linux-gtk", "4", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        True(ResultProperty(output, "reproduceCommand").Contains("--host cswebui --linux-gtk 4", StringComparison.Ordinal), output);

        (exitCode, _, _) = Run("MyApp", "--linux-gtk", "5", "--yes", "--dry-run", "--format", "json");
        Equal("2", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (exitCode, _, _) = Run("MyApp", "--host", "desktop-gtk4", "--yes", "--dry-run", "--format", "json");
        Equal("2", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test, DisplayName("project names start with a letter")]
    public void ProjectNamesAreValidated()
    {
        foreach (string name in new[] { "MyApp", "my-app", "Company.Product", "a" })
            True(CreateApplication.IsValidName(name), name);
        foreach (string name in new[] { "", "9lives", "-app", "my app", "../escape", "app/child" })
            True(!CreateApplication.IsValidName(name), name);
    }

    [Test, DisplayName("the template install is checked with English SDK messages")]
    public void InstallCheckDoesNotDependOnSdkLanguage()
    {
        // W319-003 create-09: exit code 106 covers other install failures too, and a localized SDK does not print
        // "is already installed", which turned an installed template into RCREATE004.
        var install = CreateApplication.CreateDotNetStartInfo(["new", "install", "Runic.Application.Templates@1.0.0"], capture: true, json: false);
        Equal("en", install.Environment["DOTNET_CLI_UI_LANGUAGE"]);
        Equal("new|install|Runic.Application.Templates@1.0.0", string.Join('|', install.ArgumentList));
        True(install.RedirectStandardOutput && install.RedirectStandardError, "the install output is captured");
        // dotnet new runic-app writes to the terminal in the user's language.
        var create = CreateApplication.CreateDotNetStartInfo(["new", "runic-app"], capture: false, json: false);
        True(!create.Environment.ContainsKey("DOTNET_CLI_UI_LANGUAGE") || create.Environment["DOTNET_CLI_UI_LANGUAGE"] == Environment.GetEnvironmentVariable("DOTNET_CLI_UI_LANGUAGE"),
            "dotnet new keeps the user's language");

        True(CreateApplication.IsAlreadyInstalled(106, "The following template packages will be installed:\n   Runic.Application.Templates@1.0.0\n\nRunic.Application.Templates@1.0.0 is already installed."), "already installed");
        True(!CreateApplication.IsAlreadyInstalled(106, "Runic.Application.Templates@9.9.9 could not be installed, the package does not exist."), "another 106 failure");
        True(!CreateApplication.IsAlreadyInstalled(1, "is already installed"), "another exit code");
    }

    [Test, DisplayName("plans print the same choices for dotnet new and the creator")]
    public void PlansAreReproducible()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        CreatePlan plan = new("MyApp", null, "1.2.3-preview.4",
            model.Options.Select(option => new OptionSelection(option, option.Choices[^1])).ToArray(), null);
        Equal("dotnet new install Runic.Application.Templates@1.2.3-preview.4", plan.InstallCommand);
        Equal("dotnet new runic-app --name MyApp --frontend angular --package-manager bun --host cswebui --linux-gtk 4 --view-models toolkit", plan.CreateCommand);
        Equal("dnx Runic.Create@1.2.3-preview.4 -- MyApp --frontend angular --package-manager bun --host cswebui --linux-gtk 4 --view-models toolkit", plan.CreatorCommand);
        Equal("Angular · Bun · CS-WebUI · GTK 4 · CommunityToolkit.Mvvm", plan.Summary);
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
            Equal(destination, ResultProperty(output, "directory"));
            Equal(Path.GetFullPath(destination), ResultProperty(output, "fullPath"));
            True(ResultProperty(output, "reproduceCommand").Contains("--output ", StringComparison.Ordinal), output);
            True(!Directory.Exists(destination), destination);
        }
    }

    // W319-003 D46: the hidden former name of --output and its conflict diagnostic RCREATE006 are gone.
    [Test, DisplayName("--directory is no longer an option")]
    public void DirectoryIsNotAnOption()
    {
        string destination = Path.Combine(Path.GetTempPath(), "runic-create-" + Guid.NewGuid().ToString("N"));
        (int exitCode, _, string error) = Run("MyApp", "--directory", destination, "--yes", "--dry-run");
        Equal("2", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        True(error.Contains("--directory", StringComparison.Ordinal) && !error.Contains("RCREATE006", StringComparison.Ordinal), error);
        True(!Directory.Exists(destination), destination);

        (_, string help, _) = Run("--help");
        True(help.Contains("--output", StringComparison.Ordinal), help);
        True(!help.Contains("--directory", StringComparison.Ordinal), help);
    }

    // W319-003 create-04: the creator's grammar is its one command, so "create" is not a command spelling.
    [Test, DisplayName("create is the root command, not a command spelling")]
    public void CreateIsTheRootCommand()
    {
        (int exitCode, string output, string error) = Run("create", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + error);
        Equal("create", ResultProperty(output, "name"));
        using (JsonDocument document = JsonDocument.Parse(output))
            Equal("create", document.RootElement.GetProperty("command").GetString());

        (exitCode, _, error) = Run("create", "MyApp", "--yes", "--dry-run");
        Equal("2", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        True(error.Length > 0, "two positionals are a usage error");

        (exitCode, string help, _) = Run("--help");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        True(help.Contains("Create a Runic application.", StringComparison.Ordinal), help);
        True(help.Contains("--frontend", StringComparison.Ordinal), help);
    }

    // W319-003 D13: 0 success, 1 the project could not be created, 2 usage or validation,
    // 3 internal failure, 130 cancelled. Runic Command Line's default policy has these codes.
    [Test, DisplayName("exit codes follow the Runic tool table")]
    public void ExitCodesFollowTheToolTable()
    {
        IExitCodePolicy policy = Runic.Create.Program.CreateApp(new CaptureConsole(), _ => null).ExitCodePolicy;
        string codes = string.Join(",", Enum.GetValues<CommandExitCategory>().Select(category =>
            $"{category}={policy.GetExitCode(category).ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
        Equal(
            "Success=0,Usage=2,Validation=2,Cancelled=130,Unavailable=1,CommandFailure=1,HostFailure=3",
            codes);
    }

    [Test, DisplayName("--format selects JSON output")]
    public void FormatSelectsJson()
    {
        (int exitCode, string output, _) = Run("MyApp", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Equal("MyApp", ResultProperty(output, "directory"));
    }

    // runic.create/2 names its properties in camelCase. Runic.CommandLine 0.6.0-preview.3 and later
    // serialize through the JSON context's options, so the names must not depend on them.
    [Test, DisplayName("runic.create/2 writes its camelCase payload")]
    public void JsonPayloadKeepsItsContract()
    {
        (int exitCode, string output, _) = Run("MyApp", "--yes", "--dry-run", "--format", "json");
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement root = document.RootElement;
        Equal("runic.create/2", root.GetProperty("payloadType").GetString());
        JsonElement payload = root.GetProperty("payload");
        Equal(
            "name,directory,fullPath,version,options,toggles,commands,reproduceCommand,created",
            string.Join(",", payload.EnumerateObject().Select(property => property.Name)));
        // Options and toggles are keyed by the template's option names, not by a naming policy.
        Equal(string.Join(",", Runic.Create.Program.OptionFlags),
            string.Join(",", payload.GetProperty("options").EnumerateObject().Select(property => property.Name)));
        Equal("tests=False,nix=False,packaging=False",
            string.Join(",", payload.GetProperty("toggles").EnumerateObject().Select(property => $"{property.Name}={property.Value.GetBoolean()}")));
        Equal(Path.GetFullPath("MyApp"), payload.GetProperty("fullPath").GetString());
        True(!payload.GetProperty("created").GetBoolean(), "a dry run creates nothing");
    }

    [Test, DisplayName("runic.create/2 serializes to the documented JSON text")]
    public void JsonPayloadSnapshot()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        OptionSelection[] defaults = model.Options.Select(option => new OptionSelection(option, option.Default)).ToArray();
        CreatePlan plan = new("MyApp", "apps/MyApp", "1.0.0", defaults, null, [model.Toggles[0]]);
        CreateResult result = CreateApplication.ResultOf(model, plan, "/work/apps/MyApp", created: true);
        Equal(
            "{\"name\":\"MyApp\",\"directory\":\"apps/MyApp\",\"fullPath\":\"/work/apps/MyApp\",\"version\":\"1.0.0\"," +
            "\"options\":{\"frontend\":\"react\",\"package-manager\":\"npm\",\"host\":\"desktop\",\"linux-gtk\":\"3\",\"view-models\":\"reactiveui\"}," +
            "\"toggles\":{\"tests\":true,\"nix\":false,\"packaging\":false}," +
            "\"commands\":[\"dotnet new install Runic.Application.Templates@1.0.0\"," +
            "\"dotnet new runic-app --name MyApp --output apps/MyApp --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui --tests\"]," +
            "\"reproduceCommand\":\"dnx Runic.Create@1.0.0 -- MyApp --output apps/MyApp --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui --tests\"," +
            "\"created\":true}",
            JsonSerializer.Serialize(result, CreateJsonContext.Default.CreateResult));
    }

    // The questions are Command Line prompts on standard error, so redirected standard output still asks. Without a
    // terminal that can redraw they are numbered plain lines answered with a line of input; Enter takes the default.
    [Test, DisplayName("an interactive console asks each question on standard error")]
    public void InteractiveConsoleAsks()
    {
        var console = new CaptureConsole { IsInteractive = true, Answers = new(["2", "", "", "", "", "y", "", ""]) };
        int exitCode = Runic.Create.Program.CreateApp(console, _ => null).RunAsync(["MyApp", "--dry-run"]).GetAwaiter().GetResult();
        True(exitCode == 0, console.Error.ToString());
        True(console.Answers.Count == 0, "Every question was asked.");
        string error = console.Error.ToString();
        True(error.Contains("Create a Runic application", StringComparison.Ordinal), error);
        True(error.Contains("Frontend", StringComparison.Ordinal) && error.Contains("Test project", StringComparison.Ordinal), error);
        string output = console.Output.ToString();
        True(output.Contains("dotnet new runic-app --name MyApp --frontend vue", StringComparison.Ordinal), output);
        True(output.Contains("--tests", StringComparison.Ordinal) && !output.Contains("--nix", StringComparison.Ordinal), output);
    }

    [Test, DisplayName("RUNIC_COMMANDLINE_NO_INPUT uses the defaults instead of asking")]
    public void NoInputDoesNotAsk()
    {
        var console = new CaptureConsole { IsInteractive = true, Answers = new(["2"]) };
        int exitCode = Runic.Create.Program.CreateApp(console, name => name == "RUNIC_COMMANDLINE_NO_INPUT" ? "1" : null)
            .RunAsync(["MyApp", "--dry-run"]).GetAwaiter().GetResult();
        Equal("0", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + console.Error);
        True(console.Answers.Count == 1, "No question was asked.");
        True(console.Output.ToString().Contains("dotnet new runic-app --name MyApp --frontend react", StringComparison.Ordinal), console.Output.ToString());
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
        public bool IsInteractive { get; init; }
        public bool IsOutputRedirected => true;
        public Queue<string> Answers { get; init; } = new();
        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Answers.TryDequeue(out string? answer) ? answer : null);

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
        Equal("dotnet new runic-app --name MyApp --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui --tests", plan.CreateCommand);
        Equal("dnx Runic.Create@1.0.0 -- MyApp --frontend react --package-manager npm --host desktop --linux-gtk 3 --view-models reactiveui --tests", plan.CreatorCommand);
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
