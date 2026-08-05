using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace RunicToolkit.Frontend.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class FrontendContractGenerator : IIncrementalGenerator
{
    private const string ContractAttribute =
        "RunicToolkit.MVVM.WebUiFrontendContractAttribute";
    private const string PropertyAttribute =
        "RunicToolkit.MVVM.WebUiFrontendPropertyAttribute";
    private const string CollectionAttribute =
        "RunicToolkit.MVVM.WebUiFrontendCollectionAttribute";
    private const string CommandAttribute =
        "RunicToolkit.MVVM.WebUiFrontendCommandAttribute";
    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);
    private static readonly JsonSerializerOptions ArtifactJsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly DiagnosticDescriptor InvalidContract = new(
        "RTKFE0001",
        "Invalid C#-first frontend contract",
        "{0}",
        "RunicToolkit.Frontend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor ArtifactFailure = new(
        "RTKFE0002",
        "Frontend contract artifact could not be written",
        "{0}",
        "RunicToolkit.Frontend",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<ContractSpec> contracts = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                ContractAttribute,
                static (_, _) => true,
                static (syntaxContext, cancellationToken) =>
                    CreateContract(syntaxContext, cancellationToken));
        IncrementalValueProvider<GenerationOptions> options =
            context.AnalyzerConfigOptionsProvider.Select(
                static (provider, _) => GenerationOptions.Create(provider.GlobalOptions));
        context.RegisterSourceOutput(
            contracts.Collect().Combine(options),
            static (productionContext, input) =>
                Emit(productionContext, input.Left, input.Right));
    }

    private static ContractSpec CreateContract(
        GeneratorAttributeSyntaxContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var model = (INamedTypeSymbol)context.TargetSymbol;
        AttributeData declaration = context.Attributes[0];
        string name = GetConstructorString(declaration, 0);
        string client = GetConstructorString(declaration, 1);
        var serializerContext = declaration.ConstructorArguments.Length > 2
            ? declaration.ConstructorArguments[2].Value as INamedTypeSymbol
            : null;
        string generatedNamespace = GetNamedString(declaration, "GeneratedNamespace");
        string generatedClassName = GetNamedString(declaration, "GeneratedClassName");
        Location location = GetLocation(declaration, model, cancellationToken);
        var members = ImmutableArray.CreateBuilder<MemberSpec>();
        foreach (ISymbol symbol in model.GetMembers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                string? metadataName = attribute.AttributeClass?.ToDisplayString();
                MemberKind? kind = metadataName switch
                {
                    PropertyAttribute => MemberKind.Property,
                    CollectionAttribute => MemberKind.Collection,
                    CommandAttribute => MemberKind.Command,
                    _ => null,
                };
                if (kind is null)
                {
                    continue;
                }

                members.Add(CreateMember(symbol, attribute, kind.Value, cancellationToken));
            }
        }

        return new ContractSpec(
            name,
            client,
            model,
            serializerContext,
            generatedNamespace,
            generatedClassName,
            members.ToImmutable(),
            location);
    }

    private static MemberSpec CreateMember(
        ISymbol symbol,
        AttributeData attribute,
        MemberKind kind,
        CancellationToken cancellationToken)
    {
        int id = attribute.ConstructorArguments.Length > 0
            && attribute.ConstructorArguments[0].Value is int value
                ? value
                : 0;
        string name = GetConstructorString(attribute, 1);
        string sourceMember = GetNamedString(attribute, "SourceMember");
        string typeScriptType = GetNamedString(
            attribute,
            kind == MemberKind.Command ? "TypeScriptArgument" : "TypeScriptType");
        string jsonTypeInfoProperty = GetNamedString(attribute, "JsonTypeInfoProperty");
        bool includeValidation = GetNamedBoolean(attribute, "IncludeValidation");
        bool readOnly = GetNamedBoolean(attribute, "ReadOnly");
        bool isAsync = GetNamedBoolean(attribute, "IsAsync");
        ITypeSymbol? valueType = GetValueType(symbol, kind);

        if (sourceMember.Length == 0)
        {
            sourceMember = symbol switch
            {
                IFieldSymbol field => Pascal(field.Name.TrimStart('_')),
                IMethodSymbol method => Pascal(TrimAsync(method.Name)) + "Command",
                _ => symbol.Name,
            };
        }

        if (kind == MemberKind.Property && symbol is IPropertySymbol property)
        {
            readOnly |= property.SetMethod is null;
        }

        if (kind == MemberKind.Command && symbol is IMethodSymbol commandMethod)
        {
            isAsync |= IsAwaitable(commandMethod.ReturnType);
        }

        return new MemberSpec(
            id,
            name,
            kind,
            sourceMember,
            typeScriptType,
            jsonTypeInfoProperty,
            includeValidation,
            readOnly,
            isAsync,
            valueType,
            GetLocation(attribute, symbol, cancellationToken));
    }

    private static void Emit(
        SourceProductionContext context,
        ImmutableArray<ContractSpec> contracts,
        GenerationOptions options)
    {
        if (!options.Enabled || contracts.IsDefaultOrEmpty)
        {
            return;
        }

        ImmutableArray<ContractSpec> ordered = contracts
            .OrderBy(static contract => contract.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        if (!Validate(context, ordered, options, out string generatedNamespace, out string className))
        {
            return;
        }

        context.AddSource(
            "RunicToolkit.FrontendContracts.g.cs",
            SourceText.From(GenerateCSharp(ordered, generatedNamespace, className), Encoding.UTF8));

        try
        {
            string artifact = GenerateArtifact(
                ordered,
                generatedNamespace,
                className,
                options.ProjectDirectory);
            string? directory = Path.GetDirectoryName(options.ArtifactPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(options.ArtifactPath)
                || !string.Equals(File.ReadAllText(options.ArtifactPath), artifact, StringComparison.Ordinal))
            {
                File.WriteAllText(options.ArtifactPath, artifact, new UTF8Encoding(false));
            }
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                ArtifactFailure,
                ordered[0].Location,
                $"Could not write '{options.ArtifactPath}': {exception.Message}"));
        }
    }

    private static bool Validate(
        SourceProductionContext context,
        ImmutableArray<ContractSpec> contracts,
        GenerationOptions options,
        out string generatedNamespace,
        out string className)
    {
        generatedNamespace = contracts[0].GeneratedNamespace.Length == 0
            ? contracts[0].Model.ContainingNamespace.ToDisplayString()
            : contracts[0].GeneratedNamespace;
        className = contracts[0].GeneratedClassName.Length == 0
            ? "FrontendContracts"
            : contracts[0].GeneratedClassName;
        var valid = true;
        if (options.ArtifactPath.Length == 0)
        {
            Report(context, contracts[0].Location,
                "Set RunicToolkitFrontendContractArtifact when C#-first contracts are enabled.");
            valid = false;
        }

        var contractNames = new HashSet<string>(StringComparer.Ordinal);
        var clients = new HashSet<string>(StringComparer.Ordinal);
        foreach (ContractSpec contract in contracts)
        {
            string contractNamespace = contract.GeneratedNamespace.Length == 0
                ? contract.Model.ContainingNamespace.ToDisplayString()
                : contract.GeneratedNamespace;
            string contractClass = contract.GeneratedClassName.Length == 0
                ? "FrontendContracts"
                : contract.GeneratedClassName;
            if (!string.Equals(generatedNamespace, contractNamespace, StringComparison.Ordinal)
                || !string.Equals(className, contractClass, StringComparison.Ordinal))
            {
                Report(context, contract.Location,
                    "All C#-first contracts in one project must use the same generated namespace and class name.");
                valid = false;
            }

            if (contract.Name.Length == 0 || !contractNames.Add(contract.Name))
            {
                Report(context, contract.Location, $"Contract name '{contract.Name}' must be non-empty and unique.");
                valid = false;
            }

            if (!IsIdentifier(contract.Client) || !clients.Add(contract.Client))
            {
                Report(context, contract.Location, $"Client '{contract.Client}' must be a unique C# identifier.");
                valid = false;
            }

            if (contract.SerializerContext is null)
            {
                Report(context, contract.Location, $"Contract '{contract.Name}' requires a serializer context type.");
                valid = false;
            }

            if (contract.Members.IsDefaultOrEmpty)
            {
                Report(context, contract.Location, $"Contract '{contract.Name}' must project at least one member.");
                valid = false;
                continue;
            }

            var ids = new HashSet<int>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (MemberSpec member in contract.Members)
            {
                if (member.Id <= 0 || !ids.Add(member.Id))
                {
                    Report(context, member.Location,
                        $"Member ID '{member.Id}' in '{contract.Name}' must be positive and unique.");
                    valid = false;
                }

                if (!IsIdentifier(member.Name) || !names.Add(member.Name))
                {
                    Report(context, member.Location,
                        $"Member name '{member.Name}' in '{contract.Name}' must be a unique identifier.");
                    valid = false;
                }

                if (member.Kind != MemberKind.Command && member.ValueType is null)
                {
                    Report(context, member.Location,
                        $"Member '{contract.Name}.{member.Name}' has an unsupported C# shape.");
                    valid = false;
                }

                if (member.Kind == MemberKind.Collection
                    && member.ValueType is not null
                    && TryGetCollectionItem(member.ValueType) is null)
                {
                    Report(context, member.Location,
                        $"Collection '{contract.Name}.{member.Name}' must expose a generic enumerable item type.");
                    valid = false;
                }
            }
        }

        if (!IsQualifiedIdentifier(generatedNamespace) || !IsIdentifier(className))
        {
            Report(context, contracts[0].Location,
                "The generated namespace and class name must be valid C# identifiers.");
            valid = false;
        }

        return valid;
    }

    private static string GenerateCSharp(
        ImmutableArray<ContractSpec> contracts,
        string generatedNamespace,
        string className)
    {
        var output = new StringBuilder();
        output.AppendLine("// <auto-generated />")
            .AppendLine("#nullable enable")
            .Append("namespace ").Append(generatedNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").Append(className).AppendLine()
            .AppendLine("{");
        foreach (ContractSpec contract in contracts)
        {
            string modelType = contract.Model.ToDisplayString(TypeFormat);
            string contextType = contract.SerializerContext!.ToDisplayString(TypeFormat);
            output.Append("    internal static class ").Append(contract.Client).AppendLine()
                .AppendLine("    {")
                .Append("        internal const string Name = ")
                .Append(CSharpString(contract.Name)).AppendLine(";")
                .AppendLine()
                .AppendLine("        internal static class Members")
                .AppendLine("        {");
            foreach (MemberSpec member in contract.Members.OrderBy(static member => member.Id))
            {
                output.Append("            internal const int ")
                    .Append(Pascal(member.Name)).Append(" = ")
                    .Append(member.Id.ToString(CultureInfo.InvariantCulture)).AppendLine(";");
            }

            output.AppendLine("        }")
                .AppendLine()
                .Append("        internal static global::RunicToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmBindingAdapter<")
                .Append(modelType).AppendLine("> CreateAdapter(")
                .Append("            ").Append(modelType).AppendLine(" model) =>")
                .Append("            new global::RunicToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmAdapterBuilder<")
                .Append(modelType).AppendLine(">(model)");
            foreach (MemberSpec member in contract.Members.OrderBy(static member => member.Id))
            {
                AppendBinding(output, member, contextType);
            }

            output.AppendLine("                .Build();")
                .AppendLine("    }")
                .AppendLine();
        }

        return output.AppendLine("}").ToString();
    }

    private static void AppendBinding(StringBuilder output, MemberSpec member, string contextType)
    {
        string method = member.Kind switch
        {
            MemberKind.Property when member.ReadOnly => "BindReadOnlyProperty",
            MemberKind.Property => "BindProperty",
            MemberKind.Collection => "BindCollection",
            MemberKind.Command when member.IsAsync => "BindAsyncCommand",
            _ => "BindCommand",
        };
        output.Append("                .").Append(method).AppendLine("(")
            .Append("                    Members.").Append(Pascal(member.Name)).AppendLine(",")
            .Append("                    ").Append(CSharpString(member.SourceMember)).AppendLine(",")
            .Append("                    static state => state.").Append(member.SourceMember);

        if (member.Kind == MemberKind.Property && !member.ReadOnly)
        {
            output.AppendLine(",")
                .Append("                    static (state, value) => state.")
                .Append(member.SourceMember).AppendLine(" = value,")
                .Append("                    ").Append(JsonTypeInfo(member, contextType));
        }
        else if (member.Kind is MemberKind.Property or MemberKind.Collection)
        {
            output.AppendLine(",")
                .Append("                    ").Append(JsonTypeInfo(member, contextType));
        }
        else if (member.ValueType is not null)
        {
            output.AppendLine(",")
                .Append("                    ").Append(JsonTypeInfo(member, contextType));
        }

        if (member.IncludeValidation)
        {
            output.AppendLine(",")
                .Append("                    includeValidation: true");
        }

        output.AppendLine(")");
    }

    private static string GenerateArtifact(
        ImmutableArray<ContractSpec> contracts,
        string generatedNamespace,
        string className,
        string projectDirectory)
    {
        var root = new Dictionary<string, object?>
        {
            ["$schema"] = "runic.toolkit.mvvm.frontend-contract/1",
            ["csharp"] = new Dictionary<string, object?>
            {
                ["namespace"] = generatedNamespace,
                ["className"] = className,
            },
        };
        var contractDocuments = new List<object>();
        foreach (ContractSpec contract in contracts)
        {
            var types = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            var members = new List<object>();
            foreach (MemberSpec member in contract.Members.OrderBy(static member => member.Id))
            {
                ITypeSymbol? wireType = member.Kind == MemberKind.Collection && member.ValueType is not null
                    ? TryGetCollectionItem(member.ValueType)
                    : member.ValueType;
                string? inferredType = wireType is null
                    ? null
                    : InferTypeScript(wireType, types, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
                var document = new Dictionary<string, object?>
                {
                    ["id"] = member.Id,
                    ["name"] = member.Name,
                    ["kind"] = KindName(member.Kind),
                };
                if (member.Kind is MemberKind.Property or MemberKind.Collection)
                {
                    document["type"] = member.TypeScriptType.Length == 0
                        ? inferredType
                        : member.TypeScriptType;
                }
                else if (member.ValueType is not null)
                {
                    document["argument"] = member.TypeScriptType.Length == 0
                        ? inferredType
                        : member.TypeScriptType;
                }

                if (member.Kind == MemberKind.Property)
                {
                    document["access"] = member.ReadOnly ? "readonly" : "readwrite";
                }

                if (member.IncludeValidation)
                {
                    document["validation"] = true;
                }

                document["source"] = SourceDocument(member.Location, projectDirectory);
                var csharp = new Dictionary<string, object?>
                {
                    ["sourceMember"] = member.SourceMember,
                    ["binding"] = member.Kind switch
                    {
                        MemberKind.Property when member.ReadOnly => "readOnlyProperty",
                        MemberKind.Property => "property",
                        MemberKind.Collection => "collection",
                        MemberKind.Command when member.IsAsync => "asyncCommand",
                        _ => "command",
                    },
                };
                if (member.Kind != MemberKind.Command || member.ValueType is not null)
                {
                    csharp["jsonTypeInfo"] = JsonTypeInfo(
                        member,
                        contract.SerializerContext!.ToDisplayString(TypeFormat));
                }

                document["csharp"] = csharp;
                members.Add(document);
            }

            var contractDocument = new Dictionary<string, object?>
            {
                ["name"] = contract.Name,
                ["client"] = contract.Client,
                ["csharp"] = new Dictionary<string, object?>
                {
                    ["modelType"] = contract.Model.ToDisplayString(TypeFormat),
                },
                ["source"] = SourceDocument(contract.Location, projectDirectory),
            };
            if (types.Count != 0)
            {
                contractDocument["types"] = types;
            }

            contractDocument["members"] = members;
            contractDocuments.Add(contractDocument);
        }

        root["contracts"] = contractDocuments;
        return JsonSerializer.Serialize(root, ArtifactJsonOptions) + "\n";
    }

    private static Dictionary<string, object?> SourceDocument(Location location, string projectDirectory)
    {
        FileLinePositionSpan span = location.GetLineSpan();
        string path = span.Path;
        if (projectDirectory.Length != 0 && Path.IsPathRooted(path))
        {
            path = Path.GetRelativePath(projectDirectory, path);
        }

        return new Dictionary<string, object?>
        {
            ["file"] = path.Replace('\\', '/'),
            ["line"] = span.StartLinePosition.Line + 1,
            ["column"] = span.StartLinePosition.Character + 1,
        };
    }

    private static string InferTypeScript(
        ITypeSymbol type,
        SortedDictionary<string, object?> types,
        HashSet<ITypeSymbol> visiting)
    {
        bool nullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        if (type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            return InferTypeScript(named.TypeArguments[0], types, visiting) + " | null";
        }

        string core = type.SpecialType switch
        {
            SpecialType.System_String or SpecialType.System_Char => "string",
            SpecialType.System_Boolean => "boolean",
            SpecialType.System_Byte or SpecialType.System_SByte
                or SpecialType.System_Int16 or SpecialType.System_UInt16
                or SpecialType.System_Int32 or SpecialType.System_UInt32
                or SpecialType.System_Int64 or SpecialType.System_UInt64
                or SpecialType.System_Single or SpecialType.System_Double
                or SpecialType.System_Decimal => "number",
            _ => string.Empty,
        };
        if (core.Length == 0 && TryGetCollectionItem(type) is ITypeSymbol item)
        {
            core = $"readonly {InferTypeScript(item, types, visiting)}[]";
        }

        if (core.Length == 0 && type is INamedTypeSymbol custom)
        {
            core = custom.Name;
            if (custom.TypeKind == TypeKind.Enum)
            {
                core = "number";
            }
            else if (visiting.Add(custom))
            {
                var fields = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                foreach (IPropertySymbol property in custom.GetMembers()
                    .OfType<IPropertySymbol>()
                    .Where(static property => !property.IsStatic
                        && property.DeclaredAccessibility == Accessibility.Public
                        && property.GetMethod is not null))
                {
                    fields[Camel(property.Name)] = InferTypeScript(property.Type, types, visiting);
                }

                visiting.Remove(custom);
                if (fields.Count != 0)
                {
                    types[custom.Name] = fields;
                }
            }
        }

        if (core.Length == 0)
        {
            core = "unknown";
        }

        return nullable && !core.EndsWith(" | null", StringComparison.Ordinal)
            ? core + " | null"
            : core;
    }

    private static ITypeSymbol? GetValueType(ISymbol symbol, MemberKind kind)
    {
        if (kind == MemberKind.Command)
        {
            if (symbol is IMethodSymbol method)
            {
                IParameterSymbol[] parameters = method.Parameters
                    .Where(static parameter =>
                        parameter.Type.ToDisplayString() != "System.Threading.CancellationToken")
                    .ToArray();
                return parameters.Length == 1 ? parameters[0].Type : null;
            }

            if (symbol is IPropertySymbol property
                && property.Type is INamedTypeSymbol named
                && named.TypeArguments.Length == 1)
            {
                return named.TypeArguments[0];
            }

            return null;
        }

        return symbol switch
        {
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };
    }

    private static ITypeSymbol? TryGetCollectionItem(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        if (type is not INamedTypeSymbol named)
        {
            return null;
        }

        IEnumerable<INamedTypeSymbol> candidates = new[] { named }.Concat(named.AllInterfaces);
        foreach (INamedTypeSymbol candidate in candidates)
        {
            if (candidate.TypeArguments.Length == 1
                && candidate.OriginalDefinition.SpecialType
                    == SpecialType.System_Collections_Generic_IEnumerable_T)
            {
                return candidate.TypeArguments[0];
            }
        }

        return null;
    }

    private static bool IsAwaitable(ITypeSymbol type)
    {
        string name = type.OriginalDefinition.ToDisplayString();
        return name is "System.Threading.Tasks.Task"
            or "System.Threading.Tasks.Task<TResult>"
            or "System.Threading.Tasks.ValueTask"
            or "System.Threading.Tasks.ValueTask<TResult>";
    }

    private static string JsonTypeInfo(MemberSpec member, string contextType)
    {
        ITypeSymbol? type = member.Kind == MemberKind.Collection && member.ValueType is not null
            ? TryGetCollectionItem(member.ValueType)
            : member.ValueType;
        string property = member.JsonTypeInfoProperty.Length == 0
            ? JsonTypeInfoProperty(type)
            : member.JsonTypeInfoProperty;
        return $"{contextType}.Default.{property}";
    }

    private static string JsonTypeInfoProperty(ITypeSymbol? type)
    {
        if (type is null)
        {
            return string.Empty;
        }

        if (type is INamedTypeSymbol nullable
            && nullable.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            type = nullable.TypeArguments[0];
        }

        return type.SpecialType switch
        {
            SpecialType.System_String => "String",
            SpecialType.System_Boolean => "Boolean",
            SpecialType.System_Byte => "Byte",
            SpecialType.System_SByte => "SByte",
            SpecialType.System_Int16 => "Int16",
            SpecialType.System_UInt16 => "UInt16",
            SpecialType.System_Int32 => "Int32",
            SpecialType.System_UInt32 => "UInt32",
            SpecialType.System_Int64 => "Int64",
            SpecialType.System_UInt64 => "UInt64",
            SpecialType.System_Single => "Single",
            SpecialType.System_Double => "Double",
            SpecialType.System_Decimal => "Decimal",
            _ => type.Name,
        };
    }

    private static object SourceValue(TypedConstant value) =>
        value.Value ?? string.Empty;

    private static string GetConstructorString(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index
            ? SourceValue(attribute.ConstructorArguments[index]).ToString() ?? string.Empty
            : string.Empty;

    private static string GetNamedString(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as string
        ?? string.Empty;

    private static bool GetNamedBoolean(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => pair.Key == name).Value.Value as bool?
        ?? false;

    private static Location GetLocation(
        AttributeData attribute,
        ISymbol fallback,
        CancellationToken cancellationToken) =>
        attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation()
        ?? fallback.Locations.FirstOrDefault()
        ?? Location.None;

    private static void Report(SourceProductionContext context, Location location, string message) =>
        context.ReportDiagnostic(Diagnostic.Create(InvalidContract, location, message));

    private static bool IsIdentifier(string value) =>
        !string.IsNullOrEmpty(value)
        && Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(value);

    private static bool IsQualifiedIdentifier(string value) =>
        !string.IsNullOrEmpty(value)
        && value.Split('.').All(IsIdentifier);

    private static string KindName(MemberKind kind) => kind switch
    {
        MemberKind.Property => "property",
        MemberKind.Collection => "collection",
        _ => "command",
    };

    private static string TrimAsync(string value) =>
        value.EndsWith("Async", StringComparison.Ordinal) ? value[..^5] : value;

    private static string Pascal(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string Camel(string value) =>
        value.Length == 0 ? value : char.ToLowerInvariant(value[0]) + value[1..];

    private static string CSharpString(string value) =>
        Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(value, quote: true);

    private enum MemberKind
    {
        Property,
        Collection,
        Command,
    }

    private sealed record ContractSpec(
        string Name,
        string Client,
        INamedTypeSymbol Model,
        INamedTypeSymbol? SerializerContext,
        string GeneratedNamespace,
        string GeneratedClassName,
        ImmutableArray<MemberSpec> Members,
        Location Location);

    private sealed record MemberSpec(
        int Id,
        string Name,
        MemberKind Kind,
        string SourceMember,
        string TypeScriptType,
        string JsonTypeInfoProperty,
        bool IncludeValidation,
        bool ReadOnly,
        bool IsAsync,
        ITypeSymbol? ValueType,
        Location Location);

    private sealed record GenerationOptions(
        bool Enabled,
        string ArtifactPath,
        string ProjectDirectory)
    {
        internal static GenerationOptions Create(AnalyzerConfigOptions options)
        {
            bool enabled = options.TryGetValue(
                "build_property.RunicToolkitFrontendContractCSharpFirst",
                out string? value)
                && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            options.TryGetValue(
                "build_property.RunicToolkitFrontendContractArtifact",
                out string? artifact);
            options.TryGetValue(
                "build_property.MSBuildProjectDirectory",
                out string? projectDirectory);
            return new(
                enabled,
                artifact ?? string.Empty,
                projectDirectory ?? string.Empty);
        }
    }
}
