namespace Runic.Desktop.Internal;

// Decimal numbers in HTTP headers and in the bootstrap script, without culture data. Evaluating
// CultureInfo.InvariantCulture or NumberFormatInfo.InvariantInfo runs CultureInfo's static constructor, which loads ICU
// (≈1.9 ms and ≈4.8 MiB) unless the application uses invariant globalization; the host keeps ICU off the path to its
// first served page (S1, D053.25). A non-negative integer formats without a format provider in every culture.
internal static class AsciiNumbers
{
    // Digits only, as NumberStyles.None: no sign, white space or separators.
    internal static bool TryParse(ReadOnlySpan<char> text, out long value)
    {
        value = 0;
        if (text.IsEmpty) return false;
        foreach (var character in text)
        {
            if (!char.IsAsciiDigit(character) || value > (long.MaxValue - (character - '0')) / 10) return false;
            value = value * 10 + (character - '0');
        }
        return true;
    }

    internal static string Format(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return value.ToString(provider: null);
    }
}
