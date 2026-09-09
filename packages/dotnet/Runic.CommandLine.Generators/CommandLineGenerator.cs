using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Runic.CommandLine.Generators;

/// <summary>Generates a closed, reflection-free catalog for attributed static methods.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class CommandLineGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor InvalidCommand = Descriptor("RCLI9001", "Invalid generated command", "Command '{0}' must be a non-generic static method with a supported result type.");
    private static readonly DiagnosticDescriptor InvalidParameter = Descriptor("RCLI9002", "Invalid generated command parameter", "Parameter '{0}' on command '{1}' must have exactly one supported binding attribute or be a supported context parameter.");
    private static readonly DiagnosticDescriptor UnsupportedType = Descriptor("RCLI9003", "Unsupported generated command type", "Parameter '{0}' on command '{1}' has unsupported type '{2}'.");
    private static readonly DiagnosticDescriptor DuplicateName = Descriptor("RCLI9004", "Duplicate generated command name", "Command name '{0}' is declared more than once.");
    private static readonly DiagnosticDescriptor InvalidMetadata = Descriptor("RCLI9005", "Invalid generated command metadata", "Command '{0}' has invalid {1}: '{2}'.");

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<IMethodSymbol> commands = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Runic.CommandLine.CommandAttribute",
            static (_, _) => true,
            static (attributeContext, _) => (IMethodSymbol)attributeContext.TargetSymbol);
        context.RegisterSourceOutput(commands.Collect(), static (productionContext, methods) => Emit(productionContext, methods));
    }

    private static void Emit(SourceProductionContext context, ImmutableArray<IMethodSymbol> methods)
    {
        var commands = new List<CommandModel>();
        foreach (IMethodSymbol method in methods.OrderBy(static item => item.ToDisplayString(), StringComparer.Ordinal))
        {
            CommandModel? model = TryCreate(context, method);
            if (model is not null) commands.Add(model);
        }

        foreach (IGrouping<string, CommandModel> group in commands.GroupBy(static command => command.Name, StringComparer.Ordinal).Where(static group => group.Count() > 1))
        {
            foreach (CommandModel command in group)
            {
                context.ReportDiagnostic(Diagnostic.Create(DuplicateName, command.Method.Locations.FirstOrDefault(), group.Key));
            }
        }

        commands = commands.GroupBy(static command => command.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .OrderBy(static command => command.Name, StringComparer.Ordinal)
            .ThenBy(static command => command.Method.ToDisplayString(), StringComparer.Ordinal)
            .ToList();
        if (commands.Count(command => command.IsDefault) > 1)
        {
            foreach (CommandModel command in commands.Where(static command => command.IsDefault)) context.ReportDiagnostic(Diagnostic.Create(InvalidCommand, command.Method.Locations.FirstOrDefault(), command.Name));
            commands.RemoveAll(static command => command.IsDefault);
        }
        if (commands.Count == 0) return;

        context.AddSource("Runic.CommandLine.GeneratedCatalog.g.cs", SourceText.From(Render(commands), Encoding.UTF8));
    }

    private static CommandModel? TryCreate(SourceProductionContext context, IMethodSymbol method)
    {
        AttributeData attribute = method.GetAttributes().First(static item => item.AttributeClass?.ToDisplayString() == "Runic.CommandLine.CommandAttribute");
        string? name = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
        if ((string.IsNullOrEmpty(name) || name.Split(' ').Any(segment => !IsIdentifier(segment) || IsReservedCommand(segment))) || !IsAccessible(method) || !HasAccessibleNonGenericContainingTypes(method) || method.MethodKind != MethodKind.Ordinary || !method.IsStatic || method.IsGenericMethod || !TryGetResult(method.ReturnType, out ITypeSymbol? result, out ResultShape shape))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidCommand, method.Locations.FirstOrDefault(), name ?? method.Name));
            return null;
        }

        var parameters = new List<ParameterModel>();
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            ParameterModel? parameterModel = TryCreateParameter(context, method, parameter);
            if (parameterModel is null) return null;
            parameters.Add(parameterModel);
        }

        IReadOnlyList<ParameterModel> arguments = parameters.Where(static parameter => parameter.Kind == ParameterKind.Argument).ToArray();
        ParameterModel[] variadicArguments = arguments.Where(static parameter => parameter.AllowMultipleValues).ToArray();
        if (variadicArguments.Length > 1 || (variadicArguments.Length == 1 && !ReferenceEquals(arguments[^1], variadicArguments[0])))
        {
            ParameterModel invalid = variadicArguments[0];
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, invalid.Symbol.Locations.FirstOrDefault(), invalid.Symbol.Name, name));
            return null;
        }

        if (parameters.Where(static parameter => parameter.Kind is ParameterKind.Argument or ParameterKind.Option)
            .GroupBy(static parameter => parameter.Id, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidMetadata, method.Locations.FirstOrDefault(), name, "duplicate parameter ID", parameters.First(parameter => parameters.Count(candidate => candidate.Id == parameter.Id) > 1).Id));
            return null;
        }
        string[] optionSpellings = parameters.Where(static parameter => parameter.Kind == ParameterKind.Option)
            .SelectMany(static parameter => parameter.Aliases.Prepend(parameter.Spelling!)).ToArray();
        if (optionSpellings.GroupBy(static spelling => spelling, StringComparer.Ordinal).Any(static group => group.Count() > 1))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidMetadata, method.Locations.FirstOrDefault(), name, "duplicate option spelling", optionSpellings.First(spelling => optionSpellings.Count(candidate => candidate == spelling) > 1)));
            return null;
        }

        if (!TryGetResultMetadata(method, result!, out string? payloadType, out INamedTypeSymbol? jsonContext) && !(FindAttribute(method, "Runic.CommandLine.CommandResultAttribute") is null && (result!.SpecialType is SpecialType.System_String or SpecialType.System_Void || shape is ResultShape.UnitTask)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidCommand, method.Locations.FirstOrDefault(), name));
            return null;
        }

        payloadType ??= shape is ResultShape.Unit or ResultShape.UnitTask ? "runic.unit/1" : "runic.text/1";
        if (!IsPayloadType(payloadType!))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidMetadata, method.Locations.FirstOrDefault(), name, "result payload type", payloadType));
            return null;
        }

        return new CommandModel(method, name!, result!, shape, parameters, payloadType!, jsonContext!, FindAttribute(method, "Runic.CommandLine.DefaultCommandAttribute") is not null);
    }

    private static ParameterModel? TryCreateParameter(SourceProductionContext context, IMethodSymbol method, IParameterSymbol parameter)
    {
        AttributeData? argument = FindAttribute(parameter, "Runic.CommandLine.ArgumentAttribute");
        AttributeData? option = FindAttribute(parameter, "Runic.CommandLine.OptionAttribute");
        AttributeData? service = FindAttribute(parameter, "Runic.CommandLine.FromServicesAttribute");
        int count = (argument is null ? 0 : 1) + (option is null ? 0 : 1) + (service is null ? 0 : 1);
        string methodName = GetCommandName(method);
        if (parameter.RefKind != RefKind.None || (parameter.HasExplicitDefaultValue && option is null && argument is null))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
            return null;
        }
        if (count > 1 || (count == 0 && !IsContext(parameter.Type)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
            return null;
        }

        if (service is not null)
        {
            if (!IsAccessibleType(parameter.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(UnsupportedType, parameter.Locations.FirstOrDefault(), parameter.Name, methodName, parameter.Type.ToDisplayString()));
                return null;
            }
            return new ParameterModel(parameter, ParameterKind.Service, parameter.Name, null, ImmutableArray<string>.Empty, null, false);
        }
        if (count == 0) return new ParameterModel(parameter, ParameterKind.Context, parameter.Name, null, ImmutableArray<string>.Empty, null, false);
        if (option is not null && parameter.HasExplicitDefaultValue && GetRequired(option))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
            return null;
        }
        if (option is not null && parameter.HasExplicitDefaultValue && IsBoolean(parameter.Type) && parameter.ExplicitDefaultValue is true)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
            return null;
        }
        if (option is not null && parameter.HasExplicitDefaultValue && IsList(parameter.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
            return null;
        }
        if (FindAttribute(parameter, "Runic.CommandLine.ConvertWithAttribute") is null && !IsValueType(parameter.Type) && !((option is not null || argument is not null) && IsList(parameter.Type)))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedType, parameter.Locations.FirstOrDefault(), parameter.Name, methodName, parameter.Type.ToDisplayString()));
            return null;
        }

        string id = GetId(parameter, argument);
        if (!IsIdentifier(id))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidMetadata, parameter.Locations.FirstOrDefault(), methodName, "parameter ID", id));
            return null;
        }
        if (option is not null)
        {
            string? spelling = option.ConstructorArguments.Length > 0 ? option.ConstructorArguments[0].Value as string : null;
            if (!IsOptionSpelling(spelling!) || IsReservedOption(spelling!))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidMetadata, parameter.Locations.FirstOrDefault(), methodName, "option spelling", spelling ?? string.Empty));
                return null;
            }

            ImmutableArray<string> aliases = option.ConstructorArguments.Length > 1 && !option.ConstructorArguments[1].IsNull
                ? option.ConstructorArguments[1].Values.Select(static value => value.Value as string ?? string.Empty).ToImmutableArray()
                : ImmutableArray<string>.Empty;
            if (aliases.Any(alias => !IsOptionSpelling(alias) || IsReservedOption(alias)))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidMetadata, parameter.Locations.FirstOrDefault(), methodName, "option alias", aliases.First(alias => !IsOptionSpelling(alias) || IsReservedOption(alias))));
                return null;
            }
            bool allowMultipleValues = GetAllowMultipleValues(option);
            bool allowMultipleOccurrences = GetAllowMultipleOccurrences(option);
            bool isRequired = GetRequired(option) || (!parameter.HasExplicitDefaultValue && !IsNullable(parameter) && !IsBoolean(parameter.Type) && !IsList(parameter.Type));
            if (allowMultipleValues && !IsList(parameter.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
                return null;
            }
            if (!allowMultipleOccurrences && !IsList(parameter.Type))
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
                return null;
            }
            return new ParameterModel(parameter, ParameterKind.Option, id, spelling, aliases, parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue : null, parameter.HasExplicitDefaultValue || IsNullable(parameter), allowMultipleValues, isRequired, allowMultipleOccurrences);
        }

        bool allowMultipleArgumentValues = GetAllowMultipleValues(argument!);
        if ((IsList(parameter.Type) && !allowMultipleArgumentValues) ||
            (allowMultipleArgumentValues && !IsList(parameter.Type)))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidParameter, parameter.Locations.FirstOrDefault(), parameter.Name, methodName));
            return null;
        }

        if (IsBoolean(parameter.Type))
        {
            context.ReportDiagnostic(Diagnostic.Create(UnsupportedType, parameter.Locations.FirstOrDefault(), parameter.Name, methodName, parameter.Type.ToDisplayString()));
            return null;
        }
        return new ParameterModel(parameter, ParameterKind.Argument, id, null, ImmutableArray<string>.Empty, parameter.HasExplicitDefaultValue ? parameter.ExplicitDefaultValue : null, parameter.HasExplicitDefaultValue || IsNullable(parameter), allowMultipleArgumentValues);
    }

    private static string Render(IReadOnlyList<CommandModel> commands)
    {
        var source = new StringBuilder("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS1591\nnamespace Runic.CommandLine.Generated;\n\n");
        source.AppendLine("public static class GeneratedCommandCatalog").AppendLine("{").AppendLine("    public static global::Runic.CommandLine.CommandCatalog Create(global::System.Action<global::Runic.CommandLine.CommandCatalogBuilder>? configure = null) { var builder = new global::Runic.CommandLine.CommandCatalogBuilder()");
        for (int index = 0; index < commands.Count; index++)
        {
            CommandModel command = commands[index];
            source.Append("        .CommandPath<__Options").Append(index).Append(", __Handler").Append(index).Append(", ").Append(ResultType(command)).Append(">(").Append(Literal(command.Name)).Append(", command => command");
            foreach (ParameterModel parameter in command.Parameters.Where(static parameter => parameter.Kind == ParameterKind.Option))
            {
                source.Append(".Option(").Append(Literal(parameter.Id)).Append(", ").Append(Literal(parameter.Spelling!)).Append(", global::Runic.CommandLine.CommandArity.").Append(IsBoolean(parameter.Symbol.Type) ? "Zero" : parameter.AllowMultipleValues ? "OneOrMore" : "ExactlyOne");
                if (IsList(parameter.Symbol.Type)) source.Append(", global::Runic.CommandLine.CommandOptionRepeatPolicy.").Append(parameter.AllowMultipleOccurrences ? "Append" : "Error");
                else if (parameter.IsRequired) source.Append(", global::Runic.CommandLine.CommandOptionRepeatPolicy.Error");
                AttributeData optionMetadata = FindAttribute(parameter.Symbol, "Runic.CommandLine.OptionAttribute")!;
                if (optionMetadata.NamedArguments.Any(static p => p.Key == "Sensitive" && p.Value.Value is true)) source.Append(", isSensitive: true, descriptionKey: null");
                if (parameter.IsRequired) source.Append(", isRequired: true");
                if (!parameter.Aliases.IsEmpty)
                {
                    source.Append(", aliases: [");
                    for (int aliasIndex = 0; aliasIndex < parameter.Aliases.Length; aliasIndex++)
                    {
                        if (aliasIndex != 0) source.Append(", ");
                        source.Append(Literal(parameter.Aliases[aliasIndex]));
                    }
                    source.Append(']');
                }
                source.Append(')');
            }
            foreach (ParameterModel parameter in command.Parameters.Where(static parameter => parameter.Kind == ParameterKind.Argument))
            {
                bool sensitive = FindAttribute(parameter.Symbol, "Runic.CommandLine.ArgumentAttribute")!.NamedArguments.Any(static p => p.Key == "Sensitive" && p.Value.Value is true);
                source.Append(".Argument(").Append(Literal(parameter.Id)).Append(", ").Append(Literal(parameter.Id)).Append(", global::Runic.CommandLine.CommandArity.").Append(parameter.AllowMultipleValues ? "ZeroOrMore" : parameter.HasDefault ? "ZeroOrOne" : "ExactlyOne").Append(sensitive ? ", isSensitive: true)" : ")");
            }

            AttributeData commandAttribute = FindAttribute(command.Method, "Runic.CommandLine.CommandAttribute")!;
            source.Append(".WithHelp(new global::Runic.CommandLine.CommandHelp(description: ").Append(NamedString(commandAttribute, "Description")).Append(", examples: ").Append(NamedArray(commandAttribute, "Examples")).Append("))");
            foreach (ParameterModel parameter in command.Parameters.Where(static p => p.Kind is ParameterKind.Option or ParameterKind.Argument))
            {
                AttributeData metadata = FindAttribute(parameter.Symbol, parameter.Kind == ParameterKind.Option ? "Runic.CommandLine.OptionAttribute" : "Runic.CommandLine.ArgumentAttribute")!;
                ITypeSymbol scalar = ElementType(parameter.Symbol.Type) ?? parameter.Symbol.Type;
                if (scalar is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) scalar = nullable.TypeArguments[0];
                bool numeric = scalar.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Double or SpecialType.System_Decimal;
                string choices = NamedArray(metadata, "Choices");
                if (choices == "[]" && scalar.TypeKind == TypeKind.Enum) choices = "[" + string.Join(", ", scalar.GetMembers().OfType<IFieldSymbol>().Where(static f => f.HasConstantValue).Select(static f => Literal(f.Name))) + "]";
                source.Append(".ParameterHelp(").Append(Literal(parameter.Id)).Append(", new global::Runic.CommandLine.CommandHelp(description: ").Append(NamedString(metadata, "Description"))
                    .Append(", valueName: ").Append(NamedString(metadata, "ValueName")).Append(", defaultValue: ").Append(parameter.HasDefault && parameter.DefaultValue is not null ? Literal(Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture)!) : "null")
                    .Append(", environmentVariable: ").Append(NamedString(metadata, "EnvironmentVariable")).Append(", choices: ").Append(choices).Append(", acceptsNegativeNumbers: ").Append(numeric ? "true" : "false").Append("))");
            }
            source.Append(".BindWith(__Binder").Append(index).Append(".Instance).CreateHandlerWith(__Factory").Append(index).Append(".Instance).Produces(__Codec").Append(index).Append(".Instance))").AppendLine();
        }
        CommandModel? defaultCommand = commands.FirstOrDefault(static command => command.IsDefault);
        if (defaultCommand is not null) source.Append("        .DefaultCommand(").Append(Literal(defaultCommand.Name)).AppendLine(")");
        source.AppendLine("        ; configure?.Invoke(builder); return builder.Build(); }").AppendLine();
        for (int index = 0; index < commands.Count; index++) RenderCommand(source, commands[index], index);
        source.AppendLine("}");
        return source.ToString();
    }

    private static void RenderCommand(StringBuilder source, CommandModel command, int index)
    {
        string result = ResultType(command);
        ParameterModel[] boundParameters = command.Parameters.Where(static p => p.Kind is ParameterKind.Argument or ParameterKind.Option).ToArray();
        source.Append("    private sealed record __Options").Append(index).Append('(')
            .Append(string.Join(", ", boundParameters.Select(p => ParameterType(p.Symbol) + " " + EscapeIdentifier(p.Symbol.Name)))).AppendLine(");");
        source.Append("    private sealed class __Binder").Append(index).Append(" : global::Runic.CommandLine.ICommandOptionsBinder<__Options").Append(index).AppendLine("> { public static __Binder" + index + " Instance { get; } = new(); public global::System.Threading.Tasks.ValueTask<global::Runic.CommandLine.CommandOutcome<__Options" + index + ">> BindAsync(global::Runic.CommandLine.ParsedInvocation invocation, global::System.Threading.CancellationToken cancellationToken) { try { cancellationToken.ThrowIfCancellationRequested();");
        foreach (ParameterModel parameter in command.Parameters.Where(static parameter => parameter.Kind is ParameterKind.Argument or ParameterKind.Option))
        {
            source.Append("var ").Append(EscapeIdentifier(parameter.Symbol.Name)).Append(" = ").Append(ValueExpression(parameter)).Append(';');
        }
        source.Append("return global::System.Threading.Tasks.ValueTask.FromResult(global::Runic.CommandLine.CommandOutcome.Success(new __Options").Append(index).Append("(" + string.Join(", ", boundParameters.Select(p => EscapeIdentifier(p.Symbol.Name))) + "))); } catch (global::Runic.CommandLine.GeneratedCommandBindingException exception) { return global::System.Threading.Tasks.ValueTask.FromResult(global::Runic.CommandLine.CommandOutcome.Failure<__Options").Append(index).Append(">(global::Runic.CommandLine.CommandExitCategory.Usage, new global::Runic.CommandLine.CommandFault(\"RCLI2005\", exception.Message))); } } }").AppendLine();
        source.Append("    private sealed class __Factory").Append(index).Append(" : global::Runic.CommandLine.ICommandHandlerFactory<__Handler").Append(index).AppendLine("> { public static __Factory" + index + " Instance { get; } = new(); public __Handler" + index + " Create(global::System.IServiceProvider services) => new(services); }");
        source.Append("    private sealed class __Handler").Append(index).Append(" : global::Runic.CommandLine.ICommandHandler<__Options").Append(index).Append(", ").Append(result).AppendLine("> { private readonly global::System.IServiceProvider _services; public __Handler" + index + "(global::System.IServiceProvider services) => _services = services; public async global::System.Threading.Tasks.ValueTask<global::Runic.CommandLine.CommandOutcome<" + result + ">> ExecuteAsync(__Options" + index + " options, global::Runic.CommandLine.CommandExecutionContext context, global::System.Threading.CancellationToken cancellationToken) { ");
        string invocation = "global::" + command.Method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty, StringComparison.Ordinal) + "." + EscapeIdentifier(command.Method.Name) + "(" + string.Join(", ", command.Parameters.Select(ValueForInvocation)) + ")";
        switch (command.Shape)
        {
            case ResultShape.Unit: source.Append(invocation).Append("; return global::Runic.CommandLine.CommandOutcome.Success(new global::Runic.CommandLine.CommandUnit());"); break;
            case ResultShape.UnitTask: source.Append("await ").Append(invocation).Append(".ConfigureAwait(false); return global::Runic.CommandLine.CommandOutcome.Success(new global::Runic.CommandLine.CommandUnit());"); break;
            case ResultShape.Direct: source.Append("return global::Runic.CommandLine.CommandOutcome.Success(").Append(invocation).Append(");"); break;
            case ResultShape.Task: case ResultShape.ValueTask: source.Append("return global::Runic.CommandLine.CommandOutcome.Success(await ").Append(invocation).Append(".ConfigureAwait(false));"); break;
            case ResultShape.Outcome: source.Append("return ").Append(invocation).Append(';'); break;
            case ResultShape.OutcomeTask: case ResultShape.OutcomeValueTask: source.Append("return await ").Append(invocation).Append(".ConfigureAwait(false);"); break;
        }
        source.Append(" } }").AppendLine();
        if (command.JsonContext is null)
        {
            string codec = command.Shape is ResultShape.Unit or ResultShape.UnitTask ? "Unit" : "String";
            source.Append("    private static class __Codec").Append(index).Append(" { public static global::Runic.CommandLine.ICommandResultCodec<").Append(result).Append("> Instance => global::Runic.CommandLine.CommandResultCodecs.").Append(codec).AppendLine("; }");
            return;
        }
        bool customText = command.Result.GetMembers("ToString").OfType<IMethodSymbol>().Any(static method => method.IsOverride && !method.IsImplicitlyDeclared && method.Parameters.Length == 0);
        string humanBody = customText
            ? "console.WriteOutAsync(global::System.MemoryExtensions.AsMemory(global::System.String.Concat(global::System.Convert.ToString(value, culture) ?? string.Empty, \"\\n\")), cancellationToken)"
            : "global::Runic.CommandLine.CommandResultFormatter.WriteHumanAsync(value, TypeInfo, console, cancellationToken)";
        source.Append("    private sealed class __Codec").Append(index).Append(" : global::Runic.CommandLine.ICommandResultCodec<").Append(result).AppendLine("> { public static __Codec" + index + " Instance { get; } = new(); public string PayloadType => " + Literal(command.PayloadType) + "; public global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + result + "> TypeInfo => (global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + result + ">)new " + Type(command.JsonContext) + "().GetTypeInfo(typeof(" + result + "))!; public global::System.Threading.Tasks.ValueTask WriteHumanAsync(" + result + " value, global::Runic.CommandLine.ICommandConsole console, global::System.Globalization.CultureInfo culture, global::System.Threading.CancellationToken cancellationToken) => " + humanBody + "; }");
    }

    private static string ValueForInvocation(ParameterModel parameter) => parameter.Kind switch
    {
        ParameterKind.Argument or ParameterKind.Option => "options." + EscapeIdentifier(parameter.Symbol.Name),
        ParameterKind.Service => "(" + Type(parameter.Symbol.Type) + ")(_services.GetService(typeof(" + Type(parameter.Symbol.Type) + ")) ?? throw new global::System.InvalidOperationException(\"A required command service is missing.\"))",
        _ when parameter.Symbol.Type.ToDisplayString() == "System.Threading.CancellationToken" => "cancellationToken",
        _ when parameter.Symbol.Type.ToDisplayString() == "Runic.CommandLine.CommandExecutionContext" => "context",
        _ => "context.Console",
    };

    private static string ValueExpression(ParameterModel parameter, string invocation = "invocation")
    {
        string value = RawValueExpression(parameter, invocation);
        if (FindAttribute(parameter.Symbol, "Runic.CommandLine.ValidateWithAttribute")?.ConstructorArguments[0].Value is ITypeSymbol validator)
            return "global::Runic.CommandLine.GeneratedCommandBinding.Validate<" + Type(parameter.Symbol.Type) + ", " + Type(validator) + ">(" + value + ", " + Literal(parameter.Id) + ")";
        return value;
    }

    private static string RawValueExpression(ParameterModel parameter, string invocation)
    {
        if (FindAttribute(parameter.Symbol, "Runic.CommandLine.ConvertWithAttribute") is null && ElementType(parameter.Symbol.Type) is { } element)
        {
            string values = "global::Runic.CommandLine.GeneratedCommandBinding." + (parameter.Kind == ParameterKind.Option ? "Options" : "Arguments") + "(" + invocation + ", " + Literal(parameter.Id) + ")";
            return "global::Runic.CommandLine.GeneratedCommandBinding.ParseValues<" + Type(element) + ">(" + values + ", static value => " + Conversion(element, "value", parameter.Id) + ")";
        }
        string source = parameter.Kind == ParameterKind.Argument ? "global::Runic.CommandLine.GeneratedCommandBinding.Argument(" + invocation + ", " + Literal(parameter.Id) + ")" : IsBoolean(parameter.Symbol.Type) ? "global::Runic.CommandLine.GeneratedCommandBinding.Flag(" + invocation + ", " + Literal(parameter.Id) + ")" : "global::Runic.CommandLine.GeneratedCommandBinding.Option(" + invocation + ", " + Literal(parameter.Id) + ")";
        if (parameter.HasDefault && !IsBoolean(parameter.Symbol.Type)) return "(global::Runic.CommandLine.GeneratedCommandBinding." + (parameter.Kind == ParameterKind.Option ? "Options" : "Arguments") + "(" + invocation + ", " + Literal(parameter.Id) + ").Count == 0 ? " + DefaultLiteral(parameter) + " : " + ParameterConversion(parameter, source) + ")";
        if (parameter.Kind == ParameterKind.Option && IsBoolean(parameter.Symbol.Type)) return source;
        return ParameterConversion(parameter, source);
    }

    private static string ParameterConversion(ParameterModel parameter, string value)
    {
        if (FindAttribute(parameter.Symbol, "Runic.CommandLine.ConvertWithAttribute")?.ConstructorArguments[0].Value is ITypeSymbol converter)
            return "global::Runic.CommandLine.GeneratedCommandBinding.Convert<" + Type(parameter.Symbol.Type) + ", " + Type(converter) + ">(" + value + ", " + Literal(parameter.Id) + ")";
        return Conversion(parameter.Symbol.Type, value, parameter.Id);
    }

    private static string Conversion(ITypeSymbol type, string value, string id)
    {
        if (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return Conversion(nullable.TypeArguments[0], value, id);
        if (type.TypeKind == TypeKind.Enum) return "global::Runic.CommandLine.GeneratedCommandBinding.ParseEnum<" + Type(type) + ">(" + value + ", " + Literal(id) + ")";
        return type.SpecialType switch
        {
        SpecialType.System_String => value,
        SpecialType.System_Boolean => "global::Runic.CommandLine.GeneratedCommandBinding.ParseBoolean(" + value + ", " + Literal(id) + ")",
        SpecialType.System_Int32 => "global::Runic.CommandLine.GeneratedCommandBinding.ParseInt32(" + value + ", " + Literal(id) + ")",
        SpecialType.System_Int64 => "global::Runic.CommandLine.GeneratedCommandBinding.ParseInt64(" + value + ", " + Literal(id) + ")",
        SpecialType.System_Decimal => "global::Runic.CommandLine.GeneratedCommandBinding.ParseDecimal(" + value + ", " + Literal(id) + ")",
        SpecialType.System_Double => "global::Runic.CommandLine.GeneratedCommandBinding.ParseDouble(" + value + ", " + Literal(id) + ")",
        _ => type.ToDisplayString() == "System.Guid" ? "global::Runic.CommandLine.GeneratedCommandBinding.ParseGuid(" + value + ", " + Literal(id) + ")" : value,
        };
    }

    private static bool TryGetResult(ITypeSymbol type, out ITypeSymbol? result, out ResultShape shape)
    {
        if (type.SpecialType == SpecialType.System_Void) { result = type; shape = ResultShape.Unit; return true; }
        if (type.ToDisplayString() is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask") { result = type; shape = ResultShape.UnitTask; return true; }
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            if (IsNamedDefinition(named, "System.Threading.Tasks", "Task`1") || IsNamedDefinition(named, "System.Threading.Tasks", "ValueTask`1")) { result = named.TypeArguments[0]; shape = IsNamedDefinition(named, "System.Threading.Tasks", "Task`1") ? ResultShape.Task : ResultShape.ValueTask; return UnwrapOutcome(ref result, ref shape); }
            if (IsNamedDefinition(named, "Runic.CommandLine", "CommandOutcome`1")) { result = named.TypeArguments[0]; shape = ResultShape.Outcome; return true; }
        }
        result = type; shape = ResultShape.Direct; return true;
    }

    private static bool UnwrapOutcome(ref ITypeSymbol? result, ref ResultShape shape)
    {
        if (result is INamedTypeSymbol named && IsNamedDefinition(named, "Runic.CommandLine", "CommandOutcome`1")) { result = named.TypeArguments[0]; shape = shape == ResultShape.Task ? ResultShape.OutcomeTask : ResultShape.OutcomeValueTask; }
        return result is not null;
    }

    private static bool IsValueType(ITypeSymbol type) => type.TypeKind == TypeKind.Enum || (type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T && IsValueType(nullable.TypeArguments[0])) || type.SpecialType is SpecialType.System_String or SpecialType.System_Int32 or SpecialType.System_Int64 or SpecialType.System_Decimal or SpecialType.System_Double || type.ToDisplayString() == "System.Guid" || IsBoolean(type);
    private static bool IsBoolean(ITypeSymbol type) => type.SpecialType == SpecialType.System_Boolean;
    private static ITypeSymbol? ElementType(ITypeSymbol type) => type is IArrayTypeSymbol array ? array.ElementType : type is INamedTypeSymbol named && IsNamedDefinition(named, "System.Collections.Generic", "IReadOnlyList`1") ? named.TypeArguments[0] : null;
    private static bool IsList(ITypeSymbol type) => ElementType(type) is { } element && IsValueType(element);
    private static bool IsContext(ITypeSymbol type) => type.ToDisplayString() is "System.Threading.CancellationToken" or "Runic.CommandLine.CommandExecutionContext" or "Runic.CommandLine.ICommandConsole";
    private static bool IsNamedDefinition(INamedTypeSymbol type, string @namespace, string metadataName) => type.ConstructedFrom.MetadataName == metadataName && type.ConstructedFrom.ContainingNamespace.ToDisplayString() == @namespace;
    private static AttributeData? FindAttribute(ISymbol symbol, string name) => symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);
    private static string GetId(IParameterSymbol parameter, AttributeData? argument) => argument?.ConstructorArguments.Length == 1 && argument.ConstructorArguments[0].Value is string id ? id : Kebab(parameter.Name);
    private static string GetCommandName(IMethodSymbol method) => method.GetAttributes().First(item => item.AttributeClass?.ToDisplayString() == "Runic.CommandLine.CommandAttribute").ConstructorArguments[0].Value as string ?? method.Name;
    private static bool TryGetResultMetadata(IMethodSymbol method, ITypeSymbol result, out string? payloadType, out INamedTypeSymbol? jsonContext)
    {
        AttributeData? attribute = method.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == "Runic.CommandLine.CommandResultAttribute");
        payloadType = attribute?.ConstructorArguments.Length > 0 ? attribute.ConstructorArguments[0].Value as string : null;
        jsonContext = attribute?.ConstructorArguments.Length > 1 ? attribute.ConstructorArguments[1].Value as INamedTypeSymbol : null;
        return !string.IsNullOrWhiteSpace(payloadType) && jsonContext is not null &&
            IsAccessibleType(jsonContext) &&
            InheritsFrom(jsonContext, "System.Text.Json.Serialization", "JsonSerializerContext") &&
            DeclaresMetadataFor(jsonContext, result);
    }
    private static string ResultType(CommandModel command) => command.Shape is ResultShape.Unit or ResultShape.UnitTask ? "global::Runic.CommandLine.CommandUnit" : Type(command.Result);
    private static bool IsNullable(IParameterSymbol parameter) => parameter.NullableAnnotation == NullableAnnotation.Annotated;
    private static string NamedString(AttributeData data, string name) => data.NamedArguments.FirstOrDefault(p => p.Key == name).Value.Value is string value ? Literal(value) : "null";
    private static string NamedArray(AttributeData data, string name)
    {
        TypedConstant value = data.NamedArguments.FirstOrDefault(p => p.Key == name).Value;
        return value.Kind == TypedConstantKind.Array && !value.IsNull ? "[" + string.Join(", ", value.Values.Where(static v => v.Value is string).Select(v => Literal((string)v.Value!))) + "]" : "[]";
    }
    private static string ParameterType(IParameterSymbol parameter) => Type(parameter.Type) + (parameter.Type.IsReferenceType && parameter.NullableAnnotation == NullableAnnotation.Annotated ? "?" : "");
    private static string Type(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
    private static string Kebab(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);
    private static bool IsAccessible(ISymbol symbol) => symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;
    private static bool IsAccessibleType(ITypeSymbol type) => type is not INamedTypeSymbol named || (IsAccessible(named) && HasAccessibleNonGenericContainingTypes(named));
    private static bool HasAccessibleNonGenericContainingTypes(ISymbol symbol)
    {
        for (INamedTypeSymbol? type = symbol.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType || type.IsFileLocal || !IsAccessible(type)) return false;
        }
        return true;
    }
    private static bool InheritsFrom(INamedTypeSymbol type, string @namespace, string name)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.BaseType)
        {
            if (current.Name == name && current.ContainingNamespace.ToDisplayString() == @namespace) return true;
        }
        return false;
    }
    private static bool DeclaresMetadataFor(INamedTypeSymbol context, ITypeSymbol result) =>
        context.GetAttributes().Any(attribute =>
            attribute.AttributeClass?.Name == "JsonSerializableAttribute" &&
            attribute.AttributeClass.ContainingNamespace.ToDisplayString() == "System.Text.Json.Serialization" &&
            attribute.ConstructorArguments.Length != 0 &&
            SymbolEqualityComparer.Default.Equals(attribute.ConstructorArguments[0].Value as ITypeSymbol, result));
    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] is < 'a' or > 'z') return false;
        for (int index = 1; index < value.Length; index++)
        {
            char character = value[index];
            if ((character is < 'a' or > 'z') && (character is < '0' or > '9') && character != '-') return false;
        }
        return true;
    }
    private static bool IsOptionSpelling(string value) =>
        value.StartsWith("--", StringComparison.Ordinal) ? IsIdentifier(value[2..]) :
        value.Length > 0 && value[0] == '/' ? IsIdentifier(value[1..]) :
        value.Length == 2 && value[0] == '-' && ((value[1] is >= 'a' and <= 'z') || (value[1] is >= 'A' and <= 'Z'));
    private static bool IsReservedOption(string value) => value is "--help" or "-h" or "--version";
    private static bool IsReservedCommand(string value) => value is "help" or "version" or "output";
    private static bool IsPayloadType(string value)
    {
        int separator = value.LastIndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 || value.Length > 128 || value[0] is < 'a' or > 'z') return false;
        for (int index = 0; index < separator; index++)
        {
            char character = value[index];
            if ((character is < 'a' or > 'z') && (character is < '0' or > '9') && character is not ('-' or '.')) return false;
        }
        if (value[separator + 1] == '0') return false;
        for (int index = separator + 1; index < value.Length; index++) if (value[index] is < '0' or > '9') return false;
        return true;
    }
    private static string EscapeIdentifier(string value) =>
        SyntaxFacts.GetKeywordKind(value) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(value) != SyntaxKind.None ? "@" + value : value;
    private static DiagnosticDescriptor Descriptor(string id, string title, string message) => new(id, title, message, "Runic.CommandLine", DiagnosticSeverity.Error, true);

    private enum ParameterKind { Argument, Option, Service, Context }
    private enum ResultShape { Direct, Task, ValueTask, Outcome, OutcomeTask, OutcomeValueTask, Unit, UnitTask }
    private static string DefaultLiteral(ParameterModel parameter)
    {
        if (parameter.DefaultValue is null)
        {
            string literal = "default(" + Type(parameter.Symbol.Type) + ")";
            return parameter.Symbol.Type.SpecialType == SpecialType.System_String &&
                parameter.Symbol.NullableAnnotation == NullableAnnotation.NotAnnotated ? literal + "!" : literal;
        }
        ITypeSymbol literalType = parameter.Symbol.Type is INamedTypeSymbol nullable && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? nullable.TypeArguments[0] : parameter.Symbol.Type;
        if (parameter.DefaultValue is bool boolean) return boolean ? "true" : "false";
        if (literalType.TypeKind == TypeKind.Enum) return "(" + Type(literalType) + ")" + Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture);
        if (parameter.DefaultValue is string text) return Literal(text);
        if (parameter.DefaultValue is double floatingPoint)
        {
            if (double.IsNaN(floatingPoint)) return "global::System.Double.NaN";
            if (double.IsPositiveInfinity(floatingPoint)) return "global::System.Double.PositiveInfinity";
            if (double.IsNegativeInfinity(floatingPoint)) return "global::System.Double.NegativeInfinity";
        }
        string value = Convert.ToString(parameter.DefaultValue, CultureInfo.InvariantCulture) ?? "default";
        return literalType.SpecialType switch
        {
            SpecialType.System_Int64 => value + "L",
            SpecialType.System_Decimal => value + "m",
            SpecialType.System_Double => value + "d",
            _ => value,
        };
    }
    private static bool GetAllowMultipleValues(AttributeData option) =>
        option.NamedArguments.FirstOrDefault(static pair => pair.Key == "AllowMultipleValues").Value.Value as bool? ?? false;
    private static bool GetAllowMultipleOccurrences(AttributeData option) =>
        option.NamedArguments.FirstOrDefault(static pair => pair.Key == "AllowMultipleOccurrences").Value.Value as bool? ?? true;
    private static bool GetRequired(AttributeData option) =>
        option.NamedArguments.FirstOrDefault(static pair => pair.Key == "Required").Value.Value as bool? ?? false;
    private sealed record ParameterModel(IParameterSymbol Symbol, ParameterKind Kind, string Id, string? Spelling, ImmutableArray<string> Aliases, object? DefaultValue, bool HasDefault, bool AllowMultipleValues = false, bool IsRequired = false, bool AllowMultipleOccurrences = true);
    private sealed record CommandModel(IMethodSymbol Method, string Name, ITypeSymbol Result, ResultShape Shape, IReadOnlyList<ParameterModel> Parameters, string PayloadType, INamedTypeSymbol? JsonContext, bool IsDefault);
}
