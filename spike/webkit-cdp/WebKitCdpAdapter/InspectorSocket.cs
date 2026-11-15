// SPIKE (runic-sdk#1029): a client of WebKitGTK's remote inspector server (WEBKIT_INSPECTOR_SERVER, the protocol
// GNOME Web speaks for inspector:// URLs). Framing and messages follow WebKit's WTF/wtf/glib/SocketConnection.cpp,
// JavaScriptCore/inspector/remote/glib/RemoteInspectorServer.cpp and WebKit/UIProcess/Inspector/glib/RemoteInspectorClient.cpp:
//   [u32 big-endian body size][u8 flags, bit 0 = little endian][message name, NUL][GVariant parameters]
// Client -> server: SetupInspectorClient (ay), Setup (tt), SendMessageToBackend (tts), FrontendDidClose (tt).
// Server -> client: DidSetupInspectorClient (ay), SetTargetList (ta(tsssb)), SendMessageToFrontend (tts).
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Runic.Spike.WebKitCdp;

internal sealed record InspectorTarget(ulong ConnectionId, ulong TargetId, string Type, string Name, string Url);

internal sealed class InspectorSocket : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private NetworkStream? _stream;
    private readonly Dictionary<ulong, IReadOnlyList<InspectorTarget>> _targets = new();
    private readonly object _gate = new();

    internal event Action<ulong, ulong, string>? MessageToFrontend;
    internal event Action? TargetsChanged;
    internal event Action? Closed;

    internal string BackendCommands { get; private set; } = "";

    internal IReadOnlyList<InspectorTarget> Targets
    {
        get { lock (_gate) return _targets.Values.SelectMany(static list => list).ToList(); }
    }

    internal async Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        await _client.ConnectAsync(host, port, cancellationToken);
        _stream = _client.GetStream();
        _ = Task.Run(ReadLoopAsync, CancellationToken.None);
        // An empty hash never matches the server's, so the server answers with its InspectorBackendCommands.js: the
        // exact protocol of this WebKit, which the adapter can use to detect commands.
        await SendAsync("SetupInspectorClient", [0]);
    }

    internal Task SetupAsync(ulong connectionId, ulong targetId) => SendAsync("Setup", TwoIds(connectionId, targetId));

    internal Task FrontendDidCloseAsync(ulong connectionId, ulong targetId) => SendAsync("FrontendDidClose", TwoIds(connectionId, targetId));

    internal Task SendMessageToBackendAsync(ulong connectionId, ulong targetId, string message)
    {
        var text = Encoding.UTF8.GetBytes(message);
        var body = new byte[16 + text.Length + 1];
        TwoIds(connectionId, targetId).CopyTo(body, 0);
        text.CopyTo(body, 16);
        return SendAsync("SendMessageToBackend", body);
    }

    private static byte[] TwoIds(ulong a, ulong b)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, a);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), b);
        return bytes;
    }

    private async Task SendAsync(string name, byte[] parameters)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var bodySize = nameBytes.Length + 1 + parameters.Length;
        var frame = new byte[4 + 1 + bodySize];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)bodySize);
        frame[4] = 1; // little endian
        nameBytes.CopyTo(frame, 5);
        parameters.CopyTo(frame, 5 + nameBytes.Length + 1);
        await _writeLock.WaitAsync();
        try { await _stream!.WriteAsync(frame); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[5];
        try
        {
            while (true)
            {
                await _stream!.ReadExactlyAsync(header);
                var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (size is < 2 or > 512 * 1024 * 1024) throw new InvalidDataException($"Invalid message size {size}.");
                if ((header[4] & 1) == 0) throw new InvalidDataException("Big-endian messages are not supported by the spike.");
                var body = new byte[size];
                await _stream.ReadExactlyAsync(body);
                var nul = Array.IndexOf(body, (byte)0);
                var name = Encoding.ASCII.GetString(body, 0, nul);
                Dispatch(name, body.AsSpan(nul + 1).ToArray());
            }
        }
        catch (Exception error) when (error is IOException or EndOfStreamException or ObjectDisposedException or InvalidDataException)
        {
            Log.Write("wk", $"inspector socket closed: {error.Message}");
        }
        Closed?.Invoke();
    }

    private void Dispatch(string name, byte[] p)
    {
        switch (name)
        {
            case "DidSetupInspectorClient":
                BackendCommands = Encoding.UTF8.GetString(p).TrimEnd('\0');
                Log.Write("wk", $"DidSetupInspectorClient: {BackendCommands.Length} bytes of backend commands");
                break;
            case "SetTargetList":
                ReadTargetList(p);
                TargetsChanged?.Invoke();
                break;
            case "SendMessageToFrontend":
                var connectionId = BinaryPrimitives.ReadUInt64LittleEndian(p);
                var targetId = BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(8));
                MessageToFrontend?.Invoke(connectionId, targetId, Encoding.UTF8.GetString(p, 16, p.Length - 16).TrimEnd('\0'));
                break;
            default:
                Log.Write("wk", $"ignored server message {name}");
                break;
        }
    }

    // (ta(tsssb)): a uint64 connection ID, then an array of variable-size (id, type, name, url, hasLocalDebugger).
    private void ReadTargetList(byte[] p)
    {
        var connectionId = BinaryPrimitives.ReadUInt64LittleEndian(p);
        var array = p.AsSpan(8);
        var list = new List<InspectorTarget>();
        if (array.Length > 0)
        {
            var offsetSize = OffsetSize(array.Length);
            var tableStart = (int)ReadOffset(array[^offsetSize..], offsetSize);
            var count = (array.Length - tableStart) / offsetSize;
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                var end = (int)ReadOffset(array.Slice(tableStart + i * offsetSize, offsetSize), offsetSize);
                list.Add(ReadTarget(connectionId, array[start..end]));
                start = (end + 7) & ~7;
            }
        }
        lock (_gate) _targets[connectionId] = list;
        foreach (var target in list) Log.Write("wk", $"target {target.ConnectionId}/{target.TargetId} {target.Type} \"{target.Name}\" {target.Url}");
    }

    // (tsssb): t at 0; the three strings are followed by framing offsets stored at the end in reverse order.
    private static InspectorTarget ReadTarget(ulong connectionId, ReadOnlySpan<byte> t)
    {
        var offsetSize = OffsetSize(t.Length);
        var end1 = (int)ReadOffset(t[^offsetSize..], offsetSize);
        var end2 = (int)ReadOffset(t[^(2 * offsetSize)..^offsetSize], offsetSize);
        var end3 = (int)ReadOffset(t[^(3 * offsetSize)..^(2 * offsetSize)], offsetSize);
        static string Str(ReadOnlySpan<byte> s) => Encoding.UTF8.GetString(s).TrimEnd('\0');
        return new InspectorTarget(connectionId, BinaryPrimitives.ReadUInt64LittleEndian(t), Str(t[8..end1]), Str(t[end1..end2]), Str(t[end2..end3]));
    }

    private static int OffsetSize(int containerSize) => containerSize <= 0xff ? 1 : containerSize <= 0xffff ? 2 : 4;

    private static ulong ReadOffset(ReadOnlySpan<byte> s, int size) => size switch
    {
        1 => s[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(s),
        _ => BinaryPrimitives.ReadUInt32LittleEndian(s),
    };

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static class Log
{
    private static readonly object Gate = new();
    internal static string? File { get; set; }

    internal static void Write(string channel, string text)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {channel} {text}";
        lock (Gate)
        {
            if (channel is "wk" or "adapter") Console.WriteLine(line.Length > 300 ? line[..300] + "…" : line);
            if (File is { } file) System.IO.File.AppendAllText(file, line + "\n");
        }
    }
}
