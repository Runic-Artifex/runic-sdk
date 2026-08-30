using System;
using System.Collections.Generic;

namespace Runic.CommandLine;

/// <summary>
/// Implements the version 1 portable command grammar using only library-owned
/// descriptors and BCL facilities.
/// </summary>
/// <remarks>The adapter is stateless, thread-safe, deterministic, and Native-AOT compatible.</remarks>
public sealed class PortableCommandSyntaxAdapter : ICommandSyntaxAdapter
{
    private const string UnknownOptionCode = "RCLI1001";
    private const string UnknownCommandCode = "RCLI1002";
    private const string MissingOptionValueCode = "RCLI1003";
    private const string UnexpectedOptionValueCode = "RCLI1004";
    private const string MissingArgumentCode = "RCLI1005";
    private const string UnexpectedArgumentCode = "RCLI1006";
    private const string DuplicateOptionCode = "RCLI1007";
    private const string UnsupportedShortBundleCode = "RCLI1008";
    private const string InvalidOutputModeCode = "RCLI1010";
    private const string TransportOutputCollisionCode = "RCLI1011";
    private const string MissingRequiredOptionCode = "RCLI1012";
    private const string UnexpectedRootHelpArgumentCode = "RCLI1013";
    private const int MaximumDiagnosticArgumentLength = 128;

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
        ResolvedCommand? preScanResolved = null;
        if (tokens.Length > 0)
        {
            _ = TryResolveCommand(catalog, tokens, out preScanResolved);
        }

        TransportOutputScan transportOutput = ScanTransportOutput(
            tokens,
            settings.TransportOutputOptionName,
            preScanResolved?.Command);
        CommandOutputClassification initialOutputClassification = ClassifyOutput(
            transportOutput.ExplicitOutputMode,
            settings);

        // The transport is framework-owned. Validate its shape before command
        // resolution so a command error cannot make the selected presentation
        // depend on where the transport happens to occur in argv.
        if (transportOutput.Issue is TransportOutputIssue issue)
        {
            return Error(
                issue.Code,
                issue.Kind,
                issue.TokenIndex,
                arguments: issue.Arguments,
                outputClassification: initialOutputClassification);
        }

        if (tokens.Length == 0)
        {
            return Error(UnknownCommandCode, "unknown-command", 0, outputClassification: initialOutputClassification);
        }

        if (TryFindRootSpecial(
            tokens,
            settings.TransportOutputOptionName,
            out RootSpecial rootSpecial,
            out int unexpectedHelpTokenIndex))
        {
            if (unexpectedHelpTokenIndex >= 0)
            {
                return Error(
                    UnexpectedRootHelpArgumentCode,
                    "unexpected-root-help-argument",
                    unexpectedHelpTokenIndex,
                    outputClassification: initialOutputClassification);
            }

            return rootSpecial == RootSpecial.Help
                ? CompleteSpecial(
                    initialOutputClassification,
                    classification => ParseOutcome.FromHelp(
                        new HelpRequest(CommandPath.Root),
                        classification))
                : CompleteSpecial(initialOutputClassification, ParseOutcome.FromVersion);
        }

        if (!TryResolveCommand(catalog, tokens, out ResolvedCommand? resolved) || resolved is null)
        {
            return Error(
                UnknownCommandCode,
                "unknown-command",
                0,
                arguments: TrySplitOutput(tokens[0], settings.TransportOutputOptionName, out _)
                    ? [settings.TransportOutputOptionName]
                    : [SafeUnknownCommand(tokens[0])],
                outputClassification: initialOutputClassification);
        }

        if (resolved.Command.TryGetOption(settings.TransportOutputOptionName, out _))
        {
            return Error(
                TransportOutputCollisionCode,
                "transport-output-option-collision",
                resolved.Consumed,
                resolved.Path,
                outputClassification: initialOutputClassification);
        }

        return ParseCommand(resolved, tokens, settings, initialOutputClassification);
    }

    private static ParseOutcome ParseCommand(
        ResolvedCommand resolved,
        string[] tokens,
        ParseSettings settings,
        CommandOutputClassification selectedOutputClassification)
    {
        var positionalTokens = new List<IndexedToken>();
        var optionBindings = new List<MutableBinding>();
        var optionBindingIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        CommandOutputMode? explicitOutputMode = null;
        bool outputSeen = false;
        bool recognizeOptions = true;
        int index = resolved.Consumed;

        CommandOutputClassification CurrentOutputClassification() => selectedOutputClassification;

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
                if (!selectedOutputClassification.IsValid)
                {
                    return OutputError(selectedOutputClassification, 0, resolved.Path);
                }

                return ParseOutcome.FromHelp(
                    new HelpRequest(resolved.Path),
                    selectedOutputClassification);
            }

            if (recognizeOptions && TrySplitOutput(token, settings.TransportOutputOptionName, out string? inlineOutputValue))
            {
                if (outputSeen)
                {
                    return Error(
                        DuplicateOptionCode,
                        "duplicate-option",
                        index,
                        resolved.Path,
                        [settings.TransportOutputOptionName],
                        CurrentOutputClassification());
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
                            resolved.Path,
                            [settings.TransportOutputOptionName],
                            CurrentOutputClassification());
                    }

                    if (IsOptionBoundary(resolved.Command, tokens[index], settings.TransportOutputOptionName))
                    {
                        return Error(
                            MissingOptionValueCode,
                            "missing-option-value",
                            index,
                            resolved.Path,
                            [settings.TransportOutputOptionName],
                            CurrentOutputClassification());
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
                        resolved.Path,
                        outputClassification: CurrentOutputClassification());
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
                            resolved.Path,
                            outputClassification: CurrentOutputClassification())
                        : Error(
                            UnknownOptionCode,
                            "unknown-option",
                            index,
                            resolved.Path,
                            [SafeUnknownOption(token)],
                            CurrentOutputClassification());
                }

                if (optionBindingIndexes.TryGetValue(option.Id, out int bindingIndex) &&
                    option.RepeatPolicy == CommandOptionRepeatPolicy.Error)
                {
                    return Error(
                        DuplicateOptionCode,
                        "duplicate-option",
                        index,
                        resolved.Path,
                        [option.Name],
                        CurrentOutputClassification());
                }

                if (inlineValue is not null && option.Arity.Maximum == 0)
                {
                    return Error(
                        UnexpectedOptionValueCode,
                        "unexpected-option-value",
                        index,
                        resolved.Path,
                        [option.Name],
                        CurrentOutputClassification());
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
                    settings.TransportOutputOptionName,
                    occurrenceValues,
                    CurrentOutputClassification());
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
                        resolved.Path,
                        [option.Name],
                        CurrentOutputClassification());
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

        foreach (CommandOptionDescriptor option in resolved.Command.Options)
        {
            if (option.IsRequired && !optionBindingIndexes.ContainsKey(option.Id))
            {
                return Error(
                    MissingRequiredOptionCode,
                    "missing-required-option",
                    tokens.Length,
                    resolved.Path,
                    [option.Name, resolved.Path.ToString()],
                    CurrentOutputClassification());
            }
        }

        ParseOutcome? argumentError = BindArguments(
            resolved.Command.Arguments,
            resolved.Path,
            positionalTokens,
            tokens.Length,
            CurrentOutputClassification(),
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
        string transportOutputOptionName,
        List<string> values,
        CommandOutputClassification outputClassification)
    {
        int maximum = option.Arity.Maximum ?? int.MaxValue;
        while (values.Count < maximum && index < tokens.Length)
        {
            string candidate = tokens[index];
            bool boundary = recognizeOptions && IsOptionBoundary(command, candidate, transportOutputOptionName);
            if (boundary)
            {
                if (values.Count < option.Arity.Minimum)
                {
                    return Error(
                        MissingOptionValueCode,
                        "missing-option-value",
                        index,
                        path,
                        [option.Name],
                        outputClassification);
                }

                break;
            }

            values.Add(candidate);
            index++;
        }

        if (values.Count < option.Arity.Minimum)
        {
            return Error(
                MissingOptionValueCode,
                "missing-option-value",
                tokens.Length,
                path,
                [option.Name],
                outputClassification);
        }

        return null;
    }

    private static ParseOutcome? BindArguments(
        IReadOnlyList<CommandArgumentDescriptor> descriptors,
        CommandPath path,
        IReadOnlyList<IndexedToken> tokens,
        int endTokenIndex,
        CommandOutputClassification outputClassification,
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
                return Error(
                    MissingArgumentCode,
                    "missing-argument",
                    endTokenIndex,
                    path,
                    outputClassification: outputClassification);
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
                path,
                outputClassification: outputClassification);
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
            command = catalog.DefaultCommand;
            if (command is null) return false;
            resolved = new ResolvedCommand(command, new CommandPath([command.Name]), 0);
            return true;
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

    private static bool IsOptionBoundary(CommandDescriptor command, string token, string transportOutputOptionName)
    {
        if (string.Equals(token, "--", StringComparison.Ordinal) ||
            IsHelp(token) ||
            string.Equals(token, "--version", StringComparison.Ordinal) ||
            TrySplitOutput(token, transportOutputOptionName, out _))
        {
            return true;
        }

        return TryResolveOption(command, token, out _, out _, out _);
    }

    private static bool TrySplitOutput(string token, string transportOutputOptionName, out string? inlineValue)
    {
        if (string.Equals(token, transportOutputOptionName, StringComparison.Ordinal))
        {
            inlineValue = null;
            return true;
        }

        string prefix = transportOutputOptionName + "=";
        if (token.StartsWith(prefix, StringComparison.Ordinal))
        {
            inlineValue = token[prefix.Length..];
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

    private static TransportOutputScan ScanTransportOutput(
        string[] tokens,
        string transportOutputOptionName,
        CommandDescriptor? command)
    {
        CommandOutputMode? explicitOutputMode = null;
        bool seen = false;

        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                break;
            }

            if (!TrySplitOutput(token, transportOutputOptionName, out string? inlineValue))
            {
                continue;
            }

            if (seen)
            {
                return new TransportOutputScan(
                    explicitOutputMode,
                    new TransportOutputIssue(
                        DuplicateOptionCode,
                        "duplicate-option",
                        index,
                        [transportOutputOptionName]));
            }

            seen = true;
            int valueIndex = index;
            string value;
            if (inlineValue is null)
            {
                int nextIndex = index + 1;
                if (nextIndex >= tokens.Length || IsTransportValueBoundary(
                    command,
                    tokens[nextIndex],
                    transportOutputOptionName))
                {
                    return new TransportOutputScan(
                        explicitOutputMode,
                        new TransportOutputIssue(
                            MissingOptionValueCode,
                            "missing-option-value",
                            nextIndex,
                            [transportOutputOptionName]));
                }

                valueIndex = nextIndex;
                value = tokens[nextIndex];
                index = nextIndex;
            }
            else
            {
                value = inlineValue;
            }

            if (!TryParseOutputMode(value, out CommandOutputMode mode))
            {
                return new TransportOutputScan(
                    explicitOutputMode,
                    new TransportOutputIssue(
                        InvalidOutputModeCode,
                        "invalid-output-mode",
                        valueIndex,
                        null));
            }

            explicitOutputMode = mode;
        }

        return new TransportOutputScan(explicitOutputMode, null);
    }

    private static bool IsTransportValueBoundary(
        CommandDescriptor? command,
        string token,
        string transportOutputOptionName) =>
        string.Equals(token, "--", StringComparison.Ordinal) ||
        IsHelp(token) ||
        string.Equals(token, "--version", StringComparison.Ordinal) ||
        token.StartsWith('-') ||
        TrySplitOutput(token, transportOutputOptionName, out _) ||
        (command is not null && TryResolveOption(command, token, out _, out _, out _));

    private static bool TryFindRootSpecial(
        string[] tokens,
        string transportOutputOptionName,
        out RootSpecial special,
        out int unexpectedHelpTokenIndex)
    {
        for (int index = 0; index < tokens.Length; index++)
        {
            string token = tokens[index];
            if (string.Equals(token, "--", StringComparison.Ordinal))
            {
                break;
            }

            if (TrySplitOutput(token, transportOutputOptionName, out string? inlineValue))
            {
                if (inlineValue is null)
                {
                    index++;
                }

                continue;
            }

            if (string.Equals(token, "help", StringComparison.Ordinal))
            {
                special = RootSpecial.Help;
                unexpectedHelpTokenIndex = FindUnexpectedRootHelpToken(
                    tokens,
                    index + 1,
                    transportOutputOptionName);
                return true;
            }

            if (IsHelp(token))
            {
                special = RootSpecial.Help;
                unexpectedHelpTokenIndex = -1;
                return true;
            }

            if (string.Equals(token, "--version", StringComparison.Ordinal))
            {
                special = RootSpecial.Version;
                unexpectedHelpTokenIndex = -1;
                return true;
            }

            break;
        }

        special = default;
        unexpectedHelpTokenIndex = -1;
        return false;
    }

    private static int FindUnexpectedRootHelpToken(
        string[] tokens,
        int index,
        string transportOutputOptionName)
    {
        while (index < tokens.Length)
        {
            if (!TrySplitOutput(tokens[index], transportOutputOptionName, out string? inlineValue))
            {
                return index;
            }

            index += inlineValue is null ? 2 : 1;
        }

        return -1;
    }

    private static ParseOutcome CompleteSpecial(
        CommandOutputClassification classification,
        Func<CommandOutputClassification, ParseOutcome> create)
    {
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
        CommandPath? path = null,
        IReadOnlyList<string>? arguments = null,
        CommandOutputClassification? outputClassification = null)
    {
        CommandDiagnostic diagnostic = Diagnostic(
            code,
            kind,
            MessageFor(kind),
            tokenIndex,
            path ?? CommandPath.Root,
            arguments);
        if (outputClassification is not CommandOutputClassification classification)
        {
            return ParseOutcome.FromError(diagnostic);
        }

        return classification.IsValid
            ? ParseOutcome.FromError(diagnostic, classification)
            : ParseOutcome.FromOutputError(diagnostic, classification);
    }

    private static CommandDiagnostic Diagnostic(
        string code,
        string kind,
        string message,
        int tokenIndex,
        CommandPath path,
        IReadOnlyList<string>? arguments = null) =>
        new(
            code,
            kind,
            message,
            CommandDiagnosticPhase.Parse,
            CommandDiagnosticSeverity.Error,
            tokenIndex,
            arguments,
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
        "missing-required-option" => "A required option was not supplied.",
        "unexpected-root-help-argument" => "Root help does not accept additional arguments.",
        "unsupported-short-bundle" => "Bundled short options are not supported.",
        _ => "The command line is invalid.",
    };

    private static bool IsHelp(string token) =>
        string.Equals(token, "--help", StringComparison.Ordinal) ||
        string.Equals(token, "-h", StringComparison.Ordinal);

    private static string SafeUnknownCommand(string token) => SafeDiagnosticArgument(token);

    private static string SafeUnknownOption(string token)
    {
        int valueSeparator = token.IndexOf('=');
        string value = valueSeparator >= 0 ? token[..valueSeparator] : token;
        return SafeDiagnosticArgument(value);
    }

    private static string SafeDiagnosticArgument(string value)
    {
        if (value.Length == 0 || value.Length > MaximumDiagnosticArgumentLength)
        {
            return "<invalid>";
        }

        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsControl(value[index])) return "<invalid>";
        }

        return value;
    }

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

    private sealed record TransportOutputScan(
        CommandOutputMode? ExplicitOutputMode,
        TransportOutputIssue? Issue);

    private sealed record TransportOutputIssue(
        string Code,
        string Kind,
        int TokenIndex,
        IReadOnlyList<string>? Arguments);

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

    private enum RootSpecial
    {
        Help = 0,
        Version = 1,
    }
}
