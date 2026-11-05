using System.Globalization;
using System.Text.Json;
using Runic.Platform;

namespace Runic.Application.Testing;

// Reads an appearance push (appearance/1, specs/application/README.md, "Appearance") back into the DesktopAppearance a
// host set. A member that is null or malformed stays unknown.
internal static class RunicAppearancePush
{
    internal static DesktopAppearance? Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var colorScheme = Text(root, "colorScheme") switch
        {
            "light" => DesktopColorScheme.Light,
            "dark" => DesktopColorScheme.Dark,
            _ => DesktopColorScheme.NoPreference,
        };
        DesktopContrast? contrast = Text(root, "contrast") switch
        {
            "no-preference" => DesktopContrast.NoPreference,
            "more" => DesktopContrast.More,
            "forced-colors" => DesktopContrast.ForcedColors,
            _ => null,
        };
        bool? reducedMotion = root.TryGetProperty("reducedMotion", out var motion) && motion.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? motion.GetBoolean() : null;
        double? textScale = root.TryGetProperty("textScale", out var scale) && scale.ValueKind == JsonValueKind.Number && scale.GetDouble() > 0
            ? scale.GetDouble() : null;
        var accent = Color(Text(root, "accentColor"));
        DesktopSystemColors? palette = null;
        if (root.TryGetProperty("systemColors", out var colors) && colors.ValueKind == JsonValueKind.Object
            && Color(Text(colors, "canvas")) is { } canvas && Color(Text(colors, "canvasText")) is { } canvasText
            && Color(Text(colors, "linkText")) is { } linkText && Color(Text(colors, "grayText")) is { } grayText
            && Color(Text(colors, "highlight")) is { } highlight && Color(Text(colors, "highlightText")) is { } highlightText
            && Color(Text(colors, "buttonFace")) is { } buttonFace && Color(Text(colors, "buttonText")) is { } buttonText)
            palette = new(canvas, canvasText, linkText, grayText, highlight, highlightText, buttonFace, buttonText);
        if (colorScheme == DesktopColorScheme.NoPreference && contrast is null && reducedMotion is null && textScale is null && accent is null
            && palette is null)
            return null;
        return new DesktopAppearance(colorScheme, accent, ReducedMotion: reducedMotion)
        {
            Contrast = contrast,
            SystemColors = palette,
            TextScale = textScale,
        };
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // #rrggbb, as the producer writes it.
    private static DesktopAccentColor? Color(string? text) =>
        text is { Length: 7 } && text[0] == '#'
        && int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            ? new(((rgb >> 16) & 0xFF) / 255d, ((rgb >> 8) & 0xFF) / 255d, (rgb & 0xFF) / 255d)
            : null;
}
