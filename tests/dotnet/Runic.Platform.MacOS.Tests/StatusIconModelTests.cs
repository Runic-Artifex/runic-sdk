using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

// The portable parts of the macOS status item and login item. The native paths run in the NativeAppKit category of
// tests/dotnet/Runic.Platform.Runtime.Tests on the macOS CI runner.
internal sealed class StatusIconModelTests
{
    private const long LeftUp = MacStatusIconModel.LeftMouseUp, RightUp = MacStatusIconModel.RightMouseUp, OtherUp = MacStatusIconModel.OtherMouseUp;
    private const ulong Control = MacStatusIconModel.ControlKey, Command = 1UL << 20;

    [Test]
    [Arguments(LeftUp, 0UL, true, "Primary")]
    [Arguments(LeftUp, Command, true, "Primary")]
    [Arguments(LeftUp, Control, true, "Menu")] // Control-click
    [Arguments(RightUp, 0UL, true, "Menu")]
    [Arguments(MacStatusIconModel.RightMouseDown, 0UL, true, "Menu")]
    [Arguments(RightUp, 0UL, false, "None")] // an empty menu shows none
    [Arguments(LeftUp, Control, false, "None")]
    [Arguments(OtherUp, 0UL, true, "Secondary")] // middle click
    [Arguments(0L, 0UL, true, "Primary")] // performClick: or VoiceOver's press, without a mouse event
    [Arguments(10L, 0UL, true, "Primary")] // a key event
    public void ClassifiesClicks(long type, ulong flags, bool hasMenu, string expected) =>
        Check(MacStatusIconModel.Classify(type, flags, hasMenu) == Enum.Parse<MacStatusClick>(expected), $"{type}/{flags}/{hasMenu} is not {expected}.");

    [Test]
    public void ButtonActionFollowsEveryMouseButton() =>
        Check(MacStatusIconModel.ActionMask == ((1UL << 2) | (1UL << 4) | (1UL << 26)), $"Unexpected mask {MacStatusIconModel.ActionMask:x}.");

    [Test]
    public void MenuRoutesTagsToCommandIds()
    {
        var menu = MacStatusMenu.Build(
        [
            new DesktopMenuCommand("open", "Open window"),
            new DesktopMenuCommand("daily", "Check daily") { Checked = true },
            new DesktopMenuSeparator(),
            new DesktopMenuSubmenu("More",
            [
                new DesktopMenuCommand("attention", "Ask for attention") { Checked = false },
                new DesktopMenuCommand("disabled", "Unavailable") { Enabled = false },
            ]) { Enabled = false },
            new DesktopMenuCommand("quit", "Quit_now"),
        ]);
        Check(menu.Commands.SequenceEqual(["open", "daily", "attention", "disabled", "quit"]), string.Join(",", menu.Commands));
        Check(menu.Entries.Length == 5 && menu.Entries[2].Kind == MacMenuEntryKind.Separator, "Separator expected third.");
        Check(menu.Entries[0] is { Kind: MacMenuEntryKind.Command, Label: "Open window", Enabled: true, Checked: null, Tag: 1 }, $"{menu.Entries[0]}");
        Check(menu.Entries[1] is { Checked: true, Tag: 2 }, $"{menu.Entries[1]}");
        var more = menu.Entries[3];
        Check(more is { Kind: MacMenuEntryKind.Submenu, Label: "More", Enabled: false, Tag: 0 } && more.Children.Length == 2, $"{more}");
        Check(more.Children[0] is { Checked: false, Tag: 3 } && more.Children[1] is { Enabled: false, Tag: 4 }, "Submenu entries.");
        // Labels are shown literally: AppKit menus have no access-key marker.
        Check(menu.Entries[4] is { Label: "Quit_now", Tag: 5 }, $"{menu.Entries[4]}");
        Check(menu.CommandFor(1) == "open" && menu.CommandFor(4) == "disabled" && menu.CommandFor(5) == "quit", "Tags route to IDs.");
        Check(menu.CommandFor(0) is null && menu.CommandFor(6) is null && menu.CommandFor(-1) is null, "Foreign tags route nowhere.");
        Check(MacStatusMenu.Build([]) is { Entries.Length: 0, Commands.Length: 0 }, "Empty menu.");
    }

    [Test]
    public void ToolTipAndShownIcon()
    {
        var normal = new DesktopIcon { Images = [Image(2, 2, 0, 0, 0, 255)] };
        var attention = new DesktopIcon { Images = [Image(2, 2, 255, 0, 0, 255)] };
        var state = new DesktopStatusIconState(normal, "Updates") { Tooltip = "3 available", AttentionIcon = attention };
        Check(MacStatusIconModel.ToolTip(state) == "Updates\n3 available", MacStatusIconModel.ToolTip(state));
        Check(MacStatusIconModel.ToolTip(state with { Tooltip = null }) == "Updates", "Title only.");
        Check(ReferenceEquals(MacStatusIconModel.ShownIcon(state), normal), "Active shows the icon.");
        Check(ReferenceEquals(MacStatusIconModel.ShownIcon(state with { Status = DesktopStatusIconStatus.Passive }), normal), "Passive shows the icon.");
        Check(ReferenceEquals(MacStatusIconModel.ShownIcon(state with { Status = DesktopStatusIconStatus.NeedsAttention }), attention), "Attention icon.");
        Check(ReferenceEquals(MacStatusIconModel.ShownIcon(state with { Status = DesktopStatusIconStatus.NeedsAttention, AttentionIcon = null }), normal),
            "Without an attention icon the icon stays.");
    }

    [Test]
    public void MonochromeImagesBecomeTemplates()
    {
        // A black glyph with antialiased edges, transparent elsewhere.
        var glyph = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) glyph[i * 4 + 3] = (byte)(i % 3 == 0 ? 255 : i % 3 == 1 ? 90 : 0);
        glyph[1 * 4] = glyph[1 * 4 + 1] = glyph[1 * 4 + 2] = 6; // edge noise
        var black = new DesktopIconImage(4, 4, [.. glyph]);
        Check(MacStatusIconModel.IsMonochrome(black), "A black glyph is monochrome.");
        Check(MacStatusIconModel.IsMonochrome(Image(2, 2, 255, 255, 255, 255)), "A white glyph is monochrome.");
        Check(!MacStatusIconModel.IsMonochrome(Image(2, 2, 30, 90, 200, 255)), "Blue is not.");
        Check(!MacStatusIconModel.IsMonochrome(Image(2, 2, 0, 0, 0, 0)), "An invisible image is not.");
        // Gray shades of one image that differ a lot are a picture, not a glyph.
        var shades = new DesktopIconImage(2, 1, [0, 0, 0, 255, 200, 200, 200, 255]);
        Check(!MacStatusIconModel.IsMonochrome(shades), "Two shades are not monochrome.");
        // A colour pixel that is nearly transparent does not count.
        var faint = new DesktopIconImage(2, 1, [0, 0, 0, 255, 255, 0, 0, 10]);
        Check(MacStatusIconModel.IsMonochrome(faint), "Nearly transparent colour is ignored.");
        Check(MacStatusIconModel.IsTemplate(new DesktopIcon { Images = [black, Image(8, 8, 0, 0, 0, 255)] }), "All monochrome: template.");
        Check(!MacStatusIconModel.IsTemplate(new DesktopIcon { Images = [black, Image(8, 8, 200, 0, 0, 255)] }), "One colour image: no template.");
        Check(!MacStatusIconModel.IsTemplate(new DesktopIcon { ThemeName = "applications-system" }), "No images: no template.");
    }

    [Test]
    public void IconsAreEighteenPointsHigh()
    {
        Check(MacStatusIconModel.PointSize(new DesktopIcon { Images = [Image(16, 16, 0, 0, 0, 255), Image(32, 32, 0, 0, 0, 255)] }) == (18, 18), "Square.");
        Check(MacStatusIconModel.PointSize(new DesktopIcon { Images = [Image(16, 16, 0, 0, 0, 255), Image(64, 32, 0, 0, 0, 255)] }) == (36, 18), "Largest image's aspect.");
        Check(MacStatusIconModel.PointSize(new DesktopIcon { Images = [Image(512, 1, 0, 0, 0, 255)] }) == (72, 18), "Width is bounded.");
    }

    [Test]
    public void EncodesStraightAlphaPng()
    {
        var rgba = new byte[3 * 2 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 11);
        var png = MacStatusIconModel.EncodePng(new DesktopIconImage(3, 2, [.. rgba]));
        Check(png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }), "PNG signature.");
        var chunks = new List<(string Type, byte[] Data)>();
        for (int offset = 8; offset < png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = png.AsSpan(offset + 4, 4);
            var data = png.AsSpan(offset + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Check(crc == (MacStatusIconModel.Crc32.Append(MacStatusIconModel.Crc32.Append(0xffffffff, type), data) ^ 0xffffffff), "Chunk CRC.");
            chunks.Add((System.Text.Encoding.ASCII.GetString(type), data.ToArray()));
            offset += 12 + length;
        }
        Check(chunks.Select(chunk => chunk.Type).SequenceEqual(["IHDR", "IDAT", "IEND"]), string.Join(",", chunks.Select(chunk => chunk.Type)));
        var header = chunks[0].Data;
        Check(BinaryPrimitives.ReadInt32BigEndian(header) == 3 && BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4)) == 2, "Size.");
        Check(header[8] == 8 && header[9] == 6 && header[10] == 0 && header[11] == 0 && header[12] == 0, "8-bit RGBA, not interlaced.");
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress)) zlib.CopyTo(inflated);
        var rows = inflated.ToArray();
        Check(rows.Length == 2 * (1 + 12) && rows[0] == 0 && rows[13] == 0, "Each row starts with filter None.");
        Check(rows.AsSpan(1, 12).SequenceEqual(rgba.AsSpan(0, 12)) && rows.AsSpan(14, 12).SequenceEqual(rgba.AsSpan(12, 12)),
            "The pixels are stored unchanged, with straight alpha.");
        // The standard check value of CRC-32.
        Check((MacStatusIconModel.Crc32.Append(0xffffffff, "123456789"u8) ^ 0xffffffff) == 0xcbf43926, "CRC-32 check value.");
    }

    [Test]
    public void LoginItemOutcomes()
    {
        Check(MacLoginStartupModel.IsApplicationBundle("org.example.App", "/Applications/Example.app"), "A bundle.");
        Check(MacLoginStartupModel.IsApplicationBundle("org.example.App", "/Applications/Example.APP/"), "Case and trailing slash.");
        Check(!MacLoginStartupModel.IsApplicationBundle("", "/Applications/Example.app"), "No identifier.");
        Check(!MacLoginStartupModel.IsApplicationBundle("org.example.App", "/usr/local/share/dotnet"), "A command-line process.");
        Check(MacLoginStartupModel.Enabled(MacLoginStartupModel.StatusEnabled) is PlatformResult<bool>.Success { Value: true }, "Enabled.");
        foreach (var status in new[] { MacLoginStartupModel.StatusNotRegistered, MacLoginStartupModel.StatusRequiresApproval, MacLoginStartupModel.StatusNotFound })
            Check(MacLoginStartupModel.Enabled(status) is PlatformResult<bool>.Success { Value: false }, $"Status {status} does not start at login.");
        Check(MacLoginStartupModel.Registered(MacLoginStartupModel.StatusEnabled, null) is PlatformResult<PlatformUnit>.Success, "Registered.");
        // An error does not matter once the status says the item is registered.
        Check(MacLoginStartupModel.Registered(MacLoginStartupModel.StatusEnabled, new("SMAppServiceErrorDomain", 1)) is PlatformResult<PlatformUnit>.Success,
            "Status decides.");
        var approval = MacLoginStartupModel.Registered(MacLoginStartupModel.StatusRequiresApproval, null);
        Check(approval is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic: { } hint }
            && hint.Message!.Contains("System Settings", StringComparison.Ordinal), $"{approval}");
        Check(MacLoginStartupModel.Registered(MacLoginStartupModel.StatusNotRegistered, new("SMAppServiceErrorDomain", 1))
            is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied, Diagnostic.Code: 1 }, "Not permitted.");
        Check(MacLoginStartupModel.Registered(MacLoginStartupModel.StatusNotFound, new("SMAppServiceErrorDomain", 22))
            is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Code: 22 }, "Other errors.");
        Check(MacLoginStartupModel.Registered(MacLoginStartupModel.StatusNotRegistered, null)
            is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.IoError, Diagnostic.Domain: "SMAppServiceStatus" }, "No error, not registered.");
        Check(MacLoginStartupModel.Unregistered(MacLoginStartupModel.StatusNotFound, new("SMAppServiceErrorDomain", 113)) is PlatformResult<PlatformUnit>.Success,
            "Gone is gone.");
        Check(MacLoginStartupModel.Unregistered(MacLoginStartupModel.StatusEnabled, new("SMAppServiceErrorDomain", 1))
            is PlatformResult<PlatformUnit>.Failed { Code: PlatformFailureCode.PermissionDenied }, "Still registered.");
    }

    private static DesktopIconImage Image(int width, int height, byte r, byte g, byte b, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a; }
        return new DesktopIconImage(width, height, [.. pixels]);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
