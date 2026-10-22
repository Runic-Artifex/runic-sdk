using System.Text;

namespace Runic.Application.Views;

// The one size limit of a window's Bridge inputs: setter values, command arguments,
// checked writes, operation starts and interaction requests and replies
// (WindowContentSessionOptions.MaximumInputBytes).
internal static class BridgeInputLimit
{
    internal const int Minimum = 1_024;
    internal const int Maximum = 64 * 1_048_576;

    // Validates a configured limit; null selects the default.
    internal static int Resolve(int? value, string name) => value switch
    {
        null => WindowContentSessionOptions.DefaultMaximumInputBytes,
        < Minimum or > Maximum => throw new ArgumentOutOfRangeException(name, value,
            $"The Bridge input limit must be between {Minimum} and {Maximum} bytes."),
        { } bytes => bytes,
    };

    // Throws for an option value outside the range; null is valid.
    internal static void ThrowIfInvalid(int? value, string name) => _ = Resolve(value, name);

    // Whether the UTF-8 encoding of the value fits; a UTF-16 code unit is one to three bytes.
    internal static bool Fits(string value, int maximumBytes) =>
        value.Length <= maximumBytes
        && ((long)value.Length * 3 <= maximumBytes || Encoding.UTF8.GetByteCount(value) <= maximumBytes);
}
