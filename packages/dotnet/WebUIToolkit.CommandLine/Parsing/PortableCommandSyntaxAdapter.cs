using System;
using System.Collections.Generic;

namespace WebUIToolkit.CommandLine;

/// <summary>
/// Implements the version 1 portable command grammar using only library-owned
/// descriptors and BCL facilities.
/// </summary>
/// <remarks>The adapter is stateless, thread-safe, deterministic, and Native-AOT compatible.</remarks>
public sealed class PortableCommandSyntaxAdapter : ICommandSyntaxAdapter
{
    private const string UnknownOptionCode = "WUTCLI1001";
    private const string UnknownCommandCode = "WUTCLI1002";
    private const string MissingOptionValueCode = "WUTCLI1003";
    private const string UnexpectedOptionValueCode = "WUTCLI1004";
    private const string MissingArgumentCode = "WUTCLI1005";
    private const string UnexpectedArgumentCode = "WUTCLI1006";
    private const string DuplicateOptionCode = "WUTCLI1007";
    private const string UnsupportedShortBundleCode = "WUTCLI1008";
    private const string InvalidOutputModeCode = "WUTCLI1010";

    /// <summary>Gets the shared stateless adapter.</summary>
    public static PortableCommandSyntaxAdapter Instance { get; } = new();

    /// <inheritdoc />
    public ParseOutcome Parse(
        CommandCatalog catalog,
        ReadOnlySpan<string> args,
        ParseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);

        string[] tokens = args.ToArray();
        ValidateTokens(tokens);

        if (tokens.Length == 0)
        {
            return Error(UnknownCommandCode, "unknown-command", 0);
        }

        if (IsHelp(tokens[0]))
        {
            return CompleteSpecial(
                settings,
                classification => ParseOutcome.FromHelp(
                    new HelpRequest(CommandPath.Root),
                    classification));
        }

        if (string.Equals(tokens[0], "--version", StringComparison.Ordinal))
        {
            return CompleteSpecial(settings, ParseOutcome.FromVersion);
        }

        if (!TryResolveCommand(catalog, tokens, out ResolvedCommand? resolved) || resolved is null)
        {
            return Error(UnknownCommandCode, "unknown-command", 0);
        }

        return ParseCommand(resolved, tokens, settings);
    }

    private static ParseOutcome ParseCommand(
        ResolvedCommand resolved,
        string[] tokens,
        ParseSettings settings)
    {
        var positionalTokens = new List<IndexedToken>();
        var optionBindings = new List<MutableBinding>();
        var optionBindingIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        CommandOutputMode? explicitOutputMode = null;
        bool outputSeen = false;
        bool recognizeOptions = true;
        int index = resolved.Consumed;

        while (index < tokens.Length)
        {
            string token = tokens[index];
            if (recognizeOptions && string.Equals(token, "--", StringComparison.Ordinal))
            {
                recognizeOptions = false;
                index++;
                continue;
            }

            if (recognizeOptions && IsHelp(token))
            {
                CommandOutputClassification classification = ClassifyOutput(explicitOutputMode, settings);
                if (!classification.IsValid)
                {
                    return OutputError(classification, 0, resolved.Path);
                }

                return ParseOutcome.FromHelp(
                    new HelpRequest(resolved.Path),
                    classification);
            }

            if (recognizeOptions && TrySplitOutput(token, out string? inlineOutputValue))
            {
                if (outputSeen)
                {
                    return Error(DuplicateOptionCode, "duplicate-option", index, resolved.Path);
                }

                outputSeen = true;
                int valueIndex = index;
                string value;
                if (inlineOutputValue is null)
                {
                    index++;
                    if (index >= tokens.Length)
                    {
                        return Error(
                            MissingOptionValueCode,
                            "missing-option-value",
                            tokens.Length,
                            resolved.Path);
                    }

                    if (IsOptionBoundary(resolved.Command, tokens[index]))
                    {
                        return Error(
                            MissingOptionValueCode,
                            "missing-option-value",
                            index,
                            resolved.Path);
                    }

                    valueIndex = index;
                    value = tokens[index];
                }
                else
                {
                    value = inlineOutputValue;
                }

                if (!TryParseOutputMode(value, out CommandOutputMode mode))
                {
                    return Error(
                        InvalidOutputModeCode,
                        "invalid-output-mode",
                        valueIndex,
                        resolved.Path);
                }

                explicitOutputMode = mode;
                index++;
                continue;
            }

            if (recognizeOptions && TryResolveOption(
                    resolved.Command,
                    token,
                    out CommandOptionDescriptor? option,
                    out string? inlineValue,
                    out OptionTokenKind tokenKind))
            {
                if (option is null)
                {
                    return tokenKind == OptionTokenKind.UnsupportedShortBundle
                        ? Error(
                            UnsupportedShortBundleCode,
                            "unsupported-short-bundle",
                            index,
                            resolved.Path)
                        : Error(UnknownOptionCode, "unknown-option", index, resolved.Path);
                }

                if (optionBindingIndexes.TryGetValue(option.Id, out int bindingIndex) &&
                    option.RepeatPolicy == CommandOptionRepeatPolicy.Error)
                {
                    return Error(DuplicateOptionCode, "duplicate-option", index, resolved.Path);
                }

                if (inlineValue is not null && option.Arity.Maximum == 0)
                {
                    return Error(
                        UnexpectedOptionValueCode,
                        "unexpected-option-value",
                        index,
                        resolved.Path);
                }

                var occurrenceValues = new List<string>();
                if (inlineValue is not null)
                {
                    occurrenceValues.Add(inlineValue);
                }

                index++;
                ParseOutcome? valueError = ConsumeOptionValues(
                    resolved.Command,
                    resolved.Path,
                    option,
                    tokens,
                    ref index,
                    recognizeOptions,
                    occurrenceValues);
                if (valueError is not null)
                {
                    return valueError;
                }

                if (!option.Arity.Accepts(occurrenceValues.Count))
                {
                    return Error(
                        MissingOptionValueCode,
                        "missing-option-value",
                        tokens.Length,
                        resolved.Path);
                }

                if (optionBindingIndexes.TryGetValue(option.Id, out bindingIndex))
                {
                    optionBindings[bindingIndex].Values.AddRange(occurrenceValues);
                }
                else
                {
                    optionBindingIndexes.Add(option.Id, optionBindings.Count);
                    optionBindings.Add(new MutableBinding(option.Id, occurrenceValues));
                }

                continue;
            }

            positionalTokens.Add(new IndexedToken(token, index));
            index++;
        }

        ParseOutcome? argumentError = BindArguments(
            resolved.Command.Arguments,
            resolved.Path,
            positionalTokens,
            tokens.Length,
            out IReadOnlyList<CommandValueBinding>? argumentBindings);
        if (argumentError is not null)
        {
            return argumentError;
        }

        CommandOutputClassification outputClassification = ClassifyOutput(explicitOutputMode, settings);
        if (!outputClassification.IsValid)
        {
            return OutputError(outputClassification, 0, resolved.Path);
        }

        var frozenOptions = new CommandValueBinding[optionBindings.Count];
        for (int optionIndex = 0; optionIndex < optionBindings.Count; optionIndex++)
        {
            MutableBinding binding = optionBindings[optionIndex];
            frozenOptions[optionIndex] = new CommandValueBinding(binding.Id, binding.Values);
        }

        return ParseOutcome.FromInvocation(
            new ParsedInvocation(
                resolved.Command,
                resolved.Path,
                frozenOptions,
                argumentBindings!,
                outputClassification));
    }

    private static ParseOutcome? ConsumeOptionValues(
        CommandDescriptor command,
        CommandPath path,
        CommandOptionDescriptor option,
        string[] tokens,
        ref int index,
        bool recognizeOptions,
        List<string> values)
    {
        int maximum = option.Arity.Maximum ?? int.MaxValue;
        while (values.Count < maximum && index < tokens.Length)
        {
            string candidate = tokens[index];
            bool boundary = recognizeOptions && IsOptionBoundary(command, candidate);
            if (boundary)
            {
                if (values.Count < option.Arity.Minimum)
                {
                    return Error(MissingOptionValueCode, "missing-option-value", index, path);
                }

                break;
            }

            values.Add(candidate);
            index++;
        }

        if (values.Count < option.Arity.Minimum)
        {
            return Error(MissingOptionValueCode, "missing-option-value", tokens.Length, path);
        }

        return null;
    }

    private static ParseOutcome? BindArguments(
        IReadOnlyList<CommandArgumentDescriptor> descriptors,
        CommandPath path,
        IReadOnlyList<IndexedToken> tokens,
        int endTokenIndex,
        out IReadOnlyList<CommandValueBinding>? bindings)
    {
        var result = new List<CommandValueBinding>(descriptors.Count);
        int tokenIndex = 0;
        for (int descriptorIndex = 0; descriptorIndex < descriptors.Count; descriptorIndex++)
        {
            CommandArgumentDescriptor descriptor = descriptors[descriptorIndex];
            int requiredAfter = 0;
            for (int later = descriptorIndex + 1; later < descriptors.Count; later++)
            {
                requiredAfter = checked(requiredAfter + descriptors[later].Arity.Minimum);
            }

            int available = tokens.Count - tokenIndex;
            int availableForCurrent = Math.Max(0, available - requiredAfter);
            int maximum = descriptor.Arity.Maximum ?? int.MaxValue;
            int count = Math.Min(availableForCurrent, maximum);
            if (count < descriptor.Arity.Minimum)
            {
                bindings = null;
                return Error(MissingArgumentCode, "missing-argument", endTokenIndex, path);
            }

            if (count > 0)
            {
                var values = new string[count];
                for (int valueIndex = 0; valueIndex < count; valueIndex++)
                {
                    values[valueIndex] = tokens[tokenIndex + valueIndex].Value;
                }

                result.Add(new CommandValueBinding(descriptor.Id, values));
                tokenIndex += count;
            }
        }

        if (tokenIndex < tokens.Count)
        {
            bindings = null;
            return Error(
                UnexpectedArgumentCode,
                "unexpected-argument",
                tokens[tokenIndex].Index,
                path);
        }

        bindings = result;
        return null;
    }

    private static bool TryResolveCommand(
        CommandCatalog catalog,
        string[] tokens,
        out ResolvedCommand? resolved)
    {
        resolved = null;
        if (!catalog.TryGetCommand(tokens[0], out CommandDescriptor? command) || command is null)
        {
            return false;
        }

        var path = new List<string> { command.Name };
        int consumed = 1;
        while (consumed < tokens.Length &&
               command.TryGetSubcommand(tokens[consumed], out CommandDescriptor? child) &&
               child is not null)
        {
            command = child;
            path.Add(command.Name);
            consumed++;
        }

        resolved = new ResolvedCommand(command, new CommandPath(path), consumed);
        return true;
    }

    private static bool TryResolveOption(
        CommandDescriptor command,
        string token,
        out CommandOptionDescriptor? option,
        out string? inlineValue,
        out OptionTokenKind tokenKind)
    {
        option = null;
        inlineValue = null;
        tokenKind = OptionTokenKind.NotOption;

        if (token.StartsWith("--", StringComparison.Ordinal))
        {
            tokenKind = OptionTokenKind.Option;
            int equalsIndex = token.IndexOf('=', 2);
            string name = equalsIndex < 0 ? token : token[..equalsIndex];
            if (command.TryGetOption(name, out option) && option is not null)
            {
                if (equalsIndex >= 0)
                {
                    inlineValue = token[(equalsIndex + 1)..];
                }

                return true;
            }

            return true;
        }

        if (token.Length >= 2 && token[0] == '-')
        {
            tokenKind = token.Length == 2
                ? OptionTokenKind.Option
                : OptionTokenKind.UnsupportedShortBundle;
            if (token.Length == 2)
            {
                _ = command.TryGetOption(token, out option);
            }

            return true;
        }

        if (token.Length > 0 && token[0] == '/' &&
            command.TryGetOption(token, out option) &&
            option is not null)
        {
            tokenKind = OptionTokenKind.Option;
            return true;
        }

        return false;
    }

    private static bool IsOptionBoundary(CommandDescriptor command, string token)
    {
        if (string.Equals(token, "--", StringComparison.Ordinal) ||
            IsHelp(token) ||
            string.Equals(token, "--version", StringComparison.Ordinal) ||
            TrySplitOutput(token, out _))
        {
            return true;
        }

        return TryResolveOption(command, token, out _, out _, out _);
    }

    private static bool TrySplitOutput(string token, out string? inlineValue)
    {
        if (string.Equals(token, "--output", StringComparison.Ordinal))
        {
            inlineValue = null;
            return true;
        }

        const string Prefix = "--output=";
        if (token.StartsWith(Prefix, StringComparison.Ordinal))
        {
            inlineValue = token[Prefix.Length..];
            return true;
        }

        inlineValue = null;
        return false;
    }

    private static bool TryParseOutputMode(string value, out CommandOutputMode mode)
    {
        if (string.Equals(value, "human", StringComparison.OrdinalIgnoreCase))
        {
            mode = CommandOutputMode.Human;
            return true;
        }

        if (string.Equals(value, "json", StringComparison.OrdinalIgnoreCase))
        {
            mode = CommandOutputMode.Json;
            return true;
        }

        mode = default;
        return false;
    }

    private static CommandOutputClassification ClassifyOutput(
        CommandOutputMode? explicitMode,
        ParseSettings settings) =>
        CommandOutputClassifier.Classify(
            explicitMode,
            settings.OutputEnvironmentValue,
            settings.DefaultOutputMode);

    private static ParseOutcome CompleteSpecial(
        ParseSettings settings,
        Func<CommandOutputClassification, ParseOutcome> create)
    {
        CommandOutputClassification classification = ClassifyOutput(null, settings);
        return classification.IsValid
            ? create(classification)
            : OutputError(classification, 0);
    }

    private static ParseOutcome OutputError(
        CommandOutputClassification classification,
        int tokenIndex,
        CommandPath? path = null) =>
        ParseOutcome.FromOutputError(
            Diagnostic(
                InvalidOutputModeCode,
                "invalid-output-mode",
                "The selected output mode is not supported.",
                tokenIndex,
                path ?? CommandPath.Root),
            classification);

    private static ParseOutcome Error(
        string code,
        string kind,
        int tokenIndex,
        CommandPath? path = null) =>
        ParseOutcome.FromError(
            Diagnostic(code, kind, MessageFor(kind), tokenIndex, path ?? CommandPath.Root));

    private static CommandDiagnostic Diagnostic(
        string code,
        string kind,
        string message,
        int tokenIndex,
        CommandPath path) =>
        new(
            code,
            kind,
            message,
            CommandDiagnosticPhase.Parse,
            CommandDiagnosticSeverity.Error,
            tokenIndex,
            path: path);

    private static string MessageFor(string kind) => kind switch
    {
        "unknown-option" => "An unrecognized option was supplied.",
        "unknown-command" => "An unrecognized command was supplied.",
        "missing-option-value" => "A required option value is missing.",
        "unexpected-option-value" => "A flag does not accept a value.",
        "missing-argument" => "A required command argument is missing.",
        "unexpected-argument" => "An unexpected command argument was supplied.",
        "duplicate-option" => "An option was specified more than once.",
        "unsupported-short-bundle" => "Bundled short options are not supported.",
        _ => "The command line is invalid.",
    };

    private static bool IsHelp(string token) =>
        string.Equals(token, "--help", StringComparison.Ordinal) ||
        string.Equals(token, "-h", StringComparison.Ordinal);

    private static void ValidateTokens(string[] tokens)
    {
        for (int index = 0; index < tokens.Length; index++)
        {
            if (tokens[index] is null)
            {
                throw new ArgumentException("Argument tokens cannot contain null.", nameof(tokens));
            }
        }
    }

    private sealed record ResolvedCommand(
        CommandDescriptor Command,
        CommandPath Path,
        int Consumed);

    private sealed record IndexedToken(string Value, int Index);

    private sealed class MutableBinding
    {
        internal MutableBinding(string id, List<string> values)
        {
            Id = id;
            Values = values;
        }

        internal string Id { get; }

        internal List<string> Values { get; }
    }

    private enum OptionTokenKind
    {
        NotOption = 0,
        Option = 1,
        UnsupportedShortBundle = 2,
    }
}
