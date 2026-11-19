using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Runic.Platform.Tests;
using TUnit.Core;

namespace Runic.Platform.MacOS.Tests;

/// <summary>
/// The application icon codec of Runic.Platform's build (buildTransitive/RunicApplicationIcon.cs, sdk#1207), compiled
/// into this suite: the PNG decoder against an independent encoder that uses .NET's zlib, the inflater and deflater,
/// the box filter, and the .ico and .icns it writes, parsed back with an independent PNG decoder.
/// </summary>
internal sealed class ApplicationIconCodecTests
{
    // Every colour type with every bit depth PNG defines.
    public static IEnumerable<(int Color, int Depth)> Formats() =>
    [
        (0, 1), (0, 2), (0, 4), (0, 8), (0, 16),
        (2, 8), (2, 16),
        (3, 1), (3, 2), (3, 4), (3, 8),
        (4, 8), (4, 16),
        (6, 8), (6, 16),
    ];

    [Test, DisplayName("decoder: colour type $color, bit depth $depth, interlaced $interlaced")]
    [MethodDataSource(nameof(Cases))]
    public void DecodesEveryFormat(int color, int depth, bool interlaced)
    {
        // Odd sizes leave Adam7 passes partly or wholly empty; 1 x 1 has only the first pass.
        foreach (var (width, height) in new[] { (13, 11), (1, 1), (3, 2), (9, 17) })
        {
            var image = TestImage.Create(width, height, color, depth, seed: width * 3 + height);
            var png = TestPng.Encode(image, interlaced);
            var decoded = RunicPng.Decode(png);
            Check(decoded.Width == width && decoded.Height == height, $"{width}x{height}: decoded {decoded.Width}x{decoded.Height}");
            CheckPixels(image.ExpectedRgba(), decoded.Rgba, $"type {color} depth {depth} interlaced {interlaced} {width}x{height}");
        }
    }

    public static IEnumerable<Func<(int, int, bool)>> Cases()
    {
        foreach (var (color, depth) in Formats())
        {
            yield return () => (color, depth, false);
            yield return () => (color, depth, true);
        }
    }

    [Test, DisplayName("decoder: tRNS makes one grayscale or RGB value transparent at the image's bit depth")]
    public void DecodesColorKeyTransparency()
    {
        foreach (var (color, depth) in new[] { (0, 2), (0, 8), (0, 16), (2, 8), (2, 16) })
        {
            foreach (var interlaced in new[] { false, true })
            {
                var image = TestImage.Create(7, 5, color, depth, seed: 5);
                // The key is the sample of pixel (2, 1), so at least that pixel is transparent.
                image.ColorKey = image.Samples(2, 1);
                var expected = image.ExpectedRgba();
                Check(expected[(1 * 7 + 2) * 4 + 3] == 0, "the keyed pixel is transparent");
                CheckPixels(expected, RunicPng.Decode(TestPng.Encode(image, interlaced)).Rgba, $"tRNS type {color} depth {depth} interlaced {interlaced}");
            }
        }
    }

    [Test, DisplayName("decoder: tRNS gives palette entries alpha; entries past its end are opaque")]
    public void DecodesPaletteTransparency()
    {
        foreach (var depth in new[] { 1, 2, 4, 8 })
        {
            var image = TestImage.Create(11, 6, 3, depth, seed: 9);
            image.PaletteAlpha = new byte[] { 0, 128, 255, 7 }[..Math.Min(4, image.Palette!.Length / 3 - 1)];
            var expected = image.ExpectedRgba();
            Check(expected.Where((_, i) => i % 4 == 3).Distinct().Count() > 1, $"depth {depth}: some alpha differs");
            CheckPixels(expected, RunicPng.Decode(TestPng.Encode(image, interlaced: depth % 2 == 0)).Rgba, $"palette tRNS depth {depth}");
        }
    }

    [Test, DisplayName("decoder: stored, fixed and dynamic deflate blocks, split IDAT chunks and ancillary chunks")]
    public void DecodesEveryBlockType()
    {
        var image = TestImage.Create(40, 30, 6, 8, seed: 2);
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.SmallestSize })
            CheckPixels(image.ExpectedRgba(), RunicPng.Decode(TestPng.Encode(image, interlaced: false, level)).Rgba, level.ToString());
        // The deflater's fixed Huffman block, through the PNG encoder.
        var own = RunicPngEncoder.Encode(new RunicIconImage(40, 30, image.ExpectedRgba()));
        CheckPixels(image.ExpectedRgba(), RunicPng.Decode(own).Rgba, "own encoder");
    }

    [Test, DisplayName("decoder: a damaged or invalid PNG is refused with InvalidDataException")]
    public void RefusesInvalidFiles()
    {
        var valid = TestPng.Encode(TestImage.Create(4, 4, 2, 8, seed: 1), interlaced: false);
        Refused([.. valid.Take(20)], "truncated");
        Refused([0x89, (byte)'P', (byte)'N', (byte)'X', .. valid.Skip(4)], "signature");
        var crc = valid.ToArray();
        crc[29] ^= 1; // the last byte of IHDR's CRC
        Refused(crc, "IHDR CRC");
        var data = valid.ToArray();
        int idat = IndexOf(data, "IDAT"u8.ToArray());
        data[idat + 8] ^= 0xff; // a byte of the compressed data; the chunk CRC is fixed so the inflater sees it
        FixCrc(data, idat - 4);
        Refused(data, "damaged deflate data");
        Refused(TestPng.Encode(TestImage.Create(4, 4, 2, 8, seed: 1), interlaced: false, critical: "abCD"u8.ToArray()), "unknown chunk that is not critical", refused: false);
        Refused(TestPng.Encode(TestImage.Create(4, 4, 2, 8, seed: 1), interlaced: false, critical: "ABCD"u8.ToArray()), "unknown critical chunk");
        var palette = TestImage.Create(4, 4, 3, 8, seed: 1);
        palette.Palette = palette.Palette![..6]; // two entries, while the pixels use more
        Refused(TestPng.Encode(palette, interlaced: false), "palette index past the palette");
        var depth = TestPng.Encode(TestImage.Create(4, 4, 2, 8, seed: 1), interlaced: false);
        depth[24] = 4; // RGB with bit depth 4 does not exist
        FixCrc(depth, 8);
        Refused(depth, "colour type and depth");
    }

    [Test, DisplayName("inflate and deflate: the deflater's output inflates with .NET's zlib and with the inflater")]
    public void DeflateRoundTrips()
    {
        var random = new Random(1207);
        var noise = new byte[70000];
        random.NextBytes(noise);
        var text = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The application supplies one icon. ", 3000)));
        var mixed = text.Concat(noise.Take(5000)).Concat(new byte[40000]).Concat(text.Take(1000)).ToArray();
        foreach (var (name, data) in new[] { ("empty", Array.Empty<byte>()), ("one byte", new byte[] { 42 }), ("noise", noise), ("text", text), ("mixed", mixed) })
        {
            var compressed = RunicDeflate.Zlib(data);
            using var stream = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
            var inflated = new MemoryStream();
            stream.CopyTo(inflated);
            Check(inflated.ToArray().AsSpan().SequenceEqual(data), $"{name}: .NET's zlib reads the deflater's stream");
            Check(RunicInflate.Zlib(compressed, data.Length).AsSpan().SequenceEqual(data), $"{name}: the inflater reads it");
            if (name == "text") Check(compressed.Length < data.Length / 20, $"text compresses: {compressed.Length} of {data.Length}");
        }
    }

    [Test, DisplayName("box filter: area weights in premultiplied alpha, exact and rounded half up")]
    public void ScalesWithBoxFilter()
    {
        // 4 x 4 to 2 x 2: each output pixel is the mean of a 2 x 2 block. Top left: two opaque red, two transparent
        // green pixels: the colour stays red (premultiplied: transparent pixels add no colour), the alpha is half.
        var source = new byte[4 * 4 * 4];
        void Set(int x, int y, byte r, byte g, byte b, byte a) => (source[(y * 4 + x) * 4], source[(y * 4 + x) * 4 + 1], source[(y * 4 + x) * 4 + 2], source[(y * 4 + x) * 4 + 3]) = (r, g, b, a);
        Set(0, 0, 255, 0, 0, 255); Set(1, 0, 255, 0, 0, 255); Set(0, 1, 0, 255, 0, 0); Set(1, 1, 0, 255, 0, 0);
        // Top right: opaque white and black, half each: gray 128 (127.5 rounds up).
        Set(2, 0, 255, 255, 255, 255); Set(3, 0, 0, 0, 0, 255); Set(2, 1, 255, 255, 255, 255); Set(3, 1, 0, 0, 0, 255);
        // Bottom left: opaque blue and half-transparent red: colour weighted by alpha, (255*128)/(255+128) red.
        Set(0, 2, 0, 0, 255, 255); Set(1, 2, 255, 0, 0, 128); Set(0, 3, 0, 0, 255, 255); Set(1, 3, 255, 0, 0, 128);
        // Bottom right: transparent everywhere.
        var scaled = RunicIconScaler.Resize(new RunicIconImage(4, 4, source), 2).Rgba;
        CheckPixels([255, 0, 0, 128, 128, 128, 128, 255, 85, 0, 170, 192, 0, 0, 0, 0], scaled, "4 x 4 to 2 x 2");

        // 3 x 3 to 2 x 2: output (0, 0) covers source pixel (0, 0) whole, (1, 0) and (0, 1) half and (1, 1) a quarter:
        // weights 4, 2, 2, 1 of 9. Gray values 0 at (0, 0) and 90 elsewhere: (0*4 + 90*5) / 9 = 50.
        var gray = new byte[3 * 3 * 4];
        for (int i = 0; i < 9; i++) (gray[i * 4], gray[i * 4 + 1], gray[i * 4 + 2], gray[i * 4 + 3]) = i == 0 ? ((byte)0, (byte)0, (byte)0, (byte)255) : ((byte)90, (byte)90, (byte)90, (byte)255);
        var three = RunicIconScaler.Resize(new RunicIconImage(3, 3, gray), 2).Rgba;
        Check(three[0] == 50 && three[3] == 255, $"3 x 3 to 2 x 2: {three[0]}, alpha {three[3]}");
        Check(three[4] == 90 && three[8] == 90 && three[12] == 90, "the other three outputs see only 90");

        // A uniform image stays uniform at every icon size, and the source size is returned as it is.
        var uniform = new RunicIconImage(512, 512, Enumerable.Repeat(new byte[] { 12, 200, 99, 77 }, 512 * 512).SelectMany(p => p).ToArray());
        foreach (int size in RunicApplicationIcons.IcoSizes)
        {
            var result = RunicIconScaler.Resize(uniform, size);
            Check(result.Width == size && result.Rgba.Chunk(4).All(p => p[0] == 12 && p[1] == 200 && p[2] == 99 && p[3] == 77), $"uniform at {size}");
        }
        Check(ReferenceEquals(RunicIconScaler.Resize(uniform, 512), uniform), "the source size is the source");
    }

    [Test, DisplayName("ico: 16 to 64 px as 32-bit DIBs with an AND mask, 256 px as PNG")]
    public void WritesIco()
    {
        var image = Sample(512);
        var ico = RunicApplicationIcons.Ico(image);
        Check(BinaryPrimitives.ReadUInt16LittleEndian(ico) == 0 && BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) == 1, "ICONDIR");
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        Check(count == 8, $"8 entries, found {count}");
        int end = 6 + 16 * count;
        for (int i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i, 16);
            int size = RunicApplicationIcons.IcoSizes[i];
            Check(entry[0] == (size == 256 ? 0 : size) && entry[1] == entry[0], $"entry {i}: width and height of {size}");
            Check(entry[2] == 0 && entry[3] == 0 && BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]) == 1 && BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) == 32, $"entry {i}: 32 bpp, no palette");
            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            Check(offset == end, $"entry {i}: images follow each other, at {offset}, expected {end}");
            end = offset + length;
            var data = ico.AsSpan(offset, length);
            var expected = RunicIconScaler.Resize(image, size).Rgba;
            if (size == 256)
            {
                var (width, height, rgba) = ThumbnailImages.Pixels(data);
                Check(width == 256 && height == 256, "the 256 px PNG");
                CheckPixels(expected, rgba, "256 px PNG");
                continue;
            }
            Check(BinaryPrimitives.ReadInt32LittleEndian(data) == 40 && BinaryPrimitives.ReadInt32LittleEndian(data[4..]) == size
                && BinaryPrimitives.ReadInt32LittleEndian(data[8..]) == size * 2 && BinaryPrimitives.ReadUInt16LittleEndian(data[14..]) == 32
                && BinaryPrimitives.ReadInt32LittleEndian(data[16..]) == 0, $"{size} px: BITMAPINFOHEADER");
            int maskStride = (size + 31) / 32 * 4;
            Check(length == 40 + size * size * 4 + maskStride * size, $"{size} px: pixels and mask");
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Bottom-up BGRA rows.
                    int pixel = 40 + ((size - 1 - y) * size + x) * 4, at = (y * size + x) * 4;
                    Check(data[pixel] == expected[at + 2] && data[pixel + 1] == expected[at + 1] && data[pixel + 2] == expected[at] && data[pixel + 3] == expected[at + 3], $"{size} px: pixel ({x}, {y})");
                    bool masked = (data[40 + size * size * 4 + (size - 1 - y) * maskStride + x / 8] & (0x80 >> (x % 8))) != 0;
                    Check(masked == (expected[at + 3] == 0), $"{size} px: mask at ({x}, {y})");
                }
            }
        }
        Check(end == ico.Length, "no bytes after the last image");
    }

    [Test, DisplayName("icns: PNG entries of the iconutil sizes up to the source size")]
    public void WritesIcns()
    {
        foreach (var (source, types) in new[]
        {
            (256, "icp4 ic11 icp5 ic12 ic07 ic13 ic08"),
            (512, "icp4 ic11 icp5 ic12 ic07 ic13 ic08 ic14 ic09"),
            (1024, "icp4 ic11 icp5 ic12 ic07 ic13 ic08 ic14 ic09 ic10"),
        })
        {
            var image = Sample(source);
            var icns = RunicApplicationIcons.Icns(image);
            Check(icns.AsSpan(0, 4).SequenceEqual("icns"u8) && BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(4)) == icns.Length, $"{source}: header and length");
            var found = new List<string>();
            for (int offset = 8; offset < icns.Length;)
            {
                string type = System.Text.Encoding.ASCII.GetString(icns, offset, 4);
                int length = (int)BinaryPrimitives.ReadUInt32BigEndian(icns.AsSpan(offset + 4));
                Check(length > 8 && offset + length <= icns.Length, $"{source}: {type} length {length}");
                int size = RunicApplicationIcons.IcnsEntries.Single(entry => entry.Type == type).Size;
                var (width, height, rgba) = ThumbnailImages.Pixels(icns.AsSpan(offset + 8, length - 8));
                Check(width == size && height == size, $"{source}: {type} is {width} x {height}, expected {size}");
                CheckPixels(RunicIconScaler.Resize(image, size).Rgba, rgba, $"{source}: {type}");
                found.Add(type);
                offset += length;
            }
            Check(string.Join(" ", found) == types, $"{source}: {string.Join(" ", found)}");
        }
    }

    // The same bytes on every operating system and runtime: the codec is integer arithmetic and its own deflater. A
    // change of these hashes is a change of every generated icon; update them only with such a change.
    [Test, DisplayName("determinism: the .ico and .icns of a fixed image have fixed bytes")]
    public void WritesTheSameBytes()
    {
        var image = Sample(512);
        Check(Hash(RunicApplicationIcons.Ico(image)) == Hash(RunicApplicationIcons.Ico(Sample(512))), "two runs");
        Check(Hash(RunicApplicationIcons.Ico(image)) == IcoHash, $"ico {Hash(RunicApplicationIcons.Ico(image))}");
        Check(Hash(RunicApplicationIcons.Icns(image)) == IcnsHash, $"icns {Hash(RunicApplicationIcons.Icns(image))}");
    }

    private const string IcoHash = "b0cba8d756fa1ad14a43b4c5b7212e6201668735b0def3d1466670ff461bdfc5";
    private const string IcnsHash = "e17778cf0cf4c75a743545d789178dabb971b6d3eda07aa77dc8d5ff2c683569";

    /// <summary>A square test icon: a disc with a soft edge on a transparent background and a colour gradient, so that
    /// filtering, alpha and the mask all matter.</summary>
    internal static RunicIconImage Sample(int size)
    {
        var rgba = new byte[size * size * 4];
        double center = (size - 1) / 2.0, radius = size * 0.42;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double distance = Math.Sqrt((x - center) * (x - center) + (y - center) * (y - center));
                int at = (y * size + x) * 4;
                rgba[at] = (byte)(x * 255 / (size - 1));
                rgba[at + 1] = (byte)(y * 255 / (size - 1));
                rgba[at + 2] = (byte)((x + y) % 256);
                rgba[at + 3] = (byte)Math.Clamp((int)Math.Round((radius - distance) * 32), 0, 255);
            }
        }
        return new RunicIconImage(size, size, rgba);
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static void Refused(byte[] png, string what, bool refused = true)
    {
        try
        {
            RunicPng.Decode(png);
            Check(!refused, $"{what}: decoded");
        }
        catch (InvalidDataException exception)
        {
            Check(refused, $"{what}: {exception.Message}");
        }
    }

    private static int IndexOf(byte[] data, byte[] value)
    {
        for (int i = 0; i + value.Length <= data.Length; i++)
            if (data.AsSpan(i, value.Length).SequenceEqual(value)) return i;
        return -1;
    }

    // Recomputes the CRC of the chunk at offset (its length field).
    private static void FixCrc(byte[] data, int offset)
    {
        int length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(offset));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset + 8 + length), TestPng.Crc(data.AsSpan(offset + 4, length + 4)));
    }

    private static void CheckPixels(byte[] expected, byte[] actual, string what)
    {
        Check(expected.Length == actual.Length, $"{what}: {actual.Length} bytes, expected {expected.Length}");
        for (int i = 0; i < expected.Length; i++)
            Check(expected[i] == actual[i], $"{what}: byte {i} (pixel {i / 4}, channel {i % 4}) is {actual[i]}, expected {expected[i]}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>Raw PNG samples of a test image and the RGBA that a decoder must make of them.</summary>
    internal sealed class TestImage
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Color { get; init; }
        public required int Depth { get; init; }
        public required int[] Raw { get; init; }
        public byte[]? Palette { get; set; }
        public byte[]? PaletteAlpha { get; set; }
        public int[]? ColorKey { get; set; }

        public int Channels => Color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };

        public int[] Samples(int x, int y) => Raw.AsSpan((y * Width + x) * Channels, Channels).ToArray();

        public static TestImage Create(int width, int height, int color, int depth, int seed)
        {
            int channels = color switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
            int max = (1 << depth) - 1;
            byte[]? palette = null;
            if (color == 3)
            {
                int entries = Math.Min(1 << depth, 20);
                palette = new byte[entries * 3];
                for (int i = 0; i < palette.Length; i++) palette[i] = (byte)(i * 37 + seed * 11);
                max = entries - 1;
            }
            var raw = new int[width * height * channels];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    for (int k = 0; k < channels; k++)
                        raw[(y * width + x) * channels + k] = (int)(((uint)(x * 7919 + y * 104729 + k * 1237 + seed * 31) * 2654435761u >> 7) % (uint)(max + 1));
            return new TestImage { Width = width, Height = height, Color = color, Depth = depth, Raw = raw, Palette = palette };
        }

        // The PNG specification's conversions: low depths scale to 255, 16 bits round to 8.
        private byte Eight(int sample) => Depth switch
        {
            8 => (byte)sample,
            16 => (byte)Math.Round(sample * 255.0 / 65535.0, MidpointRounding.AwayFromZero),
            _ => (byte)(sample * 255 / ((1 << Depth) - 1)),
        };

        public byte[] ExpectedRgba()
        {
            var rgba = new byte[Width * Height * 4];
            for (int p = 0; p < Width * Height; p++)
            {
                var s = Raw.AsSpan(p * Channels, Channels);
                (byte R, byte G, byte B, byte A) pixel = Color switch
                {
                    0 => (Eight(s[0]), Eight(s[0]), Eight(s[0]), ColorKey is { } key && key[0] == s[0] ? (byte)0 : (byte)255),
                    2 => (Eight(s[0]), Eight(s[1]), Eight(s[2]), ColorKey is { } key && key.AsSpan().SequenceEqual(s) ? (byte)0 : (byte)255),
                    3 => (Palette![s[0] * 3], Palette[s[0] * 3 + 1], Palette[s[0] * 3 + 2], PaletteAlpha is { } alpha && s[0] < alpha.Length ? alpha[s[0]] : (byte)255),
                    4 => (Eight(s[0]), Eight(s[0]), Eight(s[0]), Eight(s[1])),
                    _ => (Eight(s[0]), Eight(s[1]), Eight(s[2]), Eight(s[3])),
                };
                (rgba[p * 4], rgba[p * 4 + 1], rgba[p * 4 + 2], rgba[p * 4 + 3]) = pixel;
            }
            return rgba;
        }
    }

    /// <summary>A PNG encoder for the decoder tests, independent of the codec: every format, Adam7, a different filter
    /// per row, .NET's zlib, the image data split over two IDAT chunks, and ancillary chunks around them.</summary>
    internal static class TestPng
    {
        private static readonly int[] PassX = [0, 4, 0, 2, 0, 1, 0], PassY = [0, 0, 4, 0, 2, 0, 1];
        private static readonly int[] PassDx = [8, 8, 4, 4, 2, 2, 1], PassDy = [8, 8, 8, 4, 4, 2, 2];

        public static byte[] Encode(TestImage image, bool interlaced, CompressionLevel level = CompressionLevel.Optimal, byte[]? critical = null)
        {
            int bits = image.Channels * image.Depth, filterStride = Math.Max(1, bits / 8);
            var filtered = new MemoryStream();
            int rowIndex = 0;
            for (int pass = 0; pass < (interlaced ? 7 : 1); pass++)
            {
                int x0 = interlaced ? PassX[pass] : 0, y0 = interlaced ? PassY[pass] : 0, dx = interlaced ? PassDx[pass] : 1, dy = interlaced ? PassDy[pass] : 1;
                int width = image.Width > x0 ? (image.Width - x0 + dx - 1) / dx : 0, height = image.Height > y0 ? (image.Height - y0 + dy - 1) / dy : 0;
                if (width == 0 || height == 0) continue;
                int stride = (width * bits + 7) / 8;
                var previous = new byte[stride];
                for (int y = 0; y < height; y++)
                {
                    var row = new byte[stride];
                    for (int x = 0; x < width; x++)
                    {
                        var samples = image.Samples(x0 + x * dx, y0 + y * dy);
                        for (int k = 0; k < samples.Length; k++) Pack(row, x * image.Channels + k, image.Depth, samples[k]);
                    }
                    int filter = rowIndex++ % 5;
                    filtered.WriteByte((byte)filter);
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= filterStride ? row[i - filterStride] : 0, up = previous[i], upLeft = i >= filterStride ? previous[i - filterStride] : 0;
                        int predicted = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, upLeft) };
                        filtered.WriteByte((byte)(row[i] - predicted));
                    }
                    previous = row;
                }
            }
            var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, level, leaveOpen: true)) zlib.Write(filtered.ToArray());
            var data = compressed.ToArray();

            var output = new MemoryStream();
            output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height);
            header[8] = (byte)image.Depth;
            header[9] = (byte)image.Color;
            header[12] = (byte)(interlaced ? 1 : 0);
            Chunk(output, "IHDR"u8, header);
            Chunk(output, "gAMA"u8, [0, 0, 0xb1, 0x8f]);
            if (image.Palette is { } palette) Chunk(output, "PLTE"u8, palette);
            if (image.PaletteAlpha is { } alpha) Chunk(output, "tRNS"u8, alpha);
            if (image.ColorKey is { } key)
            {
                var values = new byte[key.Length * 2];
                for (int i = 0; i < key.Length; i++) BinaryPrimitives.WriteUInt16BigEndian(values.AsSpan(i * 2), (ushort)key[i]);
                Chunk(output, "tRNS"u8, values);
            }
            if (critical is not null) Chunk(output, critical, [1, 2, 3]);
            int half = data.Length / 2;
            Chunk(output, "IDAT"u8, data[..half]);
            Chunk(output, "IDAT"u8, data[half..]);
            Chunk(output, "tEXt"u8, "Comment\0test"u8.ToArray());
            Chunk(output, "IEND"u8, []);
            return output.ToArray();
        }

        private static void Pack(byte[] row, int index, int depth, int value)
        {
            switch (depth)
            {
                case 8: row[index] = (byte)value; break;
                case 16: BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(index * 2), (ushort)value); break;
                default:
                    int bit = index * depth;
                    row[bit / 8] |= (byte)(value << (8 - depth - bit % 8));
                    break;
            }
        }

        private static int Paeth(int a, int b, int c)
        {
            int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
        }

        private static void Chunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
        {
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            var typed = type.ToArray().Concat(data).ToArray();
            output.Write(typed);
            var crc = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typed));
            output.Write(crc);
        }

        public static uint Crc(ReadOnlySpan<byte> data)
        {
            uint crc = 0xffffffff;
            foreach (byte value in data)
            {
                crc ^= value;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
    }
}
