namespace RunicToolkit.MVVM.Build.Compiler;

/// <summary>The immutable, canonical semantic model for one or more binding contracts.</summary>
public sealed class BindingSemanticModel
{
    internal BindingSemanticModel(string protocolIdentity, IReadOnlyList<BindingContractModel> contracts)
    {
        ProtocolIdentity = protocolIdentity;
        Contracts = contracts;
    }

    /// <summary>Gets the exact runtime ABI identity.</summary>
    public string ProtocolIdentity { get; }

    /// <summary>Gets contracts in ordinal identity order.</summary>
    public IReadOnlyList<BindingContractModel> Contracts { get; }
}

/// <summary>A validated contract and its deterministic generated dispatch surface.</summary>
public sealed class BindingContractModel
{
    internal BindingContractModel(
        string name,
        string modelType,
        IReadOnlyList<BindingMemberModel> members,
        BindingSourceSpan nameSpan,
        BindingSourceSpan span)
    {
        Name = name;
        ModelType = modelType;
        Members = members;
        NameSpan = nameSpan;
        Span = span;
    }

    /// <summary>Gets the logical runtime contract identity.</summary>
    public string Name { get; }

    /// <summary>Gets the validated CLR model type spelling.</summary>
    public string ModelType { get; }

    /// <summary>Gets members ordered by ID, kind, and ordinal wire name.</summary>
    public IReadOnlyList<BindingMemberModel> Members { get; }

    /// <summary>Gets the exact logical contract identity token span.</summary>
    public BindingSourceSpan NameSpan { get; }

    /// <summary>Gets the contract declaration span.</summary>
    public BindingSourceSpan Span { get; }
}

/// <summary>A validated binding member ready for deterministic code generation.</summary>
public sealed class BindingMemberModel
{
    internal BindingMemberModel(
        int id,
        string name,
        BindingMemberKind kind,
        string sourceMember,
        string? valueType,
        string? parameterType,
        string? resultType,
        BindingAccess access,
        string? validationTarget,
        BindingSourceSpan span)
    {
        Id = id;
        Name = name;
        Kind = kind;
        SourceMember = sourceMember;
        ValueType = valueType;
        ParameterType = parameterType;
        ResultType = resultType;
        Access = access;
        ValidationTarget = validationTarget;
        Span = span;
    }

    /// <summary>Gets the positive stable protocol member ID.</summary>
    /// <remarks>A validation descriptor reuses the ID of its target property or collection.</remarks>
    public int Id { get; }

    /// <summary>Gets the ASCII wire member name.</summary>
    public string Name { get; }

    /// <summary>Gets the closed member kind.</summary>
    public BindingMemberKind Kind { get; }

    /// <summary>Gets the direct CLR member spelling.</summary>
    public string SourceMember { get; }

    /// <summary>Gets a property value or collection item CLR type, when applicable.</summary>
    public string? ValueType { get; }

    /// <summary>Gets the command argument CLR type, or <see langword="null"/> for an absent wire argument.</summary>
    public string? ParameterType { get; }

    /// <summary>Gets the command result CLR type, or <see langword="null"/> for no wire result.</summary>
    public string? ResultType { get; }

    /// <summary>Gets state-member access; non-property members are read-only.</summary>
    public BindingAccess Access { get; }

    /// <summary>Gets the validation target's wire name, when this is a validation descriptor.</summary>
    public string? ValidationTarget { get; }

    /// <summary>Gets the source declaration span.</summary>
    public BindingSourceSpan Span { get; }
}

/// <summary>The result of deterministic semantic validation.</summary>
public sealed class BindingSemanticResult
{
    internal BindingSemanticResult(BindingSemanticModel? model, IReadOnlyList<BindingDiagnostic> diagnostics)
    {
        Model = model;
        Diagnostics = diagnostics;
    }

    /// <summary>Gets the canonical model, or <see langword="null"/> when any error was emitted.</summary>
    public BindingSemanticModel? Model { get; }

    /// <summary>Gets all lexical, parse, and semantic diagnostics in deterministic order.</summary>
    public IReadOnlyList<BindingDiagnostic> Diagnostics { get; }

    /// <summary>Gets whether compilation emitted an error.</summary>
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == BindingDiagnosticSeverity.Error);
}

/// <summary>Validates parsed declarations and creates canonical dispatch models.</summary>
public static class BindingSemanticAnalyzer
{
    private const string SupportedProtocol = global::RunicToolkit.MVVM.MvvmProtocol.Identity;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Analyzes a recovered syntax tree without loading or reflecting over consumer assemblies.</summary>
    public static BindingSemanticResult Analyze(BindingParseResult parseResult)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        var diagnostics = new BindingDiagnosticBag(parseResult.Limits);
        diagnostics.AddRange(parseResult.Diagnostics);
        BindingDocumentSyntax syntax = parseResult.Syntax;

        string protocolIdentity = syntax.Protocol?.Identity ?? string.Empty;
        if (syntax.Protocol is not null && !string.Equals(protocolIdentity, SupportedProtocol, StringComparison.Ordinal))
        {
            diagnostics.Add(
                BindingDiagnosticIds.ProtocolMismatch,
                $"The declared protocol identity is unsupported; expected '{SupportedProtocol}'.",
                syntax.Protocol.IdentitySpan);
        }

        var contracts = new List<BindingContractModel>();
        var contractNames = new Dictionary<string, BindingSourceSpan>(StringComparer.Ordinal);
        foreach (BindingContractSyntax contract in syntax.Contracts)
        {
            ValidateContractName(contract, diagnostics);
            ValidateType(contract.ModelType, contract.ModelTypeSpan, false, parseResult.Limits, diagnostics);
            if (!contractNames.TryAdd(contract.Name, contract.NameSpan))
            {
                diagnostics.Add(
                    BindingDiagnosticIds.DuplicateContract,
                    "The contract identity is declared more than once.",
                    contract.NameSpan,
                    contractNames[contract.Name]);
            }

            contracts.Add(AnalyzeContract(contract, parseResult.Limits, diagnostics));
        }

        IReadOnlyList<BindingDiagnostic> orderedDiagnostics = BindingDiagnosticBag.Sort(diagnostics.Items);
        if (orderedDiagnostics.Any(static diagnostic => diagnostic.Severity == BindingDiagnosticSeverity.Error))
        {
            return new BindingSemanticResult(null, orderedDiagnostics);
        }

        IReadOnlyList<BindingContractModel> orderedContracts = Array.AsReadOnly(contracts
            .OrderBy(static contract => contract.Name, StringComparer.Ordinal)
            .ToArray());
        return new BindingSemanticResult(
            new BindingSemanticModel(SupportedProtocol, orderedContracts),
            orderedDiagnostics);
    }

    private static BindingContractModel AnalyzeContract(
        BindingContractSyntax contract,
        BindingCompilerLimits limits,
        BindingDiagnosticBag diagnostics)
    {
        var models = new List<BindingMemberModel>();
        var ids = new Dictionary<(BindingMemberKind Kind, int Id), BindingSourceSpan>();
        var names = new Dictionary<string, BindingSourceSpan>(StringComparer.Ordinal);
        var targets = new Dictionary<string, (int Id, BindingMemberKind Kind)>(StringComparer.Ordinal);
        var validations = new List<ValidationBindingSyntax>();
        var validationIds = new Dictionary<int, BindingSourceSpan>();

        foreach (BindingMemberSyntax member in contract.Members)
        {
            ValidateIdentifier(member.Name, member.NameSpan, "wire member", limits, diagnostics);
            ValidateIdentifier(member.SourceMember, member.SourceMemberSpan, "CLR source member", limits, diagnostics);
            if (!names.TryAdd(member.Name, member.NameSpan))
            {
                diagnostics.Add(
                    BindingDiagnosticIds.DuplicateMemberName,
                    "The wire member name is declared more than once in this contract.",
                    member.NameSpan,
                    names[member.Name]);
            }

            if (member is ValidationBindingSyntax validation)
            {
                ValidateIdentifier(validation.TargetName, validation.TargetNameSpan, "validation target", limits, diagnostics);
                validations.Add(validation);
                continue;
            }

            if (member.Id is not int id)
            {
                continue;
            }

            var descriptorKey = (member.Kind, id);
            if (!ids.TryAdd(descriptorKey, member.IdSpan))
            {
                diagnostics.Add(
                    BindingDiagnosticIds.DuplicateMemberId,
                    $"Member ID '{id}' is declared more than once for kind '{member.Kind}' in this contract.",
                    member.IdSpan,
                    ids[descriptorKey]);
            }

            targets.TryAdd(member.Name, (id, member.Kind));
            switch (member)
            {
                case PropertyBindingSyntax property:
                    ValidateType(property.ValueType, property.ValueTypeSpan, false, limits, diagnostics);
                    models.Add(new BindingMemberModel(
                        id, property.Name, property.Kind, property.SourceMember, property.ValueType,
                        null, null, property.Access ?? BindingAccess.ReadOnly, null, property.Span));
                    break;
                case CollectionBindingSyntax collection:
                    ValidateType(collection.ItemType, collection.ItemTypeSpan, false, limits, diagnostics);
                    models.Add(new BindingMemberModel(
                        id, collection.Name, collection.Kind, collection.SourceMember, collection.ItemType,
                        null, null, BindingAccess.ReadOnly, null, collection.Span));
                    break;
                case CommandBindingSyntax command:
                    ValidateType(command.ParameterType, command.ParameterTypeSpan, true, limits, diagnostics);
                    ValidateType(command.ResultType, command.ResultTypeSpan, true, limits, diagnostics);
                    models.Add(new BindingMemberModel(
                        id, command.Name, command.Kind, command.SourceMember, null,
                        NormalizeCommandType(command.ParameterType), NormalizeCommandType(command.ResultType),
                        BindingAccess.ReadOnly, null, command.Span));
                    break;
            }
        }

        foreach (ValidationBindingSyntax validation in validations)
        {
            if (!targets.TryGetValue(validation.TargetName, out (int Id, BindingMemberKind Kind) target) ||
                target.Kind is not BindingMemberKind.Property and not BindingMemberKind.Collection)
            {
                diagnostics.Add(
                    BindingDiagnosticIds.InvalidValidationTarget,
                    "A validation member must target a declared property or collection.",
                    validation.TargetNameSpan);
                continue;
            }

            if (!validationIds.TryAdd(target.Id, validation.TargetNameSpan))
            {
                diagnostics.Add(
                    BindingDiagnosticIds.DuplicateMemberId,
                    $"Member ID '{target.Id}' is declared more than once for kind '{BindingMemberKind.Validation}' in this contract.",
                    validation.TargetNameSpan,
                    validationIds[target.Id]);
                continue;
            }

            models.Add(new BindingMemberModel(
                target.Id, validation.Name, validation.Kind, validation.SourceMember, null,
                null, null, BindingAccess.ReadOnly, validation.TargetName, validation.Span));
        }

        IReadOnlyList<BindingMemberModel> orderedMembers = Array.AsReadOnly(models
            .OrderBy(static member => member.Id)
            .ThenBy(static member => member.Kind)
            .ThenBy(static member => member.Name, StringComparer.Ordinal)
            .ToArray());
        return new BindingContractModel(contract.Name, contract.ModelType, orderedMembers, contract.NameSpan, contract.Span);
    }

    private static void ValidateContractName(BindingContractSyntax contract, BindingDiagnosticBag diagnostics)
    {
        bool invalid = string.IsNullOrEmpty(contract.Name) || contract.Name.Any(char.IsControl);
        int byteCount = 0;
        try
        {
            byteCount = StrictUtf8.GetByteCount(contract.Name);
        }
        catch (EncoderFallbackException)
        {
            invalid = true;
        }

        if (invalid || byteCount > 128)
        {
            diagnostics.Add(
                BindingDiagnosticIds.InvalidContractName,
                "A contract identity must be non-empty, control-free valid Unicode of at most 128 UTF-8 bytes.",
                contract.NameSpan);
        }
    }

    private static void ValidateIdentifier(
        string value,
        BindingSourceSpan span,
        string role,
        BindingCompilerLimits limits,
        BindingDiagnosticBag diagnostics)
    {
        bool valid = value.Length is > 0 && value.Length <= limits.MaxIdentifierCharacters && IsIdentifierStart(value[0]);
        for (int index = 1; valid && index < value.Length; index++)
        {
            valid = IsIdentifierPart(value[index]);
        }

        if (!valid)
        {
            diagnostics.Add(
                BindingDiagnosticIds.InvalidMemberName,
                $"The {role} must be an ASCII identifier of at most {limits.MaxIdentifierCharacters} characters.",
                span);
        }
    }

    private static void ValidateType(
        string value,
        BindingSourceSpan span,
        bool allowNone,
        BindingCompilerLimits limits,
        BindingDiagnosticBag diagnostics)
    {
        if (allowNone && string.Equals(value, "none", StringComparison.Ordinal))
        {
            return;
        }

        int index = 0;
        bool valid = value.Length is > 0 && value.Length <= limits.MaxTypeCharacters &&
            !IsReservedNoneSpelling(value) &&
            TryParseTypeSpelling(value, ref index, 0, limits.MaxNestingDepth) &&
            index == value.Length;
        if (!valid)
        {
            diagnostics.Add(
                BindingDiagnosticIds.InvalidTypeName,
                "The CLR type spelling is not valid in binding language version 1.",
                span);
        }
    }

    private static bool TryParseTypeSpelling(string value, ref int index, int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            return false;
        }

        if (value.AsSpan(index).StartsWith("global::", StringComparison.Ordinal))
        {
            index += "global::".Length;
        }

        if (!TryParseIdentifier(value, ref index))
        {
            return false;
        }

        while (index < value.Length && value[index] == '.')
        {
            index++;
            if (!TryParseIdentifier(value, ref index))
            {
                return false;
            }
        }

        if (index < value.Length && value[index] == '<')
        {
            index++;
            if (!TryParseTypeSpelling(value, ref index, depth + 1, maxDepth))
            {
                return false;
            }

            while (index < value.Length && value[index] == ',')
            {
                index++;
                if (!TryParseTypeSpelling(value, ref index, depth + 1, maxDepth))
                {
                    return false;
                }
            }

            if (index >= value.Length || value[index] != '>')
            {
                return false;
            }

            index++;
        }

        bool nullableSeen = false;
        while (index < value.Length)
        {
            if (value[index] == '?' && !nullableSeen)
            {
                nullableSeen = true;
                index++;
                continue;
            }

            if (index + 1 < value.Length && value[index] == '[' && value[index + 1] == ']')
            {
                nullableSeen = false;
                index += 2;
                continue;
            }

            break;
        }

        return true;
    }

    private static bool TryParseIdentifier(string value, ref int index)
    {
        if (index >= value.Length || !IsIdentifierStart(value[index]))
        {
            return false;
        }

        index++;
        while (index < value.Length && IsIdentifierPart(value[index]))
        {
            index++;
        }

        return true;
    }

    private static bool IsReservedNoneSpelling(string value)
    {
        const string None = "none";
        if (!value.StartsWith(None, StringComparison.Ordinal))
        {
            return false;
        }

        int index = None.Length;
        if (index == value.Length)
        {
            return true;
        }

        while (index < value.Length)
        {
            if (value[index] == '?')
            {
                index++;
                continue;
            }

            if (index + 1 < value.Length && value[index] == '[' && value[index + 1] == ']')
            {
                index += 2;
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool IsIdentifierStart(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';

    private static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || value is >= '0' and <= '9';

    private static string? NormalizeCommandType(string value) =>
        string.Equals(value, "none", StringComparison.Ordinal) ? null : value;
}

/// <summary>Stable identities for the declarative binding language and its runtime edge.</summary>
public static class BindingLanguage
{
    /// <summary>The independently versioned binding grammar identity.</summary>
    public const string Identity = "runic.toolkit.mvvm.bindings/1";

    /// <summary>The exact runtime protocol required by this language version.</summary>
    public const string RuntimeProtocolIdentity = global::RunicToolkit.MVVM.MvvmProtocol.Identity;
}

/// <summary>Convenience facade for parsing and semantic analysis.</summary>
public static class BindingCompiler
{
    /// <summary>Compiles one declarative binding source without loading consumer code.</summary>
    public static BindingSemanticResult Compile(
        string source,
        string logicalPath = "binding.rtkmvvm",
        BindingCompilerLimits? limits = null) =>
        BindingSemanticAnalyzer.Analyze(BindingParser.Parse(source, logicalPath, limits));
}
