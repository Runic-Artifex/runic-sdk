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
    // The members the host decided for this window rather than read from the desktop (the push's override member).
    private HostDecidedAppearance _decided;

    internal DesktopAppearance? Appearance
    {
        get { lock (_appearanceGate) return _appearance; }
    }

    internal HostDecidedAppearance DecidedAppearance
    {
        get { lock (_appearanceGate) return _decided; }
    }

    // Sets the window's appearance and pushes it to every connection that uses appearance/1. False when it did not change.
    internal bool SetAppearance(DesktopAppearance? appearance) => SetAppearance(appearance, HostDecidedAppearance.None);

    // The same with the members the host decided for the window, such as a hosted region's appearance override
    // (host-protocol.md, Appearance in a region): the push lists them in override, so the page's combined appearance
    // takes them over its engine's media queries.
    internal bool SetAppearance(DesktopAppearance? appearance, HostDecidedAppearance decided)
    {
        lock (_appearanceGate)
        {
            if (Equals(_appearance, appearance) && _decided == decided) return false;
            _appearance = appearance;
            _decided = decided;
            List<string> targets = [];
            lock (_gate)
                foreach (var connection in _connections.Values)
                    if (connection.HelloCompleted && connection.UsesAppearance && connection.IsOpen) targets.Add(connection.Key);
            if (targets.Count == 0) return true;
            var push = AppearancePush(appearance, decided);
            foreach (var target in targets) BridgeTargetedPushes.For(_transport).Send(target, push);
        }
        return true;
    }

    // After a successful hello that listed the feature: the window's appearance, every member null while it is unknown,
    // so a page that reconnects never keeps an appearance it missed a change of. Under the gate, so a change that raced
    // the hello is queued after this push.
    private void PushAppearanceAfterHello(BridgeConnection connection)
    {
        lock (_appearanceGate) BridgeTargetedPushes.For(_transport).Send(connection.Key, AppearancePush(_appearance, _decided));
    }

    // Whether the hello lists appearance/1. TryReadFeatures already checked the array's shape.
    private static bool ListsAppearance(JsonElement root) => ListsFeature(root, AppearanceFeature);

    // Whether the hello lists a feature. TryReadFeatures already checked the array's shape.
    private static bool ListsFeature(JsonElement root, string name)
    {
        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) return false;
        foreach (var feature in features.EnumerateArray())
            if (feature.ValueKind == JsonValueKind.String && feature.ValueEquals(name)) return true;
        return false;
    }

    // Every member, null when unknown; a window without an appearance pushes every member null. override lists the
    // decided members whose value is known, and is absent when it lists none.
    internal static byte[] AppearancePush(DesktopAppearance? appearance, HostDecidedAppearance decided = HostDecidedAppearance.None) => BridgeMessages.Write(writer =>
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
        decided = Known(appearance, decided);
        if (decided != HostDecidedAppearance.None)
        {
            writer.WriteStartArray("override");
            if (decided.HasFlag(HostDecidedAppearance.ColorScheme)) writer.WriteStringValue("colorScheme");
            if (decided.HasFlag(HostDecidedAppearance.Contrast)) writer.WriteStringValue("contrast");
            if (decided.HasFlag(HostDecidedAppearance.ReducedMotion)) writer.WriteStringValue("reducedMotion");
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    });

    // The decided members whose value the push carries: a page ignores a listed member whose value is null.
    private static HostDecidedAppearance Known(DesktopAppearance? appearance, HostDecidedAppearance decided)
    {
        if (appearance?.ColorScheme is not (DesktopColorScheme.Light or DesktopColorScheme.Dark)) decided &= ~HostDecidedAppearance.ColorScheme;
        if (appearance?.Contrast is not (DesktopContrast.NoPreference or DesktopContrast.More or DesktopContrast.ForcedColors))
            decided &= ~HostDecidedAppearance.Contrast;
        if (appearance?.ReducedMotion is null) decided &= ~HostDecidedAppearance.ReducedMotion;
        return decided;
    }

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

// The members of a window's appearance that its host decided rather than read from the desktop (the appearance push's
// override member, specs/application/README.md, Appearance).
[Flags]
internal enum HostDecidedAppearance
{
    None = 0,
    ColorScheme = 1,
    Contrast = 2,
    ReducedMotion = 4,
}
