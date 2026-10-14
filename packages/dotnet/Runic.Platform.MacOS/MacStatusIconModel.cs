using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;

namespace Runic.Platform.MacOS;

// What a click on the status item does.
internal enum MacStatusClick
{
    None,
    Primary,
    Secondary,
    Menu,
}

internal enum MacMenuEntryKind
{
    Command,
    Separator,
    Submenu,
}

// One NSMenuItem. A command's tag is its index in MacStatusMenu.Commands plus one; 0 is no command.
internal sealed record MacMenuEntry(MacMenuEntryKind Kind, string Label, bool Enabled, bool? Checked, int Tag, ImmutableArray<MacMenuEntry> Children);

// The NSMenu built from a state when the menu opens, with the command IDs its tags route to.
internal sealed record MacStatusMenu(ImmutableArray<MacMenuEntry> Entries, ImmutableArray<string> Commands)
{
    internal static MacStatusMenu Build(ImmutableArray<DesktopMenuItem> items)
    {
        var commands = ImmutableArray.CreateBuilder<string>();
        var entries = Add(items, commands);
        return new MacStatusMenu(entries, commands.ToImmutable());
    }

    // The command a menu item's tag routes to, or null for a tag from no command of this menu.
    internal string? CommandFor(long tag) => tag >= 1 && tag <= Commands.Length ? Commands[(int)tag - 1] : null;

    private static ImmutableArray<MacMenuEntry> Add(ImmutableArray<DesktopMenuItem> items, ImmutableArray<string>.Builder commands)
    {
        var entries = ImmutableArray.CreateBuilder<MacMenuEntry>(items.Length);
        foreach (var item in items)
        {
            switch (item)
            {
                case DesktopMenuCommand command:
                    commands.Add(command.Id);
                    entries.Add(new MacMenuEntry(MacMenuEntryKind.Command, command.Label, command.Enabled, command.Checked, commands.Count, []));
                    break;
                case DesktopMenuSubmenu submenu:
                    entries.Add(new MacMenuEntry(MacMenuEntryKind.Submenu, submenu.Label, submenu.Enabled, null, 0, Add(submenu.Items, commands)));
                    break;
                default:
                    entries.Add(new MacMenuEntry(MacMenuEntryKind.Separator, "", true, null, 0, []));
                    break;
            }
        }
        return entries.MoveToImmutable();
    }
}

// The parts of the NSStatusItem provider that need no AppKit: click classification, images and text.
internal static class MacStatusIconModel
{
    // NSEventType values.
    internal const long LeftMouseDown = 1, LeftMouseUp = 2, RightMouseDown = 3, RightMouseUp = 4, OtherMouseDown = 25, OtherMouseUp = 26;
    // NSEventModifierFlagControl.
    internal const ulong ControlKey = 1UL << 18;
    // The button's action is sent on left, right and other mouse up (NSEventMask bits of those event types).
    internal const ulong ActionMask = (1UL << (int)LeftMouseUp) | (1UL << (int)RightMouseUp) | (1UL << (int)OtherMouseUp);
    // The menu bar is 22 points high (24 on some displays); its icons are 18 points high.
    internal const double IconHeight = 18;

    /// <summary>Maps the event that sent the button's action to what the click does.</summary>
    /// <remarks>
    /// A right click, or a left click with Control, opens the menu, as for other menu bar items; with an empty menu it
    /// does nothing. A middle click is the secondary action. Everything else is the primary action, including a press
    /// from VoiceOver or <c>performClick:</c>, whose current event is no mouse event.
    /// </remarks>
    internal static MacStatusClick Classify(long eventType, ulong modifierFlags, bool hasMenu)
    {
        bool menuButton = eventType is RightMouseDown or RightMouseUp
            || (eventType is LeftMouseDown or LeftMouseUp && (modifierFlags & ControlKey) != 0);
        if (menuButton) return hasMenu ? MacStatusClick.Menu : MacStatusClick.None;
        return eventType is OtherMouseDown or OtherMouseUp ? MacStatusClick.Secondary : MacStatusClick.Primary;
    }

    /// <summary>The button's tooltip: the title, then the tooltip text on its own line, as on Linux.</summary>
    internal static string ToolTip(DesktopStatusIconState state) =>
        string.IsNullOrEmpty(state.Tooltip) ? state.Title : state.Title + "\n" + state.Tooltip;

    /// <summary>The icon shown for the state: the attention icon while the state asks for attention and supplies one.</summary>
    internal static DesktopIcon ShownIcon(DesktopStatusIconState state) =>
        state.Status == DesktopStatusIconStatus.NeedsAttention && state.AttentionIcon is { } attention ? attention : state.Icon;

    /// <summary>
    /// Whether the menu bar should tint the icon as a template image: every image is monochrome. A template image
    /// uses only its alpha channel, so a black or white glyph follows light and dark menu bars.
    /// </summary>
    internal static bool IsTemplate(DesktopIcon icon) => icon.Images.Length > 0 && icon.Images.All(IsMonochrome);

    /// <summary>
    /// Whether the visible pixels (alpha 16 or more) are all one shade of gray, allowing for antialiasing noise. An
    /// image without visible pixels is not monochrome.
    /// </summary>
    internal static bool IsMonochrome(DesktopIconImage image)
    {
        var rgba = image.Rgba.AsSpan();
        int darkest = 255, lightest = 0;
        bool visible = false;
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] < 16) continue;
            int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 8) return false;
            int gray = (r + g + b) / 3;
            darkest = Math.Min(darkest, gray);
            lightest = Math.Max(lightest, gray);
            visible = true;
        }
        return visible && lightest - darkest <= 24;
    }

    /// <summary>The icon's size in points: 18 points high, with the aspect ratio of its largest image.</summary>
    internal static (double Width, double Height) PointSize(DesktopIcon icon)
    {
        var largest = icon.Images.MaxBy(static image => image.Width * image.Height)!;
        double width = Math.Clamp(IconHeight * largest.Width / largest.Height, IconHeight / 4, IconHeight * 4);
        return (width, IconHeight);
    }

    /// <summary>Encodes straight-alpha RGBA pixels as a PNG, which NSBitmapImageRep reads without premultiplying them first.</summary>
    internal static byte[] EncodePng(DesktopIconImage image)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], image.Height);
        header[8] = 8; // bits per channel
        header[9] = 6; // RGBA
        header[10] = header[11] = header[12] = 0; // deflate, adaptive filtering, no interlace
        WriteChunk(output, "IHDR"u8, header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                var rgba = image.Rgba.AsSpan();
                int stride = image.Width * 4;
                for (int row = 0; row < image.Height; row++)
                {
                    zlib.WriteByte(0); // filter type None
                    zlib.Write(rgba.Slice(row * stride, stride));
                }
            }
            WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        uint crc = Crc32.Append(Crc32.Append(0xffffffff, type), data) ^ 0xffffffff;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    // The CRC-32 of PNG chunks (ISO 3309, polynomial 0xedb88320).
    internal static class Crc32
    {
        private static readonly uint[] Table = CreateTable();

        internal static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            foreach (byte value in data) crc = Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            return crc;
        }

        private static uint[] CreateTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
