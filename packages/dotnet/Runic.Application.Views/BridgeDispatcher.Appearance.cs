using System.Globalization;
using System.Text.Json;
using Runic.Platform;

namespace Runic.Application.Views;

// appearance/1 (specs/application/README.md, "Appearance"): the window's appearance, the desktop preferences its host
// read, pushed to the connections that use the feature after their hello and on each change.
internal sealed partial class BridgeDispatcher
{
    internal const string AppearanceFeature = "appearance/1";
    // Pushes are queued under the gate, so a connection that completes its hello while the appearance changes receives
    // the last value last.
    private readonly object _appearanceGate = new();
    private DesktopAppearance? _appearance;

    internal DesktopAppearance? Appearance
    {
        get { lock (_appearanceGate) return _appearance; }
    }

    // Sets the window's appearance and pushes it to every connection that uses appearance/1. False when it did not change.
    internal bool SetAppearance(DesktopAppearance? appearance)
    {
        lock (_appearanceGate)
        {
            if (Equals(_appearance, appearance)) return false;
            _appearance = appearance;
            List<string> targets = [];
            lock (_gate)
                foreach (var connection in _connections.Values)
                    if (connection.HelloCompleted && connection.UsesAppearance && connection.IsOpen) targets.Add(connection.Key);
            if (targets.Count == 0) return true;
            var push = AppearancePush(appearance);
            foreach (var target in targets) BridgeTargetedPushes.For(_transport).Send(target, push);
        }
        return true;
    }

    // After a successful hello that listed the feature: the window's appearance, when it has one. Under the gate, so a
    // change that raced the hello is queued after this push.
    private void PushAppearanceAfterHello(BridgeConnection connection)
    {
        lock (_appearanceGate)
            if (_appearance is not null) BridgeTargetedPushes.For(_transport).Send(connection.Key, AppearancePush(_appearance));
    }

    // Whether the hello lists appearance/1. TryReadFeatures already checked the array's shape.
    private static bool ListsAppearance(JsonElement root)
    {
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return false;
        foreach (var feature in features.EnumerateArray())
            if (feature.ValueKind == JsonValueKind.String && feature.ValueEquals(AppearanceFeature)) return true;
        return false;
    }

    // Every member, null when unknown; a window without an appearance pushes every member null.
    internal static byte[] AppearancePush(DesktopAppearance? appearance) => BridgeMessages.Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "appearance");
        switch (appearance?.ColorScheme)
        {
            case DesktopColorScheme.Light: writer.WriteString("colorScheme", "light"); break;
            case DesktopColorScheme.Dark: writer.WriteString("colorScheme", "dark"); break;
            default: writer.WriteNull("colorScheme"); break;
        }
        switch (appearance?.Contrast)
        {
            case DesktopContrast.NoPreference: writer.WriteString("contrast", "no-preference"); break;
            case DesktopContrast.More: writer.WriteString("contrast", "more"); break;
            case DesktopContrast.ForcedColors: writer.WriteString("contrast", "forced-colors"); break;
            // A value a later Runic.Platform adds is unknown to this protocol version's pages.
            default: writer.WriteNull("contrast"); break;
        }
        if (appearance?.ReducedMotion is { } reduced) writer.WriteBoolean("reducedMotion", reduced);
        else writer.WriteNull("reducedMotion");
        if (appearance?.TextScale is { } scale) writer.WriteNumber("textScale", scale);
        else writer.WriteNull("textScale");
        WriteColor(writer, "accentColor", appearance?.AccentColor);
        if (appearance?.Contrast == DesktopContrast.ForcedColors && appearance.SystemColors is { } colors)
        {
            writer.WriteStartObject("systemColors");
            WriteColor(writer, "canvas", colors.Canvas);
            WriteColor(writer, "canvasText", colors.CanvasText);
            WriteColor(writer, "linkText", colors.LinkText);
            WriteColor(writer, "grayText", colors.GrayText);
            WriteColor(writer, "highlight", colors.Highlight);
            WriteColor(writer, "highlightText", colors.HighlightText);
            WriteColor(writer, "buttonFace", colors.ButtonFace);
            WriteColor(writer, "buttonText", colors.ButtonText);
            writer.WriteEndObject();
        }
        else writer.WriteNull("systemColors");
        writer.WriteEndObject();
    });

    // #rrggbb in lowercase; a component outside [0,1] (DesktopAccentColor does not check) is clamped.
    private static void WriteColor(Utf8JsonWriter writer, string name, DesktopAccentColor? color)
    {
        if (color is null || !double.IsFinite(color.Red) || !double.IsFinite(color.Green) || !double.IsFinite(color.Blue))
        {
            writer.WriteNull(name);
            return;
        }
        writer.WriteString(name, string.Create(CultureInfo.InvariantCulture,
            $"#{Component(color.Red):x2}{Component(color.Green):x2}{Component(color.Blue):x2}"));
    }

    private static int Component(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
}
