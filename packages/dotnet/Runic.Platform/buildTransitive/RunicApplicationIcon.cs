// The application icon (runic-sdk#1207, D078.1): the Windows .ico and the macOS .icns that Runic derives from the
// application's package icon, RunicPackageIcon (by default the 512 px icons/icon.png).
//
// Runic.Platform.targets compiles this file with RoslynCodeTaskFactory as the RunicWriteApplicationIcon task, and
// Runic.Platform.MacOS.Tests compiles it with RUNIC_APPLICATION_ICON_CODEC to test the codec without MSBuild. It is
// managed code only, without System.IO.Compression (whose deflate is native zlib) or an operating system tool, so it
// writes the same bytes on every operating system, MSBuild and .NET version: the PNG decoder and its inflater, the box
// filter, the deflater and PNG encoder, and the two containers. The inline task compiles against netstandard 2.0, so
// it uses arrays rather than spans.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
#if !RUNIC_APPLICATION_ICON_CODEC
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>Writes an .ico or .icns from a PNG. A PNG that cannot serve as the icon is reported through
/// <see cref="Problem"/>, which the targets turn into a RUNICICON warning; the destination is then removed.</summary>
public sealed class RunicWriteApplicationIcon : Task
{
    [Required] public string Source { get; set; } = "";
    [Required] public string Destination { get; set; } = "";
    /// <summary><c>ico</c> or <c>icns</c>.</summary>
    [Required] public string Format { get; set; } = "";
    /// <summary>Empty when the icon was written; else <c>unreadable</c>, <c>not-square</c> or <c>too-small</c>.</summary>
    [Output] public string Problem { get; set; } = "";
    /// <summary>What was wrong, for the warning's text.</summary>
    [Output] public string Detail { get; set; } = "";

    public override bool Execute()
    {
        if (Format != "ico" && Format != "icns")
        {
            Log.LogError("RunicWriteApplicationIcon: Format is '" + Format + "'; it must be ico or icns.");
            return false;
        }
        RunicIconImage image;
        try
        {
            image = RunicPng.Decode(File.ReadAllBytes(Source));
        }
        catch (Exception exception) when (exception is InvalidDataException || exception is IOException || exception is UnauthorizedAccessException)
        {
            return Refuse("unreadable", exception.Message);
        }
        if (image.Width != image.Height) return Refuse("not-square", image.Width + " x " + image.Height + " px");
        if (image.Width < RunicApplicationIcons.MinimumSize) return Refuse("too-small", image.Width + " x " + image.Height + " px");

        byte[] bytes = Format == "ico" ? RunicApplicationIcons.Ico(image) : RunicApplicationIcons.Icns(image);
        // An unchanged icon keeps its file and time stamp, so nothing that depends on it runs again.
        if (!RunicApplicationIcons.SameContent(Destination, bytes))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Destination))!);
            File.WriteAllBytes(Destination, bytes);
        }
        return true;
    }

    private bool Refuse(string problem, string detail)
    {
        Problem = problem;
        Detail = detail;
        if (File.Exists(Destination)) File.Delete(Destination);
        return true;
    }
}
#endif

/// <summary>An image of 8-bit RGBA pixels with straight (not premultiplied) alpha, row by row from the top.</summary>
internal sealed class RunicIconImage
{
    public RunicIconImage(int width, int height, byte[] rgba)
    {
        if (rgba.Length != checked(width * height * 4)) throw new ArgumentException("The pixels do not match the size.", nameof(rgba));
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
}

/// <summary>The icon sets: the .ico, the .icns and the box filter that makes their sizes.</summary>
internal static class RunicApplicationIcons
{
    /// <summary>The smallest package icon: the largest .ico entry.</summary>
    internal const int MinimumSize = 256;

    /// <summary>The sizes of the .ico, each a 32-bit DIB except 256 px, which is a PNG: the sizes Windows asks for at
    /// 100 to 250 % scale (16, 20, 24, 32, 40, 48 and 64 px) and the large icon views (256 px).</summary>
    internal static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 256 };

    /// <summary>The PNG entries of the .icns in the order and sizes that iconutil writes from a complete .iconset; one
    /// larger than the source is left out, as the box filter only scales down.</summary>
    internal static readonly (string Type, int Size)[] IcnsEntries =
    {
        ("icp4", 16), ("ic11", 32), ("icp5", 32), ("ic12", 64), ("ic07", 128), ("ic13", 256), ("ic08", 256),
        ("ic14", 512), ("ic09", 512), ("ic10", 1024),
    };

    internal static byte[] Ico(RunicIconImage image)
    {
        var entries = new List<(int Size, byte[] Data)>();
        foreach (int size in IcoSizes)
        {
            RunicIconImage scaled = RunicIconScaler.Resize(image, size);
            entries.Add((size, size >= 256 ? RunicPngEncoder.Encode(scaled) : Dib(scaled)));
        }
        var output = new MemoryStream();
        // ICONDIR: reserved, type 1 (icon), count; then a 16-byte ICONDIRENTRY per image.
        WriteUInt16(output, 0);
        WriteUInt16(output, 1);
        WriteUInt16(output, entries.Count);
        int offset = 6 + 16 * entries.Count;
        foreach (var (size, data) in entries)
        {
            output.WriteByte((byte)(size >= 256 ? 0 : size));
            output.WriteByte((byte)(size >= 256 ? 0 : size));
            output.WriteByte(0); // no palette
            output.WriteByte(0);
            WriteUInt16(output, 1); // planes
            WriteUInt16(output, 32); // bits per pixel
            WriteUInt32(output, (uint)data.Length);
            WriteUInt32(output, (uint)offset);
            offset += data.Length;
        }
        foreach (var (_, data) in entries) output.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary>A 32-bit icon DIB: a BITMAPINFOHEADER of twice the height, the BGRA rows from the bottom, then the
    /// 1-bit AND mask, set where the pixel is fully transparent, for renderers that ignore the alpha.</summary>
    internal static byte[] Dib(RunicIconImage image)
    {
        int width = image.Width, height = image.Height;
        int maskStride = (width + 31) / 32 * 4;
        int pixels = width * height * 4, mask = maskStride * height;
        var output = new MemoryStream(40 + pixels + mask);
        WriteUInt32(output, 40);
        WriteUInt32(output, (uint)width);
        WriteUInt32(output, (uint)(height * 2));
        WriteUInt16(output, 1);
        WriteUInt16(output, 32);
        WriteUInt32(output, 0); // BI_RGB
        WriteUInt32(output, (uint)(pixels + mask));
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        byte[] rgba = image.Rgba;
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                output.WriteByte(rgba[at + 2]);
                output.WriteByte(rgba[at + 1]);
                output.WriteByte(rgba[at]);
                output.WriteByte(rgba[at + 3]);
            }
        }
        var row = new byte[maskStride];
        for (int y = height - 1; y >= 0; y--)
        {
            Array.Clear(row, 0, row.Length);
            for (int x = 0; x < width; x++)
            {
                if (rgba[(y * width + x) * 4 + 3] == 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
            }
            output.Write(row, 0, row.Length);
        }
        return output.ToArray();
    }

    internal static byte[] Icns(RunicIconImage image)
    {
        // A size that two entries share (32, 256 and 512 px) is encoded once.
        var encoded = new Dictionary<int, byte[]>();
        var entries = new List<(string Type, byte[] Data)>();
        foreach (var (type, size) in IcnsEntries)
        {
            if (size > image.Width) continue;
            if (!encoded.TryGetValue(size, out byte[]? data))
            {
                data = RunicPngEncoder.Encode(RunicIconScaler.Resize(image, size));
                encoded.Add(size, data);
            }
            entries.Add((type, data));
        }
        var output = new MemoryStream();
        int length = 8;
        foreach (var (_, data) in entries) length += 8 + data.Length;
        WriteType(output, "icns");
        WriteUInt32BigEndian(output, (uint)length);
        foreach (var (type, data) in entries)
        {
            WriteType(output, type);
            WriteUInt32BigEndian(output, (uint)(8 + data.Length));
            output.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    internal static bool SameContent(string path, byte[] bytes)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) return false;
        byte[] existing = File.ReadAllBytes(path);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (existing[i] != bytes[i]) return false;
        }
        return true;
    }

    private static void WriteType(Stream output, string type)
    {
        foreach (char character in type) output.WriteByte((byte)character);
    }

    private static void WriteUInt16(Stream output, int value)
    {
        output.WriteByte((byte)value);
        output.WriteByte((byte)(value >> 8));
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        for (int shift = 0; shift < 32; shift += 8) output.WriteByte((byte)(value >> shift));
    }

    internal static void WriteUInt32BigEndian(Stream output, uint value)
    {
        for (int shift = 24; shift >= 0; shift -= 8) output.WriteByte((byte)(value >> shift));
    }
}

/// <summary>An area (box) filter in premultiplied alpha, in integers: each output pixel is the mean of the source area
/// it covers, every source pixel weighted by the part of it inside, so the result is exact and the same everywhere.
/// It only scales down.</summary>
internal static class RunicIconScaler
{
    internal static RunicIconImage Resize(RunicIconImage source, int size)
    {
        if (source.Width == size && source.Height == size) return source;
        if (size > source.Width || size > source.Height) throw new ArgumentOutOfRangeException(nameof(size), "The box filter only scales down.");
        int sourceWidth = source.Width, sourceHeight = source.Height;
        // Per axis, output pixel o covers [o * n, (o + 1) * n) and source pixel i covers [i * size, (i + 1) * size),
        // in units of 1/(n * size) of the image: the overlaps are integer weights that add up to n.
        byte[] rgba = source.Rgba;
        var output = new byte[size * size * 4];
        // The weighted sums of a band of source rows for one output row: alpha, and each colour times alpha.
        var band = new long[sourceWidth * 4];
        long total = (long)sourceWidth * sourceHeight;
        for (int oy = 0; oy < size; oy++)
        {
            Array.Clear(band, 0, band.Length);
            long top = (long)oy * sourceHeight, bottom = (long)(oy + 1) * sourceHeight;
            for (int sy = (int)(top / size); sy <= (int)((bottom - 1) / size); sy++)
            {
                long weight = Math.Min((long)(sy + 1) * size, bottom) - Math.Max((long)sy * size, top);
                int row = sy * sourceWidth * 4;
                for (int sx = 0; sx < sourceWidth; sx++)
                {
                    int at = row + sx * 4;
                    long alpha = rgba[at + 3];
                    if (alpha == 0) continue;
                    long weighted = weight * alpha;
                    band[sx * 4] += weighted * rgba[at];
                    band[sx * 4 + 1] += weighted * rgba[at + 1];
                    band[sx * 4 + 2] += weighted * rgba[at + 2];
                    band[sx * 4 + 3] += weighted;
                }
            }
            for (int ox = 0; ox < size; ox++)
            {
                long left = (long)ox * sourceWidth, right = (long)(ox + 1) * sourceWidth;
                long red = 0, green = 0, blue = 0, alpha = 0;
                for (int sx = (int)(left / size); sx <= (int)((right - 1) / size); sx++)
                {
                    long weight = Math.Min((long)(sx + 1) * size, right) - Math.Max((long)sx * size, left);
                    red += weight * band[sx * 4];
                    green += weight * band[sx * 4 + 1];
                    blue += weight * band[sx * 4 + 2];
                    alpha += weight * band[sx * 4 + 3];
                }
                int at = (oy * size + ox) * 4;
                // The colour is the alpha-weighted mean; the alpha the mean over the whole area. Both rounded half up.
                if (alpha > 0)
                {
                    output[at] = (byte)Divide(red, alpha);
                    output[at + 1] = (byte)Divide(green, alpha);
                    output[at + 2] = (byte)Divide(blue, alpha);
                }
                output[at + 3] = (byte)Divide(alpha, total);
            }
        }
        return new RunicIconImage(size, size, output);
    }

    private static long Divide(long value, long divisor) => (2 * value + divisor) / (2 * divisor);
}

/// <summary>A PNG decoder for every standard image: each colour type and bit depth, palette and tRNS transparency, and
/// Adam7 interlacing. Ancillary chunks other than tRNS, such as gamma and colour profiles, are ignored.</summary>
internal static class RunicPng
{
    /// <summary>The largest image decoded: 8192 x 8192 px, 256 MB of pixels.</summary>
    internal const int MaximumSize = 8192;

    private static readonly byte[] Signature = { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a };

    // Adam7: the first column and row of each pass and its steps.
    private static readonly int[] PassX = { 0, 4, 0, 2, 0, 1, 0 };
    private static readonly int[] PassY = { 0, 0, 4, 0, 2, 0, 1 };
    private static readonly int[] PassDx = { 8, 8, 4, 4, 2, 2, 1 };
    private static readonly int[] PassDy = { 8, 8, 8, 4, 4, 2, 2 };

    internal static RunicIconImage Decode(byte[] png)
    {
        if (png.Length < Signature.Length + 12) throw new InvalidDataException("The file is too short for a PNG.");
        for (int i = 0; i < Signature.Length; i++)
        {
            if (png[i] != Signature[i]) throw new InvalidDataException("The file is not a PNG: its signature differs.");
        }
        int width = 0, height = 0, depth = 0, color = -1, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        var compressed = new MemoryStream();
        bool header = false, end = false;
        int offset = Signature.Length;
        while (!end)
        {
            if (offset + 12 > png.Length) throw new InvalidDataException("The PNG ends before its IEND chunk.");
            uint length = ReadUInt32(png, offset);
            if (length > (uint)(png.Length - offset - 12)) throw new InvalidDataException("A PNG chunk runs past the end of the file.");
            string type = new string(new[] { (char)png[offset + 4], (char)png[offset + 5], (char)png[offset + 6], (char)png[offset + 7] });
            int body = offset + 8;
            if (RunicCrc32.Compute(png, offset + 4, (int)length + 4) != ReadUInt32(png, body + (int)length))
                throw new InvalidDataException("The PNG chunk " + type + " has a wrong CRC.");
            if (!header && type != "IHDR") throw new InvalidDataException("The PNG does not start with IHDR.");
            switch (type)
            {
                case "IHDR":
                    if (header || length != 13) throw new InvalidDataException("The PNG has an invalid IHDR chunk.");
                    header = true;
                    width = (int)Math.Min(ReadUInt32(png, body), int.MaxValue);
                    height = (int)Math.Min(ReadUInt32(png, body + 4), int.MaxValue);
                    depth = png[body + 8];
                    color = png[body + 9];
                    interlace = png[body + 12];
                    if (width == 0 || height == 0) throw new InvalidDataException("The PNG has no pixels.");
                    if (width > MaximumSize || height > MaximumSize)
                        throw new InvalidDataException("The PNG is " + width + " x " + height + " px; the largest icon Runic reads is " + MaximumSize + " px.");
                    if (!ValidDepth(color, depth)) throw new InvalidDataException("The PNG has colour type " + color + " with bit depth " + depth + ", which PNG does not define.");
                    if (png[body + 10] != 0 || png[body + 11] != 0 || interlace > 1)
                        throw new InvalidDataException("The PNG uses an unknown compression, filter or interlace method.");
                    break;
                case "PLTE":
                    if (length == 0 || length % 3 != 0 || length > 768) throw new InvalidDataException("The PNG has an invalid palette.");
                    palette = Copy(png, body, (int)length);
                    break;
                case "tRNS":
                    transparency = Copy(png, body, (int)length);
                    break;
                case "IDAT":
                    compressed.Write(png, body, (int)length);
                    break;
                case "IEND":
                    end = true;
                    break;
                default:
                    // A critical chunk (upper case first letter) this decoder does not know cannot be skipped.
                    if (type[0] >= 'A' && type[0] <= 'Z') throw new InvalidDataException("The PNG has the unknown critical chunk " + type + ".");
                    break;
            }
            offset = body + (int)length + 4;
        }
        if (color == 3 && palette == null) throw new InvalidDataException("The PNG has palette colours but no PLTE chunk.");
        if (compressed.Length == 0) throw new InvalidDataException("The PNG has no image data.");
        int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bitsPerPixel = channels * depth;
        int filterStride = Math.Max(1, bitsPerPixel / 8);

        // The size of the filtered data: per pass, per row, a filter byte and the row.
        long expected = 0;
        int passes = interlace == 1 ? 7 : 1;
        for (int pass = 0; pass < passes; pass++)
        {
            var (passWidth, passHeight) = PassSize(interlace, pass, width, height);
            if (passWidth > 0 && passHeight > 0) expected += (long)passHeight * (1 + ((long)passWidth * bitsPerPixel + 7) / 8);
        }
        byte[] data = RunicInflate.Zlib(compressed.ToArray(), checked((int)expected));

        var rgba = new byte[width * height * 4];
        int position = 0;
        for (int pass = 0; pass < passes; pass++)
        {
            var (passWidth, passHeight) = PassSize(interlace, pass, width, height);
            if (passWidth == 0 || passHeight == 0) continue;
            int stride = (passWidth * bitsPerPixel + 7) / 8;
            var previous = new byte[stride];
            var current = new byte[stride];
            for (int y = 0; y < passHeight; y++)
            {
                int filter = data[position];
                Buffer.BlockCopy(data, position + 1, current, 0, stride);
                position += 1 + stride;
                Unfilter(filter, current, previous, filterStride);
                int targetY = interlace == 1 ? PassY[pass] + y * PassDy[pass] : y;
                for (int x = 0; x < passWidth; x++)
                {
                    int targetX = interlace == 1 ? PassX[pass] + x * PassDx[pass] : x;
                    Pixel(current, x, color, depth, palette, transparency, rgba, (targetY * width + targetX) * 4);
                }
                var swap = previous;
                previous = current;
                current = swap;
            }
        }
        return new RunicIconImage(width, height, rgba);
    }

    private static bool ValidDepth(int color, int depth) => color switch
    {
        0 => depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16,
        3 => depth == 1 || depth == 2 || depth == 4 || depth == 8,
        2 or 4 or 6 => depth == 8 || depth == 16,
        _ => false,
    };

    private static (int Width, int Height) PassSize(int interlace, int pass, int width, int height)
    {
        if (interlace == 0) return (width, height);
        int passWidth = width > PassX[pass] ? (width - PassX[pass] + PassDx[pass] - 1) / PassDx[pass] : 0;
        int passHeight = height > PassY[pass] ? (height - PassY[pass] + PassDy[pass] - 1) / PassDy[pass] : 0;
        return (passWidth, passHeight);
    }

    private static void Unfilter(int filter, byte[] row, byte[] previous, int stride)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = stride; i < row.Length; i++) row[i] = (byte)(row[i] + row[i - stride]);
                break;
            case 2:
                for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + previous[i]);
                break;
            case 3:
                for (int i = 0; i < row.Length; i++) row[i] = (byte)(row[i] + (((i >= stride ? row[i - stride] : 0) + previous[i]) >> 1));
                break;
            case 4:
                for (int i = 0; i < row.Length; i++)
                    row[i] = (byte)(row[i] + Paeth(i >= stride ? row[i - stride] : 0, previous[i], i >= stride ? previous[i - stride] : 0));
                break;
            default:
                throw new InvalidDataException("The PNG has the unknown row filter " + filter + ".");
        }
    }

    internal static int Paeth(int left, int up, int upLeft)
    {
        int estimate = left + up - upLeft;
        int toLeft = Math.Abs(estimate - left), toUp = Math.Abs(estimate - up), toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }

    // Sample n of a row: 1, 2, 4, 8 or 16 bits, most significant first.
    private static int Sample(byte[] row, int index, int depth)
    {
        switch (depth)
        {
            case 8:
                return row[index];
            case 16:
                return (row[index * 2] << 8) | row[index * 2 + 1];
            default:
                int bit = index * depth;
                return (row[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
        }
    }

    // A sample at the bit depth as 8 bits: low depths are scaled up (a 1-bit 1 is 255), 16 bits rounded down to 8.
    private static byte Eight(int sample, int depth) => depth switch
    {
        8 => (byte)sample,
        16 => (byte)((sample * 255 + 32767) / 65535),
        _ => (byte)(sample * 255 / ((1 << depth) - 1)),
    };

    private static void Pixel(byte[] row, int x, int color, int depth, byte[]? palette, byte[]? transparency, byte[] rgba, int at)
    {
        switch (color)
        {
            case 0:
            {
                int gray = Sample(row, x, depth);
                byte value = Eight(gray, depth);
                rgba[at] = rgba[at + 1] = rgba[at + 2] = value;
                // tRNS for grayscale: the one sample value, at the image's bit depth, that is transparent.
                rgba[at + 3] = transparency != null && transparency.Length >= 2 && gray == ((transparency[0] << 8) | transparency[1]) ? (byte)0 : (byte)255;
                break;
            }
            case 2:
            {
                int red = Sample(row, x * 3, depth), green = Sample(row, x * 3 + 1, depth), blue = Sample(row, x * 3 + 2, depth);
                rgba[at] = Eight(red, depth);
                rgba[at + 1] = Eight(green, depth);
                rgba[at + 2] = Eight(blue, depth);
                rgba[at + 3] = transparency != null && transparency.Length >= 6
                    && red == ((transparency[0] << 8) | transparency[1])
                    && green == ((transparency[2] << 8) | transparency[3])
                    && blue == ((transparency[4] << 8) | transparency[5]) ? (byte)0 : (byte)255;
                break;
            }
            case 3:
            {
                int index = Sample(row, x, depth);
                if (index * 3 + 2 >= palette!.Length) throw new InvalidDataException("A PNG pixel names palette entry " + index + ", which the palette lacks.");
                rgba[at] = palette[index * 3];
                rgba[at + 1] = palette[index * 3 + 1];
                rgba[at + 2] = palette[index * 3 + 2];
                // tRNS for palettes: an alpha per entry; entries past its end are opaque.
                rgba[at + 3] = transparency != null && index < transparency.Length ? transparency[index] : (byte)255;
                break;
            }
            case 4:
                rgba[at] = rgba[at + 1] = rgba[at + 2] = Eight(Sample(row, x * 2, depth), depth);
                rgba[at + 3] = Eight(Sample(row, x * 2 + 1, depth), depth);
                break;
            default:
                for (int channel = 0; channel < 4; channel++) rgba[at + channel] = Eight(Sample(row, x * 4 + channel, depth), depth);
                break;
        }
    }

    private static byte[] Copy(byte[] source, int offset, int length)
    {
        var copy = new byte[length];
        Buffer.BlockCopy(source, offset, copy, 0, length);
        return copy;
    }

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}

/// <summary>The PNG encoder for the icon entries: 8-bit RGBA, not interlaced, each row with the filter whose bytes have
/// the smallest sum of magnitudes (the usual heuristic), compressed with <see cref="RunicDeflate"/>. No ancillary chunk,
/// so no time stamp, enters the file.</summary>
internal static class RunicPngEncoder
{
    internal static byte[] Encode(RunicIconImage image)
    {
        int stride = image.Width * 4;
        var filtered = new byte[image.Height * (stride + 1)];
        var previous = new byte[stride];
        var row = new byte[stride];
        var candidate = new byte[stride];
        var best = new byte[stride];
        for (int y = 0; y < image.Height; y++)
        {
            Buffer.BlockCopy(image.Rgba, y * stride, row, 0, stride);
            long bestSum = long.MaxValue;
            int bestFilter = 0;
            for (int filter = 0; filter <= 4; filter++)
            {
                long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int left = i >= 4 ? row[i - 4] : 0, up = previous[i], upLeft = i >= 4 ? previous[i - 4] : 0;
                    int predicted = filter switch
                    {
                        0 => 0,
                        1 => left,
                        2 => up,
                        3 => (left + up) >> 1,
                        _ => RunicPng.Paeth(left, up, upLeft),
                    };
                    byte value = (byte)(row[i] - predicted);
                    candidate[i] = value;
                    sum += value < 128 ? value : 256 - value;
                }
                if (sum < bestSum)
                {
                    bestSum = sum;
                    bestFilter = filter;
                    Buffer.BlockCopy(candidate, 0, best, 0, stride);
                }
            }
            filtered[y * (stride + 1)] = (byte)bestFilter;
            Buffer.BlockCopy(best, 0, filtered, y * (stride + 1) + 1, stride);
            var swap = previous;
            previous = row;
            row = swap;
        }
        var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, 0, 8);
        var header = new byte[13];
        WriteUInt32(header, 0, (uint)image.Width);
        WriteUInt32(header, 4, (uint)image.Height);
        header[8] = 8; // bit depth
        header[9] = 6; // RGBA
        Chunk(output, "IHDR", header);
        Chunk(output, "IDAT", RunicDeflate.Zlib(filtered));
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var typed = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) typed[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, typed, 4, data.Length);
        RunicApplicationIcons.WriteUInt32BigEndian(output, (uint)data.Length);
        output.Write(typed, 0, typed.Length);
        RunicApplicationIcons.WriteUInt32BigEndian(output, RunicCrc32.Compute(typed, 0, typed.Length));
    }

    private static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }
}

internal static class RunicCrc32
{
    private static readonly uint[] Table = CreateTable();

    internal static uint Compute(byte[] data, int offset, int length)
    {
        uint crc = 0xffffffff;
        for (int i = offset; i < offset + length; i++) crc = Table[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
        return crc ^ 0xffffffff;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

internal static class RunicAdler32
{
    internal static uint Compute(byte[] data, int length)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < length;)
        {
            // 5552 bytes keep b below 2^32 before the modulo (zlib's NMAX).
            int end = Math.Min(length, i + 5552);
            for (; i < end; i++)
            {
                a += data[i];
                b += a;
            }
            a %= 65521;
            b %= 65521;
        }
        return (b << 16) | a;
    }
}

/// <summary>The length and distance codes of deflate (RFC 1951), shared by the inflater and the deflater.</summary>
internal static class RunicDeflateCodes
{
    internal static readonly int[] LengthBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    internal static readonly int[] LengthExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    internal static readonly int[] DistanceBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    internal static readonly int[] DistanceExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
}

/// <summary>A zlib inflater (RFC 1950 and 1951): stored, fixed and dynamic Huffman blocks, with the canonical decoding
/// of zlib's puff.</summary>
internal sealed class RunicInflate
{
    private static readonly int[] CodeLengthOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private readonly byte[] _input;
    private int _position;
    private int _bitBuffer;
    private int _bitCount;
    private readonly byte[] _output;
    private int _written;

    private RunicInflate(byte[] input, int offset, int size)
    {
        _input = input;
        _position = offset;
        _output = new byte[size];
    }

    /// <summary>The zlib stream's data, which must be exactly <paramref name="size"/> bytes.</summary>
    internal static byte[] Zlib(byte[] input, int size)
    {
        if (input.Length < 6) throw new InvalidDataException("The PNG's image data is too short.");
        int method = input[0], flags = input[1];
        if ((method & 0x0f) != 8 || (method >> 4) > 7 || ((method << 8) | flags) % 31 != 0 || (flags & 0x20) != 0)
            throw new InvalidDataException("The PNG's image data is not a zlib stream.");
        var inflate = new RunicInflate(input, 2, size);
        inflate.Run();
        if (inflate._written != size) throw new InvalidDataException("The PNG's image data is shorter than its size.");
        // The Adler-32 of the data follows the last block, byte-aligned.
        int at = inflate._position;
        if (at + 4 > input.Length) throw new InvalidDataException("The PNG's image data has no checksum.");
        uint adler = ((uint)input[at] << 24) | ((uint)input[at + 1] << 16) | ((uint)input[at + 2] << 8) | input[at + 3];
        if (adler != RunicAdler32.Compute(inflate._output, size)) throw new InvalidDataException("The PNG's image data has a wrong checksum.");
        return inflate._output;
    }

    /// <summary>Raw deflate data (RFC 1951) of exactly <paramref name="size"/> bytes, for the tests.</summary>
    internal static byte[] Raw(byte[] input, int size)
    {
        var inflate = new RunicInflate(input, 0, size);
        inflate.Run();
        if (inflate._written != size) throw new InvalidDataException("The deflate data is shorter than its size.");
        return inflate._output;
    }

    private void Run()
    {
        bool last;
        do
        {
            last = Bits(1) == 1;
            switch (Bits(2))
            {
                case 0:
                    Stored();
                    break;
                case 1:
                    Codes(Fixed.Value.Lengths, Fixed.Value.Distances);
                    break;
                case 2:
                    Dynamic();
                    break;
                default:
                    throw new InvalidDataException("The PNG's image data has an invalid deflate block.");
            }
        }
        while (!last);
        // What remains of the last byte is padding.
        _bitBuffer = 0;
        _bitCount = 0;
    }

    private int Bits(int count)
    {
        int value = _bitBuffer;
        while (_bitCount < count)
        {
            if (_position >= _input.Length) throw new InvalidDataException("The PNG's image data ends early.");
            value |= _input[_position++] << _bitCount;
            _bitCount += 8;
        }
        _bitBuffer = value >> count;
        _bitCount -= count;
        return value & ((1 << count) - 1);
    }

    private void Stored()
    {
        _bitBuffer = 0;
        _bitCount = 0;
        if (_position + 4 > _input.Length) throw new InvalidDataException("The PNG's image data ends early.");
        int length = _input[_position] | (_input[_position + 1] << 8);
        int complement = _input[_position + 2] | (_input[_position + 3] << 8);
        _position += 4;
        if (length != (~complement & 0xffff)) throw new InvalidDataException("The PNG's image data has a damaged stored block.");
        if (_position + length > _input.Length) throw new InvalidDataException("The PNG's image data ends early.");
        if (_written + length > _output.Length) throw new InvalidDataException("The PNG's image data is longer than its size.");
        Buffer.BlockCopy(_input, _position, _output, _written, length);
        _position += length;
        _written += length;
    }

    private sealed class Huffman
    {
        // The number of codes of each length, and the symbols ordered by code.
        internal readonly short[] Count = new short[16];
        internal readonly short[] Symbol;

        internal Huffman(int[] lengths, int offset, int symbols)
        {
            Symbol = new short[symbols];
            for (int i = 0; i < symbols; i++) Count[lengths[offset + i]]++;
            if (Count[0] == symbols) return; // no codes: decoding fails if a symbol is read
            int left = 1;
            for (int length = 1; length < 16; length++)
            {
                left <<= 1;
                left -= Count[length];
                if (left < 0) throw new InvalidDataException("The PNG's image data has an oversubscribed Huffman code.");
            }
            var offsets = new short[16];
            for (int length = 1; length < 15; length++) offsets[length + 1] = (short)(offsets[length] + Count[length]);
            for (int i = 0; i < symbols; i++)
            {
                if (lengths[offset + i] != 0) Symbol[offsets[lengths[offset + i]]++] = (short)i;
            }
        }
    }

    private static readonly Lazy<(Huffman Lengths, Huffman Distances)> Fixed = new Lazy<(Huffman, Huffman)>(() =>
    {
        var lengths = new int[288 + 30];
        for (int i = 0; i < 144; i++) lengths[i] = 8;
        for (int i = 144; i < 256; i++) lengths[i] = 9;
        for (int i = 256; i < 280; i++) lengths[i] = 7;
        for (int i = 280; i < 288; i++) lengths[i] = 8;
        for (int i = 288; i < 318; i++) lengths[i] = 5;
        return (new Huffman(lengths, 0, 288), new Huffman(lengths, 288, 30));
    });

    private int Decode(Huffman huffman)
    {
        int code = 0, first = 0, index = 0;
        for (int length = 1; length < 16; length++)
        {
            code |= Bits(1);
            int count = huffman.Count[length];
            if (code - count < first) return huffman.Symbol[index + (code - first)];
            index += count;
            first += count;
            first <<= 1;
            code <<= 1;
        }
        throw new InvalidDataException("The PNG's image data has an invalid Huffman code.");
    }

    private void Codes(Huffman lengths, Huffman distances)
    {
        while (true)
        {
            int symbol = Decode(lengths);
            if (symbol < 256)
            {
                if (_written >= _output.Length) throw new InvalidDataException("The PNG's image data is longer than its size.");
                _output[_written++] = (byte)symbol;
                continue;
            }
            if (symbol == 256) return;
            symbol -= 257;
            if (symbol >= 29) throw new InvalidDataException("The PNG's image data has an invalid length code.");
            int length = RunicDeflateCodes.LengthBase[symbol] + Bits(RunicDeflateCodes.LengthExtra[symbol]);
            int distanceSymbol = Decode(distances);
            if (distanceSymbol >= 30) throw new InvalidDataException("The PNG's image data has an invalid distance code.");
            int distance = RunicDeflateCodes.DistanceBase[distanceSymbol] + Bits(RunicDeflateCodes.DistanceExtra[distanceSymbol]);
            if (distance > _written) throw new InvalidDataException("The PNG's image data refers back before its start.");
            if (_written + length > _output.Length) throw new InvalidDataException("The PNG's image data is longer than its size.");
            for (int i = 0; i < length; i++, _written++) _output[_written] = _output[_written - distance];
        }
    }

    private void Dynamic()
    {
        int literals = Bits(5) + 257, distances = Bits(5) + 1, codeLengths = Bits(4) + 4;
        if (literals > 286 || distances > 30) throw new InvalidDataException("The PNG's image data has too many Huffman codes.");
        var lengths = new int[320];
        for (int i = 0; i < codeLengths; i++) lengths[CodeLengthOrder[i]] = Bits(3);
        var lengthCode = new Huffman(lengths, 0, 19);
        Array.Clear(lengths, 0, lengths.Length);
        for (int index = 0; index < literals + distances;)
        {
            int symbol = Decode(lengthCode);
            if (symbol < 16)
            {
                lengths[index++] = symbol;
                continue;
            }
            int repeat, value = 0;
            if (symbol == 16)
            {
                if (index == 0) throw new InvalidDataException("The PNG's image data repeats a code length before the first.");
                value = lengths[index - 1];
                repeat = 3 + Bits(2);
            }
            else if (symbol == 17) repeat = 3 + Bits(3);
            else repeat = 11 + Bits(7);
            if (index + repeat > literals + distances) throw new InvalidDataException("The PNG's image data has too many code lengths.");
            while (repeat-- > 0) lengths[index++] = value;
        }
        if (lengths[256] == 0) throw new InvalidDataException("The PNG's image data has no end-of-block code.");
        var literalCode = new Huffman(lengths, 0, literals);
        var distanceCode = new Huffman(lengths, literals, distances);
        Codes(literalCode, distanceCode);
    }
}

/// <summary>A zlib deflater with greedy LZ77 matching over hash chains and one block of the fixed Huffman codes: plain
/// enough to be identical everywhere, and small for icons, whose rows repeat.</summary>
internal sealed class RunicDeflate
{
    private const int WindowSize = 32768;
    private const int MinimumMatch = 3;
    private const int MaximumMatch = 258;
    private const int MaximumChain = 128;
    private const int HashBits = 15;

    private readonly MemoryStream _output;
    private uint _bitBuffer;
    private int _bitCount;

    private RunicDeflate(MemoryStream output) => _output = output;

    internal static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);
        output.WriteByte(0x9c);
        new RunicDeflate(output).Compress(data);
        RunicApplicationIcons.WriteUInt32BigEndian(output, RunicAdler32.Compute(data, data.Length));
        return output.ToArray();
    }

    /// <summary>Raw deflate data (RFC 1951), for the tests.</summary>
    internal static byte[] Raw(byte[] data)
    {
        using var output = new MemoryStream();
        new RunicDeflate(output).Compress(data);
        return output.ToArray();
    }

    private void Compress(byte[] data)
    {
        Bits(1, 1); // the last block
        Bits(1, 2); // fixed Huffman codes
        var head = new int[1 << HashBits];
        var previous = new int[WindowSize];
        for (int i = 0; i < head.Length; i++) head[i] = -1;
        int position = 0;
        while (position < data.Length)
        {
            int bestLength = 0, bestDistance = 0;
            if (position + MinimumMatch <= data.Length)
            {
                int hash = Hash(data, position);
                int candidate = head[hash];
                int limit = Math.Min(MaximumMatch, data.Length - position);
                for (int chain = 0; candidate >= 0 && position - candidate <= WindowSize && chain < MaximumChain; chain++)
                {
                    if (data[candidate + bestLength] == data[position + bestLength])
                    {
                        int length = 0;
                        while (length < limit && data[candidate + length] == data[position + length]) length++;
                        if (length > bestLength)
                        {
                            bestLength = length;
                            bestDistance = position - candidate;
                            if (length == limit) break;
                        }
                    }
                    int next = previous[candidate & (WindowSize - 1)];
                    if (next >= candidate) break; // the slot was reused by a newer position
                    candidate = next;
                }
            }
            int advance;
            if (bestLength >= MinimumMatch)
            {
                Length(bestLength);
                Distance(bestDistance);
                advance = bestLength;
            }
            else
            {
                Literal(data[position]);
                advance = 1;
            }
            for (int end = position + advance; position < end; position++)
            {
                if (position + MinimumMatch > data.Length) continue;
                int hash = Hash(data, position);
                previous[position & (WindowSize - 1)] = head[hash];
                head[hash] = position;
            }
        }
        Literal(256);
        if (_bitCount > 0) _output.WriteByte((byte)_bitBuffer);
        _bitBuffer = 0;
        _bitCount = 0;
    }

    private static int Hash(byte[] data, int position) =>
        ((data[position] << 10) ^ (data[position + 1] << 5) ^ data[position + 2]) & ((1 << HashBits) - 1);

    private void Bits(uint value, int count)
    {
        _bitBuffer |= value << _bitCount;
        _bitCount += count;
        while (_bitCount >= 8)
        {
            _output.WriteByte((byte)_bitBuffer);
            _bitBuffer >>= 8;
            _bitCount -= 8;
        }
    }

    // A Huffman code goes out most significant bit first, so its bits are reversed into the LSB-first stream.
    private void Code(int code, int length)
    {
        int reversed = 0;
        for (int i = 0; i < length; i++) reversed |= ((code >> i) & 1) << (length - 1 - i);
        Bits((uint)reversed, length);
    }

    // The fixed literal/length code (RFC 1951 3.2.6).
    private void Literal(int symbol)
    {
        if (symbol < 144) Code(0x30 + symbol, 8);
        else if (symbol < 256) Code(0x190 + symbol - 144, 9);
        else if (symbol < 280) Code(symbol - 256, 7);
        else Code(0xc0 + symbol - 280, 8);
    }

    private void Length(int length)
    {
        int code = 28;
        while (RunicDeflateCodes.LengthBase[code] > length) code--;
        Literal(257 + code);
        Bits((uint)(length - RunicDeflateCodes.LengthBase[code]), RunicDeflateCodes.LengthExtra[code]);
    }

    private void Distance(int distance)
    {
        int code = 29;
        while (RunicDeflateCodes.DistanceBase[code] > distance) code--;
        Code(code, 5);
        Bits((uint)(distance - RunicDeflateCodes.DistanceBase[code]), RunicDeflateCodes.DistanceExtra[code]);
    }
}
