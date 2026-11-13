using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Runic.Application.Views;

// hosted-regions/1 (specs/application/README.md, "Hosted regions"): a host page declares the regions where the host
// application shows a guest's Window, and reports their size, visibility and focus. This is the producer's wire layer
// and the window's bookkeeping of them. The host application (Runic.Application.Views.Desktop's hosting) observes the
// declarations and decides what each region shows, which it sends with ShowFrame, Update, Empty and Focus.
//
// - A connection declares a name once until it removes it, and at most MaximumPerConnection names at once.
// - A name names one region of the window: when another connection declares a name, the latest declaration wins, and
//   the earlier connection's region is emptied with `detached` (Removed with Superseded).
// - A connection that closes removes its regions (Removed with ConnectionClosed): the producer cannot tell a reload
//   from a crash.
// - Observers get the events of the window one at a time, in order, outside the locks and off the request's thread.
internal sealed class BridgeHostedRegions
{
    internal const string Feature = "hosted-regions/1";
    internal const int MaximumPerConnection = 64;
    internal const int MaximumNameLength = 64;
    internal const int MaximumSize = 32_767;
    internal const int MaximumOffset = 32_767;
    internal const int MinimumOffset = -32_768;
    internal const double MinimumScale = 0.25;
    internal const double MaximumScale = 8;
    internal const int MaximumSourceLength = 2_048;
    internal const int MaximumTitleLength = 1_024;
    internal const int MaximumAllowLength = 1_024;

    private readonly Lock _gate = new();
    private readonly IBridgeTransport _transport;
    private readonly Func<ILogger> _logger;
    // The connection that currently declares each name, latest declaration first.
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.Ordinal);
    // The names each connection declared and did not remove, also those another connection's declaration superseded.
    private readonly Dictionary<string, Dictionary<string, HostedRegionSizing>> _declared = new(StringComparer.Ordinal);
    private readonly List<IHostedRegionObserver> _observers = [];
    private readonly Queue<(IHostedRegionObserver[] Observers, Action<IHostedRegionObserver> Raise)> _events = new();
    private bool _raising;

    internal BridgeHostedRegions(IBridgeTransport transport, Func<ILogger> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    private sealed class Owner(string connectionKey, HostedRegionSizing sizing)
    {
        internal string ConnectionKey { get; } = connectionKey;
        internal HostedRegionSizing Sizing { get; } = sizing;
        internal HostedRegionReport? Report { get; set; }
    }

    // Calls observer with every later event of the window until the lease is disposed, and returns the regions declared
    // now under the same lock, so the observer misses none and sees none twice.
    internal IDisposable Observe(IHostedRegionObserver observer, out IReadOnlyList<HostedRegionDeclaration> current)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            current = [.. _owners.Select(static pair => new HostedRegionDeclaration(pair.Key, pair.Value.Sizing, pair.Value.ConnectionKey, pair.Value.Report))];
            _observers.Add(observer);
        }
        return new Lease(this, observer);
    }

    private sealed class Lease(BridgeHostedRegions owner, IHostedRegionObserver observer) : IDisposable
    {
        private BridgeHostedRegions? _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } regions) return;
            lock (regions._gate) regions._observers.Remove(observer);
        }
    }

    // The regions declared now, latest declaration of each name.
    internal IReadOnlyList<HostedRegionDeclaration> Declared
    {
        get
        {
            lock (_gate)
                return [.. _owners.Select(static pair => new HostedRegionDeclaration(pair.Key, pair.Value.Sizing, pair.Value.ConnectionKey, pair.Value.Report))];
        }
    }

    // hosted-region-content: the region shows a new frame with src. False when no connection declares the name.
    internal bool ShowFrame(string name, string src, string? title, string allow)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(allow);
        if (src.Length is 0 or > MaximumSourceLength)
            throw new ArgumentException($"The frame's address is empty or longer than {MaximumSourceLength} characters.", nameof(src));
        CheckTitle(title);
        if (allow.Length > MaximumAllowLength)
            throw new ArgumentException($"The frame's permissions policy is longer than {MaximumAllowLength} characters.", nameof(allow));
        return Push(name, writer =>
        {
            writer.WriteString("kind", "hosted-region-content");
            writer.WriteString("name", name);
            writer.WriteString("embedding", "frame");
            writer.WriteString("src", src);
            if (title is null) writer.WriteNull("title");
            else writer.WriteString("title", title);
            writer.WriteString("allow", allow);
        });
    }

    // hosted-region-content with the embedding "component" (region-component/1): the region shows the guest's component,
    // which the page imports from module and connects to url with token. False when no connection declares the name.
    internal bool ShowComponent(string name, string module, string url, string token)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(token);
        if (module.Length is 0 or > MaximumSourceLength)
            throw new ArgumentException($"The component's module address is empty or longer than {MaximumSourceLength} characters.", nameof(module));
        if (url.Length is 0 or > MaximumSourceLength)
            throw new ArgumentException($"The component's connection address is empty or longer than {MaximumSourceLength} characters.", nameof(url));
        if (token.Length is 0 or > 256)
            throw new ArgumentException("The component token is empty or longer than 256 characters.", nameof(token));
        return Push(name, writer =>
        {
            writer.WriteString("kind", "hosted-region-content");
            writer.WriteString("name", name);
            writer.WriteString("embedding", "component");
            writer.WriteString("module", module);
            writer.WriteString("url", url);
            writer.WriteString("token", token);
        });
    }

    // hosted-region-update: the members given; Title.HasValue false leaves the title, null clears it. False when no
    // connection declares the name.
    internal bool Update(string name, HostedRegionTitle title = default, int? height = null)
    {
        if (title.HasValue) CheckTitle(title.Value);
        if (height is < 0 or > MaximumSize)
            throw new ArgumentOutOfRangeException(nameof(height), height, $"The region's height is not from 0 to {MaximumSize}.");
        return Push(name, writer =>
        {
            writer.WriteString("kind", "hosted-region-update");
            writer.WriteString("name", name);
            if (title.HasValue)
            {
                if (title.Value is null) writer.WriteNull("title");
                else writer.WriteString("title", title.Value);
            }
            if (height is { } value) writer.WriteNumber("height", value);
        });
    }

    // hosted-region-empty: the region removes its frame and shows its fallback. False when no connection declares the name.
    internal bool Empty(string name, HostedRegionEmptyReason reason) => Push(name, writer =>
    {
        writer.WriteString("kind", "hosted-region-empty");
        writer.WriteString("name", name);
        writer.WriteString("reason", ReasonToken(reason));
    });

    // hosted-region-focus: the page moves the keyboard focus into the region. False when no connection declares the name.
    internal bool Focus(string name) => Push(name, writer =>
    {
        writer.WriteString("kind", "hosted-region-focus");
        writer.WriteString("name", name);
    });

    private static void CheckTitle(string? title)
    {
        if (title is not null && (title.Length > MaximumTitleLength || title.Contains('\0', StringComparison.Ordinal)))
            throw new ArgumentException($"The title is longer than {MaximumTitleLength} characters or contains NUL.", nameof(title));
    }

    private static string ReasonToken(HostedRegionEmptyReason reason) => reason switch
    {
        HostedRegionEmptyReason.Waiting => "waiting",
        HostedRegionEmptyReason.Detached => "detached",
        HostedRegionEmptyReason.Ended => "ended",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "The reason is not a hosted region empty reason."),
    };

    private bool Push(string name, Action<Utf8JsonWriter> write)
    {
        ArgumentNullException.ThrowIfNull(name);
        string target;
        lock (_gate)
        {
            if (!_owners.TryGetValue(name, out var owner)) return false;
            target = owner.ConnectionKey;
        }
        BridgeTargetedPushes.For(_transport).Send(target, BridgeMessages.Write(writer =>
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }));
        return true;
    }

    // Whether a request kind belongs to the feature.
    internal static bool IsKind(string kind) =>
        kind is "hosted-region-declare" or "hosted-region-remove" or "hosted-region-state";

    // Answers a request of the feature's kinds.
    internal byte[] Handle(BridgeConnection connection, string kind, JsonElement root)
    {
        if (!connection.UsesHostedRegions)
            return BridgeMessages.Rejected("invalid-request", $"The connection does not use {Feature}.");
        if (!TryReadName(root, out var name))
            return BridgeMessages.Rejected("invalid-request",
                $"The region's name is not 1 to {MaximumNameLength} ASCII letters, digits, dots, underscores or hyphens.");
        return kind switch
        {
            "hosted-region-declare" => Declare(connection.Key, name, root),
            "hosted-region-remove" => Remove(connection.Key, name),
            _ => State(connection.Key, name, root),
        };
    }

    private byte[] Declare(string connectionKey, string name, JsonElement root)
    {
        var sizing = HostedRegionSizing.Fill;
        if (root.TryGetProperty("sizing", out var sizingElement))
        {
            if (sizingElement.ValueKind != JsonValueKind.String) return BridgeMessages.Rejected("invalid-request", "The region's sizing is not a string.");
            if (sizingElement.ValueEquals("content-height")) sizing = HostedRegionSizing.ContentHeight;
            else if (!sizingElement.ValueEquals("fill"))
                return BridgeMessages.Rejected("invalid-request", "The region's sizing is not fill or content-height.");
        }
        string? superseded = null;
        lock (_gate)
        {
            if (!_declared.TryGetValue(connectionKey, out var names)) _declared[connectionKey] = names = new(StringComparer.Ordinal);
            if (names.ContainsKey(name))
                return BridgeMessages.Rejected("invalid-request", $"The connection declared the region '{name}' already.");
            if (names.Count >= MaximumPerConnection)
                return BridgeMessages.Rejected("capacity", $"The connection declares {MaximumPerConnection} regions already.");
            names[name] = sizing;
            if (_owners.TryGetValue(name, out var earlier))
            {
                superseded = earlier.ConnectionKey;
                Raise(observer => observer.Removed(new HostedRegionRemoval(name, earlier.ConnectionKey, ConnectionClosed: false, Superseded: true)));
            }
            _owners[name] = new Owner(connectionKey, sizing);
            var declaration = new HostedRegionDeclaration(name, sizing, connectionKey, null);
            Raise(observer => observer.Declared(declaration));
        }
        // The earlier declaration's page shows its fallback; its later requests for the name are answered ok and ignored.
        if (superseded is not null)
            BridgeTargetedPushes.For(_transport).Send(superseded, BridgeMessages.Write(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("kind", "hosted-region-empty");
                writer.WriteString("name", name);
                writer.WriteString("reason", "detached");
                writer.WriteEndObject();
            }));
        return BridgeMessages.Ok.ToArray();
    }

    private byte[] Remove(string connectionKey, string name)
    {
        lock (_gate)
        {
            if (!_declared.TryGetValue(connectionKey, out var names) || !names.Remove(name))
                return BridgeMessages.Rejected("invalid-request", $"The connection did not declare the region '{name}'.");
            if (names.Count == 0) _declared.Remove(connectionKey);
            if (_owners.TryGetValue(name, out var owner) && owner.ConnectionKey == connectionKey)
            {
                _owners.Remove(name);
                Raise(observer => observer.Removed(new HostedRegionRemoval(name, connectionKey, ConnectionClosed: false, Superseded: false)));
            }
        }
        return BridgeMessages.Ok.ToArray();
    }

    private byte[] State(string connectionKey, string name, JsonElement root)
    {
        if (!TryReadNumber(root, "width", 0, MaximumSize, out var width) || !TryReadNumber(root, "height", 0, MaximumSize, out var height))
            return BridgeMessages.Rejected("invalid-request", $"The region's width or height is not a number from 0 to {MaximumSize}.");
        if (!TryReadNumber(root, "scale", MinimumScale, MaximumScale, out var scale))
            return BridgeMessages.Rejected("invalid-request", "The region's scale is not a number from 0.25 to 8.");
        if (!TryReadNumber(root, "x", MinimumOffset, MaximumOffset, out var x) || !TryReadNumber(root, "y", MinimumOffset, MaximumOffset, out var y))
            return BridgeMessages.Rejected("invalid-request", $"The region's x or y is not a number from {MinimumOffset} to {MaximumOffset}.");
        if (!TryReadBoolean(root, "visible", out var visible) || !TryReadBoolean(root, "focused", out var focused))
            return BridgeMessages.Rejected("invalid-request", "The region's visible or focused is not a boolean.");
        if (!root.TryGetProperty("focusSource", out var sourceElement) || !TryReadFocusSource(sourceElement, out var focusSource))
            return BridgeMessages.Rejected("invalid-request", "The region's focusSource is not tab, shift-tab, pointer, script or null.");
        var report = new HostedRegionReport(name, connectionKey, width, height, scale, x, y, visible, focused, focusSource);
        lock (_gate)
        {
            if (!_declared.TryGetValue(connectionKey, out var names) || !names.ContainsKey(name))
                return BridgeMessages.Rejected("invalid-request", $"The connection did not declare the region '{name}'.");
            // A superseded declaration's reports change nothing.
            if (_owners.TryGetValue(name, out var owner) && owner.ConnectionKey == connectionKey)
            {
                owner.Report = report;
                Raise(observer => observer.StateChanged(report));
            }
        }
        return BridgeMessages.Ok.ToArray();
    }

    // The connection closed: its regions are removed.
    internal void ConnectionClosed(string connectionKey)
    {
        lock (_gate)
        {
            if (!_declared.Remove(connectionKey, out var names)) return;
            foreach (var name in names.Keys)
            {
                if (!_owners.TryGetValue(name, out var owner) || owner.ConnectionKey != connectionKey) continue;
                _owners.Remove(name);
                Raise(observer => observer.Removed(new HostedRegionRemoval(name, connectionKey, ConnectionClosed: true, Superseded: false)));
            }
        }
    }

    // Queues an event for the observers registered now; the gate is held. One drain at a time raises the events in order.
    private void Raise(Action<IHostedRegionObserver> raise)
    {
        if (_observers.Count == 0) return;
        _events.Enqueue(([.. _observers], raise));
        if (_raising) return;
        _raising = true;
        _ = Task.Run(RaiseQueued);
    }

    private void RaiseQueued()
    {
        while (true)
        {
            (IHostedRegionObserver[] Observers, Action<IHostedRegionObserver> Raise) next;
            lock (_gate)
            {
                if (!_events.TryDequeue(out next))
                {
                    _raising = false;
                    return;
                }
            }
            foreach (var observer in next.Observers)
            {
                try { next.Raise(observer); }
                catch (Exception error)
                {
                    ViewsLog.RequestFailed(_logger(), error, error.GetType().FullName ?? error.GetType().Name);
                }
            }
        }
    }

    // Completes when every event queued so far was raised. Tests wait on it.
    internal async Task WhenRaised()
    {
        while (true)
        {
            lock (_gate)
                if (!_raising) return;
            await Task.Yield();
        }
    }

    // A name: 1 to 64 ASCII letters, digits, '.', '_' or '-'.
    internal static bool IsName(string value)
    {
        if (value.Length is 0 or > MaximumNameLength) return false;
        foreach (var character in value)
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) return false;
        return true;
    }

    private static bool TryReadName(JsonElement root, out string name)
    {
        name = "";
        if (!root.TryGetProperty("name", out var element) || element.ValueKind != JsonValueKind.String) return false;
        name = element.GetString()!;
        return IsName(name);
    }

    private static bool TryReadNumber(JsonElement root, string member, double minimum, double maximum, out double value)
    {
        value = 0;
        return root.TryGetProperty(member, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value) && value >= minimum && value <= maximum;
    }

    private static bool TryReadBoolean(JsonElement root, string member, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(member, out var element) || element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadFocusSource(JsonElement element, out HostedRegionFocusSource? source)
    {
        source = null;
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.String) return false;
        if (element.ValueEquals("tab")) source = HostedRegionFocusSource.Tab;
        else if (element.ValueEquals("shift-tab")) source = HostedRegionFocusSource.ShiftTab;
        else if (element.ValueEquals("pointer")) source = HostedRegionFocusSource.Pointer;
        else if (element.ValueEquals("script")) source = HostedRegionFocusSource.Script;
        else return false;
        return true;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{nameof(BridgeHostedRegions)}({_owners.Count})");
}

// How a region's height is set: by the host page's layout, or by the guest's preferred height.
internal enum HostedRegionSizing
{
    Fill,
    ContentHeight,
}

// How the keyboard focus entered a region: the user's Tab, Shift+Tab or click, or anything else (script).
internal enum HostedRegionFocusSource
{
    Tab,
    ShiftTab,
    Pointer,
    Script,
}

internal enum HostedRegionEmptyReason
{
    // No Window shows in the region yet.
    Waiting,
    // Its Window is detached and may come back.
    Detached,
    // Its Window closed or its guest went away.
    Ended,
}

// A title member of hosted-region-update: absent (default), null or a string.
internal readonly record struct HostedRegionTitle(string? Value)
{
    internal bool HasValue { get; } = true;

    internal static HostedRegionTitle Set(string? value) => new(value);
}

// A region that a connection declared, with the last report of the declaration, if any.
internal sealed record HostedRegionDeclaration(string Name, HostedRegionSizing Sizing, string ConnectionKey, HostedRegionReport? Report);

// A region's declaration ended: the page removed it (neither flag), its connection closed (ConnectionClosed), or another
// connection's declaration of the name superseded it (Superseded).
internal sealed record HostedRegionRemoval(string Name, string ConnectionKey, bool ConnectionClosed, bool Superseded);

// A region's report (hosted-region-state). X and Y, the region's place in the host page's viewport, stay with the host:
// they place its context menus and never go to a guest. FocusSource is null while the region does not hold the focus.
internal sealed record HostedRegionReport(string Name, string ConnectionKey, double Width, double Height, double Scale, double X, double Y,
    bool Visible, bool Focused, HostedRegionFocusSource? FocusSource);

// The host application's view of a window's hosted regions. Calls come one at a time, in order, on a thread-pool thread.
internal interface IHostedRegionObserver
{
    void Declared(HostedRegionDeclaration declaration);

    void Removed(HostedRegionRemoval removal);

    void StateChanged(HostedRegionReport report);
}
