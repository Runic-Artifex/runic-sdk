using System.Globalization;
using System.Runtime.Versioning;
using Tmds.DBus.Protocol;

// AT-SPI on the accessibility bus, as Orca uses it: the fixture's application by process ID, its tree (into the
// WebKit web process, whose accessible root the window embeds), the Value, Selection, Action and Component
// interfaces, and the object and focus events, which the registry delivers once the client registers for them.
[SupportedOSPlatform("linux")]
internal sealed class LinuxAccessibility : AccessibilityClient
{
    private const string Accessible = "org.a11y.atspi.Accessible";
    private readonly DBusConnection _session;
    private readonly DBusConnection _bus;
    private readonly List<IDisposable> _subscriptions = [];
    private Node _application = new("", "");

    // The application registers on the accessibility bus once its toolkit starts, and the page's controls appear once
    // WebKit's web process has loaded it; both are condition waits, scaled on a slow runner.
    private static readonly TimeSpan ApplicationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FindTimeout = TimeSpan.FromSeconds(10);

    private LinuxAccessibility(DBusConnection session, DBusConnection bus)
    {
        _session = session;
        _bus = bus;
    }

    internal override string Api => "AT-SPI";

    // Registers for events first, as a running screen reader has: WebKit's web process reads the registered events when
    // it starts. listening runs once registered, so that the fixture can start its page.
    internal static async Task<LinuxAccessibility> CreateAsync(int pid, Action listening, CancellationToken cancellationToken)
    {
        var session = new DBusConnection(DBusAddress.Session ?? throw new InvalidOperationException("No session bus."));
        await session.ConnectAsync();
        var address = await session.CallMethodAsync(Method(session, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress"),
            static (message, _) => message.GetBodyReader().ReadString());
        var bus = new DBusConnection(address);
        await bus.ConnectAsync();
        var client = new LinuxAccessibility(session, bus);
        await client.ListenAsync();
        listening();
        Node? application = null;
        var seen = new List<string>();
        if (!await TestTimeouts.UntilAsync(async () =>
            {
                seen.Clear();
                foreach (var child in await ChildrenAsync(bus, new Node("org.a11y.atspi.Registry", "/org/a11y/atspi/accessible/root")))
                {
                    var owner = await bus.CallMethodAsync(Method(bus, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus",
                        "GetConnectionUnixProcessID", "s", (ref MessageWriter writer) => writer.WriteString(child.Bus)),
                        static (message, _) => message.GetBodyReader().ReadUInt32());
                    seen.Add($"{child.Bus} (process {owner})");
                    if (owner == pid) application = child;
                }
                return application is not null;
            }, ApplicationTimeout, TimeSpan.FromMilliseconds(100), cancellationToken))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Process {pid} has no application on the accessibility bus within {TestTimeouts.Scale(ApplicationTimeout).TotalSeconds:0.###} s; it has: {string.Join(", ", seen)}."));
        }
        client._application = application!;
        return client;
    }

    private async Task ListenAsync()
    {
        foreach (var @interface in new[] { "org.a11y.atspi.Event.Object", "org.a11y.atspi.Event.Focus" })
        {
            _subscriptions.Add(await _bus.AddMatchAsync(new MatchRule { Type = MessageType.Signal, Interface = @interface },
                static (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    var detail = reader.ReadString();
                    var detail1 = reader.ReadInt32();
                    _ = reader.ReadInt32();
                    var data = reader.ReadVariantValue();
                    return new Signal(message.SenderAsString!, message.PathAsString!, message.MemberAsString!, detail, detail1,
                        data.Type == VariantValueType.String ? data.GetString() : "");
                },
                value =>
                {
                    if (value.HasValue) _ = Task.Run(() => DispatchAsync(value.Value));
                }, emitOnCapturedContext: false));
        }
        foreach (var name in new[] { "object:", "focus:" })
        {
            await _bus.CallMethodAsync(Method(_bus, "org.a11y.atspi.Registry", "/org/a11y/atspi/registry", "org.a11y.atspi.Registry",
                "RegisterEvent", "s", (ref MessageWriter writer) => writer.WriteString(name)));
        }
    }

    private readonly record struct Signal(string Bus, string Path, string Member, string Detail, int Detail1, string Data);

    private async Task DispatchAsync(Signal signal)
    {
        try
        {
            var node = new Node(signal.Bus, signal.Path);
            EventKind? kind = signal.Member switch
            {
                "PropertyChange" when signal.Detail == "accessible-value" => EventKind.ValueChanged,
                "ValueChanged" => EventKind.ValueChanged,
                "SelectionChanged" => EventKind.SelectionChanged,
                "Announcement" => EventKind.Announcement,
                // Orca follows live regions through the text and children of the region's element.
                "TextChanged" when signal.Detail.StartsWith("insert", StringComparison.Ordinal) => EventKind.LiveRegion,
                "ChildrenChanged" when signal.Detail.StartsWith("add", StringComparison.Ordinal) => EventKind.LiveRegion,
                "Focus" => EventKind.FocusChanged,
                "StateChanged" when signal.Detail == "focused" && signal.Detail1 == 1 => EventKind.FocusChanged,
                _ => null,
            };
            if (kind is null) return;
            var name = await NameAsync(node);
            var role = await RoleAsync(node);
            var data = kind switch
            {
                EventKind.ValueChanged => Format(await DoubleAsync(node, "CurrentValue")),
                EventKind.SelectionChanged => string.Join(", ", await SelectedNamesAsync(node)),
                EventKind.LiveRegion => signal.Data.Length > 0 ? signal.Data : await TextAsync(node),
                _ => signal.Data,
            };
            Raise(new AccessibilityEvent(kind.Value, name, role, $"{signal.Member}:{signal.Detail} {data}".Trim()));
        }
        catch (Exception error) when (IsMissing(error))
        {
            // The element went away before it could be read.
        }
    }

    internal override async Task<string> WindowTitleAsync(CancellationToken cancellationToken)
    {
        foreach (var child in await ChildrenAsync(_bus, _application))
        {
            var role = await RoleAsync(child);
            if (role is "frame" or "window" or "dialog") return $"{await NameAsync(child)} ({role})";
        }
        return "";
    }

    internal override async Task<Control?> FindAsync(ControlKind kind, string name, CancellationToken cancellationToken)
    {
        string[] roles = kind switch
        {
            ControlKind.Button => ["button"],
            ControlKind.Slider => ["slider"],
            // WebKitGTK exposes <input type="number"> as an entry.
            ControlKind.SpinButton => ["spin button", "entry"],
            ControlKind.ListBox => ["list box", "list"],
            _ => [],
        };
        // The page loads after the application registers.
        var wait = TestTimeouts.Start(FindTimeout);
        while (true)
        {
            var matches = new List<Control>();
            await WalkAsync(_application, 0, [], async node =>
            {
                var role = await RoleAsync(node);
                if (roles.Contains(role) && await NameAsync(node) == name) matches.Add(new Control(kind, name, role, node));
            }, cancellationToken);
            if (matches.Count == 1)
            {
                wait.Succeeded();
                return matches[0];
            }
            if (matches.Count > 1) throw new InvalidOperationException($"{matches.Count} elements match {kind} '{name}'.");
            if (wait.IsExpired) return null;
            await Task.Delay(200, cancellationToken);
        }
    }

    internal override async Task<string> DumpAsync(CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        await DumpAsync(_application, 0, [], lines, cancellationToken);
        return string.Join("\n", lines);
    }

    private async Task DumpAsync(Node node, int depth, HashSet<Node> visited, List<string> lines, CancellationToken cancellationToken)
    {
        if (depth > 40 || lines.Count > 400 || !visited.Add(node)) return;
        string role, name;
        try { (role, name) = (await RoleAsync(node), await NameAsync(node)); }
        catch (Exception error) when (IsMissing(error)) { (role, name) = ("?", error.Message); }
        var value = await DoubleAsync(node, "CurrentValue");
        lines.Add($"{new string(' ', depth * 2)}{role} '{name}'{(value is null ? "" : $" value={Format(value)}")} {node.Bus}{node.Path}");
        IReadOnlyList<Node> children;
        try { children = await ChildrenAsync(_bus, node); }
        catch (Exception error) when (IsMissing(error)) { return; }
        foreach (var child in children) await DumpAsync(child, depth + 1, visited, lines, cancellationToken);
    }

    private async Task WalkAsync(Node node, int depth, HashSet<Node> visited, Func<Node, Task> visit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 40 || visited.Count > 5000 || !visited.Add(node)) return;
        // The page can remove a node between listing and reading it; the next attempt walks the tree again.
        try { await visit(node); }
        catch (Exception error) when (IsMissing(error)) { return; }
        IReadOnlyList<Node> children;
        try { children = await ChildrenAsync(_bus, node); }
        catch (Exception error) when (IsMissing(error)) { return; }
        foreach (var child in children) await WalkAsync(child, depth + 1, visited, visit, cancellationToken);
    }

    internal override async Task<RangeState> ReadRangeAsync(Control control, CancellationToken cancellationToken)
    {
        var node = (Node)control.Handle;
        var value = await DoubleAsync(node, "CurrentValue");
        // An entry without the Value interface: its text, as a screen reader reads it.
        if (value is null && double.TryParse(await TextAsync(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var text)) value = text;
        return new RangeState(value, await DoubleAsync(node, "MinimumValue"), await DoubleAsync(node, "MaximumValue"));
    }

    internal override Task ChangeRangeAsync(Control control, double value, CancellationToken cancellationToken)
        => SetValueAsync((Node)control.Handle, value);

    internal override async Task StepAsync(Control control, int direction, CancellationToken cancellationToken)
    {
        // AT-SPI has no increment: a client sets the next value. Orca changes a web page's spin button with the arrow
        // keys; setting CurrentValue on an ARIA spin button aborts the WebKitGTK web process (GLib assertion in
        // invoke_set_property_in_idle_cb), so the probe does not.
        if (control.Role == "spin button")
            throw new NotSupportedException("AT-SPI has no increment action, and setting the value of an ARIA spin button aborts the WebKitGTK web process (#396)");
        var node = (Node)control.Handle;
        var now = await DoubleAsync(node, "CurrentValue") ?? throw new InvalidOperationException($"{control.Name} has no value.");
        var step = await DoubleAsync(node, "MinimumIncrement") is { } increment and > 0 ? increment : 1;
        await SetValueAsync(node, now + direction * step);
    }

    private async Task SetValueAsync(Node node, double value)
    {
        await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.freedesktop.DBus.Properties", "Set", "ssv", (ref MessageWriter writer) =>
        {
            writer.WriteString("org.a11y.atspi.Value");
            writer.WriteString("CurrentValue");
            writer.WriteVariantDouble(value);
        }));
    }

    internal override async Task PressAsync(Control control, CancellationToken cancellationToken)
    {
        var node = (Node)control.Handle;
        var done = await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.a11y.atspi.Action", "DoAction", "i",
            (ref MessageWriter writer) => writer.WriteInt32(0)), static (message, _) => message.GetBodyReader().ReadBool());
        if (!done) throw new InvalidOperationException($"'{control.Name}' refused its action.");
    }

    internal override async Task<IReadOnlyList<string>> ReadSelectionAsync(Control control, CancellationToken cancellationToken)
        => await SelectedNamesAsync((Node)control.Handle);

    internal override async Task SelectAsync(Control control, string item, CancellationToken cancellationToken)
    {
        var node = (Node)control.Handle;
        var children = await ChildrenAsync(_bus, node);
        for (var index = 0; index < children.Length; index++)
        {
            if (await NameAsync(children[index]) != item) continue;
            var done = await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.a11y.atspi.Selection", "SelectChild", "i",
                (ref MessageWriter writer) => writer.WriteInt32(index)), static (message, _) => message.GetBodyReader().ReadBool());
            if (!done) throw new InvalidOperationException($"'{control.Name}' refused to select '{item}'.");
            return;
        }
        throw new InvalidOperationException($"'{control.Name}' has no item '{item}'.");
    }

    internal override async Task FocusAsync(Control control, CancellationToken cancellationToken)
    {
        var node = (Node)control.Handle;
        var done = await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.a11y.atspi.Component", "GrabFocus"),
            static (message, _) => message.GetBodyReader().ReadBool());
        if (!done) throw new InvalidOperationException($"'{control.Name}' refused focus.");
    }

    private async Task<IReadOnlyList<string>> SelectedNamesAsync(Node node)
    {
        int count;
        try { count = (await PropertyAsync(node, "org.a11y.atspi.Selection", "NSelectedChildren")).GetInt32(); }
        catch (Exception error) when (IsMissing(error)) { return []; }
        var names = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var child = await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.a11y.atspi.Selection", "GetSelectedChild", "i",
                (ref MessageWriter writer) => writer.WriteInt32(index)), static (message, _) => { var reader = message.GetBodyReader(); return ReadNode(ref reader); });
            names.Add(await NameAsync(child));
        }
        return names;
    }

    private async Task<double?> DoubleAsync(Node node, string property)
    {
        try { return (await PropertyAsync(node, "org.a11y.atspi.Value", property)).GetDouble(); }
        catch (Exception error) when (IsMissing(error)) { return null; }
    }

    private async Task<string> TextAsync(Node node)
    {
        try
        {
            var count = (await PropertyAsync(node, "org.a11y.atspi.Text", "CharacterCount")).GetInt32();
            return await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.a11y.atspi.Text", "GetText", "ii", (ref MessageWriter writer) =>
            {
                writer.WriteInt32(0);
                writer.WriteInt32(count);
            }), static (message, _) => message.GetBodyReader().ReadString());
        }
        catch (Exception error) when (IsMissing(error)) { return ""; }
    }

    private async Task<string> NameAsync(Node node) => (await PropertyAsync(node, Accessible, "Name")).GetString();

    // WebKitGTK's own AT-SPI objects answer GetRoleName with an empty string; Orca uses the role number.
    private async Task<string> RoleAsync(Node node)
    {
        var role = await _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, Accessible, "GetRole"),
            static (message, _) => message.GetBodyReader().ReadUInt32());
        return role switch
        {
            16 => "dialog",
            23 => "frame",
            31 => "list",
            32 => "list item",
            43 => "button",
            51 => "slider",
            52 => "spin button",
            69 => "window",
            79 => "entry",
            98 => "list box",
            _ => "role " + role.ToString(CultureInfo.InvariantCulture),
        };
    }

    private Task<VariantValue> PropertyAsync(Node node, string @interface, string property)
        => _bus.CallMethodAsync(Method(_bus, node.Bus, node.Path, "org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter writer) =>
        {
            writer.WriteString(@interface);
            writer.WriteString(property);
        }), static (message, _) => message.GetBodyReader().ReadVariantValue());

    private static Task<Node[]> ChildrenAsync(DBusConnection bus, Node node)
        => bus.CallMethodAsync(Method(bus, node.Bus, node.Path, Accessible, "GetChildren"), static (message, _) =>
        {
            var reader = message.GetBodyReader();
            var children = new List<Node>();
            var end = reader.ReadArrayStart(DBusType.Struct);
            while (reader.HasNext(end)) children.Add(ReadNode(ref reader));
            return children.ToArray();
        });

    private static Node ReadNode(ref Reader reader)
    {
        reader.AlignStruct();
        return new Node(reader.ReadString(), reader.ReadObjectPathAsString());
    }

    private static MessageBuffer Method(DBusConnection connection, string destination, string path, string @interface, string member,
        string? signature = null, BodyWriter? write = null)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: destination, path: path, @interface: @interface, member: member, signature: signature);
            write?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    // An element without the interface, or one that went away, answers with an error.
    private static bool IsMissing(Exception error) => error is DBusErrorReplyException or DBusUnexpectedValueException;

    private static string Format(double? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    public override ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _bus.Dispose();
        _session.Dispose();
        return ValueTask.CompletedTask;
    }

    private delegate void BodyWriter(ref MessageWriter writer);

    private sealed record Node(string Bus, string Path);
}
