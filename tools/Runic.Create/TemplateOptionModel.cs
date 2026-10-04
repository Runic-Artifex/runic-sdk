using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Runic.Create;

internal sealed record TemplateChoice(string Value, string DisplayName, string Description);

internal sealed record TemplateOption(
    string Symbol,
    string LongName,
    string DisplayName,
    string Description,
    string DefaultValue,
    IReadOnlyList<TemplateChoice> Choices)
{
    public string Flag => "--" + LongName;

    public TemplateChoice Default => Find(DefaultValue)
        ?? throw new InvalidDataException($"The {Symbol} default '{DefaultValue}' is not one of its choices.");

    public TemplateChoice? Find(string value) =>
        Choices.FirstOrDefault(choice => string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The visible choice parameters of the runic-app template, read from the
/// template's own template.json and dotnetcli.host.json. The creator, the
/// template, and the documentation picker share these declarations.
/// </summary>
internal sealed class TemplateOptionModel(IReadOnlyList<TemplateOption> options)
{
    public const string TemplatePackage = "Runic.Application.Templates";
    public const string TemplateShortName = "runic-app";

    public IReadOnlyList<TemplateOption> Options { get; } = options;

    public static TemplateOptionModel Load() =>
        Parse(ReadResource("Runic.Create.template.json"), ReadResource("Runic.Create.dotnetcli.host.json"));

    public static TemplateOptionModel Parse(string templateJson, string hostJson)
    {
        using JsonDocument template = JsonDocument.Parse(templateJson);
        using JsonDocument host = JsonDocument.Parse(hostJson);
        host.RootElement.TryGetProperty("symbolInfo", out JsonElement symbolInfo);

        var options = new List<TemplateOption>();
        foreach (JsonProperty symbol in template.RootElement.GetProperty("symbols").EnumerateObject())
        {
            JsonElement value = symbol.Value;
            if (String(value, "type") != "parameter" || String(value, "datatype") != "choice") continue;
            JsonElement info = symbolInfo.ValueKind == JsonValueKind.Object &&
                symbolInfo.TryGetProperty(symbol.Name, out JsonElement found) ? found : default;
            if (info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("isHidden", out JsonElement hidden) && hidden.ValueKind == JsonValueKind.True) continue;

            TemplateChoice[] choices = value.GetProperty("choices").EnumerateArray()
                .Select(choice => new TemplateChoice(
                    String(choice, "choice") ?? throw new InvalidDataException($"A {symbol.Name} choice has no value."),
                    String(choice, "displayName") ?? String(choice, "choice")!,
                    String(choice, "description") ?? ""))
                .ToArray();
            var option = new TemplateOption(
                symbol.Name,
                (info.ValueKind == JsonValueKind.Object ? String(info, "longName") : null) ?? symbol.Name,
                String(value, "displayName") ?? symbol.Name,
                String(value, "description") ?? "",
                String(value, "defaultValue") ?? choices[0].Value,
                choices);
            _ = option.Default;
            options.Add(option);
        }
        return new TemplateOptionModel(options);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string ReadResource(string name)
    {
        using Stream stream = typeof(TemplateOptionModel).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The creator is missing its {name} resource.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
