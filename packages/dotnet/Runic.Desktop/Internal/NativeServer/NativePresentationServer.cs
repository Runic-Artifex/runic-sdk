using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static Runic.Desktop.Internal.NativeServer.NativeServerInterop;

namespace Runic.Desktop.Internal.NativeServer;

// The experimental native listener (X1): native/runic-server does HTTP/1.1, WebSocket framing, the loopback Host check
// and file streaming; every request still runs the host core's dispatch and the surfaces' own handlers, so admission,
// content, bootstrap scripts and the Views protocol are the same code as under Kestrel. Requests reach .NET as an
// HttpContext over the features of NativeExchange; accepted WebSockets are NativeWebSocket.
//
// Threading: the library calls back on its own threads (one by default). The callbacks copy what they need, queue the
// work to the thread pool and return; they never run application code and never call back into the library, whose
// stop waits for callbacks in progress.
internal sealed class NativePresentationServer(Action<IServiceCollection>? configureServices) : IPresentationServer
{
    // WebUiSession refuses larger packets itself; the library only needs a bound for reassembly.
    private const ulong MaximumMessageBytes = 64UL << 20;
    private const uint ReceiveWindow = 64;

    private readonly ConcurrentDictionary<ulong, NativeExchange> _exchanges = new();
    private readonly ConcurrentDictionary<ulong, NativeWebSocket> _sockets = new();
    private ServiceProvider? _services;
    private NativeServerHandle? _handle;
    private GCHandle _self;
    private RequestDelegate? _dispatch;
    private int _disposed;

    public IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("The presentation server has not started.");

    internal NativeServerHandle Handle =>
        _handle ?? throw new InvalidOperationException("The presentation server has not started.");

    public unsafe Task<int> StartAsync(IPAddress address, int port, RequestDelegate dispatch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.Any))
        {
            throw new ArgumentException("The native server listens on loopback or on every interface.", nameof(address));
        }
        EnsureLibrary();
        var services = new ServiceCollection();
        services.AddLogging();
        configureServices?.Invoke(services);
        _services = services.BuildServiceProvider();
        _dispatch = dispatch;
        _self = GCHandle.Alloc(this);
        var options = new RunicServerOptions
        {
            AbiVersion = NativeServerInterop.AbiVersion,
            Flags = address.Equals(IPAddress.Any) ? FlagAllInterfaces : 0,
            Port = checked((ushort)port),
            WorkerThreads = 1,
            MaxMessageBytes = MaximumMessageBytes,
            ReceiveWindow = ReceiveWindow,
            Context = (void*)GCHandle.ToIntPtr(_self),
            OnRequest = &OnRequest,
            OnAborted = &OnAborted,
            OnMessage = &OnMessage,
            OnClosed = &OnClosed,
            OnCompleted = &OnCompleted,
        };
        nint server;
        ushort bound;
        var result = NativeServerInterop.Start(&options, &server, &bound);
        if (result != Ok)
        {
            var message = LastErrorMessage();
            _self.Free();
            _self = default;
            throw result == BindFailed
                ? new IOException($"The native presentation server could not listen on {address}:{port}: {message}")
                : new InvalidOperationException($"The native presentation server did not start ({result}): {message}");
        }
        // The handle frees the context once the library stopped, after the last callback.
        _handle = new NativeServerHandle(server, _self);
        return Task.FromResult((int)bound);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        // The library frees message memory when it stops: no socket may read it afterwards.
        foreach (var socket in _sockets.Values)
        {
            socket.AbortFromServer();
        }
        foreach (var exchange in _exchanges.Values)
        {
            exchange.AbortFromServer();
        }
        // Stops the library once the calls in progress ended; no callback starts afterwards.
        _handle?.Dispose();
        if (_services is not null)
        {
            await _services.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal RequestDelegate Dispatch =>
        _dispatch ?? throw new InvalidOperationException("The presentation server has not started.");

    internal void Forget(NativeExchange exchange) => _exchanges.TryRemove(exchange.Id, out _);

    internal void Register(NativeWebSocket socket) => _sockets[socket.Id] = socket;

    internal void Forget(NativeWebSocket socket) => _sockets.TryRemove(socket.Id, out _);

    // The library is a NuGet native asset of Runic.Desktop.NativeServer, or linked into a NativeAOT executable.
    private static void EnsureLibrary()
    {
        uint version;
        try
        {
            version = AbiVersionOfLibrary();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new DesktopException(
                DesktopErrorCategory.Unavailable,
                "native-server-unavailable",
                $"The application enabled the experimental native presentation server (Runic.Desktop.NativeServer), but its library '{Library}' could not be loaded for {RuntimeInformation.RuntimeIdentifier}: {exception.Message}",
                retryable: false);
        }
        if (version != NativeServerInterop.AbiVersion)
        {
            throw new DesktopException(
                DesktopErrorCategory.Unavailable,
                "native-server-unavailable",
                $"The native presentation server library implements ABI {version}, but Runic.Desktop needs ABI {NativeServerInterop.AbiVersion}. Use the Runic.Desktop.NativeServer package of the same version as Runic.Desktop.",
                retryable: false);
        }
    }

    private static unsafe NativePresentationServer From(void* context) =>
        (NativePresentationServer)GCHandle.FromIntPtr((nint)context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnRequest(void* context, RunicRequest* request)
    {
        try
        {
            var server = From(context);
            var exchange = NativeExchange.Create(server, request);
            if (exchange is null)
            {
                // A target the host cannot represent: answered from the thread pool, never from this thread.
                var id = request->Id;
                ThreadPool.UnsafeQueueUserWorkItem(static state => state.server.RespondBadRequest(state.id), (server, id), preferLocal: false);
                return;
            }
            server._exchanges[exchange.Id] = exchange;
            ThreadPool.UnsafeQueueUserWorkItem(exchange, preferLocal: false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Runic Desktop native server: a request could not be queued: {exception}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAborted(void* context, ulong id)
    {
        try
        {
            if (From(context)._exchanges.TryGetValue(id, out var exchange))
            {
                exchange.RequesterDisconnected();
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Runic Desktop native server: an abort could not be reported: {exception}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnMessage(void* context, ulong connection, ulong message, uint kind, byte* data, nuint length)
    {
        try
        {
            var server = From(context);
            if (server._sockets.TryGetValue(connection, out var socket))
            {
                socket.Deliver(message, kind, data, length);
            }
            else
            {
                ThreadPool.UnsafeQueueUserWorkItem(static state => state.server.Consume(state.connection, state.message),
                    (server, connection, message), preferLocal: false);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Runic Desktop native server: a message could not be delivered: {exception}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnClosed(void* context, ulong connection, int code, byte* reason, nuint length)
    {
        try
        {
            if (From(context)._sockets.TryGetValue(connection, out var socket))
            {
                var description = length == 0 ? string.Empty : System.Text.Encoding.UTF8.GetString(reason, checked((int)length));
                socket.PeerClosed(code, description);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Runic Desktop native server: a closed connection could not be reported: {exception}");
        }
    }

    // Tokens are ids: a response or a socket has at most one write in flight, so the id names it. The low bit tells the
    // two apart, since an accepted WebSocket keeps its request's id.
    internal static ulong ResponseToken(ulong id) => id << 1;

    internal static ulong SocketToken(ulong id) => (id << 1) | 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnCompleted(void* context, ulong token, int accepted)
    {
        try
        {
            var server = From(context);
            var id = token >> 1;
            if ((token & 1) == 1)
            {
                if (server._sockets.TryGetValue(id, out var socket)) socket.SendCompleted(accepted != 0);
            }
            else if (server._exchanges.TryGetValue(id, out var exchange))
            {
                exchange.WriteCompleted(accepted != 0);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Runic Desktop native server: a write completion could not be reported: {exception}");
        }
    }

    private void RespondBadRequest(ulong id)
    {
        try
        {
            Respond(id, StatusCodes.Status400BadRequest, [], BodyEmpty, null);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or IOException)
        {
        }
    }

    private void Consume(ulong connection, ulong message)
    {
        try
        {
            NativeServerInterop.Consume(Handle, connection, message);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    // Sends the status and headers of request id. Header names and values are written as UTF-8.
    internal unsafe int Respond(ulong id, int status, IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> headers,
        uint body, string? filePath)
    {
        using var encoded = new EncodedHeaders(headers, filePath);
        fixed (byte* bytes = encoded.Bytes)
        fixed (RunicHeader* list = encoded.Headers)
        {
            encoded.Bind(bytes, list);
            return NativeServerInterop.Respond(Handle, id, checked((ushort)status), list, (nuint)encoded.Count, body,
                encoded.Extra(bytes));
        }
    }

    internal unsafe int AcceptWebSocket(ulong id, string? protocol,
        IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> headers)
    {
        using var encoded = new EncodedHeaders(headers, protocol);
        fixed (byte* bytes = encoded.Bytes)
        fixed (RunicHeader* list = encoded.Headers)
        {
            encoded.Bind(bytes, list);
            return NativeServerInterop.AcceptWebSocket(Handle, id, encoded.Extra(bytes), list, (nuint)encoded.Count);
        }
    }
}

// Header names, values and one extra string in one pooled UTF-8 buffer, so a response costs no native allocation.
internal readonly struct EncodedHeaders : IDisposable
{
    private readonly byte[] _bytes;
    private readonly NativeServerInterop.RunicHeader[] _headers;
    private readonly (int Name, int NameLength, int Value, int ValueLength)[] _spans;
    private readonly int _extra;
    private readonly int _extraLength;

    internal EncodedHeaders(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> headers, string? extra)
    {
        var utf8 = System.Text.Encoding.UTF8;
        var pairs = new List<(string Name, string Value)>();
        var size = extra is null ? 0 : utf8.GetByteCount(extra);
        foreach (var header in headers)
        {
            foreach (var value in header.Value)
            {
                if (value is null) continue;
                pairs.Add((header.Key, value));
                size += utf8.GetByteCount(header.Key) + utf8.GetByteCount(value);
            }
        }
        _bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(size, 1));
        _headers = System.Buffers.ArrayPool<NativeServerInterop.RunicHeader>.Shared.Rent(Math.Max(pairs.Count, 1));
        _spans = new (int, int, int, int)[pairs.Count];
        var offset = 0;
        for (var index = 0; index < pairs.Count; index++)
        {
            var name = utf8.GetBytes(pairs[index].Name, _bytes.AsSpan(offset));
            var valueOffset = offset + name;
            var value = utf8.GetBytes(pairs[index].Value, _bytes.AsSpan(valueOffset));
            _spans[index] = (offset, name, valueOffset, value);
            offset = valueOffset + value;
        }
        _extra = offset;
        _extraLength = extra is null ? 0 : utf8.GetBytes(extra, _bytes.AsSpan(offset));
        Count = pairs.Count;
    }

    internal int Count { get; }

    internal byte[] Bytes => _bytes;

    internal NativeServerInterop.RunicHeader[] Headers => _headers;

    internal unsafe void Bind(byte* bytes, NativeServerInterop.RunicHeader* list)
    {
        for (var index = 0; index < Count; index++)
        {
            var (name, nameLength, value, valueLength) = _spans[index];
            list[index] = new NativeServerInterop.RunicHeader
            {
                Name = new NativeServerInterop.RunicStr { Pointer = bytes + name, Length = (nuint)nameLength },
                Value = new NativeServerInterop.RunicStr { Pointer = bytes + value, Length = (nuint)valueLength },
            };
        }
    }

    internal unsafe NativeServerInterop.RunicStr Extra(byte* bytes) =>
        new() { Pointer = bytes + _extra, Length = (nuint)_extraLength };

    public void Dispose()
    {
        System.Buffers.ArrayPool<byte>.Shared.Return(_bytes);
        System.Buffers.ArrayPool<NativeServerInterop.RunicHeader>.Shared.Return(_headers);
    }
}
