using System.Buffers.Binary;

namespace CsWebUi.Managed.Internal;

internal static class WebUiProtocol
{
    internal const byte Signature = 0xDD;
    internal const byte JavaScript = 0xFE;
    internal const byte JavaScriptQuick = 0xFD;
    internal const byte Click = 0xFC;
    internal const byte Navigation = 0xFB;
    internal const byte Close = 0xFA;
    internal const byte CallFunction = 0xF9;
    internal const byte SendRaw = 0xF8;
    internal const byte AddBinding = 0xF7;
    internal const byte Multi = 0xF6;
    internal const byte CheckToken = 0xF5;
    internal const byte WindowDrag = 0xF4;
    internal const byte WindowResized = 0xF3;
    internal const int HeaderSize = 8;
    internal const int DataOffset = HeaderSize;
    internal const int MultiChunkSize = 65_500;

    internal static uint ReadToken(ReadOnlySpan<byte> packet)
        => BinaryPrimitives.ReadUInt32LittleEndian(packet[1..5]);

    internal static ushort ReadId(ReadOnlySpan<byte> packet)
        => BinaryPrimitives.ReadUInt16LittleEndian(packet[5..7]);

    internal static void WriteHeader(Span<byte> packet, ushort id, byte command)
    {
        packet[0] = Signature;
        packet[1..5].Fill(0xFF);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[5..7], id);
        packet[7] = command;
    }

    internal static byte[] CreatePacket(ushort id, byte command, ReadOnlySpan<byte> data)
    {
        var packet = new byte[HeaderSize + data.Length + 1];
        WriteHeader(packet, id, command);
        data.CopyTo(packet.AsSpan(DataOffset));
        return packet;
    }
}
