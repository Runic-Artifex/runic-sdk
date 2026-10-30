using System.Collections.Concurrent;
using System.Collections.Immutable;
using Runic.Platform;
using Runic.Platform.Linux.Portal;
using Tmds.DBus.Protocol;
using TUnit.Core;

internal sealed class GlobalShortcutsTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    [Test, DisplayName("global shortcuts: preferred triggers in the XDG shortcuts form")]
    public void PreferredTriggers()
    {
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+Shift+S")) == "CTRL+SHIFT+s", "letter");
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+Alt+Space")) == "CTRL+ALT+space", "space");
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Control+F5")) == "CTRL+F5", "Control is Ctrl");
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+,")) == "CTRL+comma", "punctuation keysym");
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Alt+PageDown")) == "ALT+Page_Down", "named key");
        Check(DesktopShortcutKeys.XdgTrigger(DesktopShortcut.Parse("Primary+Plus")) == "CTRL+plus", "plus");
    }

    [Test, DisplayName("global shortcuts: the desktop's trigger text is read back where it has a DesktopShortcut form")]
    public void TriggerDescriptions()
    {
        (string Text, string? Shortcut)[] cases =
        [
            ("Press <Control><Alt>space", "Primary+Alt+Space"),
            ("Press <Shift><Control>s or <Control>t", "Primary+Shift+S"),
            ("<Primary>comma", "Primary+,"),
            ("Ctrl+Shift+S", "Primary+Shift+S"),
            ("Ctrl+Alt+Return, Meta+X", "Primary+Alt+Enter"),
            ("Ctrl++", "Primary+Plus"),
            ("Alt+PgDown", "Alt+PageDown"),
            ("CTRL+SHIFT+s", "Primary+Shift+S"),
            ("F12", "F12"),
            ("Meta+S", null),
            ("<Super>s", null),
            ("Strg+Umschalt+S", null),
            ("", null),
        ];
        foreach (var (text, expected) in cases)
        {
            var parsed = DesktopShortcutKeys.ParseTrigger(text);
            Check(parsed?.ToString() == expected, $"{text}: {parsed}");
        }
    }

    [Test, Explicit, Category("DBus"), DisplayName("global shortcuts: session, BindShortcuts reply, Activated, ShortcutsChanged, ListShortcuts, version 1 and 2 ConfigureShortcuts, rebind, owner restart and dispose")]
    public async Task GlobalShortcutsAsync()
    {
        const string name = "org.runic.GlobalShortcutsPortal";
        const string appId = "org.runic.GlobalShortcutsApplication";
        using var bus = new DBusConnection(DBusAddress.Session!);
        await bus.ConnectAsync();
        var service = new Service(bus) { RequiredIdentity = appId };
        bus.AddMethodHandler(service);
        Check(await bus.TryRequestNameAsync(name, RequestNameOptions.AllowReplacement), "portal name");
        var diagnostics = new ConcurrentQueue<string>();
        var shortcuts = new PortalGlobalShortcuts(new PortalApplication(appId, diagnostic => diagnostics.Enqueue(diagnostic.Code)), destination: name);
        var activations = System.Threading.Channels.Channel.CreateUnbounded<DesktopGlobalShortcutActivation>();
        var changes = System.Threading.Channels.Channel.CreateUnbounded<DesktopGlobalShortcutsChange>();
        shortcuts.Activated += (_, activation) => activations.Writer.TryWrite(activation);
        shortcuts.BindingsChanged += (_, change) => changes.Writer.TryWrite(change);

        Check(await shortcuts.ListAsync() is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success { Value.IsEmpty: true }, "empty before the first bind");
        ImmutableArray<DesktopGlobalShortcut> set =
        [
            new("show", "Show the window") { PreferredShortcut = DesktopShortcut.Parse("Primary+Alt+Space") },
            new("record", "Start recording") { PreferredShortcut = DesktopShortcut.Parse("Primary+Shift+R") },
            new("plain", "Without a key"),
        ];
        var bound = await shortcuts.BindAsync(set);
        Check(bound is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success, $"bind: {bound}");
        var bindings = ((PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success)bound).Value;
        Check(service.Bound is [("show", "Show the window", "CTRL+ALT+space"), ("record", "Start recording", "CTRL+SHIFT+r"), ("plain", "Without a key", null)],
            "BindShortcuts sends ids, descriptions and preferred triggers: " + string.Join("; ", service.Bound));
        Check(bindings is [{ Id: "show", Status: DesktopGlobalShortcutStatus.Bound, TriggerDescription: "Press <Control><Alt>space" }, { Id: "record", Status: DesktopGlobalShortcutStatus.Bound }, { Id: "plain", Status: DesktopGlobalShortcutStatus.Unbound, Shortcut: null, TriggerDescription: null }]
            && bindings[0].Shortcut == DesktopShortcut.Parse("Primary+Alt+Space"), "bindings follow the reply: " + string.Join("; ", bindings));
        string first = shortcuts.SessionHandle!;
        Check(service.Sessions.Contains(first), "the session handle is the portal's");

        // Presses of this session's shortcuts are reported with their activation token; releases, other sessions and
        // unknown ids are not.
        service.Signal("Deactivated", first, "show", null);
        service.Signal("Activated", "/org/freedesktop/portal/desktop/session/other/x", "show", null);
        service.Signal("Activated", first, "unknown", null);
        service.Signal("Activated", first, "show", "token-1");
        var activation = await activations.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(3));
        Check(activation is { Id: "show", PlatformContext.ActivationToken: "token-1" }, $"activation: {activation}");
        await Task.Delay(100);
        Check(!activations.Reader.TryRead(out _), "a release, another session or an unknown id was reported");

        // The user changes a key in the desktop's settings.
        service.Assigned["record"] = "Ctrl+Shift+T";
        service.Changed(first, "record");
        var change = await changes.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(3));
        Check(change.Bindings is [{ Id: "show", Status: DesktopGlobalShortcutStatus.Bound }, { Id: "record", TriggerDescription: "Ctrl+Shift+T" }, { Id: "plain", Status: DesktopGlobalShortcutStatus.Unbound }]
            && change.Bindings[1].Shortcut == DesktopShortcut.Parse("Primary+Shift+T"), "ShortcutsChanged updates only the listed action");
        service.Assigned.TryRemove("show", out _);
        var listed = await shortcuts.ListAsync();
        Check(listed is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success { Value: [{ Id: "show", Status: DesktopGlobalShortcutStatus.Unbound }, { Id: "record", Status: DesktopGlobalShortcutStatus.Bound }, _] }
            && service.Calls.Contains("ListShortcuts"), $"ListShortcuts: {listed}");

        // ConfigureShortcuts is version 2.
        service.Version = 1;
        Check(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }, "version 1 has no configuration dialog");
        Check(service.Configured.IsEmpty, "ConfigureShortcuts called on version 1");
        service.Version = 2;
        Check(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Success && service.Configured.Contains(first), "version 2 opens the dialog for the session");

        // A new set replaces the session: the portal binds once per session.
        Check(await shortcuts.BindAsync([new("show", "Show the window") { PreferredShortcut = DesktopShortcut.Parse("Primary+Alt+W") }])
            is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success { Value: [{ Status: DesktopGlobalShortcutStatus.Bound }] }, "rebind");
        string second = shortcuts.SessionHandle!;
        Check(second != first && service.Closed.Contains(first), "the previous session was closed");
        Check(service.Bound is [("show", "Show the window", "CTRL+ALT+w")], "the new session binds the new set");
        service.Signal("Activated", first, "show", null);
        await Task.Delay(100);
        Check(!activations.Reader.TryRead(out _), "the closed session's activation was reported");

        // A replaced portal ends the session: the set is bound again in a new session on the replacement.
        using var replacementBus = new DBusConnection(DBusAddress.Session!);
        await replacementBus.ConnectAsync();
        var replacement = new Service(replacementBus) { RequiredIdentity = appId };
        replacementBus.AddMethodHandler(replacement);
        replacement.Assigned["show"] = "Ctrl+Alt+Q";
        Check(await replacementBus.TryRequestNameAsync(name, RequestNameOptions.ReplaceExisting), "replace the portal");
        var restarted = await changes.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(5));
        Check(restarted.Bindings is [{ Id: "show", TriggerDescription: "Ctrl+Alt+Q" }], $"the rebind is reported: {string.Join("; ", restarted.Bindings)}");
        Check(replacement.Bound is [("show", "Show the window", "CTRL+ALT+w")] && replacement.Sessions.Contains(shortcuts.SessionHandle!), "rebound on the replacement");
        replacement.Signal("Activated", shortcuts.SessionHandle!, "show", null);
        Check((await activations.Reader.ReadAsync().AsTask().WaitScaledAsync(TimeSpan.FromSeconds(3))) is { Id: "show", PlatformContext: null }, "activation after the restart");

        // An empty set closes the session; disposal closes the current one.
        string third = shortcuts.SessionHandle!;
        Check(await shortcuts.BindAsync([]) is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success { Value.IsEmpty: true }
            && replacement.Closed.Contains(third) && shortcuts.SessionHandle is null, "an empty set closes the session");
        Check(await shortcuts.BindAsync(set) is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Success, "bind again");
        string fourth = shortcuts.SessionHandle!;
        await shortcuts.DisposeAsync();
        Check(replacement.Closed.Contains(fourth), "disposal closes the session");
        try { await shortcuts.ListAsync(); throw new InvalidOperationException("ListAsync after disposal."); }
        catch (ObjectDisposedException) { }
        Check(!diagnostics.Contains("global-shortcuts-unavailable"), "an available portal reported unavailable");
        Console.WriteLine("PASS global shortcuts: session, BindShortcuts reply, Activated, ShortcutsChanged, ListShortcuts, ConfigureShortcuts, rebind, owner restart and dispose.");
    }

    [Test, Explicit, Category("DBus"), DisplayName("global shortcuts: a portal without the interface or an unidentified caller is reported")]
    public async Task UnavailableAsync()
    {
        using var bus = new DBusConnection(DBusAddress.Session!);
        await bus.ConnectAsync();
        var service = new Service(bus) { HasInterface = false };
        bus.AddMethodHandler(service);
        var diagnostics = new ConcurrentQueue<string>();
        await using (var shortcuts = new PortalGlobalShortcuts(new PortalApplication(null, diagnostic => diagnostics.Enqueue(diagnostic.Code)), destination: bus.UniqueName!))
        {
            Check(await shortcuts.BindAsync([new("show", "Show")]) is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Unavailable { Reason: PlatformUnavailableReason.BackendUnavailable }
                && diagnostics.Contains("global-shortcuts-unavailable") && !service.Calls.Contains("CreateSession"), "a portal without GlobalShortcuts is unavailable");
            Check(await shortcuts.ConfigureAsync() is PlatformResult<PlatformUnit>.Unavailable, "configure without a session");
        }
        // xdg-desktop-portal refuses CreateSession without an application ID.
        service.HasInterface = true;
        service.RefuseUnidentified = true;
        diagnostics.Clear();
        await using (var shortcuts = new PortalGlobalShortcuts(new PortalApplication(null, diagnostic => diagnostics.Enqueue(diagnostic.Code)), destination: bus.UniqueName!))
        {
            Check(await shortcuts.BindAsync([new("show", "Show")]) is PlatformResult<ImmutableArray<DesktopGlobalShortcutBinding>>.Failed { Code: PlatformFailureCode.PermissionDenied }
                && diagnostics.Contains("global-shortcuts-unavailable") && shortcuts.SessionHandle is null, "an unidentified caller is refused");
        }
        try { await new PortalApplication().CreateGlobalShortcuts().BindAsync([new("a", "A"), new("a", "B")]); throw new InvalidOperationException("A repeated id was accepted."); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS global shortcuts: a portal without the interface or an unidentified caller is reported.");
    }

    // A GlobalShortcuts portal: sessions under the caller's session namespace, BindShortcuts once per session, the
    // desktop's keys in Assigned (GNOME's "Press <accelerator>" text for a preferred trigger otherwise) and the signals.
    private sealed class Service(DBusConnection connection) : IPathMethodHandler
    {
        private const string Root = "/org/freedesktop/portal/desktop";
        public string Path => Root;
        public bool HandlesChildPaths => true;
        internal uint Version = 2;
        internal bool HasInterface = true;
        internal bool RefuseUnidentified;
        internal string? RequiredIdentity;
        internal readonly ConcurrentDictionary<string, string> Assigned = new();
        internal readonly ConcurrentQueue<string> Calls = new();
        internal readonly ConcurrentBag<string> Sessions = [];
        internal readonly ConcurrentBag<string> Closed = [];
        internal readonly ConcurrentBag<string> Configured = [];
        internal (string Id, string Description, string? Trigger)[] Bound = [];
        private readonly ConcurrentDictionary<string, string> _registered = new();
        private readonly ConcurrentDictionary<string, string[]> _sessionIds = new();

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            string member = context.Request.MemberAsString!;
            string sender = context.Request.SenderAsString!;
            Calls.Enqueue(member);
            var reader = context.Request.GetBodyReader();
            switch (member)
            {
                case "Register":
                    _registered[sender] = reader.ReadString();
                    Reply(context);
                    return ValueTask.CompletedTask;
                case "Get":
                    string interfaceName = reader.ReadString();
                    if (!HasInterface || interfaceName != "org.freedesktop.portal.GlobalShortcuts")
                    { context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "No such interface"); return ValueTask.CompletedTask; }
                    using (var property = context.CreateReplyWriter("v")) { property.WriteVariant(VariantValue.UInt32(Version)); context.Reply(property.CreateMessage()); }
                    return ValueTask.CompletedTask;
                case "Close":
                    if (context.Request.InterfaceAsString == "org.freedesktop.portal.Session") Closed.Add(context.Request.PathAsString!);
                    Reply(context);
                    return ValueTask.CompletedTask;
            }
            if ((RefuseUnidentified && !_registered.ContainsKey(sender)) || (RequiredIdentity is { } identity && (!_registered.TryGetValue(sender, out var registered) || registered != identity)))
            { context.ReplyError("org.freedesktop.portal.Error.NotAllowed", "An app id is required"); return ValueTask.CompletedTask; }
            string requests = Root + "/request/" + sender[1..].Replace('.', '_') + "/";
            switch (member)
            {
                case "CreateSession":
                {
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    string session = Root + "/session/" + sender[1..].Replace('.', '_') + "/" + options["session_handle_token"].GetString();
                    Sessions.Add(session);
                    Respond(context, requests + options["handle_token"].GetString(), (ref MessageWriter writer) =>
                    { writer.WriteDictionaryEntryStart(); writer.WriteString("session_handle"); writer.WriteVariantString(session); });
                    break;
                }
                case "BindShortcuts":
                {
                    string session = reader.ReadObjectPath().ToString();
                    if (_sessionIds.ContainsKey(session)) { context.ReplyError("org.freedesktop.DBus.Error.AccessDenied", "Already bound"); return ValueTask.CompletedTask; }
                    var bound = new List<(string, string, string?)>();
                    var items = reader.ReadArrayStart(DBusType.Struct);
                    while (reader.HasNext(items))
                    {
                        reader.AlignStruct();
                        string id = reader.ReadString();
                        var values = reader.ReadDictionaryOfStringToVariantValue();
                        string? trigger = values.TryGetValue("preferred_trigger", out var preferred) ? preferred.GetString() : null;
                        bound.Add((id, values["description"].GetString(), trigger));
                        if (trigger is not null) Assigned.TryAdd(id, "Press " + trigger.Replace("CTRL+", "<Control>").Replace("ALT+", "<Alt>").Replace("SHIFT+", "<Shift>"));
                    }
                    _ = reader.ReadString();
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    Bound = [.. bound];
                    _sessionIds[session] = [.. bound.Select(static item => item.Item1)];
                    Respond(context, requests + options["handle_token"].GetString(), (ref MessageWriter writer) =>
                    { writer.WriteDictionaryEntryStart(); writer.WriteString("shortcuts"); writer.WriteSignature("a(sa{sv})"); WriteShortcuts(ref writer, _sessionIds[session]); });
                    break;
                }
                case "ListShortcuts":
                {
                    string session = reader.ReadObjectPath().ToString();
                    var options = reader.ReadDictionaryOfStringToVariantValue();
                    Respond(context, requests + options["handle_token"].GetString(), (ref MessageWriter writer) =>
                    { writer.WriteDictionaryEntryStart(); writer.WriteString("shortcuts"); writer.WriteSignature("a(sa{sv})"); WriteShortcuts(ref writer, _sessionIds.GetValueOrDefault(session, [])); });
                    break;
                }
                case "ConfigureShortcuts":
                    if (Version < 2) { context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", "No such method"); return ValueTask.CompletedTask; }
                    Configured.Add(reader.ReadObjectPath().ToString());
                    Reply(context);
                    break;
                default:
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownMethod", member);
                    break;
            }
            return ValueTask.CompletedTask;
        }

        private void WriteShortcuts(ref MessageWriter writer, string[] ids)
        {
            var array = writer.WriteArrayStart(DBusType.Struct);
            foreach (var id in ids)
            {
                writer.WriteStructureStart();
                writer.WriteString(id);
                var values = writer.WriteDictionaryStart();
                writer.WriteDictionaryEntryStart(); writer.WriteString("description"); writer.WriteVariantString(id);
                if (Assigned.TryGetValue(id, out var trigger)) { writer.WriteDictionaryEntryStart(); writer.WriteString("trigger_description"); writer.WriteVariantString(trigger); }
                writer.WriteDictionaryEnd(values);
            }
            writer.WriteArrayEnd(array);
        }

        private delegate void Results(ref MessageWriter writer);

        // Replies with the request handle and sends its Response, success with the results.
        private void Respond(MethodContext context, string request, Results results)
        {
            var signal = connection.GetMessageWriter();
            try
            {
                signal.WriteSignalHeader(path: request, @interface: "org.freedesktop.portal.Request", member: "Response", signature: "ua{sv}");
                signal.WriteUInt32(0);
                var dictionary = signal.WriteDictionaryStart();
                results(ref signal);
                signal.WriteDictionaryEnd(dictionary);
                connection.TrySendMessage(signal.CreateMessage());
            }
            finally { signal.Dispose(); }
            using var reply = context.CreateReplyWriter("o");
            reply.WriteObjectPath(new ObjectPath(request));
            context.Reply(reply.CreateMessage());
        }

        private static void Reply(MethodContext context)
        {
            using var reply = context.CreateReplyWriter(null);
            context.Reply(reply.CreateMessage());
        }

        internal void Signal(string member, string session, string id, string? token)
        {
            using var signal = connection.GetMessageWriter();
            signal.WriteSignalHeader(path: Root, @interface: "org.freedesktop.portal.GlobalShortcuts", member: member, signature: "osta{sv}");
            signal.WriteObjectPath(new ObjectPath(session));
            signal.WriteString(id);
            signal.WriteUInt64(1234);
            var options = signal.WriteDictionaryStart();
            if (token is not null) { signal.WriteDictionaryEntryStart(); signal.WriteString("activation_token"); signal.WriteVariantString(token); }
            signal.WriteDictionaryEnd(options);
            connection.TrySendMessage(signal.CreateMessage());
        }

        internal void Changed(string session, params string[] ids)
        {
            var signal = connection.GetMessageWriter();
            try
            {
                signal.WriteSignalHeader(path: Root, @interface: "org.freedesktop.portal.GlobalShortcuts", member: "ShortcutsChanged", signature: "oa(sa{sv})");
                signal.WriteObjectPath(new ObjectPath(session));
                WriteShortcuts(ref signal, ids);
                connection.TrySendMessage(signal.CreateMessage());
            }
            finally { signal.Dispose(); }
        }
    }
}
