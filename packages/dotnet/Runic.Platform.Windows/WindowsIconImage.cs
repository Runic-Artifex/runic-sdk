using System.Buffers.Binary;
using System.IO.Compression;

namespace Runic.Platform.Windows;

// Icon pixels to PNG. GDI hands out 32-bit BGRA rows, top-down as requested; the encoder writes 8-bit RGBA without
// filtering, which suits icons of at most 256 pixels. Managed, so it runs and is tested on every OS.
internal static class WindowsIconImage
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    // Icons without an alpha channel carry their transparency in the AND mask: a set mask bit is transparent. The
    // mask arrives as 32-bit BGRA rows too, black (0) for opaque and white for transparent.
    internal static void ApplyMask(Span<byte> bgra, ReadOnlySpan<byte> maskBgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4) bgra[i + 3] = maskBgra[i] == 0 ? (byte)255 : (byte)0;
    }

    internal static bool HasAlpha(ReadOnlySpan<byte> bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4) if (bgra[i] != 0) return true;
        return false;
    }

    // The shell's image factory may return premultiplied pixels. A pixel whose colour exceeds its alpha proves the
    // pixels are straight; otherwise they are taken as premultiplied and divided out.
    internal static void Straighten(Span<byte> bgra)
    {
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte alpha = bgra[i + 3];
            if (bgra[i] > alpha || bgra[i + 1] > alpha || bgra[i + 2] > alpha) return;
        }
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            int alpha = bgra[i + 3];
            if (alpha is 0 or 255) continue;
            for (int c = 0; c < 3; c++) bgra[i + c] = (byte)Math.Min(255, (bgra[i + c] * 255 + alpha / 2) / alpha);
        }
    }

    internal static byte[] EncodePng(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("The pixels do not fill the image.", nameof(bgra));
        var raw = new byte[height * (width * 4 + 1)];
        for (int y = 0; y < height; y++)
        {
            int row = y * (width * 4 + 1); // Filter type 0 (None) starts each row.
            for (int x = 0; x < width; x++)
            {
                int from = (y * width + x) * 4, to = row + 1 + x * 4;
                raw[to] = bgra[from + 2];
                raw[to + 1] = bgra[from + 1];
                raw[to + 2] = bgra[from];
                raw[to + 3] = bgra[from + 3];
            }
        }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;  // Bit depth.
        header[9] = 6;  // Colour type: RGBA.
        header[10] = header[11] = header[12] = 0; // Deflate, adaptive filtering, no interlace.
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        png.Write(number);
        png.Write(type);
        png.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc(Crc(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu);
        png.Write(number);
    }

    internal static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
