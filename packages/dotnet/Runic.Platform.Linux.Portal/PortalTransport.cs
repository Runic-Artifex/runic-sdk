using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Tmds.DBus.Protocol;

namespace Runic.Platform.Linux.Portal;

internal sealed record PortalResponse(uint Code, string[] Uris);
internal interface IPortalTransport
{
    ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken);
}

// The small fixed protocol is encoded explicitly: no reflection, dynamic proxy or
// runtime code generation enters the NativeAOT path.
internal sealed class PortalTransport(string? address = null, string destination = "org.freedesktop.portal.Desktop", SafeHandle? file = null, bool ask = false, PortalApplication? application = null) : IPortalTransport
{
    private const string Root = "/org/freedesktop/portal/desktop";
    private const string RequestInterface = "org.freedesktop.portal.Request";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<PortalResponse> RequestAsync(string parent, string method, string argument, CancellationToken cancellationToken)
    {
        using var session = await PortalConnection.OpenAsync(address, destination, application, cancellationToken).ConfigureAwait(false);
        var connection = session.Connection;
        if (file is not null)
        {
            uint version = await connection.CallMethodAsync(VersionRequest(connection, session.Destination),
                static (message, _) => message.GetBodyReader().ReadVariantValue().GetUInt32()).WaitAsync(CallTimeout, cancellationToken).ConfigureAwait(false);
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
            handle = await connection.CallMethodAsync(CreateRequest(connection, session.Destination, parent, method, argument, token),
                static (message, _) => message.GetBodyReader().ReadObjectPath().ToString()).WaitAsync(CallTimeout, CancellationToken.None).ConfigureAwait(false);
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
                try { await connection.CallMethodAsync(CreateClose(connection, session.Destination, handle)).WaitAsync(CallTimeout, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception error) when (error is DBusExceptionBase or TimeoutException or IOException) { /* Connection disposal also releases request ownership. */ }
            }
        }
    }

    private static MessageBuffer VersionRequest(DBusConnection connection, string peer)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: peer, path: Root, @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString("org.freedesktop.portal.OpenURI"); writer.WriteString("version");
        return writer.CreateMessage();
    }

    private MessageBuffer CreateRequest(DBusConnection connection, string peer, string parent, string method, string argument, string token)
    {
        using var writer = connection.GetMessageWriter();
        bool openUri = method == "OpenURI" || file is not null;
        writer.WriteMethodCallHeader(destination: peer, path: Root,
            @interface: openUri ? "org.freedesktop.portal.OpenURI" : "org.freedesktop.portal.FileChooser", member: method, signature: file is null ? "ssa{sv}" : "sha{sv}");
        writer.WriteString(parent);
        if (file is not null) writer.WriteHandle(new BorrowedHandle(file));
        else writer.WriteString(openUri ? argument : method == "SaveFile" ? "Save file" : "Open file");
        var dictionary = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart(); writer.WriteString("handle_token"); writer.WriteVariant(VariantValue.String(token));
        if (file is not null && ask)
        { writer.WriteDictionaryEntryStart(); writer.WriteString("ask"); writer.WriteVariant(VariantValue.Bool(true)); }
        if (!openUri)
        { writer.WriteDictionaryEntryStart(); writer.WriteString("modal"); writer.WriteVariant(VariantValue.Bool(true)); }
        if (method == "SaveFile")
        { writer.WriteDictionaryEntryStart(); writer.WriteString("current_name"); writer.WriteVariant(VariantValue.String(argument)); }
        writer.WriteDictionaryEnd(dictionary);
        return writer.CreateMessage();
    }

    private static MessageBuffer CreateClose(DBusConnection connection, string peer, string path)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: peer, path: path, @interface: RequestInterface, member: "Close");
        return writer.CreateMessage();
    }

    private static (string, PortalResponse) ReadResponse(Message message)
    {
        var reader = message.GetBodyReader();
        uint code = reader.ReadUInt32();
        string[] uris = [];
        var dictionary = reader.ReadDictionaryStart();
        while (reader.HasNext(dictionary))
        {
            string key = reader.ReadString();
            var value = reader.ReadVariantValue();
            if (key == "uris") uris = value.GetArray<string>();
        }
        return (message.PathAsString!, new PortalResponse(code, uris));
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
