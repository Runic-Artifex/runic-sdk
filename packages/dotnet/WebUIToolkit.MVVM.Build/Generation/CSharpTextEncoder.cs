using System;
using System.Collections.Generic;
using System.Text;

namespace WebUIToolkit.MVVM.Build.Generation;

internal static class CSharpTextEncoder
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
        // Contextual keywords are escaped as well: a metadata name may be placed in a
        // context where the parser treats one of these spellings as a keyword.
        "add", "alias", "and", "ascending", "async", "await", "by", "descending", "dynamic", "equals",
        "file", "from", "get", "global", "group", "init", "into", "join", "let", "managed", "nameof",
        "nint", "not", "notnull", "nuint", "on", "or", "orderby", "partial", "record", "remove",
        "required", "scoped", "select", "set", "unmanaged", "value", "var", "when", "where", "with", "yield",
    };

    internal static string EscapeIdentifier(string identifier) =>
        Keywords.Contains(identifier) ? "@" + identifier : identifier;

    internal static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || !IsIdentifierStart(value[0]))
        {
            return false;
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (!IsIdentifierPart(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsNamespace(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string[] segments = value.Split('.');
        for (int index = 0; index < segments.Length; index++)
        {
            if (!IsIdentifier(segments[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal static string EscapeNamespace(string value)
    {
        string[] segments = value.Split('.');
        for (int index = 0; index < segments.Length; index++)
        {
            segments[index] = EscapeIdentifier(segments[index]);
        }

        return string.Join(".", segments);
    }

    internal static string StringLiteral(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character is >= ' ' and <= '~')
                    {
                        builder.Append(character);
                    }
                    else
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    internal static void ValidateUnicode(string value, string parameterName)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException("Text contains an unpaired UTF-16 surrogate.", parameterName);
                }

                index++;
            }
            else if (char.IsLowSurrogate(character))
            {
                throw new ArgumentException("Text contains an unpaired UTF-16 surrogate.", parameterName);
            }
        }
    }

    private static bool IsIdentifierStart(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsIdentifierPart(char value) =>
        IsIdentifierStart(value) || value is >= '0' and <= '9';
}
