using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

internal sealed record PortalResponse(uint Code, string[] Uris)
{
    internal PlatformDiagnostic Diagnostic => PortalErrors.Response(Code);
}

internal static class PortalErrors
{
    // org.freedesktop.portal.Request::Response: 1 means the user cancelled, 2 any other end.
    internal static PlatformDiagnostic Response(uint code) => new("org.freedesktop.portal.Request", code,
        code switch { 1 => "The user cancelled the interaction.", 2 => "The user interaction ended in another way.", _ => null });

    // The D-Bus error name identifies the error. The service's free-form message is omitted
    // because it is not guaranteed to be free of paths.
    internal static PlatformDiagnostic Reply(DBusErrorReplyException error) => new(error.ErrorName, 0);
}
internal interface IPortalTransport
{
    ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken);
}

// Wire calls use proxies generated at build time from the pinned portal XML: no
// reflection, dynamic proxy or runtime code generation enters the NativeAOT path.
// Subscription order, returned-handle validation, Close, FD borrowing and the
// version guard stay explicit here.
internal sealed class PortalTransport(string? address = null, string destination = "org.freedesktop.portal.Desktop", SafeHandle? file = null, bool ask = false, PortalApplication? application = null) : IPortalTransport
{
    private const string Root = "/org/freedesktop/portal/desktop";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken)
    {
        using var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
        var connection = session.Connection;
        if (file is null && method == "SelectDirectory")
        {
            // FileChooser.directory was introduced in version 3. Older portals
            // may ignore unknown options and present a file picker instead.
            uint version = await new Protocol.FileChooser(connection, session.Destination, Root).GetVersionAsync()
                .WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            if (version < 3) throw new Runic.Platform.Runtime.NativeBackendUnavailableException();
        }
        if (file is not null)
        {
            uint version = await new Protocol.OpenURI(connection, session.Destination, Root).GetVersionAsync()
                .WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
            if (version < (ask || method == "OpenDirectory" ? 3u : 2u))
                throw new Runic.Platform.Runtime.NativeBackendUnavailableException();
        }
        string prefix = "/org/freedesktop/portal/desktop/request/" + connection.UniqueName![1..].Replace('.', '_') + "/";
        string token = "runic_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        string handle = prefix + token;
        var responses = Channel.CreateBounded<(string Path, PortalResponse Response)>(16);
        using var serviceLifetime = await connection.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal, Sender = "org.freedesktop.DBus", Path = "/org/freedesktop/DBus",
            Interface = "org.freedesktop.DBus", Member = "NameOwnerChanged", Arg0 = destination,
        }, static (message, _) =>
        {
            var reader = message.GetBodyReader();
            _ = reader.ReadString();
            return reader.ReadString().Length != 0; // Initial service activation is allowed; replacement is not.
        }, notification =>
        {
            if (notification.HasValue && notification.Value)
                responses.Writer.TryComplete(new Runic.Platform.Runtime.NativeBackendUnavailableException());
            else if (notification.IsCompletion) responses.Writer.TryComplete(notification.Exception);
        }, emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
        using var subscription = await connection.AddMatchAsync(new MatchRule
        {
            Type = MessageType.Signal, Sender = session.Destination, PathNamespace = prefix.TrimEnd('/'),
            Interface = RequestInterface, Member = "Response",
        }, static (message, _) => ReadResponse(message), notification =>
        {
            if (notification.HasValue) responses.Writer.TryWrite(notification.Value);
            else if (notification.IsCompletion) responses.Writer.TryComplete(notification.Exception);
        }, emitOnCapturedContext: false, flags: ObserverFlags.EmitAll).AsTask().WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
        bool completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Subscribe before invoking: portals may signal before the method reply.
            // Let the bounded method reply finish even on cancellation so an older
            // portal's returned handle can also be closed.
            handle = (await Call(connection, session.Destination, parent, method, argument, token)
                .WaitAsync(CallTimeout, CancellationToken.None).ConfigureAwait(false)).ToString();
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
                try { await new Protocol.Request(connection, session.Destination, handle).CloseAsync().WaitAsync(CallTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is DBusExceptionBase or TimeoutException or IOException) { /* Connection disposal also releases request ownership. */ }
            }
        }
    }

    private Task<ObjectPath> Call(DBusConnection connection, string peer, string parent, string method, string argument, string token)
    {
        var options = new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) };
        if (file is not null)
        {
            if (ask) options["ask"] = VariantValue.Bool(true);
            var openUri = new Protocol.OpenURI(connection, peer, Root);
            // The generated writer takes ownership of the wrapper, never of the caller's handle.
            return method == "OpenDirectory" ? openUri.OpenDirectoryAsync(parent, new BorrowedHandle(file), options)
                : openUri.OpenFileAsync(parent, new BorrowedHandle(file), options);
        }
        if (method == "OpenURI") return new Protocol.OpenURI(connection, peer, Root).OpenURIAsync(parent, argument, options);
        options["modal"] = VariantValue.Bool(true);
        var chooser = new Protocol.FileChooser(connection, peer, Root);
        if (method == "SelectDirectory")
        {
            options["directory"] = VariantValue.Bool(true);
            return chooser.OpenFileAsync(parent, "Open directory", options);
        }
        if (method != "SaveFile") return chooser.OpenFileAsync(parent, "Open file", options);
        options["current_name"] = VariantValue.String(argument);
        return chooser.SaveFileAsync(parent, "Save file", options);
    }

    // A malformed response fails only its own request; a reader exception would
    // instead end the subscription. Wrongly typed URIs are no selection at all.
    private static (string, PortalResponse) ReadResponse(Message message)
    {
        string path = message.PathAsString!;
        try
        {
            var reader = message.GetBodyReader();
            uint code = reader.ReadUInt32();
            string[] uris = [];
            var dictionary = reader.ReadDictionaryStart();
            while (reader.HasNext(dictionary))
            {
                string key = reader.ReadString();
                var value = reader.ReadVariantValue();
                if (key != "uris") continue;
                try { uris = value.GetArray<string>(); }
                catch (InvalidOperationException) { uris = []; }
            }
            return (path, new PortalResponse(code, uris));
        }
        catch (Exception error) when (error is InvalidOperationException or DBusExceptionBase or IndexOutOfRangeException)
        { return (path, new PortalResponse(2, [])); } // The portal's "other error" response code.
    }
    // Tmds takes ownership of the wrapper. Retain, but never close, the caller's file handle.
    private sealed class BorrowedHandle : SafeHandle
    {
        private readonly SafeHandle _source;
        internal BorrowedHandle(SafeHandle source) : base(-1, true)
        {
            bool retained = false;
            source.DangerousAddRef(ref retained);
            _source = source;
            SetHandle(source.DangerousGetHandle());
        }
        public override bool IsInvalid => handle == -1;
        protected override bool ReleaseHandle() { _source.DangerousRelease(); return true; }
    }

}
