namespace Runic.Platform.Linux.DBus;

// Name and signature syntax from the D-Bus specification ("Valid Names", "Signatures").
internal static class DBusNames
{
    private const int MaximumNameLength = 255;

    internal static bool IsObjectPath(string value)
    {
        if (value.Length == 0 || value[0] != '/') return false;
        if (value.Length == 1) return true;
        if (value[^1] == '/') return false;
        bool previousSlash = true;
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '/')
            {
                if (previousSlash) return false;
                previousSlash = true;
            }
            else if (IsNameCharacter(character)) previousSlash = false;
            else return false;
        }
        return true;
    }

    // Interface and error names: two or more dot-separated elements of [A-Za-z0-9_], none starting with a digit.
    internal static bool IsInterfaceName(string value) => IsDottedName(value, allowHyphen: false, allowDigitStart: false, allowColon: false);

    // Well-known bus names also allow '-'; unique names start with ':' and may have elements starting with a digit.
    internal static bool IsBusName(string value) =>
        value.StartsWith(':') ? IsDottedName(value[1..], allowHyphen: true, allowDigitStart: true, allowColon: false) && value.Length <= MaximumNameLength
            : IsDottedName(value, allowHyphen: true, allowDigitStart: false, allowColon: false);

    internal static bool IsWellKnownBusName(string value) => !value.StartsWith(':') && IsBusName(value);

    internal static bool IsMemberName(string value)
    {
        if (value.Length is 0 or > MaximumNameLength || char.IsAsciiDigit(value[0])) return false;
        foreach (char character in value)
            if (!IsNameCharacter(character)) return false;
        return true;
    }

    private static bool IsDottedName(string value, bool allowHyphen, bool allowDigitStart, bool allowColon)
    {
        if (value.Length is 0 or > MaximumNameLength) return false;
        int elements = 0;
        foreach (var element in value.Split('.'))
        {
            if (element.Length == 0 || !allowDigitStart && char.IsAsciiDigit(element[0])) return false;
            foreach (char character in element)
                if (!IsNameCharacter(character) && !(allowHyphen && character == '-') && !(allowColon && character == ':')) return false;
            elements++;
        }
        return elements >= 2;
    }

    private static bool IsNameCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';
}

internal static class DBusSignatures
{
    private const int MaximumLength = 255;
    private const int MaximumDepth = 32;

    internal static bool IsValid(string signature)
    {
        if (signature.Length > MaximumLength) return false;
        for (int index = 0; index < signature.Length;)
        {
            int length = CompleteTypeLength(signature, index, 0, 0);
            if (length <= 0) return false;
            index += length;
        }
        return true;
    }

    internal static bool IsSingleCompleteType(string signature) =>
        signature.Length is > 0 and <= MaximumLength && CompleteTypeLength(signature, 0, 0, 0) == signature.Length;

    internal static bool IsBasic(char code) => code is 'y' or 'b' or 'n' or 'q' or 'i' or 'u' or 'x' or 't' or 'd' or 's' or 'o' or 'g' or 'h';

    // The complete types of a valid signature, in order.
    internal static List<string> Split(string signature)
    {
        var types = new List<string>();
        for (int index = 0; index < signature.Length;)
        {
            int length = CompleteTypeLength(signature, index, 0, 0);
            if (length <= 0) throw new ArgumentException($"\"{signature}\" is not a D-Bus signature.", nameof(signature));
            types.Add(signature.Substring(index, length));
            index += length;
        }
        return types;
    }

    // The length of the complete type starting at index, or 0 when it is invalid.
    private static int CompleteTypeLength(string signature, int index, int arrayDepth, int structDepth)
    {
        if (index >= signature.Length) return 0;
        char code = signature[index];
        if (IsBasic(code) || code == 'v') return 1;
        if (code == 'a')
        {
            if (arrayDepth + 1 > MaximumDepth || index + 1 >= signature.Length) return 0;
            if (signature[index + 1] == '{')
            {
                // A dictionary entry: a basic key type and one complete value type, only directly inside an array.
                if (structDepth + 1 > MaximumDepth || index + 2 >= signature.Length || !IsBasic(signature[index + 2])) return 0;
                int value = CompleteTypeLength(signature, index + 3, arrayDepth + 1, structDepth + 1);
                if (value <= 0 || index + 3 + value >= signature.Length || signature[index + 3 + value] != '}') return 0;
                return 4 + value;
            }
            int element = CompleteTypeLength(signature, index + 1, arrayDepth + 1, structDepth);
            return element <= 0 ? 0 : 1 + element;
        }
        if (code == '(')
        {
            if (structDepth + 1 > MaximumDepth) return 0;
            int position = index + 1;
            int fields = 0;
            while (position < signature.Length && signature[position] != ')')
            {
                int field = CompleteTypeLength(signature, position, arrayDepth, structDepth + 1);
                if (field <= 0) return 0;
                position += field;
                fields++;
            }
            if (position >= signature.Length || fields == 0) return 0;
            return position + 1 - index;
        }
        return 0;
    }
}
