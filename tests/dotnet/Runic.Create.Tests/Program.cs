using System;
using System.Collections.Generic;
using System.Linq;
using Runic.Create;

namespace Runic.Create.Tests;

internal static class Program
{
    public static int Main()
    {
        (string Name, Action Body)[] tests =
        [
            ("the option model mirrors the template's visible choices", ModelMirrorsTemplate),
            ("the creator exposes a flag for every template option", CreatorExposesEveryOption),
            ("explicit choices match case-insensitively and reject unknown values", ExplicitChoicesAreValidated),
            ("project names start with a letter", ProjectNamesAreValidated),
            ("plans print the same choices for dotnet new and the creator", PlansAreReproducible),
            ("plans quote values a shell would split", PlansQuoteShellValues),
            ("the test project toggle is a presence flag in every command", TestsTogglePassesThrough),
        ];

        int failures = 0;
        foreach ((string name, Action body) in tests)
        {
            try
            {
                body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}");
                Console.Error.WriteLine(exception);
            }
        }

        Console.WriteLine($"{tests.Length - failures}/{tests.Length} Runic.Create tests passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void ModelMirrorsTemplate()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        Equal("frontend,package-manager,host,view-models", string.Join(',', model.Options.Select(option => option.LongName)));
        Equal("react,npm,cswebui,toolkit", string.Join(',', model.Options.Select(option => option.Default.Value)));
        Equal("react,vue,svelte,angular", string.Join(',', model.Options[0].Choices.Select(choice => choice.Value)));
        Equal("npm,pnpm,bun", string.Join(',', model.Options[1].Choices.Select(choice => choice.Value)));
        Equal("Runic Desktop", model.Options[2].Find("desktop")!.DisplayName);
        True(model.Options.All(option => option.Choices.All(choice => choice.Description.Length > 0)), "every choice is described");
        Equal("tests", string.Join(',', model.Toggles.Select(toggle => toggle.LongName)));
        Equal("Test project", model.Toggles[0].DisplayName);
        True(model.Toggles.All(toggle => toggle.Description.Length > 0), "every toggle is described");
    }

    private static void CreatorExposesEveryOption()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        Equal(string.Join(',', model.Options.Select(option => option.LongName)), string.Join(',', Runic.Create.Program.OptionFlags));
        Equal(string.Join(',', model.Toggles.Select(toggle => toggle.LongName)), string.Join(',', Runic.Create.Program.ToggleFlags));
    }

    private static void ExplicitChoicesAreValidated()
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

    private static void ProjectNamesAreValidated()
    {
        foreach (string name in new[] { "MyApp", "my-app", "Company.Product", "a" })
            True(CreateApplication.IsValidName(name), name);
        foreach (string name in new[] { "", "9lives", "-app", "my app", "../escape", "app/child" })
            True(!CreateApplication.IsValidName(name), name);
    }

    private static void PlansAreReproducible()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        CreatePlan plan = new("MyApp", null, "1.2.3-preview.4",
            model.Options.Select(option => new OptionSelection(option, option.Choices[^1])).ToArray(), null);
        Equal("dotnet new install Runic.Application.Templates@1.2.3-preview.4", plan.InstallCommand);
        Equal("dotnet new runic-app --name MyApp --frontend angular --package-manager bun --host desktop-gtk4 --view-models reactiveui", plan.CreateCommand);
        Equal("dnx Runic.Create@1.2.3-preview.4 -- MyApp --frontend angular --package-manager bun --host desktop-gtk4 --view-models reactiveui", plan.CreatorCommand);
        Equal("Angular · Bun · Runic Desktop with GTK 4 · ReactiveUI", plan.Summary);
        Equal("cd MyApp|dotnet tool restore|dotnet runic dev", string.Join('|', plan.NextSteps));
    }

    private static void PlansQuoteShellValues()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        CreatePlan plan = new("MyApp", "My Projects/it's", "1.0.0",
            model.Options.Select(option => new OptionSelection(option, option.Default)).ToArray(), "/feeds/local nuget");
        Equal("dotnet new install Runic.Application.Templates@1.0.0 --nuget-source '/feeds/local nuget'", plan.InstallCommand);
        True(plan.CreateCommand.Contains("--output 'My Projects/it'\"'\"'s'", StringComparison.Ordinal), plan.CreateCommand);
        True(plan.CreatorCommand.Contains("--directory 'My Projects/it'\"'\"'s'", StringComparison.Ordinal), plan.CreatorCommand);
        Equal("cd 'My Projects/it'\"'\"'s'", plan.NextSteps[0]);
    }

    private static void TestsTogglePassesThrough()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        OptionSelection[] defaults = model.Options.Select(option => new OptionSelection(option, option.Default)).ToArray();
        CreatePlan plan = new("MyApp", null, "1.0.0", defaults, null, model.Toggles);
        Equal("dotnet new runic-app --name MyApp --frontend react --package-manager npm --host cswebui --view-models toolkit --tests", plan.CreateCommand);
        Equal("dnx Runic.Create@1.0.0 -- MyApp --frontend react --package-manager npm --host cswebui --view-models toolkit --tests", plan.CreatorCommand);
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
