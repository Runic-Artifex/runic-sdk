namespace Runic.Desktop.Internal;

internal enum PageZoomCommand
{
    None,
    In,
    Out,
    Reset,
}

// Browser keyboard behaviour that WebKitGTK leaves to its embedder. Shared by the
// GTK 3 host and the GTK 4 provider, which links this file.
internal static class WebKitGtkKeyboard
{
    // GdkModifierType has the same values in GTK 3 and GTK 4.
    private const uint ControlMask = 1 << 2;
    private const uint AltMask = 1 << 3;
    private const uint SuperMask = 1 << 26;
    private const uint HyperMask = 1 << 27;
    private const uint MetaMask = 1 << 28;

    // GDK key symbols.
    private const uint KeyPlus = 0x02b;
    private const uint KeyMinus = 0x02d;
    private const uint KeyZero = 0x030;
    private const uint KeyEqual = 0x03d;
    private const uint KeyUnderscore = 0x05f;
    private const uint KeyKeypadAdd = 0xffab;
    private const uint KeyKeypadSubtract = 0xffad;
    private const uint KeyKeypadZero = 0xffb0;
    private const uint LastAscii = 0x7e;

    // The key of the digit 0: evdev KEY_0 (11) plus 8, as X11 and Wayland report it.
    private const uint KeycodeZero = 19;

    internal const double DefaultZoom = 1.0;

    // Firefox's steps, from 30 % to 300 %.
    internal static readonly double[] ZoomLevels = [0.3, 0.5, 0.67, 0.8, 0.9, 1.0, 1.1, 1.2, 1.33, 1.5, 1.7, 2.0, 2.4, 3.0];

    internal static double MinimumZoom => ZoomLevels[0];

    internal static double MaximumZoom => ZoomLevels[^1];

    private const double Tolerance = 0.005;

    /// <summary>Maps a key press that the page did not handle to a zoom command.</summary>
    /// <param name="keyval">The key symbol the active layout produced.</param>
    /// <param name="state">The modifiers. Control must be held without Alt, Super, Hyper or Meta. Shift is allowed, because some layouts need it for + or _.</param>
    /// <param name="keyLevels">
    /// The symbols of the same physical key at levels 1 and 2 (unshifted and shifted) of each layout group.
    /// When the produced symbol is no zoom key, as on AZERTY, where the 0 key types à without Shift, a digit
    /// or sign on one of these levels counts, as in Firefox and Chromium.
    /// </param>
    /// <param name="keycode">
    /// The hardware keycode. Some layouts, such as Lithuanian, have the digit 0 only on a third level; the
    /// key in the place of 0 then resets the zoom when it types no ASCII character.
    /// </param>
    internal static PageZoomCommand GetZoomCommand(uint keyval, uint state, ReadOnlySpan<uint> keyLevels = default, uint keycode = 0)
    {
        if (!IsZoomModifier(state))
        {
            return PageZoomCommand.None;
        }
        var command = Map(keyval);
        foreach (var level in keyLevels)
        {
            if (command != PageZoomCommand.None)
            {
                break;
            }
            command = Map(level);
        }
        if (command == PageZoomCommand.None && keycode == KeycodeZero && keyval > LastAscii)
        {
            command = PageZoomCommand.Reset;
        }
        return command;

        static PageZoomCommand Map(uint keyval) => keyval switch
        {
            KeyPlus or KeyEqual or KeyKeypadAdd => PageZoomCommand.In,
            KeyMinus or KeyUnderscore or KeyKeypadSubtract => PageZoomCommand.Out,
            KeyZero or KeyKeypadZero => PageZoomCommand.Reset,
            _ => PageZoomCommand.None,
        };
    }

    /// <summary>Whether the modifiers are Control, with or without Shift, and no Alt, Super, Hyper or Meta.</summary>
    internal static bool IsZoomModifier(uint state) =>
        (state & ControlMask) != 0 && (state & (AltMask | SuperMask | HyperMask | MetaMask)) == 0;

    /// <summary>Maps a wheel message of the page script: 1 zooms in, -1 out.</summary>
    internal static PageZoomCommand GetWheelZoomCommand(int message) => message switch
    {
        1 => PageZoomCommand.In,
        -1 => PageZoomCommand.Out,
        _ => PageZoomCommand.None,
    };

    internal static double Apply(PageZoomCommand command, double current) => command switch
    {
        PageZoomCommand.In => ZoomIn(current),
        PageZoomCommand.Out => ZoomOut(current),
        PageZoomCommand.Reset => DefaultZoom,
        _ => current,
    };

    internal static double ZoomIn(double current)
    {
        foreach (var level in ZoomLevels)
        {
            if (level > current + Tolerance)
            {
                return level;
            }
        }
        return MaximumZoom;
    }

    internal static double ZoomOut(double current)
    {
        for (var index = ZoomLevels.Length - 1; index >= 0; index--)
        {
            if (ZoomLevels[index] < current - Tolerance)
            {
                return ZoomLevels[index];
            }
        }
        return MinimumZoom;
    }

    internal const string ScriptWorld = "runic-desktop-keyboard";

    internal const string ZoomMessageHandler = "runicZoom";

    // The script lives in WebKitGtkKeyboard.js, which a browser test also loads, and is
    // embedded in each assembly that links this file.
    private static readonly Lazy<string> ScriptSource = new(static () =>
    {
        using var stream = typeof(WebKitGtkKeyboard).Assembly.GetManifestResourceStream(ScriptResource)
            ?? throw new InvalidOperationException("The WebKitGTK keyboard script is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    internal const string ScriptResource = "Runic.Desktop.WebKitGtkKeyboard.js";

    /// <summary>The keyboard script for the top frame; see WebKitGtkKeyboard.js.</summary>
    internal static string Script => ScriptSource.Value;
}
