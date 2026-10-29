using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Runic.Navigation;

namespace Runic.Application.Views.Generated;

/// <summary>Build-time/Debug reconstruction of the generated Bridge wire shape.</summary>
[RequiresUnreferencedCode("Reflects over arbitrary model, DTO and view types; used only by code generation and Hot Reload.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class BridgeContractShape
{
    /// <summary>Computes the contract fingerprint of <paramref name="model"/> and the views in its assembly.</summary>
    /// <param name="model">The root view-model type.</param>
    /// <returns>An uppercase hexadecimal SHA-256 fingerprint.</returns>
    public static string Compute([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', Parts(model)))));
    }

    // The canonical lines the fingerprint hashes; tests inspect them.
    internal static List<string> Parts([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)] Type model)
    {
        var views = DiscoverViews(model.Assembly);
        var models = views.Select(view => view.ModelType).Append(model).Distinct().ToArray();
        var parts = new List<string> { "runic-bridge-contract-v5" };
        var nullability = new NullabilityInfoContext();
        foreach (var known in models.OrderBy(TypeName, StringComparer.Ordinal))
        {
            parts.Add($"model:{TypeName(known)}:public-name:{PublicName(known)}");
            AppendModel(parts, known, models, nullability);
        }
        foreach (var view in views.OrderBy(view => TypeName(view.ViewType), StringComparer.Ordinal))
            parts.Add($"view:{TypeName(view.ViewType)}:model:{TypeName(view.ModelType)}:contract:{view.Contract ?? "default"}");
        return parts;
    }

    private static void AppendModel(List<string> parts, Type model, IReadOnlyList<Type> models, NullabilityInfoContext nullability)
    {
        var honorJsonIgnore = BridgeDtoContract.HonorsJsonIgnore(model.Assembly);
        var properties = PublicModelProperties(model).ToArray();
        foreach (var property in properties)
        {
            var interaction = BridgeMvvmConventions.Interaction(property.PropertyType);
            var command = BridgeMvvmConventions.TypedCommand(property.PropertyType);
            var kind = interaction is not null ? "interaction" : command is not null || typeof(ICommand).IsAssignableFrom(property.PropertyType) ? "command" : "state";
            parts.Add($"member:{TypeName(model)}:{property.Name}:wire:{WireName(property)}:{kind}:access:{(property.SetMethod?.IsPublic == true ? "write" : "read")}");
            if (kind == "command" && command is not null)
            {
                AppendType(parts, command.GenericTypeArguments[0], ContractAnnotation(property, command, nullability, 0), $"{model.Name}.{property.Name}.input", [], honorJsonIgnore);
                AppendType(parts, command.GenericTypeArguments[1], ContractAnnotation(property, command, nullability, 1), $"{model.Name}.{property.Name}.result", [], honorJsonIgnore);
                var cardinality = property.GetCustomAttribute<RunicCommandResultAttribute>(true)?.Cardinality
                    ?? RunicCommandResultCardinality.Single;
                parts.Add($"command-cardinality:{TypeName(model)}:{property.Name}:{cardinality.ToString().ToLowerInvariant()}");
                AppendKey(parts, model, property);
                AppendFailure(parts, model, property);
                continue;
            }
            if (kind == "command")
            {
                var typed = BridgeMvvmConventions.InputCommand(property.PropertyType);
                var input = typed?.GenericTypeArguments[0] ?? property.GetCustomAttribute<RunicCommandInputAttribute>(true)?.Input;
                if (input is not null)
                    AppendType(parts, input, typed is null ? null : GenericAnnotation(nullability.Create(property), 0), $"{model.Name}.{property.Name}.input", [], honorJsonIgnore);
                var isAsync = BridgeMvvmConventions.IsAsyncCommand(property.PropertyType);
                parts.Add($"command-async:{TypeName(model)}:{property.Name}:{isAsync}");
                // The result of a CommunityToolkit [RelayCommand] method that returns Task<T>.
                if (isAsync && BridgeCommandSources.SourceMethod(model, property) is { ReturnType: { IsGenericType: true } returned } source
                    && returned.GetGenericTypeDefinition() == typeof(Task<>))
                    AppendType(parts, returned.GenericTypeArguments[0], GenericAnnotation(nullability.Create(source.ReturnParameter), 0),
                        $"{model.Name}.{property.Name}.result", [], honorJsonIgnore);
                AppendKey(parts, model, property);
                AppendFailure(parts, model, property);
                continue;
            }
            if (kind == "interaction" && interaction is not null)
            {
                AppendType(parts, interaction.GenericTypeArguments[0], ContractAnnotation(property, interaction, nullability, 0), $"{model.Name}.{property.Name}.input", [], honorJsonIgnore);
                AppendType(parts, interaction.GenericTypeArguments[1], ContractAnnotation(property, interaction, nullability, 1), $"{model.Name}.{property.Name}.output", [], honorJsonIgnore);
                continue;
            }
            if (kind != "state") continue;
            if (property.GetCustomAttribute<RunicCollectionAttribute>(true) is { } collection)
                parts.Add($"collection:{TypeName(model)}:{WireName(property)}:key:{collection.KeyProperty}");
            if (property.GetCustomAttribute<RunicAppendOnlyTextAttribute>(true) is not null)
                parts.Add($"append-only-text:{TypeName(model)}:{WireName(property)}");
            // A NavigationRegion<TContent> slot presents its Current; the region
            // kind is part of the contract because its wire value is nullable.
            if (NavigationRegionContent(property.PropertyType) is { } regionContent)
            {
                parts.Add($"content:{TypeName(model)}:{WireName(property)}:contract:{ContractFor(property) ?? "default"}:region:{TypeName(regionContent)}");
                continue;
            }
            // A NavigationSelection slot's wire value is its SelectedIndex, a nullable number.
            if (IsNavigationSelection(property.PropertyType))
            {
                parts.Add($"selection:{TypeName(model)}:{WireName(property)}");
                continue;
            }
            if (ContentModels(property, model, models).Length > 0)
            {
                parts.Add($"content:{TypeName(model)}:{WireName(property)}:contract:{ContractFor(property) ?? "default"}");
                continue;
            }
            AppendType(parts, property.PropertyType, nullability.Create(property), $"{model.Name}.{WireName(property)}", [], honorJsonIgnore, property.GetCustomAttribute<RunicCodecAttribute>(true));
        }
        parts.Add($"validation-errors:{TypeName(model)}:{typeof(INotifyDataErrorInfo).IsAssignableFrom(model)}");
        // D053.3: a ReactiveUI.Validation ViewModel without INotifyDataErrorInfo has validation too. Only such models
        // add the part, so other fingerprints stay.
        if (!typeof(INotifyDataErrorInfo).IsAssignableFrom(model) && ReactiveValidationConventions.IsValidatable(model))
            parts.Add($"validation-context:{TypeName(model)}:true");
    }

    // A RunicCommandKey command sends its resolver's key instead of its parameter. Only such commands add
    // parts, so other models keep their fingerprint.
    private static void AppendKey(List<string> parts, Type model, PropertyInfo command)
    {
        foreach (var (declaredOn, resolver) in BridgeCommandSources.KeyDeclarations(model, command))
        {
            parts.Add($"command-key:{TypeName(model)}:{command.Name}:{declaredOn}:{resolver}");
            if (BridgeCommandSources.KeyResolvers(model, resolver) is [var method] && method.GetParameters() is [var key, ..])
                AppendType(parts, key.ParameterType, new NullabilityInfoContext().Create(key), $"{model.Name}.{command.Name}.key", [],
                    BridgeDtoContract.HonorsJsonIgnore(model.Assembly));
        }
    }

    // Only declared commands add parts, so undeclared models keep their
    // fingerprint. A command's source method, such as a CommunityToolkit
    // [RelayCommand] method, is usually private.
    private static void AppendFailure(List<string> parts, Type model, PropertyInfo command)
    {
        foreach (var (declaredOn, failure) in BridgeCommandSources.FailureDeclarations(model, command))
        {
            parts.Add($"command-failure:{TypeName(model)}:{command.Name}:{declaredOn}");
            AppendType(parts, failure, null, $"{model.Name}.{command.Name}.failure", [], BridgeDtoContract.HonorsJsonIgnore(model.Assembly));
        }
    }

    // Canonical recursive shape only. Generated code uses direct accesses; it
    // never invokes this reflection path under trimming/AOT.
    private static void AppendType(List<string> parts, Type declared, NullabilityInfo? annotation, string path, HashSet<Type> stack, bool honorJsonIgnore, RunicCodecAttribute? memberCodec = null)
    {
        var nullable = !declared.IsValueType ? annotation?.ReadState == NullabilityState.Nullable : Nullable.GetUnderlyingType(declared) is not null;
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        parts.Add($"wire:{path}:type:{TypeName(type)}:nullable:{nullable}");
        if (typeof(INotifyDataErrorInfo).IsAssignableFrom(type))
            parts.Add($"validation-errors:{path}:true");
        else if (ReactiveValidationConventions.IsValidatable(type))
            parts.Add($"validation-context:{path}:true");
        if (IsScalar(type)) return;
        if (type.IsEnum)
        {
            parts.Add($"enum:{path}:flags:{type.IsDefined(typeof(FlagsAttribute), false)}");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(field => field.MetadataToken))
                parts.Add($"enum-case:{path}:{field.Name}:wire:{field.GetCustomAttribute<RunicAliasAttribute>()?.Name ?? field.Name}");
            return;
        }
        var custom = memberCodec ?? type.GetCustomAttribute<RunicCodecAttribute>(true);
        if (custom is not null)
        {
            var shape = custom.CodecType.GetCustomAttribute<RunicCodecShapeAttribute>();
            parts.Add($"custom:{path}:codec:{TypeName(custom.CodecType)}:ts:{shape?.TypeScriptType ?? "missing"}:decoder:{shape?.DecoderExpression ?? "missing"}:encoder:{shape?.EncoderExpression ?? "missing"}");
            return;
        }
        if (type == typeof(System.Text.Json.JsonElement) || type == typeof(System.Text.Json.Nodes.JsonNode))
        {
            parts.Add($"json:{path}");
            return;
        }
        var union = type.GetCustomAttribute<RunicUnionAttribute>();
        if (union is not null)
        {
            parts.Add($"union:{path}:root:{TypeName(type)}");
            foreach (var @case in union.Cases.OrderBy(TypeName, StringComparer.Ordinal))
            {
                var tag = @case.GetCustomAttribute<RunicUnionCaseAttribute>()?.Name ?? "missing";
                parts.Add($"union-case:{path}:{tag}:{TypeName(@case)}");
                AppendType(parts, @case, null, path + ".$case=" + tag, stack, honorJsonIgnore);
            }
            return;
        }
        if (TryDictionary(type, out var dictionaryValue))
        {
            parts.Add($"dictionary:{path}:key:string");
            AppendType(parts, dictionaryValue!, GenericAnnotation(annotation, 1), path + "{}", stack, honorJsonIgnore);
            return;
        }
        if (TryCollection(type, out var item))
        {
            parts.Add($"collection:{path}");
            AppendType(parts, item!, GenericAnnotation(annotation, 0), path + "[]", stack, honorJsonIgnore);
            return;
        }
        if (!type.IsPublic || type.IsAbstract || !(type.IsClass || type.IsValueType) || stack.Contains(type))
        {
            parts.Add($"unsupported:{path}:{TypeName(type)}");
            return;
        }
        stack.Add(type);
        parts.Add($"dto:{path}:{TypeName(type)}");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in PublicDtoProperties(type, honorJsonIgnore))
        {
            var wire = WireName(property);
            parts.Add($"dto-member:{path}:{property.DeclaringType?.FullName}:{property.Name}:wire:{wire}:set:{property.SetMethod?.IsPublic == true}:collision:{!names.Add(wire)}");
            AppendType(parts, property.PropertyType, new NullabilityInfoContext().Create(property), path + "." + wire, stack, honorJsonIgnore, property.GetCustomAttribute<RunicCodecAttribute>(true));
        }
        var constructors = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public).OrderBy(constructor => constructor.MetadataToken)
            .Select(constructor => string.Join(",", constructor.GetParameters().Select(parameter => parameter.Name + ":" + TypeName(parameter.ParameterType)
                // A parameter with a default makes its member optional for the page.
                + (parameter.HasDefaultValue ? "=default" : ""))));
        parts.Add($"dto-constructors:{path}:{string.Join("|", constructors)}");
        stack.Remove(type);
    }

    private static bool IsScalar(Type type) => type == typeof(string) || type == typeof(bool) || type == typeof(sbyte) || type == typeof(byte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(System.Numerics.BigInteger);
    private static bool TryCollection(Type type, out Type? item)
    {
        item = type.IsArray ? type.GetElementType() : null;
        if (item is not null) return true;
        if (type == typeof(string)) return false;
        var candidate = type.GetInterfaces().Append(type).FirstOrDefault(value => value.IsGenericType && value.GetGenericTypeDefinition() is var generic && (generic == typeof(IEnumerable<>) || generic == typeof(IReadOnlyList<>) || generic == typeof(IReadOnlyCollection<>) || generic == typeof(IList<>)));
        item = candidate?.GenericTypeArguments[0]; return item is not null;
    }
    private static bool TryDictionary(Type type, out Type? value)
    {
        var candidate = type.GetInterfaces().Append(type).FirstOrDefault(entry => entry.IsGenericType && entry.GetGenericTypeDefinition() is var generic && (generic == typeof(IReadOnlyDictionary<,>) || generic == typeof(IDictionary<,>)) && entry.GenericTypeArguments[0] == typeof(string));
        value = candidate?.GenericTypeArguments[1]; return value is not null;
    }
    // Excluded properties are filtered first, by attribute name: resolving the
    // signature (GetIndexParameters) of an excluded WPF-typed property would
    // fail in the code generator, which has no Windows Desktop framework.
    private static IEnumerable<PropertyInfo> PublicModelProperties(Type type) => type.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public).Where(property => !BridgeDtoContract.IsRunicIgnored(property) && property.GetIndexParameters().Length == 0);
    private static IEnumerable<PropertyInfo> PublicDtoProperties(Type type, bool honorJsonIgnore)
    {
        var hierarchy = new Stack<Type>();
        for (var current = type; current is not null && current != typeof(object) && !IsFrameworkBase(current); current = current.BaseType) hierarchy.Push(current);
        return hierarchy.SelectMany(current => current.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)).Where(property => property.GetMethod is not null && !BridgeDtoContract.IsIgnored(property, honorJsonIgnore) && property.GetIndexParameters().Length == 0);
    }
    // The generator also uses it for DTO hierarchies and to recognise a framework slot type.
    internal static bool IsFrameworkBase(Type type) => BridgeMvvmConventions.IsFrameworkBase(type) || type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true;
    // Same rule as the generator: a typed command or interaction argument
    // is annotated only when the declared property type closes the contract.
    private static NullabilityInfo? ContractAnnotation(PropertyInfo property, Type contract, NullabilityInfoContext nullability, int index) =>
        property.PropertyType.IsGenericType && property.PropertyType.GenericTypeArguments.SequenceEqual(contract.GenericTypeArguments)
            ? GenericAnnotation(nullability.Create(property), index) : null;
    private static NullabilityInfo? GenericAnnotation(NullabilityInfo? value, int index) => value is { GenericTypeArguments.Length: > 0 } && value.GenericTypeArguments.Length > index ? value.GenericTypeArguments[index] : null;
    private static string WireName(PropertyInfo property) => property.GetCustomAttribute<RunicAliasAttribute>(true)?.Name ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name ?? char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
    internal static Type? NavigationRegionContent(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(NavigationRegion<>) ? type.GenericTypeArguments[0] : null;
    internal static bool IsNavigationSelection(Type type) => type == typeof(NavigationSelection);
    private static Type[] ContentModels(PropertyInfo property, Type owner, IReadOnlyList<Type> models) { var candidate = TryCollection(property.PropertyType, out var item) ? item! : property.PropertyType; return candidate == typeof(object) ? [] : models.Where(model => model != owner && candidate.IsAssignableFrom(model)).ToArray(); }
    private static PresentationView[] DiscoverViews(Assembly assembly) => LoadableTypes(assembly).Where(type => !type.IsAbstract && type.IsClass && type.IsPublic && !type.IsNested).Select(type => (ViewType: type, ModelType: ViewModelFor(type))).Where(item => item.ModelType is not null).Select(item => new PresentationView(item.ViewType, item.ModelType!, ContractFor(item.ViewType))).ToArray();
    private static Type? ViewModelFor(Type view) { for (var current = view.BaseType; current is not null; current = current.BaseType) if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(RunicView<>)) return current.GenericTypeArguments[0]; return null; }
    private static string? ContractFor(MemberInfo member) => member.GetCustomAttribute<RunicViewContractAttribute>(true)?.Contract;
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Build-time and Debug-only contract inspection.")]
    private static IEnumerable<Type> LoadableTypes(Assembly assembly) { try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException error) { return error.Types.OfType<Type>(); } }
    private static string PublicName(Type model) => model.Name.EndsWith("ViewModel", StringComparison.Ordinal) ? model.Name[..^"ViewModel".Length] : model.Name;
    private static string TypeName(Type type) { if (type.IsArray) return TypeName(type.GetElementType()!) + "[]"; if (!type.IsGenericType) return type.FullName ?? type.Name; var name = type.GetGenericTypeDefinition().FullName!; name = name[..name.IndexOf('`')]; return name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">"; }
    private sealed record PresentationView(Type ViewType, Type ModelType, string? Contract);
}
