using System.Collections.Immutable;
using System.Text;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Runic.Platform.Linux.DBus;

namespace Runic.Platform.Linux.Portal;

internal sealed record PortalResponse(uint Code, string[] Uris)
{
    internal PlatformDiagnostic Diagnostic => PortalErrors.Response(Code);
    // FileChooser's current_filter result, (sa(us)): the filter the user left selected, when the portal says.
    internal DBusValue? CurrentFilter { get; init; }
}

internal static class PortalErrors
{
    // org.freedesktop.portal.Request::Response: 1 means the user cancelled, 2 any other end.
    internal static PlatformDiagnostic Response(uint code) => new("org.freedesktop.portal.Request", code,
        code switch { 1 => "The user cancelled the interaction.", 2 => "The user interaction ended in another way.", _ => null });

    // The D-Bus error name identifies the error. The service's free-form message is omitted
    // because it is not guaranteed to be free of paths.
    internal static PlatformDiagnostic Reply(DBusException error) => new(error.ErrorName, 0);
}

// A portal call's a{sv} options in the order they were set, as the portals' own clients send them; setting a key again
// replaces its value in place.
internal sealed class PortalOptions
{
    private readonly List<KeyValuePair<string, DBusValue>> _entries = [];

    internal DBusValue this[string key]
    {
        set
        {
            int index = _entries.FindIndex(entry => entry.Key == key);
            if (index < 0) _entries.Add(KeyValuePair.Create(key, value));
            else _entries[index] = KeyValuePair.Create(key, value);
        }
    }

    internal DBusValue ToValue() => DBusValue.VariantDictionary(_entries);

    // Calls a portal method that answers with a request handle.
    /// <exception cref="IOException">The reply is not an object path.</exception>
    internal static async Task<DBusObjectPath> RequestAsync(DBusProxy portal, string interfaceName, string member, ImmutableArray<DBusValue> arguments)
    {
        var reply = await portal.CallAsync(interfaceName, member, arguments).ConfigureAwait(false);
        return reply is [{ Signature.Value: "o" } handle] ? handle.GetObjectPath() : throw new IOException("The portal returned an invalid request handle.");
    }
}

// Closing org.freedesktop.portal.Request objects on the shared connection, which outlives every request.
internal static class PortalRequests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // Closes a request unless its portal was replaced, which ended it.
    internal static async Task CloseAsync(PortalConnection session, string handle)
    {
        if (session.OwnerChanged.IsCancellationRequested) return;
        try
        {
            await new Protocol.Request(session.Connection, session.Destination, handle).CloseAsync(cancellationToken: CancellationToken.None).AsTask()
                .WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusException or TimeoutException) { /* The portal also ends a request when its caller's connection closes. */ }
    }

    // Closes an abandoned request at the handle the portal returned, or at the expected one when the call failed or got no reply.
    internal static async Task CloseAbandonedAsync(PortalConnection session, string expected, Task<DBusObjectPath>? call)
    {
        string handle = expected;
        if (call is not null)
        {
            try { handle = (await call.WaitAsync(Timeout).ConfigureAwait(false)).Value; }
            catch (Exception error) when (error is DBusException or TimeoutException) { }
        }
        await CloseAsync(session, handle).ConfigureAwait(false);
    }

    // Sends one request and waits for its Response: subscribes to the caller's request namespace first, as portals may
    // answer before the method reply, then passes a fresh handle_token to the call. Closes the request unless it answered.
    /// <exception cref="Runtime.NativeBackendUnavailableException">The portal went away before it answered.</exception>
    /// <exception cref="IOException">The portal returned a handle outside the caller's request namespace.</exception>
    internal static async Task<(uint Code, Dictionary<string, DBusValue> Results)> ExchangeAsync(PortalConnection session,
        Func<string, Task<DBusObjectPath>> call, CancellationToken cancellationToken)
    {
        var connection = session.Connection;
        string prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName.TrimStart(':').Replace('.', '_') + "/";
        string token = "runic_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string handle = prefix + token;
        var responses = Channel.CreateUnbounded<(string Path, uint Code, Dictionary<string, DBusValue> Results)>();
        await using var subscription = await connection.SubscribeAsync(new DBusSignalFilter
        {
            Sender = session.Destination, PathNamespace = new DBusObjectPath(prefix.TrimEnd('/')),
            Interface = "org.freedesktop.portal.Request", Member = "Response",
        }, cancellationToken).AsTask().WaitAsync(Timeout, cancellationToken).ConfigureAwait(false);
        _ = PortalValues.PumpAsync(subscription, signal =>
        {
            // A malformed response is the portal's "other error".
            if (PortalValues.TryReadResponse(signal, out uint code, out var results)) responses.Writer.TryWrite((signal.Path.Value, code, results));
            else responses.Writer.TryWrite((signal.Path.Value, 2, []));
        }, error => responses.Writer.TryComplete(error));
        using var ownerGone = session.OwnerChanged.Register(() => responses.Writer.TryComplete(new Runtime.NativeBackendUnavailableException()));
        Task<DBusObjectPath>? sent = null;
        bool answered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent = call(token);
            handle = (await sent.WaitAsync(Timeout, CancellationToken.None).ConfigureAwait(false)).Value;
            if (!handle.StartsWith(prefix, StringComparison.Ordinal)) throw new IOException("The portal returned an invalid request handle.");
            while (true)
            {
                var response = await responses.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (response.Path != handle) continue;
                answered = true;
                return (response.Code, response.Results);
            }
        }
        catch (ChannelClosedException error) { throw error.InnerException ?? new Runtime.NativeBackendUnavailableException(); }
        finally
        {
            if (!answered && handle.StartsWith(prefix, StringComparison.Ordinal)) _ = CloseAbandonedAsync(session, handle, sent);
        }
    }
}

// org.freedesktop.portal.Session objects on the shared connection: a session lives until it is closed or its portal ends.
internal static class PortalSessions
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // The path prefix of this connection's sessions, which a portal's session_handle must have.
    internal static string Prefix(DBusConnection connection) =>
        "/org/freedesktop/portal/desktop/session/" + connection.UniqueName.TrimStart(':').Replace('.', '_') + "/";

    // A fresh session_handle_token.
    internal static string Token() => "runic_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // The session handle of a CreateSession response: an s, as the portals send it, or an o; null when missing or foreign.
    internal static string? Handle(PortalConnection session, Dictionary<string, DBusValue> results)
    {
        if (!results.TryGetValue("session_handle", out var value)) return null;
        string? handle = value.Signature.Value switch { "s" => value.GetString(), "o" => value.GetObjectPath().Value, _ => null };
        return handle is not null && handle.StartsWith(Prefix(session.Connection), StringComparison.Ordinal) ? handle : null;
    }

    // Closes a session unless its portal was replaced, which ended it.
    internal static async Task CloseAsync(PortalConnection session, string handle)
    {
        if (session.OwnerChanged.IsCancellationRequested) return;
        try
        {
            await new Protocol.Session(session.Connection, session.Destination, new DBusObjectPath(handle)).CloseAsync(cancellationToken: CancellationToken.None).AsTask()
                .WaitAsync(Timeout).ConfigureAwait(false);
        }
        catch (Exception error) when (error is DBusException or TimeoutException) { /* The portal also ends a session when its caller's connection closes. */ }
    }
}

// Reading values from portal replies and signals. A peer controls them, so a repeated dictionary key keeps its last
// value instead of failing the read, as the portals' own GVariant readers do.
internal static class PortalValues
{
    // An a{sv} as its entries, each value unwrapped from its variant; an empty dictionary for anything else.
    internal static Dictionary<string, DBusValue> Variants(DBusValue value)
    {
        var entries = new Dictionary<string, DBusValue>(StringComparer.Ordinal);
        if (value.Signature.Value != "a{sv}") return entries;
        foreach (var entry in value.GetEntries()) entries[entry.Key.GetString()] = entry.Value.GetVariant();
        return entries;
    }

    // Values of a Request.Response signal: the response code and its results.
    internal static bool TryReadResponse(DBusSignal signal, out uint code, out Dictionary<string, DBusValue> results)
    {
        if (signal.Arguments is [{ Signature.Value: "u" } response, { Signature.Value: "a{sv}" } values])
        {
            code = response.GetUInt32();
            results = Variants(values);
            return true;
        }
        code = 0;
        results = [];
        return false;
    }

    // Forwards a subscription's signals until it ends, then reports how it ended.
    internal static async Task PumpAsync(DBusSignalSubscription subscription, Action<DBusSignal> signal, Action<Exception?> ended)
    {
        Exception? failure = null;
        try { await foreach (var received in ((IAsyncEnumerable<DBusSignal>)subscription).ConfigureAwait(false)) signal(received); }
        catch (Exception error) { failure = error; }
        ended(failure);
    }
}
internal interface IPortalTransport
{
    ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken);
    // FileChooser requests also carry the dialog's filters, folder and multiplicity.
    ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, PortalChooserOptions? chooser, CancellationToken cancellationToken) =>
        RequestAsync(parent, method, argument, cancellationToken);
}

// FileChooser options other than the title and the suggested name. Each is a hint the portal backend may ignore.
internal sealed record PortalChooserOptions(ImmutableArray<FileDialogFilter> Filters, string? CurrentFolder, bool Multiple)
{
    internal static PortalChooserOptions? From(ImmutableArray<FileDialogFilter> filters, string? currentFolder, bool multiple) =>
        filters.IsDefaultOrEmpty && currentFolder is null && !multiple ? null : new(filters.IsDefault ? [] : filters, currentFolder, multiple);

    // FileChooser filters are (name, [(0 = glob, pattern) or (1 = MIME type, type)]). Globs match case-sensitively in GTK
    // backends.
    internal static DBusValue Filter(FileDialogFilter filter) => DBusValue.Struct(DBusValue.From(filter.Name),
        DBusValue.Array("(us)", Patterns(filter).Select(static pattern => DBusValue.Struct(DBusValue.From(pattern.Kind), DBusValue.From(pattern.Value)))));

    private static IEnumerable<(uint Kind, string Value)> Patterns(FileDialogFilter filter) =>
        FileDialogPatterns.Globs(filter).Select(static glob => (0u, glob))
            .Concat(filter.MatchesAllFiles ? [] : filter.MimeTypes.Select(static type => (1u, type.ToLowerInvariant())));

    // The request's filter that a current_filter result names: the one with the same name and patterns, else the only
    // one with that name. A portal backend may return a filter of its own, which is no request filter.
    internal static FileDialogFilter? Chosen(ImmutableArray<FileDialogFilter> filters, DBusValue? current)
    {
        if (filters.IsDefaultOrEmpty || current is not { Signature.Value: "(sa(us))" } value) return null;
        var fields = value.GetFields();
        string name = fields[0].GetString();
        var patterns = fields[1].GetItems().Select(static item => item.GetFields()).Select(static pair => (pair[0].GetUInt32(), pair[1].GetString())).ToArray();
        var named = filters.Where(filter => filter.Name == name).ToArray();
        return named.FirstOrDefault(filter => Patterns(filter).SequenceEqual(patterns)) ?? (named.Length == 1 ? named[0] : null);
    }

    internal void AddTo(PortalOptions options)
    {
        if (!Filters.IsEmpty)
        {
            options["filters"] = DBusValue.Array("(sa(us))", Filters.Select(Filter));
            options["current_filter"] = Filter(Filters[0]);
        }
        // A NUL-terminated byte string: the portal takes the path's bytes, not a URI.
        if (CurrentFolder is not null) options["current_folder"] = DBusValue.ByteArray([.. Encoding.UTF8.GetBytes(CurrentFolder), 0]);
        if (Multiple) options["multiple"] = DBusValue.From(true);
    }
}

// Case-insensitive glob patterns for an extension filter, shared by the portal and GTK 3 choosers.
internal static class FileDialogPatterns
{
    internal static IEnumerable<string> Globs(FileDialogFilter filter)
    {
        if (filter.MatchesAllFiles) return ["*"];
        return filter.Extensions.Select(static extension =>
        {
            var pattern = new StringBuilder("*.");
            foreach (char character in extension)
            {
                char lower = char.ToLowerInvariant(character), upper = char.ToUpperInvariant(character);
                if (lower == upper) pattern.Append(character);
                else pattern.Append('[').Append(lower).Append(upper).Append(']');
            }
            return pattern.ToString();
        });
    }
}

// Wire calls use proxies generated at build time from the pinned portal XML: no
// reflection, dynamic proxy or runtime code generation enters the NativeAOT path.
// Subscription order, returned-handle validation, Close and the version guard stay
// explicit here. Requests share the process's portal connection.
internal sealed class PortalTransport(string? address = null, string destination = "org.freedesktop.portal.Desktop", SafeHandle? file = null, bool ask = false, PortalApplication? application = null) : IPortalTransport
{
    private const string Root = "/org/freedesktop/portal/desktop";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken) =>
        RequestAsync(parent, method, argument, null, cancellationToken);

    public async ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, PortalChooserOptions? chooser, CancellationToken cancellationToken)
    {
        var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
        var connection = session.Connection;
        if (file is null && method == "SelectDirectory")
        {
            // FileChooser.directory was introduced in version 3. Older portals
            // may ignore unknown options and present a file picker instead.
            uint version = await new Protocol.FileChooser(connection, session.Destination, Root).GetVersionAsync(cancellationToken)
                .AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            if (version < 3) throw new Runic.Platform.Runtime.NativeBackendUnavailableException();
        }
        if (file is not null)
        {
            uint version = await new Protocol.OpenURI(connection, session.Destination, Root).GetVersionAsync(cancellationToken)
                .AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            if (version < (ask || method == "OpenDirectory" ? 3u : 2u))
                throw new Runic.Platform.Runtime.NativeBackendUnavailableException();
        }
        string prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName[1..].Replace('.', '_') + "/";
        string token = "runic_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string handle = prefix + token;
        var responses = Channel.CreateBounded<(string Path, PortalResponse Response)>(16);
        // Initial service activation is allowed; replacement is not.
        await using var serviceLifetime = await connection.SubscribeAsync(new DBusSignalFilter
        {
            Sender = "org.freedesktop.DBus", Path = new DBusObjectPath("/org/freedesktop/DBus"),
            Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = destination,
        }, cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
        _ = PortalValues.PumpAsync(serviceLifetime, signal =>
        {
            if (signal.Arguments is [_, _, { Signature.Value: "s" } owner] && owner.GetString().Length != 0)
                responses.Writer.TryComplete(new Runic.Platform.Runtime.NativeBackendUnavailableException());
        }, error => responses.Writer.TryComplete(error));
        await using var subscription = await connection.SubscribeAsync(new DBusSignalFilter
        {
            Sender = session.Destination, PathNamespace = new DBusObjectPath(prefix.TrimEnd('/')),
            Interface = RequestInterface, Member = "Response",
        }, cancellationToken).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
        _ = PortalValues.PumpAsync(subscription, signal => responses.Writer.TryWrite(ReadResponse(signal)), error => responses.Writer.TryComplete(error));
        // A portal that goes away will not answer.
        using var ownerGone = session.OwnerChanged.Register(() => responses.Writer.TryComplete(new Runic.Platform.Runtime.NativeBackendUnavailableException()));
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Subscribe before invoking: portals may signal before the method reply.
            // Let the bounded method reply finish even on cancellation so an older
            // portal's returned handle can also be closed.
            handle = (await Call(connection, session.Destination, parent, method, argument, token, chooser)
                .WaitAsync(CallTimeout, CancellationToken.None).ConfigureAwait(false)).Value;
            if (!handle.StartsWith(prefix, StringComparison.Ordinal))
                throw new IOException("The portal returned an invalid request handle.");
            while (true)
            {
                var response = await responses.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (response.Path != handle) continue;
                completed = true;
                return response.Response;
            }
        }
        finally
        {
            if (!completed && handle.StartsWith(prefix, StringComparison.Ordinal))
            {
                try { await new Protocol.Request(connection, session.Destination, handle).CloseAsync(cancellationToken: CancellationToken.None).AsTask().WaitAsync(CallTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is DBusException or TimeoutException or IOException) { /* The portal also ends a request when its caller's connection closes. */ }
            }
        }
    }

    private Task<DBusObjectPath> Call(DBusConnection connection, string peer, string parent, string method, string argument, string token,
        PortalChooserOptions? chooser = null)
    {
        var options = new PortalOptions { ["handle_token"] = DBusValue.From(token) };
        if (file is not null)
        {
            if (ask) options["ask"] = DBusValue.From(true);
            // The connection sends a duplicate of the descriptor; the caller's handle stays open.
            return PortalOptions.RequestAsync(new Protocol.OpenURI(connection, peer, Root), Protocol.OpenURI.InterfaceName,
                method == "OpenDirectory" ? "OpenDirectory" : "OpenFile", [DBusValue.From(parent), DBusValue.UnixFd(file), options.ToValue()]);
        }
        if (method == "OpenURI")
            return PortalOptions.RequestAsync(new Protocol.OpenURI(connection, peer, Root), Protocol.OpenURI.InterfaceName, "OpenURI",
                [DBusValue.From(parent), DBusValue.From(argument), options.ToValue()]);
        options["modal"] = DBusValue.From(true);
        chooser?.AddTo(options);
        var fileChooser = new Protocol.FileChooser(connection, peer, Root);
        string title;
        if (method == "SelectDirectory")
        {
            options["directory"] = DBusValue.From(true);
            title = chooser?.Multiple == true ? "Open directories" : "Open directory";
        }
        else if (method != "SaveFile") title = chooser?.Multiple == true ? "Open files" : "Open file";
        else
        {
            options["current_name"] = DBusValue.From(argument);
            return PortalOptions.RequestAsync(fileChooser, Protocol.FileChooser.InterfaceName, "SaveFile",
                [DBusValue.From(parent), DBusValue.From("Save file"), options.ToValue()]);
        }
        return PortalOptions.RequestAsync(fileChooser, Protocol.FileChooser.InterfaceName, "OpenFile", [DBusValue.From(parent), DBusValue.From(title), options.ToValue()]);
    }

    // A malformed response fails only its own request. Wrongly typed URIs are no selection at all.
    private static (string, PortalResponse) ReadResponse(DBusSignal signal)
    {
        string path = signal.Path.Value;
        if (!PortalValues.TryReadResponse(signal, out uint code, out var results))
            return (path, new PortalResponse(2, [])); // The portal's "other error" response code.
        string[] uris = results.TryGetValue("uris", out var value) && value.Signature.Value == "as" ? [.. value.GetStringArray()] : [];
        return (path, new PortalResponse(code, uris) { CurrentFilter = results.TryGetValue("current_filter", out var filter) ? filter : null });
    }
}
