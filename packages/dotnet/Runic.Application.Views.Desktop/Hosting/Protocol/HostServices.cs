using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// The base protocol's appearance and locale (host-protocol.md, Appearance and locale) and the feature window-services/1
// (host-protocol.md, Window services): their members, limits and the checks of their messages' shapes. Shared by the
// guest, the host and RunicTestHost, which links this file.
internal static class HostServices
{
    internal const string Feature = "window-services/1";

    internal const int MaximumLanguages = 32;
    internal const int MaximumTagLength = 64;
    // A clipboard read's bound (maximum): characters of text, HTML or RTF, bytes of an image, entries of a file list.
    internal const int MaximumClipboardBound = 16_777_216;
    internal const int MaximumClipboardFiles = 4_096;
    internal const int MaximumDroppedPaths = 1_024;
    internal const int MaximumMonitors = 64;
    internal const int MaximumPath = 4_096;
    internal const int MaximumUri = 8_192;
    // A drop's routing label (data-runic-drop-target), as the page reported it.
    internal const int MaximumDropTarget = 256;

    // The host's limits by default (host-protocol.md, Limits): clipboard and monitor requests per second across the
    // guest's windows, and launches per ten seconds.
    internal const int DefaultServiceRequestsPerSecond = 20;
    internal const int DefaultLaunchesPerTenSeconds = 5;

    // ----- Appearance

    // The members of an appearance push (the Views protocol's appearance push without its kind), every member present.
    internal static JsonObject WriteAppearance(DesktopAppearance? appearance)
    {
        var push = JsonNode.Parse(Views.BridgeDispatcher.AppearancePush(appearance))!.AsObject();
        push.Remove("kind");
        return push;
    }

    // An appearance object back into the DesktopAppearance it describes: null for null, or when every member is unknown; a
    // member that is null, missing or malformed stays unknown.
    internal static DesktopAppearance? ReadAppearance(JsonNode? node)
    {
        if (node is not JsonObject members) return null;
        var colorScheme = HostProtocol.String(members, "colorScheme") switch
        {
            "light" => DesktopColorScheme.Light,
            "dark" => DesktopColorScheme.Dark,
            _ => DesktopColorScheme.NoPreference,
        };
        DesktopContrast? contrast = HostProtocol.String(members, "contrast") switch
        {
            "no-preference" => DesktopContrast.NoPreference,
            "more" => DesktopContrast.More,
            "forced-colors" => DesktopContrast.ForcedColors,
            _ => null,
        };
        var reducedMotion = HostProtocol.Boolean(members, "reducedMotion");
        double? textScale = members["textScale"] is JsonValue scaleValue && scaleValue.TryGetValue<double>(out var scale) &&
            double.IsFinite(scale) && scale > 0 && scale <= 100 ? scale : null;
        var accent = Color(HostProtocol.String(members, "accentColor"));
        DesktopSystemColors? palette = null;
        if (members["systemColors"] is JsonObject colors
            && Color(HostProtocol.String(colors, "canvas")) is { } canvas && Color(HostProtocol.String(colors, "canvasText")) is { } canvasText
            && Color(HostProtocol.String(colors, "linkText")) is { } linkText && Color(HostProtocol.String(colors, "grayText")) is { } grayText
            && Color(HostProtocol.String(colors, "highlight")) is { } highlight && Color(HostProtocol.String(colors, "highlightText")) is { } highlightText
            && Color(HostProtocol.String(colors, "buttonFace")) is { } buttonFace && Color(HostProtocol.String(colors, "buttonText")) is { } buttonText)
            palette = new(canvas, canvasText, linkText, grayText, highlight, highlightText, buttonFace, buttonText);
        if (colorScheme == DesktopColorScheme.NoPreference && contrast is null && reducedMotion is null && textScale is null &&
            accent is null && palette is null)
            return null;
        return new DesktopAppearance(colorScheme, accent, ReducedMotion: reducedMotion)
        {
            Contrast = contrast,
            SystemColors = palette,
            TextScale = textScale,
        };
    }

    // An appearance member is null or an object; its members are checked as they are read.
    internal static bool CheckAppearance(JsonNode? node) => node is null || node is JsonObject;

    // #rrggbb, as the Views protocol writes it.
    private static DesktopAccentColor? Color(string? text) =>
        text is { Length: 7 } && text[0] == '#'
        && int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb)
            ? new(((rgb >> 16) & 0xFF) / 255d, ((rgb >> 8) & 0xFF) / 255d, (rgb & 0xFF) / 255d)
            : null;

    // ----- Locale

    // A BCP 47 tag as the application locale takes it: 1 to 64 letters, digits and '-', starting with a letter.
    internal static bool IsLocale(string? tag) => tag is { Length: > 0 and <= MaximumTagLength } && Views.BridgeDispatcher.IsLocale(tag);

    // The locale members of hello and the locale push: locale, a tag or null; languages, at most 32 tags. A tag that is
    // not one is dropped, so a host's odd language list never fails the channel.
    internal static (string? Locale, ImmutableArray<string> Languages) ReadLocale(JsonObject message)
    {
        var locale = HostProtocol.String(message, "locale") is { } tag && IsLocale(tag) ? tag : null;
        var languages = message["languages"] is JsonArray list
            ? list.Take(MaximumLanguages)
                .Select(static item => item is JsonValue value && value.TryGetValue<string>(out var text) && IsLocale(text) ? text : null)
                .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray()
            : [];
        return (locale, languages);
    }

    internal static JsonArray WriteLanguages(IEnumerable<string> languages) =>
        new([.. languages.Where(IsLocale).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumLanguages).Select(static tag => (JsonNode)tag)]);

    // ----- Clipboard

    internal static string? FormatToken(DesktopClipboardFormats format) => format switch
    {
        DesktopClipboardFormats.Text => "text",
        DesktopClipboardFormats.Html => "html",
        DesktopClipboardFormats.Rtf => "rtf",
        DesktopClipboardFormats.Image => "image",
        DesktopClipboardFormats.Files => "files",
        _ => null,
    };

    internal static DesktopClipboardFormats? Format(string? token) => token switch
    {
        "text" => DesktopClipboardFormats.Text,
        "html" => DesktopClipboardFormats.Html,
        "rtf" => DesktopClipboardFormats.Rtf,
        "image" => DesktopClipboardFormats.Image,
        "files" => DesktopClipboardFormats.Files,
        _ => null,
    };

    internal static JsonArray WriteFormats(DesktopClipboardFormats formats) =>
        new([.. ((DesktopClipboardFormats[])[DesktopClipboardFormats.Text, DesktopClipboardFormats.Html, DesktopClipboardFormats.Rtf,
                DesktopClipboardFormats.Image, DesktopClipboardFormats.Files])
            .Where(format => formats.HasFlag(format)).Select(static format => (JsonNode)FormatToken(format)!)]);

    // Unknown tokens are ignored: the set is open.
    internal static DesktopClipboardFormats ReadFormats(JsonNode? node) =>
        node is JsonArray list
            ? list.Select(static item => item is JsonValue value && value.TryGetValue<string>(out var token) ? Format(token) : null)
                .Aggregate(DesktopClipboardFormats.None, static (all, format) => all | (format ?? DesktopClipboardFormats.None))
            : DesktopClipboardFormats.None;

    // clipboard-read: format "formats" without a bound, or a format and its bound.
    internal static bool CheckClipboardRead(JsonObject request) =>
        HostProtocol.String(request, "format") switch
        {
            "formats" => request["maximum"] is null,
            var token when Format(token) is not null => HostProtocol.Integer(request, "maximum") is >= 0 and <= MaximumClipboardBound,
            _ => false,
        };

    // clipboard-write: content with at least one format; the platform checks NUL, PNG and existing files.
    internal static bool CheckClipboardWrite(JsonObject request)
    {
        if (request["content"] is not JsonObject content) return false;
        var any = false;
        foreach (var name in (string[])["text", "html", "rtf", "image"])
        {
            if (content[name] is null) continue;
            if (HostProtocol.String(content, name) is null) return false;
            any = true;
        }
        if (content["image"] is not null && Base64(HostProtocol.String(content, "image")) is null) return false;
        if (content["files"] is not null)
        {
            if (content["files"] is not JsonArray { Count: > 0 and <= MaximumClipboardFiles } files || !files.All(IsAbsolutePath)) return false;
            any = true;
        }
        return any;
    }

    internal static DesktopClipboardContent ReadClipboardContent(JsonObject content) => new()
    {
        Text = HostProtocol.String(content, "text"),
        Html = HostProtocol.String(content, "html"),
        Rtf = HostProtocol.String(content, "rtf"),
        Image = HostProtocol.String(content, "image") is { } image ? Base64(image) ?? [] : ReadOnlyMemory<byte>.Empty,
        Files = content["files"] is JsonArray files ? [.. files.Select(static file => file!.GetValue<string>())] : [],
    };

    internal static JsonObject WriteClipboardContent(DesktopClipboardContent content)
    {
        var members = new JsonObject();
        if (content.Text is { } text) members["text"] = text;
        if (content.Html is { } html) members["html"] = html;
        if (content.Rtf is { } rtf) members["rtf"] = rtf;
        if (!content.Image.IsEmpty) members["image"] = Convert.ToBase64String(content.Image.Span);
        if (!content.Files.IsDefaultOrEmpty) members["files"] = new JsonArray([.. content.Files.Select(static file => (JsonNode)file)]);
        return members;
    }

    // Standard base64 with padding, or null when it is not.
    internal static byte[]? Base64(string? text)
    {
        if (text is null) return null;
        var bytes = new byte[text.Length / 4 * 3 + 3];
        return Convert.TryFromBase64String(text, bytes, out var written) ? bytes[..written] : null;
    }

    // The serialized size of a message, for the 1 MiB limit (host-protocol.md, Messages).
    internal static int Size(JsonObject message) => Encoding.UTF8.GetByteCount(message.ToJsonString());

    // Whether a reply of this size fits a message; replies keep room for their id and ok.
    internal static bool Fits(JsonObject message) => Size(message) <= HostProtocol.MaximumMessageBytes - 64;

    // ----- Monitors

    internal static JsonObject WriteMonitor(DesktopMonitor monitor) => new()
    {
        ["id"] = monitor.Id,
        ["name"] = monitor.DisplayName,
        ["bounds"] = Rect(monitor.Bounds),
        ["workArea"] = Rect(monitor.WorkArea),
        ["scale"] = double.IsFinite(monitor.Scale) && monitor.Scale > 0 ? monitor.Scale : 1,
        ["primary"] = monitor.IsPrimary,
    };

    private static JsonObject Rect(DesktopRect rect) => new() { ["x"] = rect.X, ["y"] = rect.Y, ["width"] = rect.Width, ["height"] = rect.Height };

    // A monitor of the monitors reply, or null when one is not valid.
    internal static DesktopMonitor? ReadMonitor(JsonNode? node)
    {
        if (node is not JsonObject members || HostProtocol.Text(members, "id", 512) is not { Length: > 0 } id ||
            HostProtocol.Text(members, "name", 512) is not { } name || ReadRect(members["bounds"]) is not { } bounds ||
            ReadRect(members["workArea"]) is not { } workArea || HostProtocol.Boolean(members, "primary") is not { } primary ||
            members["scale"] is not JsonValue scaleValue || !scaleValue.TryGetValue<double>(out var scale) || !double.IsFinite(scale) || scale <= 0)
            return null;
        return new DesktopMonitor(id, name) { Bounds = bounds, WorkArea = workArea, Scale = scale, IsPrimary = primary };
    }

    private static DesktopRect? ReadRect(JsonNode? node) =>
        node is JsonObject rect && Coordinate(rect, "x") is { } x && Coordinate(rect, "y") is { } y &&
        HostProtocol.Integer(rect, "width") is >= 0 and <= int.MaxValue and var width && HostProtocol.Integer(rect, "height") is >= 0 and <= int.MaxValue and var height
            ? new DesktopRect(x, y, (int)width!, (int)height!)
            : null;

    private static int? Coordinate(JsonObject rect, string name) =>
        HostProtocol.Integer(rect, name) is >= int.MinValue and <= int.MaxValue and var value ? (int)value! : null;

    // ----- Launch and drops

    // launch: exactly one of an absolute uri or an absolute path; operation for a path only.
    internal static bool CheckLaunch(JsonObject request)
    {
        var uri = request["uri"];
        var path = request["path"];
        if ((uri is null) == (path is null)) return false;
        if (uri is not null)
            return request["operation"] is null && HostProtocol.Text(request, "uri", MaximumUri) is { } text && !text.Contains('\0') &&
                Uri.TryCreate(text, UriKind.Absolute, out _);
        return IsAbsolutePath(path) && Operation(HostProtocol.String(request, "operation") ?? "open") is not null;
    }

    internal static DesktopFileOperation? Operation(string? token) => token switch
    {
        "open" => DesktopFileOperation.Open,
        "reveal" => DesktopFileOperation.Reveal,
        "choose-application" => DesktopFileOperation.ChooseApplication,
        _ => null,
    };

    internal static string OperationToken(DesktopFileOperation operation) => operation switch
    {
        DesktopFileOperation.Reveal => "reveal",
        DesktopFileOperation.ChooseApplication => "choose-application",
        _ => "open",
    };

    internal static DesktopDragEffects Effect(string? token) => token switch
    {
        "move" => DesktopDragEffects.Move,
        "link" => DesktopDragEffects.Link,
        _ => DesktopDragEffects.Copy,
    };

    internal static string EffectToken(DesktopDragEffects effect) => effect switch
    {
        DesktopDragEffects.Move => "move",
        DesktopDragEffects.Link => "link",
        _ => "copy",
    };

    // files-dropped: 1 to 1,024 absolute paths; the effect and the page's routing label are optional.
    internal static bool CheckDrop(JsonObject push) =>
        push["paths"] is JsonArray { Count: > 0 and <= MaximumDroppedPaths } paths && paths.All(IsAbsolutePath) &&
        (push["target"] is null || HostProtocol.Text(push, "target", MaximumDropTarget) is not null) &&
        (push["effect"] is null || HostProtocol.String(push, "effect") is "copy" or "move" or "link");

    internal static bool IsAbsolutePath(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var path) && path.Length is > 0 and <= MaximumPath && !path.Contains('\0') &&
        Path.IsPathFullyQualified(path);

    // The schemes a guest may open by default (host-protocol.md, Window services): web and mail links, as the host
    // application's own links open in the default browser.
    internal static bool IsDefaultUri(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);

    // Paths compare as the file system does by default: without case on Windows and macOS.
    internal static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
