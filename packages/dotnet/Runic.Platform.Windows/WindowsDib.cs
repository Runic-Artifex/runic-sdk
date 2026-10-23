using System.Buffers.Binary;

namespace Runic.Platform.Windows;

// Device-independent bitmaps on the clipboard (CF_DIB, CF_DIBV5) and the PNG bytes beside them. WIC decodes a DIB once
// it has a BMP file header, and the writer offers a written PNG as a CF_DIBV5 with alpha for applications that read only
// bitmaps. Portable: nothing here calls native code.
internal static class WindowsDib
{
    private const int FileHeaderSize = 14, InfoHeaderSize = 40, V5HeaderSize = 124;
    private const uint BitFields = 3, AlphaBitFields = 6;

    // The BITMAPFILEHEADER that makes a packed DIB a BMP file, or null for a DIB too short or malformed.
    internal static byte[]? FileHeader(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < InfoHeaderSize) return null;
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize < InfoHeaderSize || headerSize > dib.Length) return null;
        ushort bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        uint compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        uint colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);
        // A BITMAPINFOHEADER keeps its masks after the header; the V4 and V5 headers hold them.
        long masks = headerSize == InfoHeaderSize ? compression switch { BitFields => 12, AlphaBitFields => 16, _ => 0 } : 0;
        long colors = colorsUsed != 0 ? colorsUsed : bitCount is > 0 and <= 8 ? 1L << bitCount : 0;
        long offset = FileHeaderSize + headerSize + masks + colors * 4;
        if (offset - FileHeaderSize > dib.Length) return null;
        var header = new byte[FileHeaderSize];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(2), checked((uint)(FileHeaderSize + dib.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(10), (uint)offset);
        return header;
    }

    // A CF_DIBV5 of top-down 32-bit BGRA pixels with straight alpha: a BITMAPV5HEADER with BI_BITFIELDS masks, in sRGB,
    // and bottom-up rows, as most readers expect.
    internal static byte[] EncodeV5(int width, int height, ReadOnlySpan<byte> bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        int stride = checked(width * 4);
        if (bgra.Length != checked(stride * height)) throw new ArgumentException("The pixels do not match the size.", nameof(bgra));
        var dib = new byte[checked(V5HeaderSize + bgra.Length)];
        var header = dib.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(header, V5HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], BitFields);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], (uint)bgra.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000); // red
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00); // green
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF); // blue
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xFF000000); // alpha
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742); // LCS_sRGB
        BinaryPrimitives.WriteUInt32LittleEndian(header[108..], 4); // LCS_GM_IMAGES
        for (int row = 0; row < height; row++)
            bgra.Slice(row * stride, stride).CopyTo(dib.AsSpan(V5HeaderSize + (height - 1 - row) * stride, stride));
        return dib;
    }

    // The length of the PNG file at the start of bytes, through its IEND chunk; -1 when it is not a whole PNG. Clipboard
    // memory may be larger than what the writer put in it.
    internal static int PngLength(ReadOnlySpan<byte> bytes)
    {
        if (!DesktopClipboardRules.IsPng(bytes)) return -1;
        long position = 8;
        while (position + 12 <= bytes.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes[(int)position..]);
            var type = bytes.Slice((int)position + 4, 4);
            long next = position + 12 + length;
            if (next > bytes.Length) return -1;
            if (type.SequenceEqual("IEND"u8)) return (int)next;
            position = next;
        }
        return -1;
    }
}
