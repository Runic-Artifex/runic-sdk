using System;

namespace WebUIToolkit.DependencyNotices.Spdx;

/// <summary>Describes invalid SPDX expression syntax.</summary>
public sealed class SpdxParseException : FormatException
{
    /// <summary>Creates an SPDX parse exception.</summary>
    public SpdxParseException()
        : this("Invalid SPDX expression.", 0, "valid SPDX syntax")
    {
    }

    /// <summary>Creates an SPDX parse exception with a message.</summary>
    /// <param name="message">The error message.</param>
    public SpdxParseException(string message)
        : this(message, 0, "valid SPDX syntax")
    {
    }

    /// <summary>Creates an SPDX parse exception with a message and inner exception.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The exception that caused this exception.</param>
    public SpdxParseException(string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(message);
        Offset = 0;
        Expected = "valid SPDX syntax";
    }

    /// <summary>Creates an SPDX parse exception at a particular input offset.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="offset">The zero-based UTF-16 offset in the input.</param>
    /// <param name="expected">A description of the expected token or construct.</param>
    public SpdxParseException(string message, int offset, string expected)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        Offset = offset;
        Expected = expected;
    }

    /// <summary>Gets the zero-based UTF-16 offset at which parsing failed.</summary>
    public int Offset { get; }

    /// <summary>Gets a description of the expected token or construct.</summary>
    public string Expected { get; }
}

/// <summary>Parses SPDX license expressions without performing identifier-catalog validation.</summary>
public static class SpdxParser
{
    /// <summary>Parses an SPDX license expression.</summary>
    /// <param name="expression">The expression text to parse.</param>
    /// <returns>The original text, immutable syntax tree, and canonical text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is null.</exception>
    /// <exception cref="SpdxParseException">The expression has invalid syntax.</exception>
    public static SpdxExpression Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Parser parser = new(expression);
        return new SpdxExpression(expression, parser.Parse());
    }

    private enum TokenKind
    {
        End,
        Identifier,
        And,
        Or,
        With,
        OpenParenthesis,
        CloseParenthesis,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Offset);

    private sealed class Parser
    {
        private readonly string _text;
        private int _nextOffset;
        private Token _current;

        public Parser(string text)
        {
            _text = text;
            _current = ReadToken();
        }

        public SpdxExpressionNode Parse()
        {
            SpdxExpressionNode root = ParseOr();
            if (_current.Kind != TokenKind.End)
            {
                throw Error(_current.Offset, "end of expression");
            }

            return root;
        }

        private SpdxExpressionNode ParseOr()
        {
            SpdxExpressionNode left = ParseAnd();
            while (_current.Kind == TokenKind.Or)
            {
                MoveNext();
                left = new SpdxOrNode(left, ParseAnd());
            }

            return left;
        }

        private SpdxExpressionNode ParseAnd()
        {
            SpdxExpressionNode left = ParseWith();
            while (_current.Kind == TokenKind.And)
            {
                MoveNext();
                left = new SpdxAndNode(left, ParseWith());
            }

            return left;
        }

        private SpdxExpressionNode ParseWith()
        {
            bool isDirectIdentifier = _current.Kind == TokenKind.Identifier;
            SpdxExpressionNode primary = ParsePrimary();
            if (_current.Kind != TokenKind.With)
            {
                return primary;
            }

            if (!isDirectIdentifier || primary is not SpdxLicenseIdentifierNode license)
            {
                throw Error(_current.Offset, "AND, OR, or end of expression");
            }

            MoveNext();
            if (_current.Kind != TokenKind.Identifier
                || IsLicenseReference(_current.Text)
                || !IsIdentifierPart(_current.Text.AsSpan()))
            {
                throw Error(_current.Offset, "SPDX exception identifier");
            }

            string exceptionIdentifier = _current.Text;
            MoveNext();
            return new SpdxWithExceptionNode(license, exceptionIdentifier);
        }

        private SpdxExpressionNode ParsePrimary()
        {
            if (_current.Kind == TokenKind.Identifier)
            {
                SpdxLicenseIdentifierNode result = new(_current.Text);
                MoveNext();
                return result;
            }

            if (_current.Kind == TokenKind.OpenParenthesis)
            {
                MoveNext();
                SpdxExpressionNode result = ParseOr();
                if (_current.Kind != TokenKind.CloseParenthesis)
                {
                    throw Error(_current.Offset, "')'");
                }

                MoveNext();
                return result;
            }

            throw Error(_current.Offset, "license identifier or '('");
        }

        private void MoveNext() => _current = ReadToken();

        private Token ReadToken()
        {
            while (_nextOffset < _text.Length && char.IsWhiteSpace(_text[_nextOffset]))
            {
                _nextOffset++;
            }

            if (_nextOffset == _text.Length)
            {
                return new Token(TokenKind.End, string.Empty, _nextOffset);
            }

            int offset = _nextOffset;
            char current = _text[_nextOffset];
            if (current == '(')
            {
                _nextOffset++;
                return new Token(TokenKind.OpenParenthesis, "(", offset);
            }

            if (current == ')')
            {
                _nextOffset++;
                return new Token(TokenKind.CloseParenthesis, ")", offset);
            }

            while (_nextOffset < _text.Length
                && !char.IsWhiteSpace(_text[_nextOffset])
                && _text[_nextOffset] != '('
                && _text[_nextOffset] != ')')
            {
                _nextOffset++;
            }

            string text = _text[offset.._nextOffset];
            TokenKind keyword = text switch
            {
                "AND" => TokenKind.And,
                "OR" => TokenKind.Or,
                "WITH" => TokenKind.With,
                _ => TokenKind.Identifier,
            };

            if (keyword != TokenKind.Identifier)
            {
                return new Token(keyword, text, offset);
            }

            if (!IsValidLicenseIdentifier(text))
            {
                throw Error(offset, "valid SPDX license identifier");
            }

            return new Token(TokenKind.Identifier, text, offset);
        }

        private static SpdxParseException Error(int offset, string expected) =>
            new($"Invalid SPDX expression at offset {offset}: expected {expected}.", offset, expected);
    }

    private static bool IsValidLicenseIdentifier(string value)
    {
        const string documentPrefix = "DocumentRef-";
        const string licensePrefix = "LicenseRef-";

        if (value.StartsWith(documentPrefix, StringComparison.Ordinal))
        {
            int separator = value.IndexOf(":" + licensePrefix, documentPrefix.Length, StringComparison.Ordinal);
            return separator > documentPrefix.Length
                && separator + licensePrefix.Length + 1 < value.Length
                && value.IndexOf(':', separator + 1) < 0
                && IsReferencePart(value.AsSpan(documentPrefix.Length, separator - documentPrefix.Length))
                && IsReferencePart(value.AsSpan(separator + licensePrefix.Length + 1));
        }

        if (value.StartsWith(licensePrefix, StringComparison.Ordinal))
        {
            return value.Length > licensePrefix.Length
                && IsReferencePart(value.AsSpan(licensePrefix.Length));
        }

        if (value.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> identifier = value.AsSpan();
        if (identifier[^1] == '+')
        {
            identifier = identifier[..^1];
        }

        return IsIdentifierPart(identifier);
    }

    private static bool IsLicenseReference(string value) =>
        value.StartsWith("LicenseRef-", StringComparison.Ordinal)
        || value.StartsWith("DocumentRef-", StringComparison.Ordinal);

    private static bool IsReferencePart(ReadOnlySpan<char> value)
        => IsIdentifierPart(value);

    private static bool IsIdentifierPart(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (!IsAsciiLetterOrDigit(character) && character != '-' && character != '.')
            {
                return false;
            }
        }

        return value.Length > 0;
    }

    private static bool IsAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z'
        or >= 'A' and <= 'Z'
        or >= '0' and <= '9';
}
