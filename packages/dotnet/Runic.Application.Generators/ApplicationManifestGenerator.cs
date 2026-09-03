using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Runic.Application.Generators;

/// <summary>Generates the sole application composition manifest from assembly declarations.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ApplicationManifestGenerator : IIncrementalGenerator
{
    private const string ManifestAttribute = "Runic.Application.RunicApplicationManifestAttribute";
    private const string CapabilityAttribute = "Runic.Application.RunicApplicationCapabilityAttribute";
    private const string ArtifactAttribute = "Runic.Application.RunicApplicationArtifactAttribute";
    private const string BridgeCompositionAttribute = "Runic.Application.RunicApplicationBridgeCompositionAttribute";
    private static readonly DiagnosticDescriptor MissingManifest = new(
        "RAPP0000",
        "Application manifest is required",
        "Declare [assembly: RunicApplicationManifest(\"entry-point\")] so Runic.Application can generate the composition manifest.",
        "Runic.Application",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor PreviewIdentity = new(
        "RAPP0001",
        "Preview package identity must migrate",
        "Reference '{0}' is a preview identity. {1}",
        "Runic.Application.Migration",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
    private static readonly DiagnosticDescriptor InvalidDeclaration = new(
        "RAPP0002",
        "Application manifest declaration is invalid",
        "{0}",
        "Runic.Application",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<AdditionalText>> bridgeManifests = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith("bridge.ir.json", StringComparison.OrdinalIgnoreCase))
            .Collect();
        context.RegisterSourceOutput(context.CompilationProvider.Combine(bridgeManifests), static (productionContext, input) =>
        {
            ReportPreviewReferences(productionContext, input.Left);
            EmitManifest(productionContext, input.Left, BridgeContracts(input.Right, productionContext.CancellationToken));
        });
    }

    private static void EmitManifest(SourceProductionContext context, Compilation compilation, ImmutableArray<BridgeContract> bridgeContracts)
    {
        if (string.Equals(compilation.Assembly.Name, "Runic.Application", StringComparison.Ordinal))
        {
            return;
        }

        ImmutableArray<AttributeData> attributes = compilation.Assembly.GetAttributes();
        ImmutableArray<AttributeData> manifests = attributes.Where(static attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), ManifestAttribute, StringComparison.Ordinal)).ToImmutableArray();
        if (manifests.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(MissingManifest, Location.None));
            return;
        }
        if (manifests.Length != 1)
        {
            foreach (AttributeData duplicate in manifests)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidDeclaration, AttributeLocation(duplicate), "Declare exactly one RunicApplicationManifest attribute."));
            }
            return;
        }
        AttributeData manifest = manifests[0];

        ImmutableArray<AttributeData> bridgeCompositions = attributes.Where(static attribute =>
            string.Equals(attribute.AttributeClass?.ToDisplayString(), BridgeCompositionAttribute, StringComparison.Ordinal)).ToImmutableArray();
        if (bridgeCompositions.Length > 1)
        {
            foreach (AttributeData duplicate in bridgeCompositions)
            {
                context.ReportDiagnostic(Diagnostic.Create(InvalidDeclaration, AttributeLocation(duplicate), "Declare at most one RunicApplicationBridgeComposition attribute."));
            }
            return;
        }

        string? entryPoint = Argument(manifest, 0);
        string version = Named(manifest, "Version") ?? "0.0.0";
        string provenance = Named(manifest, "Provenance") ?? "local";
        if (string.IsNullOrWhiteSpace(entryPoint) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(provenance))
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidDeclaration, AttributeLocation(manifest), "Entry point, version, and provenance must be non-blank."));
            return;
        }

        ImmutableArray<AttributeData> capabilityAttributes = attributes
            .Where(static attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), CapabilityAttribute, StringComparison.Ordinal))
            .ToImmutableArray();
        ImmutableArray<string> capabilities = capabilityAttributes
            .Select(static attribute => Argument(attribute, 0))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        ImmutableArray<AttributeData> artifactAttributes = attributes
            .Where(static attribute => string.Equals(attribute.AttributeClass?.ToDisplayString(), ArtifactAttribute, StringComparison.Ordinal))
            .ToImmutableArray();
        ImmutableArray<(string Kind, string Identity, string Fingerprint)> declaredArtifacts = artifactAttributes
            .Select(static attribute => (Argument(attribute, 0), Argument(attribute, 1), Argument(attribute, 2)))
            .Where(static value => !string.IsNullOrWhiteSpace(value.Item1) && !string.IsNullOrWhiteSpace(value.Item2) && !string.IsNullOrWhiteSpace(value.Item3))
            .Select(static value => (value.Item1!, value.Item2!, value.Item3!))
            .OrderBy(static value => value.Item1, StringComparer.Ordinal)
            .ThenBy(static value => value.Item2, StringComparer.Ordinal)
            .ThenBy(static value => value.Item3, StringComparer.Ordinal)
            .Distinct()
            .ToImmutableArray();
        ImmutableArray<(string Kind, string Identity, string Fingerprint)> artifacts = declaredArtifacts
            .Concat(bridgeContracts.Select(static contract => (
                "bridge-contract",
                contract.Identity,
                contract.Fingerprint)))
            .Distinct()
            .OrderBy(static value => value.Item1, StringComparer.Ordinal)
            .ThenBy(static value => value.Item2, StringComparer.Ordinal)
            .ThenBy(static value => value.Item3, StringComparer.Ordinal)
            .ToImmutableArray();
        if (capabilities.Length != capabilityAttributes.Length || declaredArtifacts.Length != artifactAttributes.Length)
        {
            context.ReportDiagnostic(Diagnostic.Create(InvalidDeclaration, AttributeLocation(manifest), "Capabilities and artifacts must be non-blank and declared at most once."));
            return;
        }

        string typeSuffix = StableTypeSuffix(compilation.Assembly.Name ?? "application");
        string generatedTypeName = "Runic.Application.Generated.ManifestRegistration_" + typeSuffix;
        if (compilation.GetTypeByMetadataName(generatedTypeName) is not null)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidDeclaration,
                AttributeLocation(manifest),
                $"The generated manifest registration type '{generatedTypeName}' already exists. Rename the conflicting user type."));
            return;
        }
        var source = new StringBuilder("// <auto-generated/>\n// runic.application/1: ");
        source.Append(CanonicalManifestJson(entryPoint!, version, provenance, capabilities, artifacts));
        source.Append("\n#nullable enable\nnamespace Runic.Application.Generated;\ninternal static class ManifestRegistration_").Append(typeSuffix).Append("\n{\n    [global::System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void Register() => global::Runic.Application.RunicApplicationManifestRegistry.Register(new(\n        ");
        source.Append(Literal(entryPoint!)).Append(",\n        ").Append(Literal(version)).Append(",\n        ").Append(Literal(provenance)).Append(",\n        new string[] { ");
        AppendLiterals(source, capabilities);
        source.Append(" },\n        new global::Runic.Application.ApplicationManifestArtifact[] { ");
        for (int index = 0; index < artifacts.Length; index++)
        {
            if (index > 0) source.Append(", ");
            (string kind, string identity, string fingerprint) = artifacts[index];
            source.Append("new(").Append(Literal(kind)).Append(", ").Append(Literal(identity)).Append(", ").Append(Literal(fingerprint)).Append(')');
        }

        source.Append(" }));\n}\n");
        if (bridgeCompositions.Length == 1)
        {
            AttributeData composition = bridgeCompositions[0];
            INamedTypeSymbol? handler = TypeArgument(composition, 0);
            INamedTypeSymbol? dispatcher = TypeArgument(composition, 1);
            string? handlerFailure = null;
            bool validHandler = handler is not null && CanConstructHandler(handler, out handlerFailure);
            if (!validHandler)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidDeclaration,
                    AttributeLocation(composition),
                    handlerFailure ?? "RunicApplicationBridgeComposition requires a concrete handler type."));
                return;
            }
            string? dispatcherFailure = null;
            bool validDispatcher = dispatcher is not null && CanConstructDispatcher(dispatcher, handler!, compilation, out dispatcherFailure);
            if (!validDispatcher)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidDeclaration,
                    AttributeLocation(composition),
                    dispatcherFailure ?? "RunicApplicationBridgeComposition requires a compatible dispatcher type."));
                return;
            }

            string bridgeRegistration = "Runic.Application.Generated.BridgeCompositionRegistration_" + typeSuffix;
            if (compilation.GetTypeByMetadataName(bridgeRegistration) is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    InvalidDeclaration,
                    AttributeLocation(composition),
                    $"The generated bridge composition registration type '{bridgeRegistration}' already exists. Rename the conflicting user type."));
                return;
            }

            source.Append("internal static class BridgeCompositionRegistration_").Append(typeSuffix).Append("\n{\n    [global::System.Runtime.CompilerServices.ModuleInitializer]\n    internal static void Register() => global::Runic.Application.RunicApplicationBridgeCompositionRegistry.Register(static () => new global::Runic.Application.Bridge.ApplicationBridgeSession(new ")
                .Append(DeclaredTypeName(dispatcher!, handler!, bridgeContracts)).Append("(new ")
                .Append(DeclaredTypeName(handler!, null, bridgeContracts)).Append("())));\n}\n");
        }
        context.AddSource("Runic.Application.GeneratedManifest.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static void ReportPreviewReferences(SourceProductionContext context, Compilation compilation)
    {
        foreach (IAssemblySymbol reference in compilation.References.Select(compilation.GetAssemblyOrModuleSymbol).OfType<IAssemblySymbol>().OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            string? destination = reference.Name switch
            {
                "RunicToolkit.Hosting" or "RunicToolkit.Desktop" or "RunicToolkit.Hosting.Abstractions" or "RunicToolkit.Hosting.Generators" or "RunicToolkit.Hosting.WebUi" => "Move the application reference to Runic.Application.",
                "RunicToolkit.Hosting.GenericHost" => "Move the Generic Host integration reference to Runic.Application.Hosting.",
                "RunicToolkit.Hosting.CsWebUi" or "RunicToolkit.Hosting.CsWebUi.App" or "RunicToolkit.Hosting.CsWebUi.ApplicationBridge" => "Remove the retired CS-WEBUI integration and compose Runic.Application.Desktop, or use standalone CsWebUi directly.",
                _ => null,
            };
            if (destination is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(PreviewIdentity, Microsoft.CodeAnalysis.Location.None, reference.Name, destination));
            }
        }
    }

    private static string? Argument(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index ? attribute.ConstructorArguments[index].Value as string : null;

    private static INamedTypeSymbol? TypeArgument(AttributeData attribute, int index) =>
        attribute.ConstructorArguments.Length > index
            ? attribute.ConstructorArguments[index].Value as INamedTypeSymbol
            : null;

    private static bool CanConstructHandler(INamedTypeSymbol type, out string? failure)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || !IsGeneratedAccessible(type))
        {
            failure = $"Bridge handler '{type.ToDisplayString()}' must be an accessible, non-abstract, non-generic class.";
            return false;
        }

        if (!type.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 0 && IsGeneratedAccessible(constructor)))
        {
            failure = $"Bridge handler '{type.ToDisplayString()}' must have an accessible parameterless constructor.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool CanConstructDispatcher(
        INamedTypeSymbol dispatcher,
        INamedTypeSymbol handler,
        Compilation compilation,
        out string? failure)
    {
        if (dispatcher.TypeKind == TypeKind.Error)
        {
            if (IsExpectedGeneratedDispatcher(dispatcher, handler))
            {
                failure = null;
                return true;
            }

            failure = $"Bridge dispatcher '{dispatcher.ToDisplayString()}' must be a generated dispatcher for a handler contract or an accessible, concrete dispatcher class.";
            return false;
        }

        if (dispatcher.TypeKind != TypeKind.Class || dispatcher.IsAbstract || dispatcher.IsGenericType || !IsGeneratedAccessible(dispatcher))
        {
            failure = $"Bridge dispatcher '{dispatcher.ToDisplayString()}' must be an accessible, non-abstract, non-generic class.";
            return false;
        }

        INamedTypeSymbol? dispatcherContract = compilation.GetTypeByMetadataName("Runic.Application.Bridge.IApplicationBridgeDispatcher");
        if (dispatcherContract is null || !dispatcher.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, dispatcherContract)))
        {
            failure = $"Bridge dispatcher '{dispatcher.ToDisplayString()}' must implement Runic.Application.Bridge.IApplicationBridgeDispatcher.";
            return false;
        }

        if (!dispatcher.InstanceConstructors.Any(constructor =>
                constructor.Parameters.Length == 1 &&
                IsGeneratedAccessible(constructor) &&
                CanPassHandler(handler, constructor.Parameters[0].Type)))
        {
            failure = $"Bridge dispatcher '{dispatcher.ToDisplayString()}' must have an accessible one-argument constructor compatible with handler '{handler.ToDisplayString()}'.";
            return false;
        }

        failure = null;
        return true;
    }

    private static bool IsExpectedGeneratedDispatcher(INamedTypeSymbol dispatcher, INamedTypeSymbol handler)
    {
        foreach (INamedTypeSymbol contract in handler.Interfaces.Concat(handler.AllInterfaces))
        {
            if (IsExpectedDispatcherName(dispatcher.Name, contract.Name)) return true;
        }

        // A dispatcher emitted by another source generator is represented as an
        // ErrorType in this generator's compilation. The handler's declared
        // generated-contract interface is the only source evidence accepted in
        // that case; ordinary source-defined types still go through complete
        // dispatcher validation above.
        foreach (SyntaxReference declaration in handler.DeclaringSyntaxReferences)
        {
            if (declaration.GetSyntax() is not TypeDeclarationSyntax type || type.BaseList is null) continue;
            foreach (BaseTypeSyntax baseType in type.BaseList.Types)
            {
                string contractName = baseType.Type switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                    AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                    _ => string.Empty,
                };
                if (IsExpectedDispatcherName(dispatcher.Name, contractName)) return true;
            }
        }
        return false;
    }

    private static bool IsExpectedDispatcherName(string dispatcherName, string contractName) =>
        contractName.StartsWith('I') &&
        contractName.EndsWith("BridgeHandler", StringComparison.Ordinal) &&
        string.Equals(dispatcherName, contractName[1..^"Handler".Length] + "Dispatcher", StringComparison.Ordinal);

    private static string DeclaredTypeName(INamedTypeSymbol type, INamedTypeSymbol? handler, ImmutableArray<BridgeContract> bridgeContracts)
    {
        if (type.TypeKind != TypeKind.Error)
        {
            return type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        }

        INamespaceSymbol? contractNamespace = handler?.Interfaces.Concat(handler.AllInterfaces)
            .FirstOrDefault(candidate => IsExpectedDispatcherName(type.Name, candidate.Name))?.ContainingNamespace;
        if (contractNamespace is not null)
        {
            return QualifiedTypeName(contractNamespace, type.Name);
        }

        BridgeContract? generatedContract = bridgeContracts.FirstOrDefault(contract =>
            IsExpectedDispatcherName(type.Name, "I" + contract.Name + "BridgeHandler"));
        return generatedContract is null
            ? type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            : QualifiedTypeName(generatedContract.Namespace, type.Name);
    }

    private static string QualifiedTypeName(INamespaceSymbol typeNamespace, string typeName) =>
        typeNamespace.IsGlobalNamespace
            ? $"global::{typeName}"
            : $"global::{typeNamespace.ToDisplayString()}.{typeName}";

    private static string QualifiedTypeName(string typeNamespace, string typeName) =>
        string.IsNullOrWhiteSpace(typeNamespace)
            ? $"global::{typeName}"
            : $"global::{typeNamespace}.{typeName}";

    private static ImmutableArray<BridgeContract> BridgeContracts(ImmutableArray<AdditionalText> manifests, CancellationToken cancellationToken)
    {
        AdditionalText? manifest = manifests
            .OrderBy(static file => file.Path, StringComparer.Ordinal)
            .FirstOrDefault();
        if (manifest is null)
        {
            return [];
        }

        SourceText? text = manifest.GetText(cancellationToken);
        if (text is null)
        {
            return [];
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(text.ToString());
            JsonElement csharp = document.RootElement.GetProperty("csharp");
            string? typeNamespace = csharp.GetProperty("namespace").GetString();
            string? name = csharp.GetProperty("contractName").GetString();
            JsonElement wire = document.RootElement.GetProperty("wire");
            JsonElement protocol = wire.GetProperty("protocol");
            string? identity = protocol.GetProperty("identity").GetString();
            int version = protocol.GetProperty("version").GetInt32();
            string? fingerprint = document.RootElement.GetProperty("fingerprint").GetProperty("value").GetString();
            return !string.IsNullOrWhiteSpace(typeNamespace) && !string.IsNullOrWhiteSpace(name) &&
                   !string.IsNullOrWhiteSpace(identity) && !string.IsNullOrWhiteSpace(fingerprint)
                ? [new(typeNamespace, name, $"{identity}/{version}", fingerprint)]
                : [];
        }
        catch (JsonException)
        {
            // The Bridge generator owns Bridge IR diagnostics.
        }
        catch (KeyNotFoundException)
        {
            // The Bridge generator owns Bridge IR diagnostics.
        }
        catch (InvalidOperationException)
        {
            // The Bridge generator owns Bridge IR diagnostics.
        }
        return [];
    }

    private static bool CanPassHandler(INamedTypeSymbol handler, ITypeSymbol parameter) =>
        SymbolEqualityComparer.Default.Equals(handler, parameter) ||
        handler.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, parameter)) ||
        Inherits(handler, parameter);

    private static bool Inherits(INamedTypeSymbol type, ITypeSymbol candidate)
    {
        for (INamedTypeSymbol? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, candidate)) return true;
        }
        return false;
    }

    private static bool IsGeneratedAccessible(ISymbol symbol) =>
        symbol.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal;

    private static string? Named(AttributeData attribute, string name) =>
        attribute.NamedArguments.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)).Value.Value as string;

    private static Location AttributeLocation(AttributeData attribute) => attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation() ?? Microsoft.CodeAnalysis.Location.None;

    private static void AppendLiterals(StringBuilder source, ImmutableArray<string> values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (index > 0) source.Append(", ");
            source.Append(Literal(values[index]));
        }
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    private static string CanonicalManifestJson(
        string entryPoint,
        string version,
        string provenance,
        ImmutableArray<string> capabilities,
        ImmutableArray<(string Kind, string Identity, string Fingerprint)> artifacts)
    {
        var json = new StringBuilder("{\"schema\":\"runic.application/1\",\"entryPoint\":");
        json.Append(JsonLiteral(entryPoint)).Append(",\"version\":").Append(JsonLiteral(version)).Append(",\"provenance\":").Append(JsonLiteral(provenance)).Append(",\"capabilities\":[");
        AppendJsonLiterals(json, capabilities);
        json.Append("],\"artifacts\":[");
        for (int index = 0; index < artifacts.Length; index++)
        {
            if (index > 0) json.Append(',');
            (string kind, string identity, string fingerprint) = artifacts[index];
            json.Append("{\"kind\":").Append(JsonLiteral(kind)).Append(",\"identity\":").Append(JsonLiteral(identity)).Append(",\"fingerprint\":").Append(JsonLiteral(fingerprint)).Append('}');
        }
        return json.Append("]}").ToString();
    }

    private static string StableTypeSuffix(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value)
        {
            hash = (hash ^ character) * 16777619;
        }
        return hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AppendJsonLiterals(StringBuilder source, ImmutableArray<string> values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (index > 0) source.Append(',');
            source.Append(JsonLiteral(values[index]));
        }
    }

    private sealed record BridgeContract(string Namespace, string Name, string Identity, string Fingerprint);

    private static string JsonLiteral(string value) => "\"" + System.Text.Json.JsonEncodedText.Encode(value).ToString() + "\"";
}
