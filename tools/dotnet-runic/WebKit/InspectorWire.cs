using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Runic.Application.Tool.WebKit;

/// <summary>A page, worker or JavaScript context that WebKit's remote inspector server lists.</summary>
internal sealed record InspectorTarget(ulong ConnectionId, ulong TargetId, string Type, string Name, string Url)
{
    /// <summary>
    /// Which connection to an inspector server listed the target. A restarted Window numbers its targets from 1 again, and
    /// a debugger that saw a target ID go away does not attach to it again, so the CDP ID includes it.
    /// </summary>
    internal long Generation { get; init; }

    /// <summary>The target ID the CDP side uses, unique within the dev session.</summary>
    internal string PageId => $"webkit-{Generation}-{ConnectionId}-{TargetId}";

    internal bool IsPage => Type == "WebPage";
}

/// <summary>
/// The messages of WebKitGTK's remote inspector server (<c>WEBKIT_INSPECTOR_SERVER</c>, the socket GNOME Web opens for
/// <c>inspector://</c>), as WebKit's <c>WTF/wtf/glib/SocketConnection.cpp</c> frames them:
/// <c>[u32 big-endian body size][u8 flags, bit 0 = little endian][name, NUL][GVariant parameters]</c>.
/// A client sends <c>SetupInspectorClient (ay)</c>, <c>Setup (tt)</c>, <c>SendMessageToBackend (tts)</c> and
/// <c>FrontendDidClose (tt)</c>; the server sends <c>DidSetupInspectorClient (ay)</c>, <c>SetTargetList (ta(tsssb))</c>
/// and <c>SendMessageToFrontend (tts)</c> (<c>RemoteInspectorServer.cpp</c>, <c>RemoteInspectorClient.cpp</c>).
/// </summary>
internal static class InspectorWire
{
    internal const int HeaderSize = 5;
    internal const int MaximumBodySize = 512 * 1024 * 1024;
    private const byte LittleEndianFlag = 1;

    /// <summary>One frame: a message name and its serialized GVariant parameters.</summary>
    internal static byte[] Frame(string name, ReadOnlySpan<byte> parameters)
    {
        int nameLength = Encoding.ASCII.GetByteCount(name);
        int bodySize = nameLength + 1 + parameters.Length;
        var frame = new byte[HeaderSize + bodySize];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)bodySize);
        frame[4] = LittleEndianFlag;
        Encoding.ASCII.GetBytes(name, frame.AsSpan(HeaderSize));
        parameters.CopyTo(frame.AsSpan(HeaderSize + nameLength + 1));
        return frame;
    }

    /// <summary>
    /// <c>SetupInspectorClient</c> with a backend-commands hash. The empty hash never matches the server's, so the server
    /// answers with its <c>InspectorBackendCommands.js</c>, the protocol of exactly this WebKit; the hash of that script
    /// (<see cref="BackendCommandsHash"/>) gets an empty answer.
    /// </summary>
    internal static byte[] SetupInspectorClient(string backendCommandsHash = "")
    {
        var parameters = new byte[Encoding.ASCII.GetByteCount(backendCommandsHash) + 1];
        Encoding.ASCII.GetBytes(backendCommandsHash, parameters);
        return Frame("SetupInspectorClient", parameters);
    }

    /// <summary>
    /// The hash under which WebKit's server knows a client's copy of the backend commands script: SHA-1, upper-case hex
    /// (<c>RemoteInspectorUtils.cpp</c>, <c>backendCommandsHash</c>). A cache key, not a security measure.
    /// </summary>
    internal static string BackendCommandsHash(ReadOnlySpan<byte> parameters)
    {
        int end = parameters.IndexOf((byte)0);
#pragma warning disable CA5350 // WebKit's cache key for its protocol description, not a security measure.
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(end < 0 ? parameters : parameters[..end]));
#pragma warning restore CA5350
    }

    internal static byte[] Setup(ulong connectionId, ulong targetId) => Frame("Setup", TwoIds(connectionId, targetId));

    internal static byte[] FrontendDidClose(ulong connectionId, ulong targetId) => Frame("FrontendDidClose", TwoIds(connectionId, targetId));

    internal static byte[] SendMessageToBackend(ulong connectionId, ulong targetId, string message)
    {
        byte[] text = Encoding.UTF8.GetBytes(message);
        var parameters = new byte[16 + text.Length + 1];
        TwoIds(connectionId, targetId).CopyTo(parameters, 0);
        text.CopyTo(parameters, 16);
        return Frame("SendMessageToBackend", parameters);
    }

    private static byte[] TwoIds(ulong first, ulong second)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, first);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), second);
        return bytes;
    }

    /// <summary>
    /// The body size of a frame from its header, or an <see cref="InvalidOperationException"/> for a size WebKit never
    /// sends; big-endian parameters are refused, since no supported platform's WebKit sends them.
    /// </summary>
    internal static int ReadBodySize(ReadOnlySpan<byte> header)
    {
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (size < 2 || size > MaximumBodySize) throw new InvalidOperationException($"The inspector server sent a message of {size} bytes.");
        if ((header[4] & LittleEndianFlag) == 0) throw new InvalidOperationException("The inspector server sent big-endian parameters.");
        return (int)size;
    }

    /// <summary>Splits a frame body into its name and parameters.</summary>
    internal static (string Name, byte[] Parameters) ReadBody(ReadOnlySpan<byte> body)
    {
        int end = body.IndexOf((byte)0);
        if (end <= 0) throw new InvalidOperationException("The inspector server sent a message without a name.");
        return (Encoding.ASCII.GetString(body[..end]), body[(end + 1)..].ToArray());
    }

    /// <summary><c>SendMessageToFrontend (tts)</c>: the connection, the target and the protocol message.</summary>
    internal static (ulong ConnectionId, ulong TargetId, string Message) ReadMessageToFrontend(ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length < 17) throw new InvalidOperationException("The inspector server sent a short SendMessageToFrontend.");
        return (BinaryPrimitives.ReadUInt64LittleEndian(parameters), BinaryPrimitives.ReadUInt64LittleEndian(parameters[8..]),
            ReadString(parameters[16..]));
    }

    /// <summary><c>DidSetupInspectorClient (ay)</c>: a byte string, the backend commands script or empty.</summary>
    internal static string ReadBackendCommands(ReadOnlySpan<byte> parameters) => ReadString(parameters);

    /// <summary>
    /// <c>SetTargetList (ta(tsssb))</c>: the connection (one per web process) and its targets. In GVariant's serialization
    /// the array of variable-size tuples ends with a table of their end offsets; each tuple stores the end offsets of its
    /// three strings, last member first, at its own end. Offsets are 1, 2, 4 or 8 bytes by the container's size.
    /// </summary>
    internal static (ulong ConnectionId, IReadOnlyList<InspectorTarget> Targets) ReadTargetList(ReadOnlySpan<byte> parameters)
    {
        if (parameters.Length < 8) throw new InvalidOperationException("The inspector server sent a short SetTargetList.");
        ulong connectionId = BinaryPrimitives.ReadUInt64LittleEndian(parameters);
        ReadOnlySpan<byte> array = parameters[8..];
        var targets = new List<InspectorTarget>();
        if (array.Length == 0) return (connectionId, targets);
        int offsetSize = OffsetSize(array.Length);
        int tableStart = ReadOffset(array[^offsetSize..], offsetSize);
        if (tableStart > array.Length || (array.Length - tableStart) % offsetSize != 0)
            throw new InvalidOperationException("The inspector server sent a malformed target list.");
        int count = (array.Length - tableStart) / offsetSize;
        int start = 0;
        for (int index = 0; index < count; index++)
        {
            int end = ReadOffset(array.Slice(tableStart + index * offsetSize, offsetSize), offsetSize);
            if (end < start || end > tableStart) throw new InvalidOperationException("The inspector server sent a malformed target list.");
            targets.Add(ReadTarget(connectionId, array[start..end]));
            start = (end + 7) & ~7;
        }
        return (connectionId, targets);
    }

    private static InspectorTarget ReadTarget(ulong connectionId, ReadOnlySpan<byte> tuple)
    {
        int offsetSize = OffsetSize(tuple.Length);
        if (tuple.Length < 8 + 3 * offsetSize + 1) throw new InvalidOperationException("The inspector server sent a malformed target.");
        int typeEnd = ReadOffset(tuple[^offsetSize..], offsetSize);
        int nameEnd = ReadOffset(tuple[^(2 * offsetSize)..^offsetSize], offsetSize);
        int urlEnd = ReadOffset(tuple[^(3 * offsetSize)..^(2 * offsetSize)], offsetSize);
        if (typeEnd < 8 || nameEnd < typeEnd || urlEnd < nameEnd || urlEnd > tuple.Length - 3 * offsetSize)
            throw new InvalidOperationException("The inspector server sent a malformed target.");
        return new InspectorTarget(connectionId, BinaryPrimitives.ReadUInt64LittleEndian(tuple),
            ReadString(tuple[8..typeEnd]), ReadString(tuple[typeEnd..nameEnd]), ReadString(tuple[nameEnd..urlEnd]));
    }

    /// <summary>Serializes a target list as WebKit does, for tests of <see cref="ReadTargetList"/>.</summary>
    internal static byte[] WriteTargetList(ulong connectionId, IReadOnlyList<InspectorTarget> targets)
    {
        var array = new List<byte>();
        var ends = new List<int>();
        foreach (InspectorTarget target in targets)
        {
            while (array.Count % 8 != 0) array.Add(0);
            var tuple = new List<byte>();
            var id = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(id, target.TargetId);
            tuple.AddRange(id);
            var stringEnds = new List<int>();
            foreach (string text in (string[])[target.Type, target.Name, target.Url])
            {
                tuple.AddRange(Encoding.UTF8.GetBytes(text));
                tuple.Add(0);
                stringEnds.Add(tuple.Count);
            }
            tuple.Add(0); // hasLocalDebugger: false
            int size = tuple.Count;
            int offsetSize = 1;
            while (OffsetSize(size + 3 * offsetSize) != offsetSize) offsetSize = OffsetSize(size + 3 * offsetSize);
            for (int index = stringEnds.Count - 1; index >= 0; index--) tuple.AddRange(WriteOffset(stringEnds[index], offsetSize));
            array.AddRange(tuple);
            ends.Add(array.Count);
        }
        if (ends.Count > 0)
        {
            int body = array.Count;
            int offsetSize = 1;
            while (OffsetSize(body + ends.Count * offsetSize) != offsetSize) offsetSize = OffsetSize(body + ends.Count * offsetSize);
            foreach (int end in ends) array.AddRange(WriteOffset(end, offsetSize));
        }
        var result = new byte[8 + array.Count];
        BinaryPrimitives.WriteUInt64LittleEndian(result, connectionId);
        array.CopyTo(result, 8);
        return result;
    }

    private static string ReadString(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static int OffsetSize(int containerSize) => containerSize <= 0xff ? 1 : containerSize <= 0xffff ? 2 : 4;

    private static int ReadOffset(ReadOnlySpan<byte> bytes, int size) => size switch
    {
        1 => bytes[0],
        2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
        _ => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes)),
    };

    private static byte[] WriteOffset(int value, int size)
    {
        var bytes = new byte[size];
        switch (size)
        {
            case 1: bytes[0] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value); break;
            default: BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)value); break;
        }
        return bytes;
    }
}
