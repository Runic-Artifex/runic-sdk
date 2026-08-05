namespace RunicToolkit.MVVM.Build.Compiler;

internal enum BindingTokenKind
{
    EndOfFile,
    Word,
    String,
    LeftBrace,
    RightBrace,
    Colon,
    Semicolon,
    LessThan,
    GreaterThan,
    Comma,
    Question,
    LeftBracket,
    RightBracket,
    FatArrow,
    ThinArrow,
}

internal readonly record struct BindingToken(
    BindingTokenKind Kind,
    string Text,
    string Value,
    BindingSourceSpan Span);

internal sealed class BindingLexResult(
    IReadOnlyList<BindingToken> tokens,
    IReadOnlyList<BindingDiagnostic> diagnostics)
{
    internal IReadOnlyList<BindingToken> Tokens { get; } = tokens;
    internal IReadOnlyList<BindingDiagnostic> Diagnostics { get; } = diagnostics;
}

internal sealed class BindingLexer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _source;
    private readonly string _path;
    private readonly BindingCompilerLimits _limits;
    private readonly BindingDiagnosticBag _diagnostics;
    private readonly List<BindingToken> _tokens = [];
    private int _offset;
    private int _line;
    private int _column;
    private bool _tokenLimitReported;

    private BindingLexer(string source, string path, BindingCompilerLimits limits)
    {
        _source = source;
        _path = path;
        _limits = limits;
        _diagnostics = new BindingDiagnosticBag(limits);
    }

    internal static BindingLexResult Lex(string source, string path, BindingCompilerLimits limits)
    {
        var lexer = new BindingLexer(source, path, limits);
        lexer.Run();
        return new BindingLexResult(
            Array.AsReadOnly(lexer._tokens.ToArray()),
            BindingDiagnosticBag.Sort(lexer._diagnostics.Items));
    }

    private void Run()
    {
        BindingSourcePosition origin = Position;
        if (_source.Length > _limits.MaxSourceCharacters)
        {
            _diagnostics.Add(
                BindingDiagnosticIds.SourceLimitExceeded,
                $"The source contains {_source.Length} UTF-16 characters; the configured limit is {_limits.MaxSourceCharacters}.",
                new BindingSourceSpan(_path, origin, PositionAtEnd()));
            AddEndOfFile(origin);
            return;
        }

        try
        {
            int byteCount = StrictUtf8.GetByteCount(_source);
            if (byteCount > _limits.MaxSourceUtf8Bytes)
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.SourceLimitExceeded,
                    $"The UTF-8 source contains {byteCount} bytes; the configured limit is {_limits.MaxSourceUtf8Bytes}.",
                    new BindingSourceSpan(_path, origin, PositionAtEnd()));
                AddEndOfFile(origin);
                return;
            }
        }
        catch (EncoderFallbackException)
        {
            int invalidOffset = FindInvalidSurrogate();
            BindingSourcePosition start = PositionForOffset(invalidOffset);
            BindingSourcePosition end = PositionForOffset(Math.Min(invalidOffset + 1, _source.Length));
            _diagnostics.Add(
                BindingDiagnosticIds.InvalidEscape,
                "The source contains an unpaired UTF-16 surrogate.",
                new BindingSourceSpan(_path, start, end));
            AddEndOfFile(PositionAtEnd());
            return;
        }

        while (_offset < _source.Length && !_tokenLimitReported)
        {
            SkipTrivia();
            if (_offset >= _source.Length || _tokenLimitReported)
            {
                break;
            }

            LexToken();
        }

        AddEndOfFile(Position);
    }

    private void SkipTrivia()
    {
        while (_offset < _source.Length)
        {
            char current = _source[_offset];
            if (char.IsWhiteSpace(current))
            {
                Advance();
                continue;
            }

            if (current == '/' && Peek(1) == '/')
            {
                Advance();
                Advance();
                while (_offset < _source.Length && _source[_offset] is not '\r' and not '\n')
                {
                    Advance();
                }

                continue;
            }

            if (current == '/' && Peek(1) == '*')
            {
                BindingSourcePosition start = Position;
                Advance();
                Advance();
                bool closed = false;
                while (_offset < _source.Length)
                {
                    if (_source[_offset] == '*' && Peek(1) == '/')
                    {
                        Advance();
                        Advance();
                        closed = true;
                        break;
                    }

                    Advance();
                }

                if (!closed)
                {
                    _diagnostics.Add(
                        BindingDiagnosticIds.UnterminatedText,
                        "The block comment is not terminated.",
                        new BindingSourceSpan(_path, start, Position));
                }

                continue;
            }

            break;
        }
    }

    private void LexToken()
    {
        BindingSourcePosition start = Position;
        int startOffset = _offset;
        char current = _source[_offset];
        switch (current)
        {
            case '{': AddSingle(BindingTokenKind.LeftBrace, start, startOffset); return;
            case '}': AddSingle(BindingTokenKind.RightBrace, start, startOffset); return;
            case ':': AddSingle(BindingTokenKind.Colon, start, startOffset); return;
            case ';': AddSingle(BindingTokenKind.Semicolon, start, startOffset); return;
            case '<': AddSingle(BindingTokenKind.LessThan, start, startOffset); return;
            case '>': AddSingle(BindingTokenKind.GreaterThan, start, startOffset); return;
            case ',': AddSingle(BindingTokenKind.Comma, start, startOffset); return;
            case '?': AddSingle(BindingTokenKind.Question, start, startOffset); return;
            case '[': AddSingle(BindingTokenKind.LeftBracket, start, startOffset); return;
            case ']': AddSingle(BindingTokenKind.RightBracket, start, startOffset); return;
            case '=' when Peek(1) == '>': AddDouble(BindingTokenKind.FatArrow, start, startOffset); return;
            case '-' when Peek(1) == '>': AddDouble(BindingTokenKind.ThinArrow, start, startOffset); return;
            case '"': LexString(start, startOffset); return;
        }

        if (!IsDelimiter(current))
        {
            do
            {
                Advance();
            }
            while (_offset < _source.Length &&
                !char.IsWhiteSpace(_source[_offset]) &&
                !IsDelimiter(_source[_offset]) &&
                !(_source[_offset] == '/' && Peek(1) is '/' or '*'));

            string text = _source[startOffset.._offset];
            AddToken(BindingTokenKind.Word, text, text, start, Position);
            return;
        }

        Advance();
        _diagnostics.Add(
            BindingDiagnosticIds.UnexpectedCharacter,
            $"Unexpected character U+{(int)current:X4}.",
            new BindingSourceSpan(_path, start, Position));
    }

    private void LexString(BindingSourcePosition start, int startOffset)
    {
        Advance();
        var value = new StringBuilder();
        bool closed = false;
        while (_offset < _source.Length)
        {
            char current = _source[_offset];
            if (current == '"')
            {
                Advance();
                closed = true;
                break;
            }

            if (current is '\r' or '\n')
            {
                break;
            }

            if (current != '\\')
            {
                value.Append(current);
                Advance();
                continue;
            }

            BindingSourcePosition escapeStart = Position;
            Advance();
            if (_offset >= _source.Length)
            {
                break;
            }

            char escape = _source[_offset];
            Advance();
            switch (escape)
            {
                case '"': value.Append('"'); break;
                case '\\': value.Append('\\'); break;
                case 'n': value.Append('\n'); break;
                case 'r': value.Append('\r'); break;
                case 't': value.Append('\t'); break;
                case 'u':
                    if (!TryReadUnicodeEscape(value))
                    {
                        _diagnostics.Add(
                            BindingDiagnosticIds.InvalidEscape,
                            "A Unicode escape must contain exactly four hexadecimal digits.",
                            new BindingSourceSpan(_path, escapeStart, Position));
                    }

                    break;
                default:
                    _diagnostics.Add(
                        BindingDiagnosticIds.InvalidEscape,
                        $"The escape sequence '\\{escape}' is not supported.",
                        new BindingSourceSpan(_path, escapeStart, Position));
                    break;
            }
        }

        if (!closed)
        {
            _diagnostics.Add(
                BindingDiagnosticIds.UnterminatedText,
                "The string literal is not terminated.",
                new BindingSourceSpan(_path, start, Position));
        }

        string decoded = value.ToString();
        try
        {
            int byteCount = StrictUtf8.GetByteCount(decoded);
            if (byteCount > _limits.MaxStringUtf8Bytes)
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.SourceLimitExceeded,
                    $"The decoded string contains {byteCount} UTF-8 bytes; the configured limit is {_limits.MaxStringUtf8Bytes}.",
                    new BindingSourceSpan(_path, start, Position));
            }
        }
        catch (EncoderFallbackException)
        {
            _diagnostics.Add(
                BindingDiagnosticIds.InvalidEscape,
                "The string literal contains an unpaired UTF-16 surrogate.",
                new BindingSourceSpan(_path, start, Position));
        }

        AddToken(BindingTokenKind.String, _source[startOffset.._offset], decoded, start, Position);
    }

    private bool TryReadUnicodeEscape(StringBuilder value)
    {
        if (_source.Length - _offset < 4)
        {
            while (_offset < _source.Length && _source[_offset] is not '"' and not '\r' and not '\n')
            {
                Advance();
            }

            return false;
        }

        int scalar = 0;
        for (int index = 0; index < 4; index++)
        {
            int hex = HexValue(_source[_offset]);
            if (hex < 0)
            {
                Advance();
                return false;
            }

            scalar = (scalar << 4) | hex;
            Advance();
        }

        value.Append((char)scalar);
        return true;
    }

    private void AddSingle(BindingTokenKind kind, BindingSourcePosition start, int startOffset)
    {
        Advance();
        string text = _source[startOffset.._offset];
        AddToken(kind, text, text, start, Position);
    }

    private void AddDouble(BindingTokenKind kind, BindingSourcePosition start, int startOffset)
    {
        Advance();
        Advance();
        string text = _source[startOffset.._offset];
        AddToken(kind, text, text, start, Position);
    }

    private void AddToken(BindingTokenKind kind, string text, string value, BindingSourcePosition start, BindingSourcePosition end)
    {
        if (_tokens.Count >= _limits.MaxTokens)
        {
            _diagnostics.Add(
                BindingDiagnosticIds.TokenLimitExceeded,
                $"The token count exceeds the configured limit of {_limits.MaxTokens}.",
                new BindingSourceSpan(_path, start, end));
            _tokenLimitReported = true;
            return;
        }

        _tokens.Add(new BindingToken(kind, text, value, new BindingSourceSpan(_path, start, end)));
    }

    private void AddEndOfFile(BindingSourcePosition position) =>
        _tokens.Add(new BindingToken(
            BindingTokenKind.EndOfFile,
            string.Empty,
            string.Empty,
            new BindingSourceSpan(_path, position, position)));

    private char Peek(int distance)
    {
        int index = _offset + distance;
        return index < _source.Length ? _source[index] : '\0';
    }

    private void Advance()
    {
        char current = _source[_offset];
        char previous = _offset == 0 ? '\0' : _source[_offset - 1];
        _offset++;
        if (current == '\r')
        {
            _line++;
            _column = 0;
        }
        else if (current == '\n')
        {
            if (previous != '\r')
            {
                _line++;
            }

            _column = 0;
        }
        else
        {
            _column++;
        }
    }

    private BindingSourcePosition Position => new(_offset, _line, _column);

    private BindingSourcePosition PositionAtEnd() => PositionForOffset(_source.Length);

    private BindingSourcePosition PositionForOffset(int target)
    {
        int line = 0;
        int column = 0;
        for (int index = 0; index < target; index++)
        {
            if (_source[index] == '\r')
            {
                line++;
                column = 0;
            }
            else if (_source[index] == '\n')
            {
                if (index == 0 || _source[index - 1] != '\r')
                {
                    line++;
                }

                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new BindingSourcePosition(target, line, column);
    }

    private int FindInvalidSurrogate()
    {
        for (int index = 0; index < _source.Length; index++)
        {
            char current = _source[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 < _source.Length && char.IsLowSurrogate(_source[index + 1]))
                {
                    index++;
                    continue;
                }

                return index;
            }

            if (char.IsLowSurrogate(current))
            {
                return index;
            }
        }

        return 0;
    }

    private static bool IsDelimiter(char value) => value is
        '{' or '}' or ':' or ';' or '<' or '>' or ',' or '?' or '[' or ']' or '"' or '=' ||
        (value == '-' /* potential thin arrow; ordinary dashes remain inside words */);

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
