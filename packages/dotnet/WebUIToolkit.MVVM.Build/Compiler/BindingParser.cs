namespace WebUIToolkit.MVVM.Build.Compiler;

/// <summary>Parses the closed, declarative <c>.wutmvvm</c> binding language.</summary>
public static class BindingParser
{
    /// <summary>Parses one source file into a recoverable immutable syntax tree.</summary>
    /// <param name="source">Binding language source text.</param>
    /// <param name="logicalPath">A deterministic logical path used only in diagnostics.</param>
    /// <param name="limits">Optional lower resource limits, primarily for controlled hosts and tests.</param>
    /// <returns>The syntax tree and deterministic exact-span diagnostics.</returns>
    public static BindingParseResult Parse(
        string source,
        string logicalPath = "binding.wutmvvm",
        BindingCompilerLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalPath);
        ValidateLogicalPath(logicalPath);
        limits ??= BindingCompilerLimits.Default;
        limits.Validate();

        BindingLexResult lex = BindingLexer.Lex(source, logicalPath, limits);
        if (lex.Diagnostics.Any(static diagnostic => diagnostic.Id == BindingDiagnosticIds.SourceLimitExceeded) &&
            lex.Tokens.Count == 1)
        {
            BindingSourceSpan emptySpan = lex.Tokens[0].Span;
            return new BindingParseResult(
                new BindingDocumentSyntax(null, Array.Empty<BindingContractSyntax>(), emptySpan),
                lex.Diagnostics,
                limits);
        }

        return new Parser(lex, limits).ParseDocument();
    }

    private static void ValidateLogicalPath(string logicalPath)
    {
        if (logicalPath.Length > 1_024 ||
            logicalPath[0] == '/' ||
            logicalPath.Contains('\\') ||
            logicalPath.Contains(':') ||
            logicalPath.Any(char.IsControl))
        {
            throw new ArgumentException("The logical path must be a bounded project-relative slash path.", nameof(logicalPath));
        }

        string[] segments = logicalPath.Split('/');
        if (segments.Any(static segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new ArgumentException("The logical path cannot contain empty, current-directory, or parent-directory segments.", nameof(logicalPath));
        }
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<BindingToken> _tokens;
        private readonly BindingCompilerLimits _limits;
        private readonly BindingDiagnosticBag _diagnostics;
        private int _index;

        internal Parser(BindingLexResult lex, BindingCompilerLimits limits)
        {
            _tokens = lex.Tokens;
            _limits = limits;
            _diagnostics = new BindingDiagnosticBag(limits);
            _diagnostics.AddRange(lex.Diagnostics);
        }

        internal BindingParseResult ParseDocument()
        {
            BindingSourcePosition start = Current.Span.Start;
            BindingProtocolSyntax? protocol = null;
            var contracts = new List<BindingContractSyntax>();

            if (IsWord("protocol"))
            {
                protocol = ParseProtocol();
            }
            else
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.ProtocolRequired,
                    "A binding source must begin with 'protocol webuitoolkit.mvvm/1;'.",
                    Current.Span);
            }

            while (Current.Kind != BindingTokenKind.EndOfFile)
            {
                if (!IsWord("contract"))
                {
                    Unexpected("a contract declaration");
                    SynchronizeTopLevel();
                    continue;
                }

                if (contracts.Count >= _limits.MaxContracts)
                {
                    _diagnostics.Add(
                        BindingDiagnosticIds.ContractLimitExceeded,
                        $"The contract count exceeds the configured limit of {_limits.MaxContracts}.",
                        Current.Span);
                    SkipContract();
                    continue;
                }

                contracts.Add(ParseContract());
            }

            if (contracts.Count == 0)
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.ContractRequired,
                    "A binding source must declare at least one contract.",
                    Current.Span);
            }

            var document = new BindingDocumentSyntax(
                protocol,
                Array.AsReadOnly(contracts.ToArray()),
                new BindingSourceSpan(Current.Span.LogicalPath, start, Current.Span.End));
            return new BindingParseResult(document, BindingDiagnosticBag.Sort(_diagnostics.Items), _limits);
        }

        private BindingProtocolSyntax ParseProtocol()
        {
            BindingToken keyword = Take();
            BindingToken identity = ExpectValue("a protocol identity");
            BindingToken end = Expect(BindingTokenKind.Semicolon, "';'");
            return new BindingProtocolSyntax(
                identity.Value,
                identity.Span,
                SpanFrom(keyword.Span, end.Span));
        }

        private BindingContractSyntax ParseContract()
        {
            BindingToken keyword = Take();
            BindingToken name = ExpectValue("a contract identity");
            if (!IsWord("model") && !IsWord("for"))
            {
                Unexpected("'model'");
            }
            else
            {
                Take();
            }

            ParsedType modelType = ParseType("a CLR model type");
            Expect(BindingTokenKind.LeftBrace, "'{'");
            var members = new List<BindingMemberSyntax>();
            while (Current.Kind is not BindingTokenKind.RightBrace and not BindingTokenKind.EndOfFile)
            {
                if (!IsMemberKeyword(Current))
                {
                    Unexpected("a property, collection, command, or validation declaration");
                    SynchronizeMember();
                    continue;
                }

                BindingToken memberKeyword = Current;
                BindingMemberSyntax? member = ParseMember();
                if (member is null)
                {
                    SynchronizeMember();
                    continue;
                }

                if (members.Count >= _limits.MaxMembersPerContract)
                {
                    _diagnostics.Add(
                        BindingDiagnosticIds.MemberLimitExceeded,
                        $"The member count exceeds the configured limit of {_limits.MaxMembersPerContract}.",
                        memberKeyword.Span);
                }
                else
                {
                    members.Add(member);
                }
            }

            BindingToken close = Expect(BindingTokenKind.RightBrace, "'}'");
            return new BindingContractSyntax(
                name.Value,
                name.Span,
                modelType.Text,
                modelType.Span,
                Array.AsReadOnly(members.ToArray()),
                SpanFrom(keyword.Span, close.Span));
        }

        private BindingMemberSyntax? ParseMember()
        {
            if (IsWord("property"))
            {
                return ParseProperty();
            }

            if (IsWord("collection"))
            {
                return ParseCollection();
            }

            if (IsWord("command"))
            {
                return ParseCommand();
            }

            if (IsWord("validation"))
            {
                return ParseValidation();
            }

            return null;
        }

        private PropertyBindingSyntax ParseProperty()
        {
            BindingToken keyword = Take();
            (int? id, BindingToken idToken) = ParseMemberId();
            BindingToken name = ExpectWord("an ASCII wire member name");
            Expect(BindingTokenKind.Colon, "':'");
            ParsedType valueType = ParseType("a property CLR type");
            Expect(BindingTokenKind.FatArrow, "'=>'");
            BindingToken source = ExpectWord("a direct CLR property name");

            BindingAccess? access = null;
            if (IsWord("readonly"))
            {
                Take();
                access = BindingAccess.ReadOnly;
            }
            else if (IsWord("readwrite"))
            {
                Take();
                access = BindingAccess.ReadWrite;
            }
            else
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.InvalidMemberOption,
                    "A property must declare exactly one access mode: 'readonly' or 'readwrite'.",
                    Current.Span);
            }

            BindingToken end = Expect(BindingTokenKind.Semicolon, "';'");
            return new PropertyBindingSyntax(
                id, idToken.Span, name.Value, name.Span, valueType.Text, valueType.Span,
                source.Value, source.Span, access, SpanFrom(keyword.Span, end.Span));
        }

        private CollectionBindingSyntax ParseCollection()
        {
            BindingToken keyword = Take();
            (int? id, BindingToken idToken) = ParseMemberId();
            BindingToken name = ExpectWord("an ASCII wire member name");
            Expect(BindingTokenKind.Colon, "':'");
            ParsedType itemType = ParseType("a collection item CLR type");
            Expect(BindingTokenKind.FatArrow, "'=>'");
            BindingToken source = ExpectWord("a direct CLR collection property name");
            if (IsWord("readonly"))
            {
                Take();
            }
            else if (IsWord("readwrite"))
            {
                BindingToken option = Take();
                _diagnostics.Add(
                    BindingDiagnosticIds.InvalidMemberOption,
                    "Collections are read-only projections in binding language version 1.",
                    option.Span);
            }

            BindingToken end = Expect(BindingTokenKind.Semicolon, "';'");
            return new CollectionBindingSyntax(
                id, idToken.Span, name.Value, name.Span, itemType.Text, itemType.Span,
                source.Value, source.Span, SpanFrom(keyword.Span, end.Span));
        }

        private CommandBindingSyntax ParseCommand()
        {
            BindingToken keyword = Take();
            (int? id, BindingToken idToken) = ParseMemberId();
            BindingToken name = ExpectWord("an ASCII wire member name");
            Expect(BindingTokenKind.Colon, "':'");
            ParsedType parameterType = ParseType("a command argument type or 'none'");
            Expect(BindingTokenKind.ThinArrow, "'->'");
            ParsedType resultType = ParseType("a command result type or 'none'");
            Expect(BindingTokenKind.FatArrow, "'=>'");
            BindingToken source = ExpectWord("a direct CLR command name");
            BindingToken end = Expect(BindingTokenKind.Semicolon, "';'");
            return new CommandBindingSyntax(
                id, idToken.Span, name.Value, name.Span, parameterType.Text, parameterType.Span,
                resultType.Text, resultType.Span, source.Value, source.Span,
                SpanFrom(keyword.Span, end.Span));
        }

        private ValidationBindingSyntax ParseValidation()
        {
            BindingToken keyword = Take();
            BindingToken name = ExpectWord("an ASCII validation member name");
            if (!IsWord("for"))
            {
                Unexpected("'for'");
            }
            else
            {
                Take();
            }

            BindingToken target = ExpectWord("a property or collection wire name");
            Expect(BindingTokenKind.FatArrow, "'=>'");
            BindingToken source = ExpectWord("a direct CLR validation member name");
            BindingToken end = Expect(BindingTokenKind.Semicolon, "';'");
            BindingSourceSpan missingId = new(keyword.Span.LogicalPath, keyword.Span.End, keyword.Span.End);
            return new ValidationBindingSyntax(
                null, missingId, name.Value, name.Span, target.Value, target.Span,
                source.Value, source.Span, SpanFrom(keyword.Span, end.Span));
        }

        private (int? Id, BindingToken Token) ParseMemberId()
        {
            BindingToken token = ExpectWord("a positive Int32 member ID");
            int value = 0;
            bool valid = token.Value.Length > 0;
            foreach (char character in token.Value)
            {
                if (character is < '0' or > '9')
                {
                    valid = false;
                    break;
                }

                int digit = character - '0';
                if (value > (int.MaxValue - digit) / 10)
                {
                    valid = false;
                    break;
                }

                value = (value * 10) + digit;
            }

            if (!valid || value <= 0)
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.InvalidMemberId,
                    $"Member ID '{token.Value}' must be an integer from 1 through {int.MaxValue}.",
                    token.Span);
                return (null, token);
            }

            return (value, token);
        }

        private ParsedType ParseType(string expected)
        {
            BindingToken first = Current;
            var builder = new StringBuilder();
            if (!ParseTypeCore(builder, 0))
            {
                Unexpected(expected);
                return new ParsedType(string.Empty, first.Span);
            }

            BindingSourceSpan span = new(first.Span.LogicalPath, first.Span.Start, Previous.Span.End);
            if (builder.Length > _limits.MaxTypeCharacters)
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.InvalidTypeName,
                    $"The CLR type spelling exceeds the configured limit of {_limits.MaxTypeCharacters} characters.",
                    span);
            }

            return new ParsedType(builder.ToString(), span);
        }

        private bool ParseTypeCore(StringBuilder builder, int depth)
        {
            if (depth > _limits.MaxNestingDepth)
            {
                _diagnostics.Add(
                    BindingDiagnosticIds.NestingLimitExceeded,
                    $"The type nesting exceeds the configured limit of {_limits.MaxNestingDepth}.",
                    Current.Span);
                return false;
            }

            if (Current.Kind != BindingTokenKind.Word)
            {
                return false;
            }

            builder.Append(Take().Value);
            if (Previous.Value == "global" && Current.Kind == BindingTokenKind.Colon)
            {
                builder.Append(Take().Text);
                if (Current.Kind != BindingTokenKind.Colon)
                {
                    Unexpected("the second ':' in 'global::'");
                    return false;
                }

                builder.Append(Take().Text);
                if (Current.Kind != BindingTokenKind.Word)
                {
                    Unexpected("a qualified CLR type name");
                    return false;
                }

                builder.Append(Take().Value);
            }

            if (Current.Kind == BindingTokenKind.LessThan)
            {
                builder.Append(Take().Text);
                if (!ParseTypeCore(builder, depth + 1))
                {
                    return false;
                }

                while (Current.Kind == BindingTokenKind.Comma)
                {
                    builder.Append(Take().Text);
                    if (!ParseTypeCore(builder, depth + 1))
                    {
                        return false;
                    }
                }

                if (Current.Kind != BindingTokenKind.GreaterThan)
                {
                    Unexpected("'>'");
                    return false;
                }

                builder.Append(Take().Text);
            }

            while (true)
            {
                if (Current.Kind == BindingTokenKind.Question)
                {
                    builder.Append(Take().Text);
                    continue;
                }

                if (Current.Kind == BindingTokenKind.LeftBracket)
                {
                    builder.Append(Take().Text);
                    if (Current.Kind != BindingTokenKind.RightBracket)
                    {
                        Unexpected("']'");
                        return false;
                    }

                    builder.Append(Take().Text);
                    continue;
                }

                break;
            }

            return true;
        }

        private BindingToken ExpectValue(string expected)
        {
            if (Current.Kind is BindingTokenKind.Word or BindingTokenKind.String)
            {
                return Take();
            }

            Unexpected(expected);
            return MissingToken();
        }

        private BindingToken ExpectWord(string expected)
        {
            if (Current.Kind == BindingTokenKind.Word)
            {
                return Take();
            }

            Unexpected(expected);
            return MissingToken();
        }

        private BindingToken Expect(BindingTokenKind kind, string expected)
        {
            if (Current.Kind == kind)
            {
                return Take();
            }

            Unexpected(expected);
            return MissingToken(kind);
        }

        private void Unexpected(string expected) =>
            _diagnostics.Add(
                BindingDiagnosticIds.UnexpectedToken,
                $"Expected {expected}, but found {Display(Current)}.",
                Current.Span);

        private void SynchronizeTopLevel()
        {
            if (Current.Kind != BindingTokenKind.EndOfFile)
            {
                Take();
            }

            while (Current.Kind != BindingTokenKind.EndOfFile && !IsWord("contract"))
            {
                Take();
            }
        }

        private void SynchronizeMember()
        {
            if (Current.Kind != BindingTokenKind.EndOfFile)
            {
                Take();
            }

            while (Current.Kind is not BindingTokenKind.EndOfFile and not BindingTokenKind.RightBrace)
            {
                if (IsMemberKeyword(Current))
                {
                    return;
                }

                if (Current.Kind == BindingTokenKind.Semicolon)
                {
                    Take();
                    return;
                }

                Take();
            }
        }

        private void SkipContract()
        {
            int braces = 0;
            do
            {
                if (Current.Kind == BindingTokenKind.LeftBrace)
                {
                    braces++;
                }
                else if (Current.Kind == BindingTokenKind.RightBrace && braces > 0)
                {
                    braces--;
                }

                Take();
            }
            while (Current.Kind != BindingTokenKind.EndOfFile && (braces > 0 || !IsWord("contract")));
        }

        private BindingToken MissingToken(BindingTokenKind kind = BindingTokenKind.Word)
        {
            BindingSourcePosition position = Current.Span.Start;
            return new BindingToken(
                kind,
                string.Empty,
                string.Empty,
                new BindingSourceSpan(Current.Span.LogicalPath, position, position));
        }

        private BindingToken Take()
        {
            BindingToken token = Current;
            if (_index < _tokens.Count - 1)
            {
                _index++;
            }

            return token;
        }

        private bool IsWord(string value) =>
            Current.Kind == BindingTokenKind.Word && string.Equals(Current.Value, value, StringComparison.Ordinal);

        private BindingToken Current => _tokens[_index];
        private BindingToken Previous => _tokens[Math.Max(0, _index - 1)];

        private static bool IsMemberKeyword(BindingToken token) =>
            token.Kind == BindingTokenKind.Word && token.Value is "property" or "collection" or "command" or "validation";

        private static string Display(BindingToken token) => token.Kind == BindingTokenKind.EndOfFile
            ? "end of file"
            : $"'{token.Text}'";

        private static BindingSourceSpan SpanFrom(BindingSourceSpan start, BindingSourceSpan end) =>
            new(start.LogicalPath, start.Start, end.End);

        private readonly record struct ParsedType(string Text, BindingSourceSpan Span);
    }
}
