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
    }

    private static void CreatorExposesEveryOption()
    {
        TemplateOptionModel model = TemplateOptionModel.Load();
        Equal(string.Join(',', model.Options.Select(option => option.LongName)), string.Join(',', Runic.Create.Program.OptionFlags));
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
        Equal("dotnet new runic-app --name MyApp --frontend angular --package-manager bun --host desktop --view-models reactiveui", plan.CreateCommand);
        Equal("dnx Runic.Create@1.2.3-preview.4 -- MyApp --frontend angular --package-manager bun --host desktop --view-models reactiveui", plan.CreatorCommand);
        Equal("Angular · Bun · Runic Desktop · ReactiveUI", plan.Summary);
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
