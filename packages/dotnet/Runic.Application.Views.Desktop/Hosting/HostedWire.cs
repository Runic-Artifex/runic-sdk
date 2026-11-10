using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Runic.Platform;
using Runic.Platform.Runtime;

namespace Runic.Application.Views.Desktop.Hosting;

// The menus/1 and dialogs/1 messages as Runic's models (host-protocol.md, Menus and Dialogs), both ways: the guest
// writes its menus, file dialogs and message boxes; the host reads them into the models its native windows show.
internal static class HostedWire
{
    // The command id of a guest's menu item in the host's menus; its command parameter is the guest's item id. Not a
    // command of the host application: the hosted window's menu target runs it (HostedMenuTarget).
    internal const string ItemCommand = "runic-hosted-item";

    // ----- Guest: menus

    // The wire entries of menus or a context menu. Each item gets an id, recorded in items; state gives an item's
    // enabled and checked state, such as from the window's command path.
    internal static JsonArray WriteEntries(IEnumerable<DesktopMenuEntry> entries, Dictionary<string, DesktopMenuItem> items,
        Func<string, DesktopMenuItem, (bool Enabled, bool? Checked)> state)
    {
        var list = new JsonArray();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case DesktopMenuItem item:
                    var id = "i" + (items.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    items[id] = item;
                    var (enabled, isChecked) = state(id, item);
                    var node = new JsonObject
                    {
                        ["kind"] = "item",
                        ["id"] = id,
                        ["title"] = item.Title,
                        ["shortcut"] = item.Shortcut is { Key.Length: > 0 } shortcut ? shortcut.ToString() : null,
                        ["enabled"] = enabled,
                        ["checked"] = isChecked,
                    };
                    if (item.RadioGroup is { } group) node["radioGroup"] = group;
                    if (item.AccessKey is { } key) node["accessKey"] = key.ToString();
                    list.Add((JsonNode)node);
                    break;
                case DesktopMenuSeparator:
                    list.Add((JsonNode)new JsonObject { ["kind"] = "separator" });
                    break;
                case DesktopMenu menu:
                    var submenu = new JsonObject
                    {
                        ["kind"] = "submenu",
                        ["title"] = menu.Title,
                        ["role"] = RoleToken(menu.Role),
                        ["enabled"] = menu.IsEnabled,
                    };
                    if (menu.AccessKey is { } menuKey) submenu["accessKey"] = menuKey.ToString();
                    submenu["items"] = WriteEntries(menu.Items, items, state);
                    list.Add((JsonNode)submenu);
                    break;
                // An entry kind of a later release that the protocol does not carry.
            }
        }
        return list;
    }

    private static string? RoleToken(DesktopMenuRole role) => role switch
    {
        DesktopMenuRole.Application => "application",
        DesktopMenuRole.File => "file",
        DesktopMenuRole.Help => "help",
        _ => null,
    };

    // ----- Host: menus

    // The host's model of checked wire entries (HostFeatures.CheckMenu): a guest item runs ItemCommand with its id. Throws
    // ArgumentException for an entry Runic's menus do not accept, such as a radio group name outside the command id rule.
    internal static ImmutableArray<DesktopMenuEntry> ReadEntries(JsonArray entries)
    {
        var list = ImmutableArray.CreateBuilder<DesktopMenuEntry>(entries.Count);
        foreach (var node in entries)
        {
            var entry = (JsonObject)node!;
            switch (HostProtocol.String(entry, "kind"))
            {
                case "item":
                    list.Add(new DesktopMenuItem(HostProtocol.String(entry, "title")!, ItemCommand)
                    {
                        CommandParameter = HostProtocol.String(entry, "id"),
                        Shortcut = HostProtocol.String(entry, "shortcut") is { } shortcut ? DesktopShortcut.Parse(shortcut) : null,
                        IsEnabled = HostProtocol.Boolean(entry, "enabled") ?? true,
                        IsChecked = HostProtocol.Boolean(entry, "checked"),
                        RadioGroup = HostProtocol.String(entry, "radioGroup"),
                        AccessKey = HostProtocol.String(entry, "accessKey") is { Length: 1 } key ? key[0] : null,
                    });
                    break;
                case "separator":
                    list.Add(new DesktopMenuSeparator());
                    break;
                case "submenu":
                    list.Add(new DesktopMenu(HostProtocol.String(entry, "title")!, ReadEntries((JsonArray)entry["items"]!))
                    {
                        Role = HostProtocol.String(entry, "role") switch
                        {
                            "application" => DesktopMenuRole.Application,
                            "file" => DesktopMenuRole.File,
                            "help" => DesktopMenuRole.Help,
                            _ => DesktopMenuRole.None,
                        },
                        IsEnabled = HostProtocol.Boolean(entry, "enabled") ?? true,
                        AccessKey = HostProtocol.String(entry, "accessKey") is { Length: 1 } menuKey ? menuKey[0] : null,
                    });
                    break;
            }
        }
        return list.MoveToImmutable();
    }

    // The guest's id of an item in the host's menus, or null for an item of another source, such as one a policy added.
    internal static string? ItemId(DesktopMenuItem item) => item.CommandId == ItemCommand ? item.CommandParameter : null;

    // ----- Guest: dialogs

    // A file dialog of the guest's picker (NativePickerBackend) as a file-dialog request.
    internal static JsonObject WriteFileDialog(NativeFileDialogRequest request, bool folder)
    {
        var message = new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "file-dialog",
            ["dialog"] = folder
                ? request.AllowMultiple ? "folder-multiple" : "folder"
                : request.Save ? "save" : request.AllowMultiple ? "open-multiple" : "open",
        };
        if (request.Title is { } title) message["title"] = title;
        if (request.InitialDirectory is { } directory) message["initialDirectory"] = directory;
        if (request.Save) message["suggestedName"] = request.SuggestedName ?? "";
        if (!folder && !request.Filters.IsDefaultOrEmpty)
        {
            message["filters"] = new JsonArray([.. request.Filters.Select(static filter => (JsonNode)new JsonObject
            {
                ["name"] = filter.Name,
                ["extensions"] = new JsonArray([.. filter.Extensions.Select(static extension => (JsonNode)extension)]),
                ["mimeTypes"] = new JsonArray([.. filter.MimeTypes.Select(static type => (JsonNode)type)]),
            })]);
            if (request.ShowFilterChoice) message["showFilterChoice"] = true;
        }
        return message;
    }

    internal static JsonObject WriteMessageBox(DesktopMessageBox box)
    {
        var message = new JsonObject
        {
            ["id"] = 0,
            ["kind"] = "message-box",
            ["icon"] = box.Kind switch
            {
                DesktopMessageBoxKind.Information => "information",
                DesktopMessageBoxKind.Warning => "warning",
                DesktopMessageBoxKind.Error => "error",
                _ => "none",
            },
            ["message"] = box.Message,
        };
        if (box.Title is { } title) message["title"] = title;
        if (box.Detail is { } detail) message["detail"] = detail;
        var buttons = new JsonArray();
        foreach (var button in box.Buttons.IsDefault ? [] : box.Buttons)
        {
            var node = new JsonObject
            {
                ["id"] = button.Id,
                ["label"] = button.Label,
                ["role"] = button.Role switch
                {
                    DesktopMessageBoxButtonRole.Default => "default",
                    DesktopMessageBoxButtonRole.Cancel => "cancel",
                    DesktopMessageBoxButtonRole.Destructive => "destructive",
                    _ => "normal",
                },
            };
            if (button.AccessKey is { } key) node["accessKey"] = key.ToString();
            buttons.Add((JsonNode)node);
        }
        message["buttons"] = buttons;
        return message;
    }

    // ----- Host: dialogs

    // The dialog of a checked file-dialog request (HostFeatures.CheckFileDialog). Throws ArgumentException for options
    // the platform's dialogs do not accept, such as a relative initial directory.
    internal static (RunicHostedDialogKind Kind, NativeFileDialogRequest Request) ReadFileDialog(JsonObject message)
    {
        var kind = HostProtocol.String(message, "dialog") switch
        {
            "open" => RunicHostedDialogKind.OpenFile,
            "open-multiple" => RunicHostedDialogKind.OpenFiles,
            "save" => RunicHostedDialogKind.SaveFile,
            "folder" => RunicHostedDialogKind.OpenFolder,
            _ => RunicHostedDialogKind.OpenFolders,
        };
        var directory = HostProtocol.String(message, "initialDirectory");
        if (directory is not null && !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The initial directory is not an absolute path.");
        var filters = ImmutableArray<FileDialogFilter>.Empty;
        if (message["filters"] is JsonArray list)
        {
            filters = [.. list.Select(static node =>
            {
                var filter = (JsonObject)node!;
                return new FileDialogFilter(HostProtocol.String(filter, "name")!,
                    [.. ((JsonArray)filter["extensions"]!).Select(static extension => extension!.GetValue<string>())])
                {
                    MimeTypes = filter["mimeTypes"] is JsonArray types ? [.. types.Select(static type => type!.GetValue<string>())] : [],
                };
            })];
        }
        var save = kind == RunicHostedDialogKind.SaveFile;
        return (kind, new NativeFileDialogRequest(save, save ? HostProtocol.String(message, "suggestedName") : null)
        {
            Filters = filters,
            InitialDirectory = directory,
            AllowMultiple = kind is RunicHostedDialogKind.OpenFiles or RunicHostedDialogKind.OpenFolders,
            ShowFilterChoice = HostProtocol.Boolean(message, "showFilterChoice") ?? false,
            Title = HostProtocol.String(message, "title"),
        });
    }

    // The box of a checked message-box request (HostFeatures.CheckMessageBox); unknown icons and roles read as none
    // and normal.
    internal static DesktopMessageBox ReadMessageBox(JsonObject message) => new(HostProtocol.String(message, "message")!)
    {
        Title = HostProtocol.String(message, "title"),
        Detail = HostProtocol.String(message, "detail"),
        Kind = HostProtocol.String(message, "icon") switch
        {
            "information" => DesktopMessageBoxKind.Information,
            "warning" => DesktopMessageBoxKind.Warning,
            "error" => DesktopMessageBoxKind.Error,
            _ => DesktopMessageBoxKind.None,
        },
        Buttons = message["buttons"] is JsonArray buttons
            ? [.. buttons.Select(static node =>
            {
                var button = (JsonObject)node!;
                return new DesktopMessageBoxButton(HostProtocol.String(button, "id")!, HostProtocol.String(button, "label")!)
                {
                    Role = HostProtocol.String(button, "role") switch
                    {
                        "default" => DesktopMessageBoxButtonRole.Default,
                        "cancel" => DesktopMessageBoxButtonRole.Cancel,
                        "destructive" => DesktopMessageBoxButtonRole.Destructive,
                        _ => DesktopMessageBoxButtonRole.Normal,
                    },
                    AccessKey = HostProtocol.String(button, "accessKey") is { Length: 1 } key ? key[0] : null,
                };
            })]
            : [],
    };
}
