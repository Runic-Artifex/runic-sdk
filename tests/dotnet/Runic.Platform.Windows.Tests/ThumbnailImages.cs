using System.Buffers.Binary;
using System.IO.Compression;

namespace Runic.Platform.Tests;

// Test images for the thumbnail cases on Windows and macOS: a generated BMP to thumbnail, and a small decoder for the
// non-interlaced 8-bit RGB and RGBA PNG files the providers write, to check the pixels.
internal static class ThumbnailImages
{
    /// <summary>A 24-bit BMP of one colour.</summary>
    public static byte[] Bmp(int width, int height, byte red, byte green, byte blue)
    {
        int stride = (width * 3 + 3) & ~3;
        var bmp = new byte[54 + stride * height];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(34), stride * height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int offset = 54 + y * stride + x * 3;
                bmp[offset] = blue; bmp[offset + 1] = green; bmp[offset + 2] = red;
            }
        return bmp;
    }

    /// <summary>The size from the IHDR chunk and the pixel at (x, y) as RGBA.</summary>
    public static (int Width, int Height, (byte R, byte G, byte B, byte A) Pixel) Decode(ReadOnlySpan<byte> png, double x, double y)
    {
        if (!png.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a])) throw new InvalidDataException("Not a PNG.");
        int width = 0, height = 0, depth = 0, color = 0, interlace = 0;
        var compressed = new MemoryStream();
        for (int offset = 8; offset + 12 <= png.Length;)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            var type = png.Slice(offset + 4, 4);
            var body = png.Slice(offset + 8, length);
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(body);
                height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                depth = body[8]; color = body[9]; interlace = body[12];
            }
            else if (type.SequenceEqual("IDAT"u8)) compressed.Write(body);
            else if (type.SequenceEqual("IEND"u8)) break;
            offset += 12 + length;
        }
        if (depth != 8 || color is not (2 or 6) || interlace != 0) throw new InvalidDataException($"Unexpected PNG format: depth {depth}, colour type {color}, interlace {interlace}.");
        int channels = color == 6 ? 4 : 3, stride = width * channels;
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var raw = new MemoryStream();
        inflater.CopyTo(raw);
        var data = raw.ToArray();
        var pixels = new byte[stride * height];
        for (int row = 0; row < height; row++)
        {
            byte filter = data[row * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                int left = i >= channels ? pixels[row * stride + i - channels] : 0;
                int up = row > 0 ? pixels[(row - 1) * stride + i] : 0;
                int upLeft = row > 0 && i >= channels ? pixels[(row - 1) * stride + i - channels] : 0;
                int value = data[row * (stride + 1) + 1 + i];
                pixels[row * stride + i] = (byte)(filter switch
                {
                    0 => value,
                    1 => value + left,
                    2 => value + up,
                    3 => value + (left + up) / 2,
                    4 => value + Paeth(left, up, upLeft),
                    _ => throw new InvalidDataException($"Unknown PNG filter {filter}."),
                });
            }
        }
        int at = (int)(y * (height - 1)) * stride + (int)(x * (width - 1)) * channels;
        return (width, height, (pixels[at], pixels[at + 1], pixels[at + 2], channels == 4 ? pixels[at + 3] : (byte)255));
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    public static bool Near(byte actual, byte expected) => Math.Abs(actual - expected) <= 24;
}
