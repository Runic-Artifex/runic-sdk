using System;
using System.Globalization;

namespace RunicToolkit.MVVM.BindingCompiler;

internal static class TerminalText
{
    internal const int MaximumDisplayedPathCharacters = 4_096;
    internal const int MaximumDisplayedMessageCharacters = 8_192;

    public static string Path(string value) => Sanitize(value, MaximumDisplayedPathCharacters, '_');

    public static string Message(string value) => Sanitize(value, MaximumDisplayedMessageCharacters, ' ');

    private static string Sanitize(string value, int maximumCharacters, char controlReplacement)
    {
        ArgumentNullException.ThrowIfNull(value);
        int length = Math.Min(value.Length, maximumCharacters);
        char[]? sanitized = null;
        for (int index = 0; index < length; index++)
        {
            char character = value[index];
            if (!IsTerminalControl(value, index))
            {
                if (sanitized is not null)
                {
                    sanitized[index] = character;
                }

                continue;
            }

            sanitized ??= value[..length].ToCharArray();
            sanitized[index] = controlReplacement;
        }

        string result = sanitized is null ? value[..length] : new string(sanitized);
        return value.Length > maximumCharacters ? result + "..." : result;
    }

    private static bool IsTerminalControl(string value, int index)
    {
        char character = value[index];
        return char.IsControl(character) ||
            character is '\u2028' or '\u2029' ||
            CharUnicodeInfo.GetUnicodeCategory(value, index) == UnicodeCategory.Format;
    }
}
