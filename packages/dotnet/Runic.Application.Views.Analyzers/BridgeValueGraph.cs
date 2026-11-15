using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Runic.Application.Views.Analyzers;

/// <summary>Where a Bridge value crosses (BridgeValueDirection of the generator).</summary>
[Flags]
internal enum ValueDirection { ToPage = 1, FromPage = 2, Both = ToPage | FromPage }

/// <summary>The wire kinds of the generator's BridgeWireKind that the rules tell apart.</summary>
internal enum ValueKind { Scalar, String, Blob, Upload, Enum, Union, Custom, TextReference, Json, Array, List, StringDictionary, Dto }

internal sealed class ValueNode(ITypeSymbol type, bool isNullable)
{
    /// <summary>The type without Nullable&lt;T&gt; and without a nullable annotation.</summary>
    internal ITypeSymbol Type { get; } = type;
    internal bool IsNullable { get; } = isNullable;
    internal ValueKind Kind { get; set; }
    internal ValueNode? Element { get; set; }
    internal ValueNode? Value { get; set; }
    internal List<ValueMember> Members { get; } = [];
    internal List<ValueNode> Cases { get; } = [];
    internal IMethodSymbol? Constructor { get; set; }
}

internal sealed class ValueMember(IPropertySymbol property, string wireName, ValueNode type)
{
    internal IPropertySymbol Property { get; } = property;
    internal string WireName { get; } = wireName;
    internal ValueNode Type { get; } = type;
    internal bool Readable => Property.GetMethod?.DeclaredAccessibility == Accessibility.Public;
    internal bool Writable => Property.SetMethod?.DeclaredAccessibility == Accessibility.Public;
}

/// <summary>A value that is not a Bridge value: BridgeTypeGraphException, "{path}: {detail}".</summary>
internal sealed class ValueGraphException(string path, string detail, ISymbol? member = null, string? code = null) : Exception($"{path}: {detail}")
{
    internal string Path { get; } = path;
    internal string Detail { get; } = detail;
    internal ISymbol? Member { get; } = member;
    /// <summary>The rule, when it is not RUNICBRIDGE003.</summary>
    internal string? Code { get; } = code;
}

/// <summary>
/// The value graph of one Bridge value from symbols: the generator's BridgeTypeGraph.Builder (BuildNode, BuildUnion,
/// TryCollection, TryStringDictionary, UnsupportedCollection, IsDto, DataProperties, SelectConstructor), with its
/// messages and paths. Nullability comes from the declarations' annotations; Roslyn substitutes a generic DTO's type
/// arguments with the use site's, as the generator does with the compiler's annotation bytes.
/// </summary>
internal sealed class BridgeValueGraph
{
    private readonly BridgeSymbols _symbols;
    private readonly bool _honorJsonIgnore;
    private readonly ValueDirection _direction;

    private BridgeValueGraph(BridgeSymbols symbols, bool honorJsonIgnore, ValueDirection direction)
    {
        _symbols = symbols;
        _honorJsonIgnore = honorJsonIgnore;
        _direction = direction;
    }

    internal List<ValueNode> Nodes { get; } = [];
    internal ValueNode Root { get; private set; } = null!;

    /// <summary>
    /// The graph of a declared type. With <paramref name="annotated"/> false the root and its elements are not
    /// nullable, as the generator treats a type without annotations (typeof, a contract argument).
    /// </summary>
    internal static BridgeValueGraph Discover(BridgeSymbols symbols, ITypeSymbol type, string path, ISymbol? origin, bool honorJsonIgnore,
        ValueDirection direction, bool annotated = true)
    {
        var graph = new BridgeValueGraph(symbols, honorJsonIgnore, direction);
        graph.Root = graph.Build(type, annotated, path, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), null, origin: origin);
        return graph;
    }

    /// <summary>BridgeDtoContract.HonorsJsonIgnore: unless the model assembly declares [assembly: RunicIncludeJsonIgnored].</summary>
    internal static bool HonorsJsonIgnore(IAssemblySymbol assembly) =>
        BridgeSymbols.Attribute(assembly, "Runic.Application.Views.RunicIncludeJsonIgnoredAttribute") is null;

    private ValueNode Build(ITypeSymbol declared, bool annotated, string path, HashSet<ITypeSymbol> stack, AttributeData? memberCodec,
        bool unionCase = false, ISymbol? origin = null)
    {
        try
        {
            return BuildNode(declared, annotated, path, stack, memberCodec, unionCase);
        }
        catch (ValueGraphException error) when (error.Member is null && origin is not null)
        {
            throw new ValueGraphException(error.Path, error.Detail, origin, error.Code);
        }
    }

    private ValueNode BuildNode(ITypeSymbol declared, bool annotated, string path, HashSet<ITypeSymbol> stack, AttributeData? memberCodec, bool unionCase)
    {
        var underlying = Underlying(declared);
        var nullable = declared.IsValueType ? !SymbolEqualityComparer.Default.Equals(underlying, declared)
            : annotated && declared.NullableAnnotation == NullableAnnotation.Annotated;
        var type = underlying.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        if (stack.Contains(type)) throw new ValueGraphException(path, "Recursive DTO graphs are not a bridge contract.");
        var node = new ValueNode(type, nullable);
        Nodes.Add(node);
        if (type.SpecialType == SpecialType.System_String) { node.Kind = ValueKind.String; return node; }
        if (IsScalar(type)) { node.Kind = ValueKind.Scalar; return node; }
        var name = BridgeSymbols.MetadataName(type);
        if (name == "Runic.Application.Views.RunicBlob")
        {
            if (_direction == ValueDirection.FromPage)
                throw new ValueGraphException(path, "A RunicBlob only goes to the page: it cannot be a command input or an interaction result. Take a RunicUpload, which the page creates with client.upload(file).",
                    code: BridgeRules.Blob.Id);
            node.Kind = ValueKind.Blob;
            return node;
        }
        if (name == "Runic.Application.Views.RunicUpload") { node.Kind = ValueKind.Upload; return node; }
        if (type.TypeKind == TypeKind.Enum)
        {
            if (BridgeSymbols.Attribute(type, "System.FlagsAttribute") is not null)
                throw new ValueGraphException(path, "Flags enums need an explicit combination codec.");
            node.Kind = ValueKind.Enum;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in type.GetMembers().OfType<IFieldSymbol>().Where(field => field.IsStatic && field.HasConstantValue
                && field.DeclaredAccessibility == Accessibility.Public))
            {
                var wire = BridgeSymbols.Attribute(field, "Runic.Application.Views.RunicAliasAttribute") is { ConstructorArguments.Length: 1 } alias
                    && alias.ConstructorArguments[0].Value is string aliasName ? aliasName : field.Name;
                if (!names.Add(wire)) throw new ValueGraphException(path, $"Enum wire name {wire} is declared more than once.");
            }
            return node;
        }
        if (BridgeSymbols.Attribute(type, "Runic.Application.Views.RunicUnionAttribute") is { } union)
        {
            if (memberCodec is not null || BridgeSymbols.Attribute(type, "Runic.Application.Views.RunicCodecAttribute") is not null)
                throw new ValueGraphException(path, "A closed union cannot also use an opaque custom codec.");
            BuildUnion(node, union, path, stack);
            return node;
        }
        if ((memberCodec ?? BridgeSymbols.Attribute(type, "Runic.Application.Views.RunicCodecAttribute")) is { } custom)
        {
            node.Kind = ValueKind.Custom;
            CheckCodec(type, custom, path);
            return node;
        }
        if (name == "Runic.Application.Views.RunicTextReference"
            || name == "Runic.Translations.TranslationReference" && type.ContainingAssembly?.Name == "Runic.Translations")
        {
            node.Kind = ValueKind.TextReference;
            return node;
        }
        if (name is "System.Text.Json.JsonElement" or "System.Text.Json.Nodes.JsonNode") { node.Kind = ValueKind.Json; return node; }
        if (DerivesFrom(type, "System.Text.Json.Nodes.JsonNode") || name == "System.Text.Json.JsonDocument")
            throw new ValueGraphException(path, $"{TypeIdentity(type)} is not a bridge value. Declare JsonNode or JsonElement for any JSON value, or a DTO for a known shape.");
        if (TryCollection(type, path, out var kind, out var element))
        {
            node.Kind = kind;
            stack.Add(type);
            node.Element = Build(element!, annotated, path + "[]", stack, null);
            stack.Remove(type);
            return node;
        }
        if (TryStringDictionary(type, out var value))
        {
            node.Kind = ValueKind.StringDictionary;
            stack.Add(type);
            node.Value = Build(value!, annotated, path + "{}", stack, null);
            stack.Remove(type);
            return node;
        }
        if (UnsupportedCollection(type) is { } collectionError) throw new ValueGraphException(path, collectionError);
        if (!IsDto(type))
            throw new ValueGraphException(path, $"{TypeIdentity(type)} is not an explicitly supported bridge value. Use a supported scalar, collection, public DTO, RunicUnion, or RunicCodec.{NavigationResultHint(type)}");
        node.Kind = ValueKind.Dto;
        stack.Add(type);
        var members = DataProperties((INamedTypeSymbol)type, path).ToArray();
        if (members.Length == 0 && !unionCase)
            throw new ValueGraphException(path, $"{TypeIdentity(type)} has no public readable properties, so it would always serialize as {{}}. Add properties or a RunicCodec.");
        foreach (var (property, wire) in members)
            node.Members.Add(new ValueMember(property, wire, Build(property.Type, true, path + "." + wire, stack,
                BridgeSymbols.Attribute(property, "Runic.Application.Views.RunicCodecAttribute"), origin: property)));
        if (_direction.HasFlag(ValueDirection.FromPage)) node.Constructor = SelectConstructor((INamedTypeSymbol)type, node.Members, path);
        stack.Remove(type);
        return node;
    }

    private void BuildUnion(ValueNode node, AttributeData union, string path, HashSet<ITypeSymbol> stack)
    {
        if (!node.Type.IsAbstract && node.Type.TypeKind != TypeKind.Interface)
            throw new ValueGraphException(path, "A bridge union root must be an abstract class or interface.");
        node.Kind = ValueKind.Union;
        var cases = union.ConstructorArguments.SelectMany(argument => argument.Kind == TypedConstantKind.Array ? argument.Values : [argument])
            .Select(argument => argument.Value).OfType<INamedTypeSymbol>()
            .OrderBy(type => TypeIdentity(type), StringComparer.Ordinal).ToArray();
        var seenTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var seenTags = new HashSet<string>(StringComparer.Ordinal);
        stack.Add(node.Type);
        foreach (var type in cases)
        {
            if (!seenTypes.Add(type) || type.DeclaredAccessibility != Accessibility.Public || type.ContainingType is not null || type.IsAbstract
                || type.IsUnboundGenericType || type.TypeParameters.Length > 0 && SymbolEqualityComparer.Default.Equals(type, type.OriginalDefinition)
                || !_symbols.IsAssignable(type, node.Type))
                throw new ValueGraphException(path, $"{TypeIdentity(type)} is not a distinct public concrete case of {TypeIdentity(node.Type)}.");
            if (BridgeSymbols.Attribute(type, "Runic.Application.Views.RunicCodecAttribute") is not null)
                throw new ValueGraphException(path, $"{TypeIdentity(type)} cannot be both a union case and an opaque custom codec.");
            var tag = BridgeSymbols.Attribute(type, "Runic.Application.Views.RunicUnionCaseAttribute") is { ConstructorArguments.Length: 1 } caseAttribute
                ? caseAttribute.ConstructorArguments[0].Value as string : null;
            if (string.IsNullOrWhiteSpace(tag) || tag!.Length > 128 || tag.Any(char.IsControl) || !seenTags.Add(tag))
                throw new ValueGraphException(path, $"{TypeIdentity(type)} needs a unique RunicUnionCase discriminator of at most 128 printable characters.");
            var caseNode = Build(type, false, path + "." + tag, stack, null, unionCase: true, origin: type);
            if (caseNode.Members.Any(member => member.WireName == "$case"))
                throw new ValueGraphException(path + "." + tag, "A union case cannot export the reserved $case field.");
            node.Cases.Add(caseNode);
        }
        stack.Remove(node.Type);
    }

    // CustomCodecDescription.Create: the codec implements IRunicCodec<T> of the value type and declares its TypeScript shape.
    private static void CheckCodec(ITypeSymbol valueType, AttributeData codec, string path)
    {
        if (codec.ConstructorArguments.Length != 1 || codec.ConstructorArguments[0].Value is not INamedTypeSymbol codecType) return;
        if (!codecType.AllInterfaces.Any(candidate => BridgeSymbols.MetadataName(candidate) == "Runic.Application.Views.IRunicCodec`1"
            && SymbolEqualityComparer.Default.Equals(candidate.TypeArguments[0].WithNullableAnnotation(NullableAnnotation.NotAnnotated), valueType)))
            throw new ValueGraphException(path, $"{CSharpType(codecType)} must implement IRunicCodec<{CSharpType(valueType)}>.");
        var shape = BridgeSymbols.Attribute(codecType, "Runic.Application.Views.RunicCodecShapeAttribute");
        if (shape is null || shape.ConstructorArguments.Length < 2
            || string.IsNullOrWhiteSpace(shape.ConstructorArguments[0].Value as string) || string.IsNullOrWhiteSpace(shape.ConstructorArguments[1].Value as string))
            throw new ValueGraphException(path, $"{CSharpType(codecType)} must provide TypeScriptType and TypeScriptDecoderExpression metadata.");
    }

    private static readonly HashSet<string> ScalarNames = new(StringComparer.Ordinal)
    {
        "System.Guid", "System.DateOnly", "System.TimeOnly", "System.DateTime", "System.DateTimeOffset", "System.TimeSpan",
        "System.Numerics.BigInteger",
    };

    private static bool IsScalar(ITypeSymbol type) => type.SpecialType is SpecialType.System_Boolean or SpecialType.System_SByte
        or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
        or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single
        or SpecialType.System_Double or SpecialType.System_Decimal or SpecialType.System_DateTime
        || ScalarNames.Contains(BridgeSymbols.MetadataName(type));

    private static readonly HashSet<string> SupportedCollections = new(StringComparer.Ordinal)
    {
        "System.Collections.Generic.List`1", "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IReadOnlyCollection`1",
        "System.Collections.Generic.IList`1", "System.Collections.Generic.ICollection`1", "System.Collections.Generic.IEnumerable`1",
        "System.Collections.Immutable.ImmutableArray`1", "System.Collections.Immutable.ImmutableList`1",
        "System.Collections.ObjectModel.ObservableCollection`1", "System.Collections.ObjectModel.ReadOnlyObservableCollection`1",
        "System.Collections.ObjectModel.ReadOnlyCollection`1",
    };

    private static bool TryCollection(ITypeSymbol type, string path, out ValueKind kind, out ITypeSymbol? element)
    {
        if (type is IArrayTypeSymbol array)
        {
            if (array.Rank != 1)
                throw new ValueGraphException(path, "Multi-dimensional arrays are not a bridge contract; use a jagged array or a list.");
            kind = ValueKind.Array; element = array.ElementType; return true;
        }
        if (type is INamedTypeSymbol { IsGenericType: true } named && SupportedCollections.Contains(BridgeSymbols.MetadataName(named)))
        {
            kind = ValueKind.List; element = named.TypeArguments[0]; return true;
        }
        kind = ValueKind.Scalar; element = null;
        return false;
    }

    private static IEnumerable<INamedTypeSymbol> SelfAndInterfaces(ITypeSymbol type) =>
        type.AllInterfaces.Concat(type is INamedTypeSymbol named ? [named] : []);

    private static string? UnsupportedCollection(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return null;
        if (SelfAndInterfaces(type).Any(candidate => BridgeSymbols.MetadataName(candidate) is "System.Collections.Generic.IDictionary`2" or "System.Collections.Generic.IReadOnlyDictionary`2"))
            return $"{TypeIdentity(type)} is not a supported bridge dictionary. Use Dictionary<string, T>, IDictionary<string, T>, or IReadOnlyDictionary<string, T>.";
        return SelfAndInterfaces(type).Any(candidate => BridgeSymbols.MetadataName(candidate) == "System.Collections.IEnumerable")
            ? $"{TypeIdentity(type)} is not a supported bridge collection. Use T[], List<T>, IReadOnlyList<T>, IReadOnlyCollection<T>, IList<T>, ICollection<T>, IEnumerable<T>, ImmutableArray<T>, ImmutableList<T>, ObservableCollection<T>, ReadOnlyObservableCollection<T>, or ReadOnlyCollection<T>."
            : null;
    }

    private static bool TryStringDictionary(ITypeSymbol type, out ITypeSymbol? value)
    {
        var dictionary = SelfAndInterfaces(type).FirstOrDefault(candidate =>
            BridgeSymbols.MetadataName(candidate) is "System.Collections.Generic.IReadOnlyDictionary`2" or "System.Collections.Generic.IDictionary`2"
            && candidate.TypeArguments[0].SpecialType == SpecialType.System_String);
        value = dictionary?.TypeArguments[1];
        return value is not null && (type.TypeKind == TypeKind.Interface
            || type is INamedTypeSymbol { IsGenericType: true } named && BridgeSymbols.MetadataName(named) == "System.Collections.Generic.Dictionary`2");
    }

    // Type.IsPublic (a public top-level type), a class or struct, not abstract, no delegate, outside System.*.
    private static bool IsDto(ITypeSymbol type) => type.DeclaredAccessibility == Accessibility.Public && type.ContainingType is null
        && type.TypeKind is TypeKind.Class or TypeKind.Struct && !type.IsAbstract
        && Namespace(type) is var ns && ns != "System" && ns?.StartsWith("System.", StringComparison.Ordinal) != true;

    private static string NavigationResultHint(ITypeSymbol type)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
            if (current.IsGenericType && BridgeSymbols.MetadataName(current) == "Runic.Navigation.NavigationResult`1")
                return " A NavigationResult holds .NET content and cannot cross the Bridge: return its Outcome"
                    + " (a NavigationOutcome) instead. Runic.Navigation.ReactiveUI's CreateBackCommand() already returns"
                    + " a NavigationOutcome.";
        return "";
    }

    // The public readable instance properties from the base class down, without RunicIgnore and JSON ignores; the
    // framework base classes (and System types) end the hierarchy.
    private IEnumerable<(IPropertySymbol Property, string WireName)> DataProperties(INamedTypeSymbol type, string path)
    {
        var hierarchy = new Stack<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object && !IsFrameworkBase(current); current = current.BaseType)
            hierarchy.Push(current);
        var names = new Dictionary<string, IPropertySymbol>(StringComparer.Ordinal);
        foreach (var current in hierarchy)
            foreach (var property in current.GetMembers().OfType<IPropertySymbol>()
                .Where(property => !property.IsStatic && !property.IsIndexer && property.GetMethod is not null
                    && (IsPublic(property.GetMethod) || IsPublic(property.SetMethod)) && !IsIgnored(property)))
            {
                var wire = WireName(property);
                if (names.TryGetValue(wire, out var other))
                    throw new ValueGraphException(path + "." + wire, $"Wire name collides between {other.ContainingType.MetadataName}.{other.Name} and {property.ContainingType.MetadataName}.{property.Name}; use RunicAlias or RunicIgnore.");
                names.Add(wire, property);
                yield return (property, wire);
            }
    }

    private static bool IsPublic(IMethodSymbol? accessor) => accessor?.DeclaredAccessibility == Accessibility.Public;

    // BridgeContractShape.IsFrameworkBase.
    internal static bool IsFrameworkBase(ITypeSymbol type) =>
        BridgeSymbols.MetadataName(type) is "ReactiveUI.ReactiveObject" or "ReactiveUI.Validation.Helpers.ReactiveValidationObject"
            or "ReactiveUI.Validation.Reactive.Helpers.ReactiveValidationObject" or "CommunityToolkit.Mvvm.ComponentModel.ObservableObject"
        || Namespace(type)?.StartsWith("System.", StringComparison.Ordinal) == true;

    // BridgeDtoContract.IsIgnored.
    private bool IsIgnored(IPropertySymbol property)
    {
        if (Chain(property).Any(declared => BridgeSymbols.Attribute(declared, "Runic.Application.Views.RunicIgnoreAttribute") is not null)) return true;
        if (property.Name == "ValidationContext" && property.ContainingType.AllInterfaces.Concat(property.ContainingType.TypeKind == TypeKind.Interface ? [property.ContainingType] : [])
            .Any(candidate => BridgeSymbols.MetadataName(candidate) is "ReactiveUI.Validation.Abstractions.IValidatableViewModel" or "ReactiveUI.Validation.Reactive.Abstractions.IValidatableViewModel"))
            return true;
        if (!_honorJsonIgnore) return false;
        if (Chain(property).Select(declared => BridgeSymbols.Attribute(declared, "System.Text.Json.Serialization.JsonIgnoreAttribute"))
                .FirstOrDefault(attribute => attribute is not null) is { } jsonIgnore
            && (jsonIgnore.NamedArguments.FirstOrDefault(argument => argument.Key == "Condition").Value.Value is int condition ? condition : 1) == 1)
            return true;
        return Chain(property).Any(declared => BridgeSymbols.Attribute(declared, "System.Text.Json.Serialization.JsonExtensionDataAttribute") is not null);
    }

    private static IEnumerable<IPropertySymbol> Chain(IPropertySymbol property)
    {
        for (IPropertySymbol? current = property; current is not null; current = current.OverriddenProperty) yield return current;
    }

    internal static AttributeData? InheritedAttribute(IPropertySymbol property, string name) =>
        Chain(property).Select(declared => BridgeSymbols.Attribute(declared, name)).FirstOrDefault(attribute => attribute is not null);

    private static bool DerivesFrom(ITypeSymbol type, string name)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (BridgeSymbols.MetadataName(current) == name) return true;
        return false;
    }

    internal static string WireName(IPropertySymbol property) =>
        (InheritedAttribute(property, "Runic.Application.Views.RunicAliasAttribute")
            ?? InheritedAttribute(property, "System.Text.Json.Serialization.JsonPropertyNameAttribute"))
            is { ConstructorArguments.Length: 1 } attribute && attribute.ConstructorArguments[0].Value is string name
            ? name : char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);

    private static IMethodSymbol? SelectConstructor(INamedTypeSymbol type, List<ValueMember> members, string path)
    {
        var names = new HashSet<string>(members.Select(member => member.Property.Name), StringComparer.OrdinalIgnoreCase);
        var constructor = type.InstanceConstructors.Where(candidate => candidate.DeclaredAccessibility == Accessibility.Public
                && candidate.Parameters.Length == members.Count && candidate.Parameters.All(parameter => names.Contains(parameter.Name)))
            .OrderByDescending(candidate => candidate.Parameters.Length).FirstOrDefault();
        if (constructor is not null) return constructor;
        if (type.InstanceConstructors.Any(candidate => candidate.DeclaredAccessibility == Accessibility.Public && candidate.Parameters.Length == 0)
            && members.All(member => member.Writable))
            return null;
        throw new ValueGraphException(path, $"{TypeIdentity(type)} is read from the page, as a command input, an interaction result or a settable property, so it needs a public constructor whose parameters map to every required property, or a public parameterless constructor with public setters. "
            + "Exclude computed or .NET-only DTO properties with [RunicIgnore] or an unconditional [JsonIgnore]. "
            + "[assembly: RunicIncludeJsonIgnored] in the presentation/ViewModel assembly keeps [JsonIgnore] properties in the contract. JsonIgnore conditions Never, WhenWritingNull and WhenWritingDefault remain included in the Bridge contract.");
    }

    internal static ITypeSymbol Underlying(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

    private static string? Namespace(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    /// <summary>
    /// Type.FullName: the metadata name, with the assembly-qualified type arguments of a constructed generic type. A
    /// type argument from a reference assembly that the runtime forwards (System.Runtime) is named after
    /// System.Private.CoreLib, as at run time; other forwarded types may name their reference assembly.
    /// </summary>
    internal static string TypeIdentity(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array)
            return TypeIdentity(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]";
        if (type is not INamedTypeSymbol named) return type.Name;
        if (!named.IsGenericType || named.IsUnboundGenericType || SymbolEqualityComparer.Default.Equals(named, named.OriginalDefinition))
            return BridgeSymbols.MetadataName(named);
        return BridgeSymbols.MetadataName(named.OriginalDefinition) + "["
            + string.Join(",", named.TypeArguments.Select(argument => "[" + TypeIdentity(argument) + ", " + AssemblyName(argument) + "]")) + "]";
    }

    private static string AssemblyName(ITypeSymbol type)
    {
        var identity = type.ContainingAssembly?.Identity;
        if (identity is null) return "";
        if (identity.Name is "System.Runtime" or "netstandard" or "mscorlib")
            return $"System.Private.CoreLib, Version={identity.Version}, Culture=neutral, PublicKeyToken=7cec85d7bea7798e";
        return identity.GetDisplayName();
    }

    /// <summary>BridgeTypeGraph.CSharpType: global::, the full name with nested types joined by '.', and type arguments.</summary>
    internal static string CSharpType(ITypeSymbol type)
    {
        if (type is IArrayTypeSymbol array) return CSharpType(array.ElementType) + "[]";
        if (type is not INamedTypeSymbol named) return type.Name;
        var name = BridgeSymbols.MetadataName(named.IsGenericType ? named.OriginalDefinition : named).Replace('+', '.');
        if (!named.IsGenericType) return "global::" + name;
        var tick = name.IndexOf('`');
        return "global::" + (tick < 0 ? name : name.Substring(0, tick)) + "<" + string.Join(", ", named.TypeArguments.Select(CSharpType)) + ">";
    }
}
