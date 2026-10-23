namespace Runic.Platform.Windows;

// The overlay icon of a taskbar badge, drawn by Runic: white digits on a red circle for one digit, on a red rounded
// rectangle for two or "99+", and a smaller red circle for a dot. The digits come from a 3×5 pixel font scaled by whole pixels, so
// they stay sharp at every size; the shapes are antialiased. Portable: nothing here calls native code.
internal static class WindowsBadgeGlyph
{
    // Windows 11's critical red (#C42B1C) and white.
    private const byte Red = 0xC4, Green = 0x2B, Blue = 0x1C;
    private const int Samples = 4;

    private static readonly Dictionary<char, string> Font = new()
    {
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "111001111100111", ['3'] = "111001111001111",
        ['4'] = "101101111001001", ['5'] = "111100111001111", ['6'] = "111100111101111", ['7'] = "111001001001001",
        ['8'] = "111101111101111", ['9'] = "111101111001111", ['+'] = "000010111010000",
    };

    // The badge's pixels, size × size, top-down 32-bit BGRA with straight alpha. An empty label draws the dot.
    internal static byte[] Render(string label, int size)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 8);
        foreach (char character in label)
            if (!Font.ContainsKey(character)) throw new ArgumentException($"The badge cannot draw '{character}'.", nameof(label));
        int glyphs = label.Length;
        // The whole-pixel unit nearest to text of 80 % of the width or 60 % of the height, leaving a pixel at each side.
        int textUnits = glyphs == 0 ? 0 : glyphs * 4 - 1;
        int unit = glyphs == 0 ? 0 : (int)Math.Round(Math.Min(size * 0.8 / textUnits, size * 0.6 / 5));
        while (unit > 1 && textUnits * unit > size - 2) unit--;
        unit = glyphs == 0 ? 0 : Math.Max(1, unit);
        int textWidth = textUnits * unit, textHeight = 5 * unit;
        double shapeHeight = glyphs switch { 0 => size * 0.75, 1 => size, _ => Math.Min(size, textHeight + 2.0 * Math.Max(1, unit)) };
        double shapeWidth = glyphs switch { 0 => size * 0.75, 1 => size, _ => size };
        // Two or more glyphs fill the width, so the corners are rounded less than a pill's to keep the glyphs inside.
        double left = (size - shapeWidth) / 2, top = (size - shapeHeight) / 2, radius = glyphs > 1 ? Math.Min(shapeHeight / 2, size * 0.2) : shapeHeight / 2;
        int textLeft = (size - textWidth) / 2, textTop = (size - textHeight) / 2;

        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < Samples; sy++)
                    for (int sx = 0; sx < Samples; sx++)
                        if (InShape(x + (sx + 0.5) / Samples, y + (sy + 0.5) / Samples, left, top, shapeWidth, shapeHeight, radius)) inside++;
                if (inside == 0) continue;
                bool text = glyphs > 0 && x >= textLeft && x < textLeft + textWidth && y >= textTop && y < textTop + textHeight
                    && Lit(label, (x - textLeft) / unit, (y - textTop) / unit);
                int i = (y * size + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2]) = text ? ((byte)0xFF, (byte)0xFF, (byte)0xFF) : (Blue, Green, Red);
                pixels[i + 3] = (byte)((inside * 255 + Samples * Samples / 2) / (Samples * Samples));
            }
        return pixels;
    }

    // Whether the font lights the text cell at column, row: four columns per glyph, the fourth the gap.
    private static bool Lit(string label, int column, int row)
    {
        int glyph = column / 4, x = column % 4;
        return x < 3 && Font[label[glyph]][row * 3 + x] == '1';
    }

    // A rectangle with rounded corners; a circle when it is square and the radius is half its side.
    private static bool InShape(double x, double y, double left, double top, double width, double height, double radius)
    {
        if (y < top || y > top + height || x < left || x > left + width) return false;
        double cx = Math.Clamp(x, left + radius, left + width - radius), cy = Math.Clamp(y, top + radius, top + height - radius);
        double dx = x - cx, dy = y - cy;
        return dx * dx + dy * dy <= radius * radius;
    }
}
