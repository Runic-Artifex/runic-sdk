using System.Text.Json.Nodes;
using Runic.Platform;

namespace Runic.Application.Views.Desktop.Hosting;

// The features menus/1 and dialogs/1 of the Runic host protocol (host-protocol.md, Menus and Dialogs): their names,
// limits and the checks of their messages' shapes. Shared by the guest, the host and RunicTestHost, which links this file.
internal static class HostFeatures
{
    internal const string Menus = "menus/1";
    internal const string Dialogs = "dialogs/1";

    // What Runic's guest and host implement, in the order their hellos list them.
    internal static readonly string[] Implemented = [Menus, Dialogs];

    // A menu bar: at most 8 levels of entry lists and 1,000 entries in all. A context menu: at most 5 levels (four nested
    // submenus) and 256 entries, as IWindowPlatformServices.ShowContextMenuAsync allows.
    internal const int MaximumBarDepth = 8;
    internal const int MaximumBarEntries = 1_000;
    internal const int MaximumContextDepth = 5;
    internal const int MaximumContextEntries = 256;
    internal const int MaximumTitleLength = 128;
    internal const int MaximumMessageBoxButtons = 6;
    internal const int MaximumMessageBoxText = 4_096;
    internal const int MaximumFilters = 64;

    // The requests that wait for the user, which a requester may give up with a cancel push (host-protocol.md, Dialogs).
    internal static bool WaitsForUser(string? kind) => kind is "file-dialog" or "message-box" or "show-context-menu";

    // Checks the entries of set-menu-bar (bar) or show-context-menu: null when they are valid, else the error reason,
    // "capacity" for a menu over a limit or "invalid-request". Item ids are unique in the menu.
    internal static string? CheckMenu(JsonNode? menu, bool bar)
    {
        if (menu is not JsonArray entries) return "invalid-request";
        if (!bar && entries.Count == 0) return "invalid-request";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        return CheckEntries(entries, bar, 1, ids, ref count);
    }

    private static string? CheckEntries(JsonArray entries, bool bar, int depth, HashSet<string> ids, ref int count)
    {
        if (depth > (bar ? MaximumBarDepth : MaximumContextDepth)) return "capacity";
        foreach (var node in entries)
        {
            if (++count > (bar ? MaximumBarEntries : MaximumContextEntries)) return "capacity";
            if (node is not JsonObject entry) return "invalid-request";
            switch (HostProtocol.String(entry, "kind"))
            {
                case "item":
                    // A bar's top level holds menus only.
                    if (bar && depth == 1) return "invalid-request";
                    if (!HostProtocol.IsIdentifier(HostProtocol.String(entry, "id")) || !ids.Add(HostProtocol.String(entry, "id")!) ||
                        !Title(entry) || !OptionalBoolean(entry, "enabled") || !OptionalBoolean(entry, "checked") ||
                        !OptionalString(entry, "radioGroup", 64) || !AccessKey(entry))
                        return "invalid-request";
                    if (entry["shortcut"] is not null &&
                        !(HostProtocol.String(entry, "shortcut") is { } shortcut && DesktopShortcut.TryParse(shortcut, out _)))
                        return "invalid-request";
                    if (entry["radioGroup"] is not null && HostProtocol.Boolean(entry, "checked") is null) return "invalid-request";
                    break;
                case "separator":
                    if (bar && depth == 1) return "invalid-request";
                    break;
                case "submenu":
                    if (!Title(entry) || !OptionalBoolean(entry, "enabled") || !AccessKey(entry) ||
                        !(entry["role"] is null || HostProtocol.String(entry, "role") is not null) || entry["items"] is not JsonArray items)
                        return "invalid-request";
                    if (CheckEntries(items, bar, depth + 1, ids, ref count) is { } reason) return reason;
                    break;
                default:
                    return "invalid-request";
            }
        }
        return null;
    }

    // update-menu-items: an array of 1 to 1,000 {id, title?, enabled?, checked?}.
    internal static bool CheckMenuUpdates(JsonNode? items)
    {
        if (items is not JsonArray list || list.Count is 0 or > MaximumBarEntries) return false;
        foreach (var node in list)
        {
            if (node is not JsonObject item || !HostProtocol.IsIdentifier(HostProtocol.String(item, "id")) ||
                (item["title"] is not null && !Title(item)) || !OptionalBoolean(item, "enabled") || !OptionalBoolean(item, "checked"))
                return false;
        }
        return true;
    }

    // A point {x, y} of finite numbers, or null for the pointer.
    internal static bool CheckPoint(JsonNode? point) =>
        point is null || point is JsonObject members && Coordinate(members["x"]) && Coordinate(members["y"]);

    private static bool Coordinate(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) && double.IsFinite(number) && Math.Abs(number) <= 1_000_000;

    // file-dialog: its dialog token and options (host-protocol.md, Dialogs).
    internal static bool CheckFileDialog(JsonObject request)
    {
        var dialog = HostProtocol.String(request, "dialog");
        if (dialog is not ("open" or "open-multiple" or "save" or "folder" or "folder-multiple")) return false;
        if (!OptionalString(request, "title", MaximumMessageBoxText) || !OptionalPath(request, "initialDirectory")) return false;
        if (dialog == "save" ? HostProtocol.Text(request, "suggestedName", 255) is null || HostProtocol.String(request, "suggestedName")!.Contains('\0')
            : request["suggestedName"] is not null)
            return false;
        if (!OptionalBoolean(request, "showFilterChoice")) return false;
        if (request["filters"] is null) return true;
        if (dialog is "folder" or "folder-multiple" || request["filters"] is not JsonArray filters || filters.Count > MaximumFilters) return false;
        foreach (var node in filters)
        {
            if (node is not JsonObject filter || HostProtocol.Text(filter, "name", MaximumTitleLength) is null ||
                filter["extensions"] is not JsonArray { Count: > 0 and <= 64 } extensions ||
                !extensions.All(static extension => extension is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 64) ||
                !(filter["mimeTypes"] is null || filter["mimeTypes"] is JsonArray { Count: <= 64 } mimeTypes &&
                    mimeTypes.All(static type => type is JsonValue value && value.TryGetValue<string>(out var text) && text.Length is > 0 and <= 255)))
                return false;
        }
        return true;
    }

    // message-box: its kind, texts and buttons (host-protocol.md, Dialogs).
    internal static bool CheckMessageBox(JsonObject request)
    {
        if (HostProtocol.Text(request, "message", MaximumMessageBoxText) is not { } message || string.IsNullOrWhiteSpace(message) || message.Contains('\0'))
            return false;
        if (!OptionalString(request, "title", MaximumMessageBoxText) || !OptionalString(request, "detail", MaximumMessageBoxText)) return false;
        // The box's icon, an open set: an unknown one shows none.
        if (!OptionalString(request, "icon", 64)) return false;
        if (request["buttons"] is null) return true;
        if (request["buttons"] is not JsonArray { Count: <= MaximumMessageBoxButtons } buttons) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in buttons)
        {
            if (node is not JsonObject button || HostProtocol.Text(button, "id", 128) is not { } id || !ids.Add(id) ||
                HostProtocol.Text(button, "label", MaximumTitleLength) is null ||
                !(button["role"] is null || HostProtocol.String(button, "role") is not null) || !AccessKey(button))
                return false;
        }
        return true;
    }

    private static bool Title(JsonObject entry) =>
        HostProtocol.String(entry, "title") is { Length: > 0 and <= MaximumTitleLength } title && !string.IsNullOrWhiteSpace(title) && !title.Contains('\0');

    private static bool OptionalBoolean(JsonObject entry, string name) => entry[name] is null || HostProtocol.Boolean(entry, name) is not null;

    private static bool OptionalString(JsonObject entry, string name, int maximum) =>
        entry[name] is null || HostProtocol.String(entry, name) is { } text && text.Length <= maximum && !text.Contains('\0');

    private static bool OptionalPath(JsonObject entry, string name) =>
        entry[name] is null || HostProtocol.String(entry, name) is { Length: > 0 and <= 4_096 } path && !path.Contains('\0');

    private static bool AccessKey(JsonObject entry) =>
        entry["accessKey"] is null || HostProtocol.String(entry, "accessKey") is { Length: 1 } key && char.IsLetterOrDigit(key[0]);
}
