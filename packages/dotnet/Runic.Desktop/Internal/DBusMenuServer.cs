using System.Collections.Immutable;
using Runic.Platform.Linux.DBus;

namespace Runic.Platform.Linux.DBus.Menus;

// A com.canonical.dbusmenu server (libdbusmenu's dbus-menu.xml, Version 3), shared by the status icon menu
// (Runic.Platform.Linux.Portal) and the Linux global menu of Runic Desktop's GTK 3 and GTK 4 windows. Source-linked into
// both packages and Runic.Desktop.Gtk4, so it stays self-contained.
//
// The owner publishes immutable layouts; calls read the current one on the connection's thread. Calls are answered one
// at a time and id lists are bounded, because any program on the session bus can call the object.

// One entry of a menu: its numeric id (0 is the root), the properties that differ from dbusmenu's defaults, its
// children's ids and the owner's own object for the entry.
internal sealed record DBusMenuNode(int Id, ImmutableArray<KeyValuePair<string, DBusValue>> Properties, ImmutableArray<int> Children,
    object? Source = null)
{
    // The properties of an entry, without those that have dbusmenu's default value. toggleType is "checkmark", "radio"
    // or null; shortcut is one key combination as Qt's key names, such as ["Control", "S"].
    internal static ImmutableArray<KeyValuePair<string, DBusValue>> Describe(bool separator = false, string? label = null,
        bool enabled = true, string? toggleType = null, bool toggled = false, bool submenu = false, ImmutableArray<string> shortcut = default)
    {
        var properties = ImmutableArray.CreateBuilder<KeyValuePair<string, DBusValue>>(7);
        if (separator) properties.Add(new("type", DBusValue.From("separator")));
        if (label is not null) properties.Add(new("label", DBusValue.From(label)));
        if (!enabled) properties.Add(new("enabled", DBusValue.From(false)));
        if (toggleType is not null)
        {
            properties.Add(new("toggle-type", DBusValue.From(toggleType)));
            properties.Add(new("toggle-state", DBusValue.From(toggled ? 1 : 0)));
        }
        if (!shortcut.IsDefaultOrEmpty) properties.Add(new("shortcut", DBusValue.Array("as", [DBusValue.StringArray(shortcut)])));
        if (submenu) properties.Add(new("children-display", DBusValue.From("submenu")));
        return properties.ToImmutable();
    }

    internal DBusValue? this[string name]
    {
        get
        {
            foreach (var property in Properties)
            {
                if (property.Key == name) return property.Value;
            }
            return null;
        }
    }
}

// A menu as the server answers it, with the revision LayoutUpdated announced.
internal sealed class DBusMenuLayout
{
    internal DBusMenuLayout(uint revision, IReadOnlyDictionary<int, DBusMenuNode> nodes)
    {
        Revision = revision;
        Nodes = nodes;
    }

    internal static DBusMenuLayout Empty { get; } = new(0, new Dictionary<int, DBusMenuNode>
    {
        [0] = new(0, DBusMenuNode.Describe(submenu: true), []),
    });

    internal uint Revision { get; }
    internal IReadOnlyDictionary<int, DBusMenuNode> Nodes { get; }

    internal DBusValue Layout(DBusMenuNode node, int depth) => DBusValue.Struct(DBusValue.From(node.Id), DBusMenuServer.Properties(node),
        DBusValue.Array("v", depth == 0 ? [] : node.Children.Where(Nodes.ContainsKey).Select(child => DBusValue.Variant(Layout(Nodes[child], depth - 1)))));
}

// What the server asks its owner, on the connection's thread.
internal interface IDBusMenuOwner
{
    DBusMenuLayout Layout { get; }

    // A "clicked" event on an entry; dbusmenu's other events ("hovered", "opened", "closed") choose nothing.
    void Clicked(DBusMenuNode node);

    // Whether the entry's submenu changed before it opens; true makes the caller read it again.
    ValueTask<bool> AboutToShowAsync(DBusMenuNode node);
}

internal sealed class DBusMenuServer(IDBusMenuOwner owner) : DBusObject
{
    internal const string Interface = "com.canonical.dbusmenu";
    // Id lists (GetGroupProperties, EventGroup, AboutToShowGroup) longer than this are refused before they are read in full.
    internal const int MenuIdLimit = 1024;
    // The properties this server sets; ItemsPropertiesUpdated resets the others an entry no longer has.
    private static readonly string[] KnownProperties = ["type", "label", "enabled", "toggle-type", "toggle-state", "shortcut", "children-display"];
    private static readonly string[] MenuObjectProperties = ["Version", "TextDirection", "Status", "IconThemePath"];

    protected override int MaxConcurrentCalls => 1;
    protected override string IntrospectionXml => Xml;

    // Tells clients to read the layout below parent again.
    internal bool LayoutUpdated(uint revision, int parent = 0) =>
        EmitSignal(Interface, "LayoutUpdated", DBusValue.From(revision), DBusValue.From(parent));

    // Sends the entries' current properties; a property an entry no longer has goes back to its default.
    internal bool ItemsPropertiesUpdated(IEnumerable<DBusMenuNode> nodes)
    {
        var list = nodes.ToList();
        if (list.Count == 0) return true;
        return EmitSignal(Interface, "ItemsPropertiesUpdated",
            DBusValue.Array("(ia{sv})", list.Select(static node => DBusValue.Struct(DBusValue.From(node.Id), Properties(node)))),
            DBusValue.Array("(ias)", list.Select(static node => DBusValue.Struct(DBusValue.From(node.Id),
                DBusValue.StringArray(KnownProperties.Where(name => node[name] is null)))))
        );
    }

    internal static DBusValue Properties(DBusMenuNode node) => DBusValue.VariantDictionary(node.Properties);

    // A property with dbusmenu's default when the entry does not set it; null for a name dbusmenu does not define.
    internal static DBusValue? Property(DBusMenuNode node, string name) => node[name] ?? name switch
    {
        "type" => DBusValue.From("standard"),
        "label" => DBusValue.From(""),
        "enabled" or "visible" => DBusValue.From(true),
        "toggle-type" => DBusValue.From(""),
        "toggle-state" => DBusValue.From(0),
        "children-display" => DBusValue.From(""),
        _ => null,
    };

    protected override async ValueTask<ImmutableArray<DBusValue>> HandleMethodAsync(DBusMethodContext context)
    {
        if (context.Interface != Interface) throw UnknownMethod(context);
        Takes(context, context.Member switch
        {
            "GetLayout" => "iias",
            "GetGroupProperties" => "aias",
            "GetProperty" => "is",
            "Event" => "isvu",
            "EventGroup" => "a(isvu)",
            "AboutToShow" => "i",
            "AboutToShowGroup" => "ai",
            _ => throw UnknownMethod(context),
        });
        var menu = owner.Layout;
        var arguments = context.Arguments;
        switch (context.Member)
        {
            case "GetLayout":
            {
                int parent = arguments[0].GetInt32(), depth = arguments[1].GetInt32();
                if (!menu.Nodes.TryGetValue(parent, out var node)) throw UnknownId(parent);
                return [DBusValue.From(menu.Revision), menu.Layout(node, depth)];
            }
            case "GetGroupProperties":
            {
                // Each id is answered once, so the reply is no longer than the menu.
                var items = arguments[0].GetItems();
                if (items.Length > MenuIdLimit) throw TooMany();
                var answered = new List<DBusValue>();
                var seen = new HashSet<int>();
                foreach (var item in items)
                {
                    int id = item.GetInt32();
                    if (seen.Add(id) && menu.Nodes.TryGetValue(id, out var node)) answered.Add(DBusValue.Struct(DBusValue.From(id), Properties(node)));
                }
                return [DBusValue.Array("(ia{sv})", answered)];
            }
            case "GetProperty":
            {
                int id = arguments[0].GetInt32(); string name = arguments[1].GetString();
                if (!menu.Nodes.TryGetValue(id, out var node)) throw UnknownId(id);
                return Property(node, name) is { } value ? [DBusValue.Variant(value)]
                    : throw new DBusException(DBusException.InvalidArgs, $"No property {name}.");
            }
            case "Event":
            {
                int id = arguments[0].GetInt32(); string kind = arguments[1].GetString();
                if (!menu.Nodes.TryGetValue(id, out var node)) throw UnknownId(id);
                if (kind == "clicked") owner.Clicked(node);
                return [];
            }
            case "EventGroup":
            {
                // The events are read in full before any is chosen, so a refused group activates nothing. An entry
                // clicked twice in one group is chosen once, and an unknown id is reported once.
                var events = arguments[0].GetItems();
                if (events.Length > MenuIdLimit) throw TooMany();
                var unknown = new List<int>();
                var chosen = new List<DBusMenuNode>();
                var seen = new HashSet<(int, string)>();
                foreach (var entry in events)
                {
                    var fields = entry.GetFields();
                    int id = fields[0].GetInt32(); string kind = fields[1].GetString();
                    if (!seen.Add((id, kind))) continue;
                    if (menu.Nodes.TryGetValue(id, out var node))
                    {
                        if (kind == "clicked") chosen.Add(node);
                    }
                    else if (!unknown.Contains(id))
                    {
                        unknown.Add(id);
                    }
                }
                foreach (var node in chosen) owner.Clicked(node);
                return [DBusValue.Array("i", unknown.Select(static id => DBusValue.From(id)))];
            }
            case "AboutToShow":
            {
                // An unknown id needs no update: the caller reads the layout again when it changed anyway.
                int id = arguments[0].GetInt32();
                return [DBusValue.From(menu.Nodes.TryGetValue(id, out var node) && await owner.AboutToShowAsync(node).ConfigureAwait(false))];
            }
            default: // AboutToShowGroup
            {
                var ids = arguments[0].GetItems();
                if (ids.Length > MenuIdLimit) throw TooMany();
                var updates = new List<int>();
                var unknown = new List<int>();
                foreach (var id in ids.Select(static id => id.GetInt32()).Distinct())
                {
                    if (!menu.Nodes.TryGetValue(id, out var node)) unknown.Add(id);
                    else if (await owner.AboutToShowAsync(node).ConfigureAwait(false)) updates.Add(id);
                }
                return [DBusValue.Array("i", updates.Select(static id => DBusValue.From(id))), DBusValue.Array("i", unknown.Select(static id => DBusValue.From(id)))];
            }
        }
    }

    // Checks a call's signature before its arguments are used, so a peer cannot make a handler misread them.
    private static void Takes(DBusMethodContext context, string signature)
    {
        if (context.Signature.Value != signature)
            throw new DBusException(DBusException.InvalidArgs, $"{context.Member} takes \"{signature}\", not \"{context.Signature.Value}\".");
    }

    private static DBusException UnknownMethod(DBusMethodContext context) =>
        new(DBusException.UnknownMethod, $"No method {context.Member} on {context.Interface}.");

    private static DBusException UnknownId(int id) => new(DBusException.InvalidArgs, $"No menu entry {id}.");

    private static DBusException TooMany() => new(DBusException.LimitsExceeded, $"A menu call takes at most {MenuIdLimit} ids or events.");

    protected override ValueTask<DBusValue> GetPropertyAsync(string interfaceName, string name, DBusMethodContext context) =>
        interfaceName == Interface && MenuProperty(name) is { } value ? ValueTask.FromResult(value)
            : throw new DBusException(DBusException.UnknownProperty, $"No property {name}.");

    protected override ValueTask<ImmutableDictionary<string, DBusValue>> GetAllPropertiesAsync(string interfaceName, DBusMethodContext context)
    {
        var properties = ImmutableDictionary.CreateBuilder<string, DBusValue>(StringComparer.Ordinal);
        if (interfaceName == Interface)
            foreach (var name in MenuObjectProperties) properties[name] = MenuProperty(name)!.Value;
        return ValueTask.FromResult(properties.ToImmutable());
    }

    protected override ValueTask SetPropertyAsync(string interfaceName, string name, DBusValue value, DBusMethodContext context) =>
        throw new DBusException(DBusException.PropertyReadOnly, "Menu properties are read-only.");

    // TextDirection stays ltr until the menus follow the layout direction (B12).
    private static DBusValue? MenuProperty(string name) => name switch
    {
        "Version" => DBusValue.From(3u),
        "TextDirection" => DBusValue.From("ltr"),
        "Status" => DBusValue.From("normal"),
        "IconThemePath" => DBusValue.StringArray([]),
        _ => null,
    };

    private const string Xml = """
        <interface name="com.canonical.dbusmenu">
          <property name="Version" type="u" access="read"/>
          <property name="TextDirection" type="s" access="read"/>
          <property name="Status" type="s" access="read"/>
          <property name="IconThemePath" type="as" access="read"/>
          <method name="GetLayout"><arg type="i" name="parentId" direction="in"/><arg type="i" name="recursionDepth" direction="in"/><arg type="as" name="propertyNames" direction="in"/><arg type="u" name="revision" direction="out"/><arg type="(ia{sv}av)" name="layout" direction="out"/></method>
          <method name="GetGroupProperties"><arg type="ai" name="ids" direction="in"/><arg type="as" name="propertyNames" direction="in"/><arg type="a(ia{sv})" name="properties" direction="out"/></method>
          <method name="GetProperty"><arg type="i" name="id" direction="in"/><arg type="s" name="name" direction="in"/><arg type="v" name="value" direction="out"/></method>
          <method name="Event"><arg type="i" name="id" direction="in"/><arg type="s" name="eventId" direction="in"/><arg type="v" name="data" direction="in"/><arg type="u" name="timestamp" direction="in"/></method>
          <method name="EventGroup"><arg type="a(isvu)" name="events" direction="in"/><arg type="ai" name="idErrors" direction="out"/></method>
          <method name="AboutToShow"><arg type="i" name="id" direction="in"/><arg type="b" name="needUpdate" direction="out"/></method>
          <method name="AboutToShowGroup"><arg type="ai" name="ids" direction="in"/><arg type="ai" name="updatesNeeded" direction="out"/><arg type="ai" name="idErrors" direction="out"/></method>
          <signal name="ItemsPropertiesUpdated"><arg type="a(ia{sv})" name="updatedProps"/><arg type="a(ias)" name="removedProps"/></signal>
          <signal name="LayoutUpdated"><arg type="u" name="revision"/><arg type="i" name="parent"/></signal>
          <signal name="ItemActivationRequested"><arg type="i" name="id"/><arg type="u" name="timestamp"/></signal>
        </interface>
        """;
}
