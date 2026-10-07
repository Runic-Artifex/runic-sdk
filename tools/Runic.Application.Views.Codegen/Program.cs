using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Runic.Application.Views;
using Runic.Application.Views.Codegen.Toolkit;
using Runic.Application.Views.Codegen.ReactiveUI;

try
{
    if (args.Length == 3 && args[0] == "--composition-stub")
    {
        GenerateCompositionStub(args[2], args[1]);
    }
    else if (args.Length >= 1 && args[0] == "--generate")
    {
        var aot = false;
        var registerGlobally = true;
        string? compositionType = null;
        var values = new List<string> { args[0] };
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--aot": aot = true; break;
                case "--no-registry": registerGlobally = false; break;
                case "--di-composition" when i + 1 < args.Length:
                    compositionType = args[++i];
                    break;
                case "--reactiveui-flavor" when i + 1 < args.Length:
                    CodegenOptions.ReactiveUiFlavor = args[++i] switch
                    {
                        "primitives" => ReactiveUiFlavor.Default,
                        "reactive" => ReactiveUiFlavor.SystemReactive,
                        var value => throw new ArgumentException($"Unsupported ReactiveUI flavor '{value}'. Expected 'primitives' or 'reactive'."),
                    };
                    break;
                default: values.Add(args[i]); break;
            }
        }
        var positional = values.ToArray();
        if (positional.Length != 4)
            throw new ArgumentException("Usage: BridgeCodegen --generate <model.dll> <C# output dir> <TypeScript output dir> [--aot] [--no-registry] [--di-composition <namespace.type>]");
        if (compositionType is not null && registerGlobally)
            throw new ArgumentException("--di-composition requires --no-registry.");

        string[] cacheArguments = [$"aot={aot}", $"registry={registerGlobally}",
            $"composition={compositionType}", $"reactiveui={CodegenOptions.ReactiveUiFlavor}"];
        if (BridgeGenerationCache.TryHit(positional[1], positional[2], positional[3], cacheArguments))
        {
            Console.WriteLine("Bridge generation is up to date.");
            return;
        }
        var assembly = Assembly.LoadFrom(Path.GetFullPath(positional[1]));
        var viewTypes = new Dictionary<Type, List<Type>>();
        foreach (var type in assembly.GetTypes())
        {
            try
            {
                if (type.IsAbstract) continue;
                var model = ViewModelFor(type);
                if (model is null) continue;
                if (!type.IsClass || !type.IsPublic || type.IsNested)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                        $"{type.FullName}: a Runic Window or View must be a public, top-level concrete class.", type);
                if (!viewTypes.TryGetValue(model, out var variants))
                    viewTypes.Add(model, variants = []);
                var contract = ContractFor(type);
                if (variants.Any(existing => string.Equals(ContractFor(existing), contract, StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                        $"{type.FullName}: {model.FullName} already has a Runic View with contract '{contract ?? "default"}'.", type);
                variants.Add(type);
            }
            catch (Exception error) when (Diagnostics.IsReportable(error))
            {
                Diagnostics.Report(error);
            }
        }
        if (Diagnostics.HasErrors) return;
        var models = viewTypes.Keys.Select(type => (Model: type, Name: PublicName(type))).ToArray();
        if (models.Length == 0)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                $"{assembly.GetName().Name}: no Runic Window/View classes found.");
        foreach (var group in models.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"Bridge ViewModel names must be unique within an application: {string.Join(" and ", group.Select(entry => entry.Model.FullName))} both use '{group.Key}'.",
                group.Last().Model);
        var presentationKinds = models.SelectMany(entry => viewTypes.TryGetValue(entry.Model, out var variants)
            ? variants.Select(view => (Kind: PageKind(entry.Name, ContractFor(view)), Member: (MemberInfo)view))
            : [(Kind: PageKind(entry.Name, null), Member: entry.Model)]).ToArray();
        foreach (var group in presentationKinds.GroupBy(kind => kind.Kind, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"Bridge presentation kinds must be unique across ViewModels and View contracts: '{group.Key}' is used by {string.Join(" and ", group.Select(kind => kind.Member.Name))}.",
                group.Last().Member);
        if (aot)
        {
            var unsupported = models.FirstOrDefault(entry => ToolkitCommandInspector.HasUnsupportedAotValidation(entry.Model));
            if (unsupported.Model is not null)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Aot,
                    $"{unsupported.Model.FullName} inherits CommunityToolkit.Mvvm.ObservableValidator. Its validation failed in this prototype's Native AOT probe; use a framework-dependent publish until an AOT-safe validator is verified.",
                    unsupported.Model);
        }

        Directory.CreateDirectory(positional[2]);
        Directory.CreateDirectory(positional[3]);
        // View partials and ViewModel bridges use distinct suffixes and full
        // type names: MainWindow and MainWindowViewModel, or two ItemViews in
        // different namespaces, must not overwrite each other's file.
        var csharpOutputs = models.Select(entry => (Path: BridgeFileName(entry.Model), Owner: entry.Model.FullName!))
            .Concat(viewTypes.Values.SelectMany(variants => variants).Select(view => (Path: ViewFileName(view), Owner: view.FullName!)))
            .ToArray();
        var typescriptOutputs = models.Select(entry => (Path: $"{LowerFirst(entry.Name)}.ts", Owner: entry.Model.FullName!))
            .Concat(models.Select(entry => (Path: $"{LowerFirst(entry.Name)}.mock.ts", Owner: entry.Model.FullName!)))
            .Append((Path: $"{TypeScriptNamedTypes.ModuleName}.ts", Owner: "the generated named types")).ToArray();
        foreach (var group in csharpOutputs.Concat(typescriptOutputs)
            .GroupBy(output => output.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"generated file '{group.Key}' would be written by {string.Join(" and ", group.Select(output => output.Owner))}. Rename one type.",
                models.FirstOrDefault(entry => group.Any(output => output.Owner == entry.Model.FullName)).Model);
        var namedTypesPath = Path.Combine(positional[3], $"{TypeScriptNamedTypes.ModuleName}.ts");
        if (File.Exists(namedTypesPath) && !GeneratedOutput.IsGenerated(namedTypesPath))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"{Path.GetFullPath(namedTypesPath)} is not generated, but the generator writes its named TypeScript types there. Move the file or set RunicBridgeTypescriptDir to a dedicated directory.");
        var expectedCsharp = csharpOutputs.Select(output => Path.GetFullPath(Path.Combine(positional[2], output.Path)))
            .ToHashSet(StringComparer.Ordinal);
        var expectedTypescript = typescriptOutputs.Select(output => Path.GetFullPath(Path.Combine(positional[3], output.Path)))
            .ToHashSet(StringComparer.Ordinal);
        // Remove only stale generator output. The TypeScript directory may be
        // an application source directory with hand-written modules.
        foreach (var path in Directory.GetFiles(positional[2], "*.g.cs"))
            if (!Path.GetFileName(path).StartsWith("RunicBridgeComposition", StringComparison.Ordinal)
                && !expectedCsharp.Contains(Path.GetFullPath(path)) && GeneratedOutput.IsGenerated(path)) File.Delete(path);
        foreach (var path in Directory.GetFiles(positional[3], "*.ts"))
            if (!expectedTypescript.Contains(Path.GetFullPath(path)) && GeneratedOutput.IsGenerated(path)) File.Delete(path);
        var contentRequired = new HashSet<Type>();
        foreach (var entry in models)
        {
            // Report every ViewModel's problem in one build rather than one
            // ViewModel per build.
            try
            {
                if (GenerateOne(entry.Model, Path.Combine(positional[2], BridgeFileName(entry.Model)),
                    Path.Combine(positional[3], $"{LowerFirst(entry.Name)}.ts"), entry.Name, registerGlobally, models, viewTypes))
                    contentRequired.Add(entry.Model);
                if (viewTypes.TryGetValue(entry.Model, out var views))
                    foreach (var view in views)
                        GenerateViewPartial(Path.Combine(positional[2], ViewFileName(view)), view, entry.Model, entry.Name);
            }
            catch (Exception error) when (Diagnostics.IsReportable(error))
            {
                Diagnostics.Report(error, entry.Model);
            }
        }
        if (Diagnostics.HasErrors) return;
        TypeScriptModules.WriteAll(positional[3]);
        var compositionPath = Path.Combine(positional[2], "RunicBridgeComposition.g.cs");
        if (compositionType is null)
        {
            if (File.Exists(compositionPath)) File.Delete(compositionPath);
        }
        else GenerateCompositionRegistration(compositionPath, compositionType, models, contentRequired,
            viewTypes.Values.SelectMany(variants => variants).Where(view => !IsWindow(view))
                .OrderBy(view => view.FullName, StringComparer.Ordinal));
        BridgeGenerationCache.Save(positional[1], positional[2], positional[3], cacheArguments);
    }
    else
    {
        if (args.Length is not (4 or 5))
            throw new ArgumentException("Usage: BridgeCodegen <model.dll> <ViewModel type> <output.cs> <output.ts> [public name]");
        var model = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType(args[1], throwOnError: true)!;
        GenerateOne(model, args[2], args[3], args.Length == 5 ? args[4] : PublicName(model));
        TypeScriptModules.WriteAll(Path.GetDirectoryName(Path.GetFullPath(args[3]))!);
    }
}
catch (Exception error) when (Diagnostics.IsReportable(error))
{
    Diagnostics.Report(error);
}

static string PublicName(Type model) => model.Name.EndsWith("ViewModel", StringComparison.Ordinal)
    ? model.Name[..^"ViewModel".Length] : model.Name;

static string BridgeFileName(Type model) => $"{model.FullName}.Bridge.g.cs";

static string ViewFileName(Type view) => $"{view.FullName}.View.g.cs";

// The generated bridge is a sibling of its ViewModel, which may be in the
// global namespace.
static string BridgeTypeName(Type model, string shortName) =>
    $"global::{(model.Namespace is null ? "" : model.Namespace + ".")}{shortName}Bridge";

static void AppendNamespace(StringBuilder source, string? ns)
{
    if (ns is not null) source.AppendLine($"namespace {ns};");
}

static Type? ViewModelFor(Type view)
{
    for (var current = view.BaseType; current is not null; current = current.BaseType)
        if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Runic.Application.Views.RunicView`1")
            return current.GenericTypeArguments[0];
    return null;
}

static string? ContractFor(MemberInfo member)
{
    var attribute = member.CustomAttributes.FirstOrDefault(value =>
        value.AttributeType.FullName == "Runic.Application.Views.RunicViewContractAttribute");
    if (attribute is null) return null;
    var contract = attribute.ConstructorArguments.Single().Value as string;
    if (string.IsNullOrWhiteSpace(contract) || !char.IsLetter(contract[0])
        || !contract.All(char.IsLetterOrDigit))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
            $"{member.DeclaringType?.Name}{(member.DeclaringType is null ? "" : ".")}{member.Name}: a View contract must contain only letters and digits and start with a letter.", member);
    return contract;
}

static string PageKind(string name, string? contract) =>
    LowerFirst(name) + (contract is null ? "" : char.ToUpperInvariant(contract[0]) + contract[1..]);

static int InheritanceDepth(Type type)
{
    var depth = 0;
    for (var current = type.BaseType; current is not null; current = current.BaseType) depth++;
    return depth;
}

static void GenerateViewPartial(string path, Type view, Type model, string shortName)
{
    if (view.ContainsGenericParameters)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
            $"{view.FullName}: a Runic View must be a closed, named class.", view);
    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    AppendNamespace(cs, view.Namespace);
    cs.AppendLine($"public {(view.IsSealed ? "sealed " : "")}partial class {view.Name}");
    cs.AppendLine("{");
    cs.AppendLine("    internal global::System.IDisposable AttachRunicBridge(");
    cs.AppendLine("        global::Runic.Application.Views.IBridgeTransport transport, string route,");
    cs.AppendLine("        global::Runic.Application.Views.WindowContentSession content) =>");
    cs.AppendLine($"        new {BridgeTypeName(model, shortName)}(transport,");
    cs.AppendLine($"            DataContext ?? throw new global::System.InvalidOperationException(\"{view.Name} has no DataContext.\"),");
    cs.AppendLine("            route, content);");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

static bool IsWindow(Type view)
{
    for (var current = view.BaseType; current is not null; current = current.BaseType)
        if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "Runic.Application.Views.RunicWindow`1")
            return true;
    return false;
}

static void GenerateCompositionRegistration(string path, string compositionType, (Type Model, string Name)[] models,
    IReadOnlySet<Type> contentRequired, IEnumerable<Type> views)
{
    var parts = CompositionParts(compositionType);

    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection.Extensions;");
    cs.AppendLine("using Runic.Application.Views;");
    cs.AppendLine($"namespace {string.Join('.', parts[..^1])};");
    cs.AppendLine($"public static class {parts[^1]}");
    cs.AppendLine("{");
    cs.AppendLine("    public static IServiceCollection AddRunicBridges(this IServiceCollection services)");
    cs.AppendLine("    {");
    cs.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(services);");
    foreach (var (model, name) in models)
    {
        var modelType = $"global::{model.FullName}";
        var bridgeType = BridgeTypeName(model, name);
        // GenerateOne decided whether the bridge constructor requires a content
        // session (ViewModel content or interactions). Only offer the
        // transport-only factory when it can actually construct the bridge.
        if (!contentRequired.Contains(model))
        {
            cs.AppendLine($"        services.AddScoped<global::System.Func<IBridgeTransport, {modelType}, global::System.IDisposable>>(");
            cs.AppendLine($"            _ => (transport, vm) => new {bridgeType}(transport, vm));");
        }
        cs.AppendLine($"        services.AddScoped<global::System.Func<IBridgeTransport, WindowContentSession, {modelType}, global::System.IDisposable>>(");
        cs.AppendLine($"            _ => (transport, content, vm) => new {bridgeType}(transport, vm, content: content));");
    }
    cs.AppendLine("        return services;");
    cs.AppendLine("    }");
    cs.AppendLine();
    // Views are registered by their concrete generated-time types, so the
    // registrations stay explicit and trimming/AOT safe. ViewModels remain
    // application registrations because their lifetime is an application choice.
    cs.AppendLine("    /// <summary>Registers the generated Bridges, every non-Window View as transient, and the default service-provider View locator.</summary>");
    cs.AppendLine("    public static IServiceCollection AddRunicViews(this IServiceCollection services)");
    cs.AppendLine("    {");
    cs.AppendLine("        AddRunicBridges(services);");
    foreach (var view in views)
        cs.AppendLine($"        services.TryAddTransient<global::{view.FullName}>();");
    cs.AppendLine("        services.TryAddScoped<IRunicViewLocator, ServiceProviderViewLocator>();");
    cs.AppendLine("        return services;");
    cs.AppendLine("    }");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

static void GenerateCompositionStub(string path, string compositionType)
{
    var parts = CompositionParts(compositionType);
    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    cs.AppendLine($"namespace {string.Join('.', parts[..^1])};");
    cs.AppendLine($"public static class {parts[^1]}");
    cs.AppendLine("{");
    cs.AppendLine("    public static IServiceCollection AddRunicBridges(this IServiceCollection services) => services;");
    cs.AppendLine("    public static IServiceCollection AddRunicViews(this IServiceCollection services) => services;");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

static string[] CompositionParts(string compositionType)
{
    var parts = compositionType.Split('.');
    if (parts.Length < 2 || parts.Any(part => part.Length == 0 || !(char.IsLetter(part[0]) || part[0] == '_')
        || part.Skip(1).Any(character => !(char.IsLetterOrDigit(character) || character == '_'))))
        throw new ArgumentException("DI composition needs a fully qualified C# class name.");
    return parts;
}

// Returns true when the generated bridge requires a WindowContentSession.
static bool GenerateOne(Type model, string csharpPath, string typescriptPath, string shortName,
    bool registerGlobally = true, (Type Model, string Name)[]? knownModels = null,
    IReadOnlyDictionary<Type, List<Type>>? viewTypes = null)
{
    knownModels ??= [(model, shortName)];
    if (!typeof(INotifyPropertyChanged).IsAssignableFrom(model))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName} must implement INotifyPropertyChanged.", model);
    if (model.IsNested || !model.IsClass || !model.IsPublic)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName}: a Bridge ViewModel must be a public, top-level class.", model);

    var declared = model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetIndexParameters().Length == 0
            && !property.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Runic.Application.Views.RunicIgnoreAttribute"))
        .OrderBy(property => property.MetadataToken)
        .ToArray();
    var contractFingerprint = BridgeContractShape.Compute(model);
    var interactions = InteractionCodeEmitter.Discover(declared, model.Name, contractFingerprint);
    var commands = declared.Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)
        || InspectCommand(model, property, () => ReactiveCommandInspector.InspectContract(property, CodegenOptions.ReactiveUiFlavor)) is not null).ToArray();
    var properties = declared.Except(commands).Except(interactions.Select(plan => plan.Property)).ToArray();
    if (properties.FirstOrDefault(property => WireName(property) == "revision") is { } revision)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
            $"{model.Name}.{revision.Name} conflicts with the generated Bridge revision field.", revision);
    var duplicateWireName = properties.GroupBy(WireName, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
    if (duplicateWireName is not null)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
            $"{model.Name}: state properties {string.Join(" and ", duplicateWireName.Select(property => property.Name))} share the generated wire name '{duplicateWireName.Key}'.",
            duplicateWireName.Last());
    var nullability = new NullabilityInfoContext();
    if (properties.Length == 0 && commands.Length == 0 && interactions.Length == 0)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.Name}: a ViewModel needs at least one state property, command, or interaction.", model);
    var contentProperties = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var contentCollections = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var valueProperties = new Dictionary<PropertyInfo, BridgeTypeGraph>();
    foreach (var property in properties)
    {
        if (property.GetMethod is null)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                $"{model.Name}.{property.Name}: a public getter is required.", property);
        // A routed interface or base property may admit several registered
        // ViewModels. Emit the most specific class first: a base pattern ahead
        // of its derived class would hide the derived View (or fail to compile).
        var contentModels = knownModels.Where(entry => entry.Model != model
            && property.PropertyType.IsAssignableFrom(entry.Model))
            .OrderByDescending(entry => InheritanceDepth(entry.Model))
            .ThenBy(entry => entry.Model.FullName, StringComparer.Ordinal)
            .ToArray();
        if (contentModels.Length > 0 && property.PropertyType != typeof(object))
        {
            if (property.SetMethod?.IsPublic == true)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                    $"{model.Name}.{property.Name}: ViewModel content must be set by .NET, not the web view.", property);
            contentProperties.Add(property, contentModels);
            continue;
        }
        if (property.PropertyType.IsGenericType
            && property.PropertyType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var itemType = property.PropertyType.GenericTypeArguments[0];
            var itemModels = knownModels.Where(entry => entry.Model != model
                && itemType != typeof(object) && itemType.IsAssignableFrom(entry.Model))
                .OrderByDescending(entry => InheritanceDepth(entry.Model))
                .ThenBy(entry => entry.Model.FullName, StringComparer.Ordinal)
                .ToArray();
            if (itemModels.Length > 0)
            {
                if (property.SetMethod?.IsPublic == true)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                        $"{model.Name}.{property.Name}: ViewModel collections must be set by .NET, not the web view.", property);
                contentCollections.Add(property, itemModels);
                continue;
            }
            if (itemType == typeof(object) || typeof(INotifyPropertyChanged).IsAssignableFrom(itemType) &&
                property.GetCustomAttribute<RunicCollectionAttribute>(true) is null)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                    $"{model.Name}.{property.Name}: a ViewModel collection needs a specific item interface or base class with a registered View.", property);
        }
        valueProperties.Add(property, BridgeTypeGraph.Discover(property.PropertyType,
            nullability.Create(property), $"{model.Name}.{property.Name}", property));
    }
    var incrementalCollections = new Dictionary<PropertyInfo, PropertyInfo>();
    foreach (var property in properties)
    {
        if (property.GetCustomAttribute<RunicCollectionAttribute>(true) is not { } attribute) continue;
        if (property.SetMethod?.IsPublic == true || !valueProperties.TryGetValue(property, out var collectionGraph) ||
            collectionGraph.Root.Kind is not (BridgeWireKind.Array or BridgeWireKind.List) || collectionGraph.Root.IsNullable ||
            collectionGraph.Root.Element is not { IsNullable: false, Kind: BridgeWireKind.Dto } item)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Collection,
                $"{model.Name}.{property.Name}: RunicCollection requires a nonnullable, read-only collection of nonnullable DTO rows.", property);
        var key = item.Members.SingleOrDefault(member => member.Property.Name == attribute.KeyProperty);
        if (key is null || key.Type.IsNullable || key.Type.NonNullableType != typeof(string) && key.Type.NonNullableType != typeof(Guid) && key.Type.NonNullableType != typeof(int))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Collection,
                $"{model.Name}.{property.Name}: the collection key '{attribute.KeyProperty}' must name a nonnullable string, Guid or Int32 row property.", property);
        incrementalCollections.Add(property, key.Property);
    }
    var hasContent = contentProperties.Count > 0 || contentCollections.Count > 0;
    var contentBindings = contentProperties.Concat(contentCollections).ToArray();
    if ((hasContent || interactions.Length > 0) && registerGlobally)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Invocation,
            $"{model.Name} contains ViewModel content or interactions. Generate it with --no-registry (RunicBridgeRegisterGlobally=false) and attach it through a window content session.", model);
    var fullType = $"global::{model.FullName}";
    var commandPlans = new Dictionary<PropertyInfo, GeneratedCommandPlan>();
    var commandSources = commands.ToDictionary(command => command, command => ToolkitSourceMethod(model, command));
    foreach (var command in commands)
    {
        var source = (MemberInfo?)commandSources[command] ?? command;
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command,
                $"{model.Name}.{command.Name}: Bridge commands must end with Command.", source);
        var toolkit = InspectCommand(model, command, () => ToolkitCommandInspector.InspectContract(command));
        var reactive = InspectCommand(model, command, () => ReactiveCommandInspector.InspectContract(command, CodegenOptions.ReactiveUiFlavor));
        var plainInput = command.GetCustomAttribute<RunicCommandInputAttribute>(true);
        var plan = toolkit is { } existing
            ? new GeneratedCommandPlan(false, existing.IsAsync, ToolkitContract: existing,
                InputGraph: existing.Input is { } toolkitInput ? BridgeTypeGraph.Discover(toolkitInput,
                    ContractNullability.Argument(command, nullability, [toolkitInput], 0),
                    $"{model.Name}.{command.Name}.input", source) : null)
            : reactive is { } contract
                ? GeneratedCommandPlan.Reactive(contract,
                    contract.HasInput ? BridgeTypeGraph.Discover(contract.Input,
                        ContractNullability.Argument(command, nullability, [contract.Input, contract.Result], 0),
                        $"{model.Name}.{command.Name}.input", source) : null,
                    contract.HasResult ? BridgeTypeGraph.Discover(contract.Result,
                        ContractNullability.Argument(command, nullability, [contract.Input, contract.Result], 1),
                        $"{model.Name}.{command.Name}.result", source) : null)
                : plainInput is not null
                    ? GeneratedCommandPlan.Plain(BridgeTypeGraph.Discover(plainInput.Input,
                        rootPath: $"{model.Name}.{command.Name}.input", origin: source))
                : throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command,
                    $"{model.Name}.{command.Name}: unsupported command shape. Use a CommunityToolkit IRelayCommand or IAsyncRelayCommand, a ReactiveUI ReactiveCommand, or an ICommand with RunicCommandInput.", source);
        if (plan.ReactiveContract is null && command.GetCustomAttribute<RunicCommandResultAttribute>(true) is not null)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command,
                $"{model.Name}.{command.Name}: RunicCommandResult selects a ReactiveUI command's result cardinality; CommunityToolkit and plain commands have no result value.", source);
        commandPlans.Add(command, plan with { ParameterName = CommandParameterName(commandSources[command], plan) });
    }
    var operationPlans = commands.Where(command => commandPlans[command].IsAsync).Select(command =>
    {
        var plan = commandPlans[command];
        return new OperationTypeScriptPlan(command.Name[..^"Command".Length],
            plan.InputGraph?.TypeScriptType() ?? "never",
            plan.ResultGraph?.TypeScriptType() ?? "never",
            plan.ResultGraph?.EmitTypeScriptDecoder("value") ?? "undefined as never",
            plan.InputGraph?.EncodeTypeScript(plan.ParameterName) ?? "undefined",
            plan.HasArgument,
            plan.ReactiveContract?.Cardinality is BridgeCommandResultCardinality.Stream,
            plan.ParameterName);
    }).ToArray();

    if (shortName.Length == 0 || !shortName.All(char.IsLetterOrDigit) || !char.IsLetter(shortName[0]))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName}: the public Bridge name '{shortName}' must be a C#/TypeScript identifier.", model);
    var prefix = LowerFirst(shortName);
    var hasErrors = typeof(INotifyDataErrorInfo).IsAssignableFrom(model);
    var hasValidation = hasErrors || valueProperties.Values.Any(graph =>
        graph.Nodes.Any(node => typeof(INotifyDataErrorInfo).IsAssignableFrom(node.Type)));
    if (hasValidation && properties.FirstOrDefault(property => WireName(property) == "validation") is { } validationProperty)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
            $"{model.Name}.{validationProperty.Name}: validation is reserved for generated validation state. Alias the model property.", validationProperty);
    // Checked writes require a WindowContentSession-owned provider. Keep the
    // established global Bridge surface direct until it gains an equivalent
    // explicit window owner. The window slice supports the scalar codecs that
    // the ordinary Bridge already serializes without reflection.
    var checkedProperties = !registerGlobally
        ? properties.Where(property => property.SetMethod?.IsPublic == true && valueProperties.ContainsKey(property)).ToArray()
        : [];
    var needsCheckedWriter = checkedProperties.Length > 0;
    InteractionCodeEmitter.ValidatePublicSurface(model.Name, properties, commands, commandPlans, interactions,
        checkedProperties, WireName, hasErrors, hasValidation);

    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Runic.Application.Views;");
    AppendNamespace(cs, model.Namespace);
    cs.AppendLine($"internal sealed class {shortName}Bridge : ViewModelBridge<{fullType}>");
    cs.AppendLine("{");
    if (interactions.Length > 0)
        cs.AppendLine("    private readonly global::System.IDisposable? _rootInteractionMount;");
    if (hasContent)
    {
        cs.AppendLine("    private readonly WindowContentSession _content;");
        cs.AppendLine($"    private readonly {fullType} _owner;");
    }
    cs.AppendLine($"    internal {shortName}Bridge(IBridgeTransport transport, {fullType} vm, string? routePrefix = null, WindowContentSession? content = null) : base(");
    cs.AppendLine($"        transport, vm, routePrefix ?? \"{prefix}\",");
    cs.AppendLine(needsCheckedWriter
        ? "        CreateWriter(content),"
        : hasContent
        ? "        CreateWriter(content),"
        : "        WriteSnapshot,");
    cs.AppendLine("        [");
    foreach (var property in properties)
    {
        var setter = property.SetMethod?.IsPublic == true
            && valueProperties.TryGetValue(property, out var graph)
                ? property.PropertyType == typeof(int)
                    ? $"(vm, e) => vm.{property.Name} = checked((int)e.GetInt64())"
                    : property.PropertyType == typeof(bool)
                        ? $"(vm, e) => vm.{property.Name} = e.GetBoolean()"
                        : property.PropertyType == typeof(string) && !IsNullableString(property)
                            ? $"(vm, e) => vm.{property.Name} = e.GetString()"
                            : property.PropertyType == typeof(string)
                                ? $"(vm, e) => vm.{property.Name} = global::Runic.Application.Views.BridgeJson.ReadNullableString(e.GetString())"
                                : $"(vm, e) => {{ using var document = global::System.Text.Json.JsonDocument.Parse(e.GetString()); vm.{property.Name} = {property.Name}ValueCodec.Read(document.RootElement); }}"
            : "null";
        cs.AppendLine($"            new PropertyDescriptor<{fullType}>(\"{property.Name}\", vm => vm.{property.Name}, {setter}),");
    }
    cs.AppendLine("        ],");
    if (needsCheckedWriter)
    {
        cs.AppendLine("        [");
        foreach (var property in checkedProperties)
        {
            var type = valueProperties[property].RootCSharpType();
            cs.AppendLine($"            new CheckedPropertyDescriptor<{fullType}>(\"{property.Name}\", \"{WireName(property)}\", CheckedFieldValueKind.Json, vm => vm.{property.Name}, (vm, value) => vm.{property.Name} = ({type})value!, ReadValue: element => {property.Name}ValueCodec.Read(element), WriteValue: (writer, value) => {property.Name}ValueCodec.Write(writer, ({type})value!)),");
        }
        cs.AppendLine("        ],");
    }
    cs.AppendLine("        [");
    foreach (var command in commands)
        cs.AppendLine($"            {commandPlans[command].DescriptorFor(command, fullType)},");
    cs.AppendLine($"        ], \"{contractFingerprint}\", content,");
    if (interactions.Length > 0)
    {
        cs.AppendLine("        [");
        foreach (var descriptor in InteractionCodeEmitter.CSharpDescriptors(interactions, fullType))
            cs.AppendLine($"            {descriptor},");
        cs.AppendLine("        ],");
    }
    else cs.AppendLine("        null,");
    cs.AppendLine("        dataSubscriptions: DataMetadata,");
    cs.AppendLine("        collections: [");
    foreach (var (property, key) in incrementalCollections)
    {
        // Validation aggregates must remain atomic with their full snapshot, so
        // their collections only check keys and never publish frames.
        var itemType = BridgeTypeGraph.CSharpType(valueProperties[property].Root.Element!.NonNullableType);
        var keyAccess = $"(({itemType})item!).{key.Name}";
        var keyExpression = key.PropertyType == typeof(string) ? keyAccess
            : key.PropertyType == typeof(Guid) ? keyAccess + ".ToString(\"D\")"
            : keyAccess + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)";
        cs.AppendLine($"            new global::Runic.Application.Views.BridgeCollectionDescriptor<{fullType}>(\"{WireName(property)}\", static vm => vm.{property.Name}, {property.Name}ValueCodec.WriteItem, static item => {keyExpression}{(hasValidation ? ", PublishesChanges: false" : "")}),");
    }
    cs.AppendLine("        ]");
    cs.AppendLine("        )");
    cs.AppendLine("    {");
    if (hasContent) cs.AppendLine("        _content = content!; _owner = vm;");
    if (interactions.Length > 0)
    {
        cs.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(content);");
        cs.AppendLine($"        _rootInteractionMount = routePrefix is null ? content.AttachRootInteractionPresentation(\"{prefix}\") : null;");
    }
    cs.AppendLine("    }");
    cs.AppendLine();
    cs.AppendLine("    private static readonly global::Runic.Application.Views.BridgeDataSubscriptionMember[] DataMetadata =");
    BridgeDataSubscriptionEmitter.AppendMetadata(cs, fullType, valueProperties);
    cs.AppendLine("    ;");
    foreach (var (property, graph) in valueProperties)
    {
        graph.AppendCSharpCodec(cs, property.Name + "ValueCodec");
        cs.AppendLine();
    }
    foreach (var (command, plan) in commandPlans)
    {
        if (plan.InputGraph is { } input) { input.AppendCSharpCodec(cs, command.Name + "InputCodec"); cs.AppendLine(); }
        if (plan.ResultGraph is { } result) { result.AppendCSharpCodec(cs, command.Name + "ResultCodec"); cs.AppendLine(); }
    }
    InteractionCodeEmitter.AppendCSharpCodecs(cs, interactions);
    if (commandPlans.Values.Any(plan => plan.ToolkitContract is { IsAsync: true }))
    {
        cs.AppendLine("    private sealed class ToolkitRunningSubscription : global::System.IDisposable");
        cs.AppendLine("    {");
        cs.AppendLine("        private readonly global::System.ComponentModel.INotifyPropertyChanged _command;");
        cs.AppendLine("        private readonly global::System.ComponentModel.PropertyChangedEventHandler _handler;");
        cs.AppendLine("        internal ToolkitRunningSubscription(object command, global::System.Action changed)");
        cs.AppendLine("        {");
        cs.AppendLine("            _command = (global::System.ComponentModel.INotifyPropertyChanged)command;");
        cs.AppendLine("            _handler = (_, e) => { if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == \"IsRunning\") changed(); };");
        cs.AppendLine("            _command.PropertyChanged += _handler;");
        cs.AppendLine("        }");
        cs.AppendLine("        public void Dispose() => _command.PropertyChanged -= _handler;");
        cs.AppendLine("    }");
        cs.AppendLine();
    }
    if (hasContent || needsCheckedWriter)
    {
        cs.AppendLine(needsCheckedWriter
            ? $"    private static global::Runic.Application.Views.BridgeSnapshotWriter<{fullType}> CreateWriter(WindowContentSession? content)"
            : $"    private static global::System.Action<global::System.Text.Json.Utf8JsonWriter, {fullType}, long> CreateWriter(WindowContentSession? content)");
        cs.AppendLine("    {");
        if (hasContent) cs.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(content);");
        cs.AppendLine(needsCheckedWriter
            ? (hasContent
                ? "        return (writer, current, revision, writeFieldMetadata) => WriteSnapshot(writer, current, revision, content, writeFieldMetadata);"
                : "        return (writer, current, revision, writeFieldMetadata) => WriteSnapshot(writer, current, revision, writeFieldMetadata);")
            : "        return (writer, current, revision) => WriteSnapshot(writer, current, revision, content);");
        cs.AppendLine("    }");
        cs.AppendLine();
    }
    cs.AppendLine($"    private static void WriteSnapshot(global::System.Text.Json.Utf8JsonWriter writer, {fullType} vm, long revision{(hasContent ? ", WindowContentSession content" : "")}{(needsCheckedWriter ? ", global::System.Action<global::System.Text.Json.Utf8JsonWriter> writeFieldMetadata" : "")})");
    cs.AppendLine("    {");
    cs.AppendLine("        writer.WriteStartObject();");
    cs.AppendLine("        writer.WriteNumber(\"revision\", revision);");
    foreach (var property in properties)
    {
        var jsonName = WireName(property);
        if (contentProperties.TryGetValue(property, out var pageModels))
        {
            cs.AppendLine($"        writer.WritePropertyName(\"{jsonName}\");");
            cs.AppendLine($"        if (vm.{property.Name} is null) {{ content.Clear(vm, \"{property.Name}\"); writer.WriteNullValue(); }}");
            cs.AppendLine("        else");
            cs.AppendLine("        {");
            cs.AppendLine($"            switch (vm.{property.Name})");
            cs.AppendLine("            {");
            foreach (var (pageModel, pageName) in pageModels)
            {
                var pageType = $"global::{pageModel.FullName}";
                var bridgeType = BridgeTypeName(pageModel, pageName);
                Type? selectedView = null;
                var contract = ContractFor(property);
                if (viewTypes is not null && viewTypes.TryGetValue(pageModel, out var variants))
                {
                    selectedView = variants.FirstOrDefault(view => ContractFor(view) == contract);
                    if (selectedView is null)
                        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                            $"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract ?? "default"}'.", property);
                }
                else if (contract is not null)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                            $"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract}'.", property);
                var pageKind = PageKind(pageName, contract);
                var contractArgument = contract is null ? "" : $", contract: \"{contract}\"";
                cs.AppendLine($"                case {pageType} page:");
                cs.AppendLine("                {");
                if (selectedView is not null)
                    cs.AppendLine($"                    var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content){contractArgument}));");
                else
                    cs.AppendLine($"                    var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => new {bridgeType}(transport, current, route, content));");
                cs.AppendLine("                    writer.WriteStartObject();");
                cs.AppendLine("                    writer.WriteString(\"kind\", reference.Kind);");
                cs.AppendLine("                    writer.WriteString(\"id\", reference.Id);");
                cs.AppendLine("                    writer.WriteEndObject();");
                cs.AppendLine("                    break;");
                cs.AppendLine("                }");
            }
            cs.AppendLine($"                default: throw new global::System.NotSupportedException(\"{property.Name} contains an unregistered ViewModel type.\");");
            cs.AppendLine("            }");
            cs.AppendLine("        }");
        }
        else if (contentCollections.TryGetValue(property, out var collectionModels))
        {
            var activeName = $"active{property.Name}Ids";
            cs.AppendLine($"        writer.WritePropertyName(\"{jsonName}\");");
            cs.AppendLine($"        var {activeName} = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);");
            cs.AppendLine($"        if (vm.{property.Name} is null) {{ content.PruneCollection(vm, \"{property.Name}\", {activeName}); writer.WriteNullValue(); }}");
            cs.AppendLine("        else");
            cs.AppendLine("        {");
            cs.AppendLine("            writer.WriteStartArray();");
            cs.AppendLine($"            foreach (var item in vm.{property.Name})");
            cs.AppendLine("            {");
            cs.AppendLine("                if (item is null) { writer.WriteNullValue(); continue; }");
            cs.AppendLine("                switch (item)");
            cs.AppendLine("                {");
            foreach (var (pageModel, pageName) in collectionModels)
            {
                var pageType = $"global::{pageModel.FullName}";
                var bridgeType = BridgeTypeName(pageModel, pageName);
                var contract = ContractFor(property);
                Type? selectedView = null;
                if (viewTypes is not null && viewTypes.TryGetValue(pageModel, out var variants))
                {
                    selectedView = variants.SingleOrDefault(view => ContractFor(view) == contract);
                    if (selectedView is null)
                        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                            $"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract ?? "default"}'.", property);
                }
                else if (contract is not null)
                    throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                            $"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract}'.", property);
                var pageKind = PageKind(pageName, contract);
                var contractArgument = contract is null ? "" : $", contract: \"{contract}\"";
                cs.AppendLine($"                    case {pageType} page:");
                cs.AppendLine("                    {");
                if (selectedView is not null)
                    cs.AppendLine($"                        var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content){contractArgument}));");
                else
                    cs.AppendLine($"                        var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => new {bridgeType}(transport, current, route, content));");
                cs.AppendLine($"                        {activeName}.Add(reference.Id);");
                cs.AppendLine("                        writer.WriteStartObject();");
                cs.AppendLine("                        writer.WriteString(\"kind\", reference.Kind);");
                cs.AppendLine("                        writer.WriteString(\"id\", reference.Id);");
                cs.AppendLine("                        writer.WriteEndObject();");
                cs.AppendLine("                        break;");
                cs.AppendLine("                    }");
            }
            cs.AppendLine($"                    default: throw new global::System.NotSupportedException(\"{property.Name} contains an unregistered ViewModel type.\");");
            cs.AppendLine("                }");
            cs.AppendLine("            }");
            cs.AppendLine($"            content.PruneCollection(vm, \"{property.Name}\", {activeName});");
            cs.AppendLine("            writer.WriteEndArray();");
            cs.AppendLine("        }");
        }
        else if (valueProperties.ContainsKey(property))
        {
            cs.AppendLine($"        writer.WritePropertyName(\"{jsonName}\");");
            cs.AppendLine($"        {property.Name}ValueCodec.Write(writer, vm.{property.Name});");
        }
        else throw new InvalidOperationException($"{model.Name}.{property.Name}: no generated value codec.");
        if (hasErrors)
            cs.AppendLine($"        global::Runic.Application.Views.BridgeJson.WriteErrors(writer, vm, \"{property.Name}\", \"{jsonName}Errors\");");
    }
    if (hasValidation)
    {
        cs.AppendLine("        writer.WritePropertyName(\"validation\");");
        cs.AppendLine("        global::Runic.Application.Views.BridgeValidation.Write(writer, vm, DataMetadata);");
    }
    // Availability without an argument is state; a command with an argument
    // is queried through can<Name>(argument). Execution state needs no
    // argument and is written for every asynchronous command that has it.
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var name = command.Name[..^"Command".Length];
        if (plan.ReactiveContract is { } contract)
        {
            var commandType = $"global::ReactiveUI.IReactiveCommand<{plan.InputGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Input)}, {plan.ResultGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Result)}>";
            var helper = contract.Flavor is ReactiveUiFlavor.SystemReactive
                ? "global::Runic.Application.Views.ReactiveUI.Reactive.ReactiveCommandExecution"
                : "global::Runic.Application.Views.ReactiveUI.ReactiveCommandExecution";
            var input = contract.Input.FullName == "System.Reactive.Unit"
                ? "default(global::System.Reactive.Unit)"
                : "global::ReactiveUI.Primitives.RxVoid.Default";
            if (!plan.HasArgument)
                cs.AppendLine($"        writer.WriteBoolean(\"can{name}\", {helper}.CanExecute(({commandType})vm.{command.Name}, {input}));");
            cs.AppendLine($"        writer.WriteBoolean(\"is{name}Executing\", {helper}.IsExecuting(({commandType})vm.{command.Name}));");
            continue;
        }
        if (!plan.HasArgument)
            cs.AppendLine($"        writer.WriteBoolean(\"can{name}\", ((global::System.Windows.Input.ICommand)vm.{command.Name}).CanExecute(null));");
        if (plan.ToolkitContract is { IsAsync: true })
            cs.AppendLine($"        writer.WriteBoolean(\"is{name}Executing\", ((global::CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.{command.Name}).IsRunning);");
    }
    if (needsCheckedWriter) cs.AppendLine("        writeFieldMetadata(writer);");
    cs.AppendLine("        writer.WriteEndObject();");
    cs.AppendLine("    }");
    if (hasContent || interactions.Length > 0)
    {
        cs.AppendLine("    public override void Dispose()");
        cs.AppendLine("    {");
        if (interactions.Length > 0) cs.AppendLine("        _rootInteractionMount?.Dispose();");
        cs.AppendLine("        base.Dispose();");
        if (hasContent) cs.AppendLine("        _content.ClearOwner(_owner);");
        cs.AppendLine("    }");
    }
    cs.AppendLine("}");
    if (registerGlobally)
    {
        cs.AppendLine($"internal static class {shortName}BridgeRegistration");
        cs.AppendLine("{");
        cs.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        cs.AppendLine($"    internal static void Register() => global::Runic.Application.Views.Bridge.Register<{fullType}>((transport, vm) => new {shortName}Bridge(transport, vm));");
        cs.AppendLine("}");
    }

    // The transport, shared page runtime, codecs and error types live in
    // @runic-artifex/views. A generated module holds only this ViewModel's
    // types, decoders and connect functions, so every module and bundle on a
    // page shares one BridgeError class and one route registry.
    var ts = new StringBuilder();
    var contentImports = new StringBuilder();
    foreach (var (pageName, contracts) in contentBindings.SelectMany(entry => entry.Value.Select(page => (page.Name, Contract: ContractFor(entry.Key))))
        .GroupBy(entry => entry.Name).Select(group => (group.Key, Contracts: group.Select(entry => entry.Contract).Distinct())))
    {
        var members = string.Join(", ", contracts.SelectMany(contract =>
        {
            var variant = char.ToUpperInvariant(PageKind(pageName, contract)[0]) + PageKind(pageName, contract)[1..];
            return new[] { $"page{variant}", $"type {variant}PageReference" };
        }));
        contentImports.AppendLine($"import {{ {members} }} from \"./{LowerFirst(pageName)}.js\";");
    }
    var clientName = $"{shortName}Client";
    ts.AppendLine($"export interface {shortName}State {{");
    if (hasValidation) ts.AppendLine("  readonly validation: BridgeValidationState;");
    foreach (var property in properties)
    {
        var propertyType = contentProperties.TryGetValue(property, out var pages)
            ? string.Join(" | ", pages.Select(page =>
                $"{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}PageReference"))
                + (nullability.Create(property).ReadState == NullabilityState.Nullable ? " | null" : "")
            : contentCollections.TryGetValue(property, out var collectionPages)
                ? "readonly (" + string.Join(" | ", collectionPages.Select(page =>
                    $"{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}PageReference"))
                    + ")[]" + (nullability.Create(property).ReadState == NullabilityState.Nullable ? " | null" : "")
            : valueProperties.TryGetValue(property, out var valueGraph)
                ? valueGraph.TypeScriptType()
                : throw new InvalidOperationException($"{model.Name}.{property.Name}: no generated TypeScript type.");
        XmlDocumentation.Append(ts, "  ", XmlDocumentation.Summary(property));
        ts.AppendLine($"  readonly {TsPropertyName(WireName(property))}: {propertyType};");
        if (hasErrors) ts.AppendLine($"  readonly {TsPropertyName(WireName(property) + "Errors")}: readonly string[];");
    }
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        if (!plan.HasArgument)
            ts.AppendLine($"  readonly can{command.Name[..^"Command".Length]}: boolean;");
        if (plan.HasExecutionState)
            ts.AppendLine($"  readonly is{command.Name[..^"Command".Length]}Executing: boolean;");
    }
    ts.AppendLine("}");
    ts.AppendLine();
    InteractionCodeEmitter.AppendTypeScriptSurface(ts, interactions, shortName);
    if (needsCheckedWriter)
    {
        ts.AppendLine($"export interface {shortName}CheckedFields {{");
        foreach (var property in checkedProperties)
            ts.AppendLine($"  readonly {TsPropertyName(WireName(property))}: {TsPropertyType(property)};");
        ts.AppendLine("}");
        ts.AppendLine();
    }
    OperationTypeScriptEmitter.AppendDefinitions(ts, operationPlans, shortName);
    var modelDocumentation = XmlDocumentation.Summary(model);
    XmlDocumentation.Append(ts, "", $"A connected {shortName} ViewModel. Dispose it when its presentation ends."
        + (modelDocumentation is null ? "" : "\n\n" + modelDocumentation));
    ts.AppendLine($"export interface {clientName} extends ViewClient<{shortName}State> {{");
    if (interactions.Length > 0) ts.AppendLine($"  readonly interactions: {shortName}Interactions;");
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
    {
        XmlDocumentation.Append(ts, "  ", XmlDocumentation.Summary(property));
        ts.AppendLine($"  set{property.Name}(value: {TsPropertyType(property)}): Promise<{shortName}State>;");
    }
    foreach (var property in checkedProperties)
    {
        XmlDocumentation.Append(ts, "  ", XmlDocumentation.Summary(property));
        ts.AppendLine($"  write{property.Name}(value: {TsPropertyType(property)}, options: FieldWriteOptions<{TsPropertyType(property)}>): Promise<FieldWriteReceipt<{TsPropertyType(property)}>>;");
    }
    if (needsCheckedWriter)
        ts.AppendLine($"  fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K): FieldBaseline<{shortName}CheckedFields[K]>;");
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var input = plan.HasArgument ? $"{plan.ParameterName}: {CommandInputType(plan)}" : "";
        XmlDocumentation.Append(ts, "  ", CommandDocumentation(command, commandSources[command]));
        ts.AppendLine($"  {LowerFirst(command.Name[..^"Command".Length])}({input}): Promise<{shortName}State>;");
        if (plan.HasArgument) ts.AppendLine($"  can{command.Name[..^"Command".Length]}({input}): Promise<boolean>;");
    }
    foreach (var command in commands.Where(command => commandPlans[command].IsAsync))
    {
        var plan = commandPlans[command];
        var operationName = command.Name[..^"Command".Length];
        var input = plan.HasArgument ? $"{plan.ParameterName}: {CommandInputType(plan)}" : "";
        var suffix = plan.HasArgument ? $", {input}" : "";
        ts.AppendLine($"  start{operationName}({input}): Promise<{shortName}{operationName}Operation>;");
        ts.AppendLine($"  start{operationName}WithRequestId(requestId: string{suffix}): Promise<{shortName}{operationName}Operation>;");
        ts.AppendLine($"  recover{operationName}WithRequestId(requestId: string): Promise<{shortName}{operationName}Operation>;");
    }
    ts.AppendLine("}");
    ts.AppendLine();
    var ownContracts = viewTypes is not null && viewTypes.TryGetValue(model, out var ownViews)
        ? ownViews.Select(ContractFor).ToArray() : [null];
    foreach (var contract in ownContracts)
    {
        var ownKind = PageKind(shortName, contract);
        var functionName = char.ToUpperInvariant(ownKind[0]) + ownKind[1..];
        var referenceType = $"{functionName}PageReference";
        var cacheName = $"page{functionName}References";
        ts.AppendLine($"export interface {referenceType} {{");
        ts.AppendLine($"  readonly kind: \"{ownKind}\";");
        ts.AppendLine($"  connect(): Promise<{clientName}>;");
        ts.AppendLine("}");
        ts.AppendLine($"const {cacheName} = viewReferences<{referenceType}>(id => ({{ kind: \"{ownKind}\", connect: () => connect{shortName}At(`content${{id}}`, true) }}));");
        ts.AppendLine($"export function page{functionName}(id: string): {referenceType} {{ return {cacheName}(id); }}");
        ts.AppendLine();
    }
    // The public state is the decoded TypeScript contract. The transport is
    // JSON, so every graph-backed field must remain unknown until hydrate
    // validates and converts it (for example Int64 strings to bigint). The
    // runtime orders states by revision; it is not part of the public state.
    if (hasContent || needsCheckedWriter || valueProperties.Count > 0 || hasValidation)
    {
        var names = contentBindings.Select(entry => $"\"{WireName(entry.Key)}\"")
            .Concat(valueProperties.Keys.Select(property => $"\"{WireName(property)}\""))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hasValidation) names.Add("\"validation\"");
        if (names.Count == 0) names.Add("never");
        ts.AppendLine($"type WireState = Omit<{shortName}State, {string.Join(" | ", names)}> & {{");
        ts.AppendLine("  readonly revision: number;");
        if (hasValidation) ts.AppendLine("  readonly validation: unknown;");
        foreach (var property in valueProperties.Keys)
            ts.AppendLine($"  readonly {TsPropertyName(WireName(property))}: unknown;");
        foreach (var (property, pages) in contentProperties)
        {
            var rawType = string.Join(" | ", pages.Select(page => $"{{ readonly kind: \"{PageKind(page.Name, ContractFor(property))}\"; readonly id: string }}"));
            if (nullability.Create(property).ReadState == NullabilityState.Nullable) rawType += " | null";
            ts.AppendLine($"  readonly {TsPropertyName(WireName(property))}: {rawType};");
        }
        foreach (var (property, pages) in contentCollections)
        {
            var rawType = "readonly (" + string.Join(" | ", pages.Select(page =>
                $"{{ readonly kind: \"{PageKind(page.Name, ContractFor(property))}\"; readonly id: string }}")) + ")[]";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable) rawType += " | null";
            ts.AppendLine($"  readonly {TsPropertyName(WireName(property))}: {rawType};");
        }
        if (needsCheckedWriter)
        {
            ts.AppendLine("  readonly __runicFields: {");
            foreach (var property in checkedProperties)
                ts.AppendLine($"    readonly {TsPropertyName(WireName(property))}: {{ readonly version: number }};");
            ts.AppendLine("  };");
        }
        ts.AppendLine("};");
        ts.AppendLine($"function hydrate(wire: WireState): {shortName}State {{");
        ts.AppendLine(needsCheckedWriter
            ? "  const { revision: _revision, __runicFields: _runicFields, ...state } = wire;"
            : "  const { revision: _revision, ...state } = wire;");
        ts.AppendLine("  return {");
        ts.AppendLine("    ...state,");
        if (hasValidation) ts.AppendLine("    validation: decodeBridgeValidation(wire.validation),");
        foreach (var (property, graph) in valueProperties)
        {
            var field = WireName(property);
            ts.AppendLine($"    {TsLiteralKey(field)}: {graph.EmitTypeScriptDecoder(TsAccess("wire", field))},");
        }
        foreach (var (property, pages) in contentProperties)
        {
            var field = WireName(property);
            var expression = string.Join(" : ", pages.Select(page =>
                $"{TsAccess("wire", field)}.kind === \"{PageKind(page.Name, ContractFor(property))}\" ? page{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}({TsAccess("wire", field)}.id)"));
            expression += $" : (() => {{ throw new BridgeError(\"failed\", \"Unknown {field} kind.\"); }})()";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable)
                expression = $"{TsAccess("wire", field)} === null ? null : {expression}";
            ts.AppendLine($"    {TsLiteralKey(field)}: {expression},");
        }
        foreach (var (property, pages) in contentCollections)
        {
            var field = WireName(property);
            var expression = string.Join(" : ", pages.Select(page =>
                $"item.kind === \"{PageKind(page.Name, ContractFor(property))}\" ? page{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}(item.id)"));
            expression += $" : (() => {{ throw new BridgeError(\"failed\", \"Unknown {field} kind.\"); }})()";
            var hydrated = $"{TsAccess("wire", field)}.map(item => {expression})";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable)
                hydrated = $"{TsAccess("wire", field)} === null ? null : {hydrated}";
            ts.AppendLine($"    {TsLiteralKey(field)}: {hydrated},");
        }
        ts.AppendLine("  };");
        ts.AppendLine("}");
    }
    else
    {
        ts.AppendLine($"type WireState = {shortName}State & {{ readonly revision: number }};");
        ts.AppendLine($"function hydrate(wire: WireState): {shortName}State {{ const {{ revision: _revision, ...state }} = wire; return state; }}");
    }
    ts.AppendLine($"const bridgeContract = \"{model.FullName}:{contractFingerprint}\";");
    InteractionCodeEmitter.AppendTypeScriptDefinitions(ts, interactions);
    if (needsCheckedWriter)
        ts.AppendLine($"const checkedFieldNames = [{string.Join(", ", checkedProperties.Select(property => JsonSerializer.Serialize(WireName(property))))}];");
    ts.AppendLine();
    if (incrementalCollections.Count > 0)
    {
        ts.AppendLine("const collectionDefinitions = defineCollections({");
        foreach (var (property, key) in incrementalCollections)
        {
            var graph = valueProperties[property];
            var keyMember = graph.Root.Element!.Members.Single(member => member.Property == key);
            ts.AppendLine($"  {TsLiteralKey(WireName(property))}: defineCollection<{graph.ItemTypeScriptType()}>(wire => {BridgeTypeGraph.ArrowBody(graph.EmitItemTypeScriptDecoder("wire"))}, item => String({TsAccess("item", keyMember.WireName)})),");
        }
        ts.AppendLine("});");
    }
    var connectOptions = "contract: bridgeContract, route, mount, hydrate"
        + (incrementalCollections.Count > 0 ? ", collections: collectionDefinitions" : "")
        + (needsCheckedWriter ? ", checkedFields: checkedFieldNames" : "")
        + (interactions.Length > 0 ? ", interactions: interactionDefinitions" : "")
        + (operationPlans.Length > 0 ? ", operations: bridgeOperations" : "");
    ts.AppendLine($"export function connect{shortName}(): Promise<{clientName}> {{ return connect{shortName}At(\"{prefix}\", {(interactions.Length > 0 ? "true" : "false")}); }}");
    ts.AppendLine($"async function connect{shortName}At(route: string, mount = false): Promise<{clientName}> {{");
    ts.AppendLine($"  const view = await connectView({{ {connectOptions} }});");
    OperationTypeScriptEmitter.AppendRuntime(ts, operationPlans);
    ts.AppendLine("  return {");
    ts.AppendLine("    get snapshot() { return view.snapshot; },");
    ts.AppendLine("    subscribe: view.subscribe,");
    ts.AppendLine("    dispose: view.dispose,");
    if (interactions.Length > 0)
        ts.AppendLine($"    interactions: view.interactions as unknown as {shortName}Interactions,");
    if (needsCheckedWriter)
        ts.AppendLine($"    fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K) {{ return view.fieldBaseline(field) as FieldBaseline<{shortName}CheckedFields[K]>; }},");
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
    {
        ts.AppendLine($"    async set{property.Name}(value) {{");
        if (property.PropertyType == typeof(int))
            ts.AppendLine("      if (!Number.isSafeInteger(value)) throw new RangeError(\"Value must be an integer.\");");
        var argument = valueProperties.TryGetValue(property, out var valueGraph)
            && property.PropertyType != typeof(int)
            && property.PropertyType != typeof(bool)
            && property.PropertyType != typeof(string)
                ? $"JSON.stringify({valueGraph.EncodeTypeScript("value")})"
                : IsNullableString(property) ? "JSON.stringify(value)" : "value";
        ts.AppendLine($"      return view.invoke(`${{route}}Set{property.Name}`, {argument});");
        ts.AppendLine("    },");
    }
    foreach (var property in checkedProperties)
    {
        ts.AppendLine($"    async write{property.Name}(value, options) {{");
        ts.AppendLine("      if (typeof options?.requestId !== \"string\" || options.requestId.trim().length === 0) throw new RangeError(\"A checked field requestId is required.\");");
        ts.AppendLine("      const baseline = options.baseline;");
        var check = property.PropertyType == typeof(int)
            ? "typeof value !== \"number\" || !Number.isSafeInteger(value) || !baseline || typeof baseline.value !== \"number\" || !Number.isSafeInteger(baseline.value)"
            : property.PropertyType == typeof(bool)
                ? "typeof value !== \"boolean\" || !baseline || typeof baseline.value !== \"boolean\""
            : IsNullableString(property)
                ? "(value !== null && typeof value !== \"string\") || !baseline || (baseline.value !== null && typeof baseline.value !== \"string\")"
                    : property.PropertyType == typeof(string)
                        ? "typeof value !== \"string\" || !baseline || typeof baseline.value !== \"string\""
                        : "!baseline";
        ts.AppendLine($"      if ({check} || !Number.isSafeInteger(baseline.version) || baseline.version < 0) throw new RangeError(\"A checked field value and baseline are required.\");");
        var valueGraph = valueProperties[property];
        var encodedBaseline = valueGraph.EncodeTypeScript("baseline.value");
        var encodedValue = valueGraph.EncodeTypeScript("value");
        ts.AppendLine($"      return view.writeField<{TsPropertyType(property)}>(`${{route}}Write{property.Name}`, JSON.stringify({{ requestId: options.requestId, expectedVersion: baseline.version, expectedValue: {encodedBaseline}, value: {encodedValue} }}), value => {BridgeTypeGraph.ArrowBody(valueGraph.EmitTypeScriptDecoder("value"))});");
        ts.AppendLine("    },");
    }
    foreach (var command in commands)
    {
        var name = command.Name[..^"Command".Length];
        var plan = commandPlans[command];
        var parameter = plan.ParameterName;
        var argument = plan.InputGraph is { } inputGraph
            ? $", JSON.stringify({inputGraph.EncodeTypeScript(parameter)})"
            : plan.HasStringArgument ? $", JSON.stringify({parameter})" : "";
        ts.AppendLine($"    async {LowerFirst(name)}({(plan.HasArgument ? parameter : "")}) {{ return view.command(`${{route}}{name}`{argument}); }},");
        if (plan.HasArgument)
            ts.AppendLine($"    async can{name}({parameter}) {{ return view.query(`${{route}}Can{name}`{argument}); }},");
    }
    OperationTypeScriptEmitter.AppendClientMethods(ts, operationPlans);
    ts.AppendLine("  };");
    ts.AppendLine("}");

    var body = ts.ToString();
    // Application-facing types and errors come from the package root; the
    // runtime only generated code calls comes from its /generated entry, and
    // only the protocol parts this ViewModel uses are imported.
    var publicImports = new List<string>();
    if (body.Contains("new BridgeError(", StringComparison.Ordinal)) publicImports.Add("BridgeError");
    if (hasValidation) publicImports.Add("type BridgeValidationState");
    publicImports.Add("type ViewClient");
    if (needsCheckedWriter) publicImports.AddRange(["type FieldBaseline", "type FieldWriteOptions", "type FieldWriteReceipt"]);
    if (operationPlans.Any(operation => !operation.IsStream)) publicImports.Add("type BridgeOperation");
    if (operationPlans.Any(operation => operation.IsStream)) publicImports.Add("type BridgeStreamOperation");
    if (interactions.Length > 0) publicImports.Add("type BridgeInteractionContext");
    var runtimeImports = new List<string> { "connectView", "viewReferences" };
    if (incrementalCollections.Count > 0) runtimeImports.AddRange(["defineCollection", "defineCollections"]);
    if (interactions.Length > 0) runtimeImports.Add("defineInteractions");
    if (operationPlans.Length > 0) runtimeImports.Add("bridgeOperations");
    if (hasValidation) runtimeImports.Add("decodeBridgeValidation");
    ts.Clear();
    ts.AppendLine("// <auto-generated />");
    ts.AppendLine(publicImports.All(name => name.StartsWith("type ", StringComparison.Ordinal))
        ? $"import type {{ {string.Join(", ", publicImports.Select(name => name["type ".Length..]))} }} from \"@runic-artifex/views\";"
        : $"import {{ {string.Join(", ", publicImports)} }} from \"@runic-artifex/views\";");
    ts.AppendLine($"import {{ {string.Join(", ", runtimeImports)} }} from \"@runic-artifex/views/generated\";");
    // A namespace import of the decoder module lets every bundler drop unused decoders.
    if (body.Contains("bridgeWire.", StringComparison.Ordinal))
        ts.AppendLine("import * as bridgeWire from \"@runic-artifex/views/generated/wire\";");
    ts.Append(contentImports);
    ts.AppendLine(TypeScriptModules.NamedTypeImports);
    ts.AppendLine();
    ts.Append(body);

    WriteIfChanged(csharpPath, cs.ToString());
    TypeScriptModules.Add(typescriptPath, ts.ToString());
    TypeScriptModules.Add(MockModulePath(typescriptPath), MockTypeScriptEmitter.Emit(new MockTypeScriptPlan(
        shortName, prefix,
        ownContracts.Select(contract => PageKind(shortName, contract)).ToArray(),
        valueProperties.Select(entry => new MockValueField(WireName(entry.Key), entry.Value)).ToArray(),
        contentBindings.Select(entry => new MockContentField(WireName(entry.Key),
            string.Join(" | ", entry.Value.Select(page => $"MockReference<{JsonSerializer.Serialize(PageKind(page.Name, ContractFor(entry.Key)))}>")),
            contentCollections.ContainsKey(entry.Key),
            nullability.Create(entry.Key).ReadState == NullabilityState.Nullable)).ToArray(),
        MockDefaults(),
        checkedProperties.Select(WireName).ToArray(),
        properties.Where(property => property.SetMethod?.IsPublic == true && valueProperties.ContainsKey(property))
            .Select(property => new MockSetter($"set{property.Name}", $"Set{property.Name}", WireName(property), TsPropertyType(property),
                property.PropertyType == typeof(int) || property.PropertyType == typeof(bool)
                    || property.PropertyType == typeof(string) && !IsNullableString(property)
                    ? "raw" : "JSON.parse(raw as string)",
                checkedProperties.Contains(property) ? $"Write{property.Name}" : null)).ToArray(),
        commands.Select(command =>
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            return new MockCommand(LowerFirst(name), name, plan.HasArgument ? CommandInputType(plan) : null,
                plan.HasArgument ? plan.InputGraph?.EmitTypeScriptDecoder("wire") ?? "bridgeWire.string(wire)" : null,
                plan.HasArgument ? null : $"can{name}");
        }).ToArray(),
        commands.Where(command => commandPlans[command].IsAsync).Select(command =>
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            return new MockOperation(LowerFirst(name), name, plan.HasArgument ? CommandInputType(plan) : "void",
                plan.ResultGraph?.TypeScriptType() ?? "never", plan.InputGraph?.EmitTypeScriptDecoder("wire"),
                plan.ResultGraph?.EncodeTypeScript("typed"), plan.ReactiveContract?.Cardinality is BridgeCommandResultCardinality.Stream);
        }).ToArray(),
        interactions.Select(plan => new MockInteraction(LowerFirst(plan.Property.Name), plan.Input.TypeScriptType(),
            plan.Output.TypeScriptType(), plan.Input.EncodeTypeScript("typed"), plan.Output.EmitTypeScriptDecoder("wire"))).ToArray(),
        incrementalCollections.Select(entry =>
        {
            var graph = valueProperties[entry.Key];
            var keyMember = graph.Root.Element!.Members.Single(member => member.Property == entry.Value);
            return new MockCollection(WireName(entry.Key), graph.ItemTypeScriptType(), graph.EncodeItemTypeScript("typed"),
                graph.EmitItemTypeScriptDecoder("wire"), $"String({TsAccess("item", keyMember.WireName)})");
        }).ToArray(), $"{model.FullName}:{contractFingerprint}"), Path.GetFileNameWithoutExtension(typescriptPath)));
    Console.WriteLine($"Generated {shortName} bridge from compiled {model.Name}: {properties.Length} properties, {commands.Length} commands.");
    return hasContent || interactions.Length > 0;

    string TsPropertyType(PropertyInfo property) => valueProperties.TryGetValue(property, out var graph)
        ? graph.TypeScriptType()
        : throw new InvalidOperationException($"{model.Name}.{property.Name}: no generated TypeScript type.");

    string CommandInputType(GeneratedCommandPlan plan) => plan.InputGraph?.TypeScriptType()
        ?? (plan.HasStringArgument ? "string" : "never");

    bool IsNullableString(PropertyInfo property) =>
        property.PropertyType == typeof(string)
        && nullability.Create(property).ReadState == NullabilityState.Nullable;

    // State a mock fills in unless the test sets it: command availability and
    // execution, and empty validation.
    (string Field, string Default)[] MockDefaults()
    {
        var defaults = new List<(string Field, string Default)>();
        if (hasValidation) defaults.Add(("validation", "{ hasErrors: false, truncated: false, errors: [] }"));
        if (hasErrors) defaults.AddRange(properties.Select(property => (WireName(property) + "Errors", "[]")));
        foreach (var command in commands)
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            if (!plan.HasArgument) defaults.Add(($"can{name}", "true"));
            if (plan.HasExecutionState) defaults.Add(($"is{name}Executing", "false"));
        }
        return [.. defaults];
    }
}

static string MockModulePath(string typescriptPath) =>
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(typescriptPath))!, $"{Path.GetFileNameWithoutExtension(typescriptPath)}.mock.ts");

// The command inspectors live in adapter assemblies and report shape errors
// as NotSupportedException; attach the command diagnostic code and location.
static T? InspectCommand<T>(Type model, PropertyInfo command, Func<T?> inspect) where T : class
{
    try
    {
        return inspect();
    }
    catch (NotSupportedException error) when (error is not BridgeDiagnosticException and not BridgeTypeGraphException)
    {
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command, $"{model.Name}.{error.Message}",
            (MemberInfo?)ToolkitSourceMethod(model, command) ?? command);
    }
}

// CommunityToolkit generates SaveCommand from [RelayCommand] Save, SaveAsync
// or OnSave. The method carries the parameter name, documentation and the
// source line that the generated property lacks. The lookup is shared with
// BridgeContractShape, which fingerprints failure declarations on the method.
static MethodInfo? ToolkitSourceMethod(Type model, PropertyInfo command) =>
    BridgeCommandSources.ToolkitSourceMethod(model, command);

// A [RelayCommand] property's generated comment only says which method it
// wraps, so a Toolkit command uses its method's documentation alone.
static string? CommandDocumentation(PropertyInfo command, MethodInfo? source) =>
    source is null ? XmlDocumentation.Summary(command) : XmlDocumentation.Summary(source);

// The TypeScript parameter of a command with an argument: the Toolkit method's
// parameter name, else the argument's C# type name, else "input".
static string CommandParameterName(MethodInfo? source, GeneratedCommandPlan plan)
{
    if (!plan.HasArgument) return "input";
    var candidate = source?.GetParameters().FirstOrDefault(parameter => parameter.ParameterType != typeof(CancellationToken))?.Name;
    if (candidate is null && plan.InputGraph?.Root is { Kind: BridgeWireKind.Dto or BridgeWireKind.Union or BridgeWireKind.Enum } root)
        candidate = LowerFirst(root.NonNullableType.Name.Split('`')[0]);
    if (candidate is null || !IsTypeScriptIdentifier(candidate)) return "input";
    return TypeScriptModules.IsReservedParameterName(candidate) ? candidate + "Value" : candidate;
}

static string LowerFirst(string text) => char.ToLowerInvariant(text[0]) + text[1..];

static string WireName(PropertyInfo property)
{
    var name = property.GetCustomAttribute<RunicAliasAttribute>(true)?.Name
        ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name
        ?? LowerFirst(property.Name);
    if (string.IsNullOrWhiteSpace(name))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
            $"{property.DeclaringType?.Name}.{property.Name}: a bridge wire name is required.", property);
    return name;
}

static bool IsTypeScriptIdentifier(string name) => name.Length > 0
    && (char.IsLetter(name[0]) || name[0] is '_' or '$')
    && name.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '_' or '$');

static string TsPropertyName(string name) => IsTypeScriptIdentifier(name)
    ? name
    : JsonSerializer.Serialize(name);

// An object-literal key. "__proto__" stays computed: as a plain or quoted key
// it would set the literal's prototype instead of an own property.
static string TsLiteralKey(string name) => name == "__proto__" ? $"[{JsonSerializer.Serialize(name)}]" : TsPropertyName(name);

// Reads an own property; "__proto__" is bracketed like any other non-plain name.
static string TsAccess(string target, string name) => IsTypeScriptIdentifier(name) && name != "__proto__"
    ? $"{target}.{name}"
    : $"{target}[{JsonSerializer.Serialize(name)}]";

static void WriteIfChanged(string path, string content)
{
    var fullPath = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    if (File.Exists(fullPath) && File.ReadAllText(fullPath) == content) return;
    // Frontend watchers (Vite, tsc --watch) must never observe a truncated
    // module, so replace the file in one rename.
    var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
    try
    {
        File.WriteAllText(temporary, content);
        File.Move(temporary, fullPath, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

/// <summary>
/// Diagnostic IDs. The codegen section of the Runic.Application README
/// documents each one with its remediation.
/// </summary>
static class BridgeDiagnosticCodes
{
    /// <summary>Invalid generator invocation or build configuration, or an internal generator error.</summary>
    internal const string Invocation = "RUNICBRIDGE001";
    /// <summary>The ViewModel's validation is not supported under Native AOT.</summary>
    internal const string Aot = "RUNICBRIDGE002";
    /// <summary>A state, command, or interaction value type is not a supported bridge value.</summary>
    internal const string ValueType = "RUNICBRIDGE003";
    /// <summary>Two generated names, routes, or files collide.</summary>
    internal const string NameCollision = "RUNICBRIDGE004";
    /// <summary>The model assembly or one of its dependencies could not be loaded.</summary>
    internal const string AssemblyLoad = "RUNICBRIDGE005";
    /// <summary>A Window or View class, or its View contract, is invalid.</summary>
    internal const string View = "RUNICBRIDGE006";
    /// <summary>A ViewModel class has an unsupported shape.</summary>
    internal const string ViewModel = "RUNICBRIDGE007";
    /// <summary>A state property or ViewModel content member has an unsupported shape.</summary>
    internal const string State = "RUNICBRIDGE008";
    /// <summary>A command has an unsupported name or shape.</summary>
    internal const string Command = "RUNICBRIDGE009";
    /// <summary>A RunicCollection member is not a keyed collection of DTO rows.</summary>
    internal const string Collection = "RUNICBRIDGE010";
    /// <summary>A ReactiveUI interaction has an unsupported shape.</summary>
    internal const string Interaction = "RUNICBRIDGE011";
}

/// <summary>A generator diagnostic with its ID and the declaration it concerns.</summary>
sealed class BridgeDiagnosticException(string code, string message, MemberInfo? member = null) : NotSupportedException(message)
{
    internal string Code { get; } = code;
    internal MemberInfo? Member { get; } = member;
}

/// <summary>
/// Writes MSBuild canonical errors, with the source location of the member
/// when the model assembly's PDB has one. Codes are in <see cref="BridgeDiagnosticCodes"/>.
/// </summary>
static class Diagnostics
{
    internal static bool HasErrors { get; private set; }

    internal static bool IsReportable(Exception error) => error is ArgumentException or InvalidOperationException
        or NotSupportedException or IOException or UnauthorizedAccessException or BadImageFormatException
        or TypeLoadException or ReflectionTypeLoadException;

    internal static void Report(Exception error, Type? model = null)
    {
        HasErrors = true;
        Environment.ExitCode = 1;
        var code = error switch
        {
            BridgeDiagnosticException diagnostic => diagnostic.Code,
            BridgeTypeGraphException => BridgeDiagnosticCodes.ValueType,
            ReflectionTypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException
                or TypeLoadException => BridgeDiagnosticCodes.AssemblyLoad,
            _ => BridgeDiagnosticCodes.Invocation,
        };
        var member = error switch
        {
            BridgeDiagnosticException diagnostic => diagnostic.Member,
            BridgeTypeGraphException graph => graph.Member,
            _ => null,
        } ?? model;
        var message = error is ReflectionTypeLoadException load
            ? $"{error.Message} {string.Join(" ", load.LoaderExceptions.OfType<Exception>().Select(inner => inner.Message).Distinct())}"
            : error.Message;
        // Name the ViewModel when the message does not already start with it.
        if (model is not null && !message.StartsWith(model.Name, StringComparison.Ordinal)
            && !message.StartsWith(model.FullName ?? model.Name, StringComparison.Ordinal))
            message = $"{model.FullName}: {message}";
        var location = SourceLocator.Find(member);
        Console.Error.WriteLine(location is { } at
            ? $"{at.Path}({at.Line},{at.Column}): error {code}: {message}"
            : $"error {code}: {message}");
    }
}

/// <summary>
/// Collects the generated ViewModel modules, then writes them together with
/// the shared named-type module once every name is known.
/// </summary>
static class TypeScriptModules
{
    /// <summary>A line the module writer replaces with the named-type imports.</summary>
    internal const string NamedTypeImports = "\u0002named-type-imports\u0002";

    // Identifiers generated client methods use, and JavaScript reserved
    // words. A command parameter with such a name gets a "Value" suffix.
    internal static readonly HashSet<string> ReservedParameterNames = new(StringComparer.Ordinal)
    {
        "view", "route", "mount", "requestId", "bridgeWire", "BridgeError", "JSON", "String", "Number", "Object",
        "globalThis", "hydrate", "bridgeContract", "collectionDefinitions", "interactionDefinitions", "checkedFieldNames",
        "arguments", "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete",
        "do", "else", "enum", "eval", "export", "extends", "false", "finally", "for", "function", "if", "implements",
        "import", "in", "instanceof", "interface", "let", "new", "null", "package", "private", "protected", "public",
        "return", "static", "super", "switch", "this", "throw", "true", "try", "typeof", "undefined", "var", "void",
        "while", "with", "yield",
    };

    // Encoders declare bridgeNullable<N>, bridgeUnion<N> and
    // bridgeDecodedUnion<N> locals around the argument expression.
    internal static bool IsReservedParameterName(string name) => ReservedParameterNames.Contains(name)
        || name.StartsWith("bridgeNullable", StringComparison.Ordinal) || name.StartsWith("bridgeUnion", StringComparison.Ordinal)
        || name.StartsWith("bridgeDecodedUnion", StringComparison.Ordinal);

    private static readonly List<(string Path, string Text)> Pending = [];

    internal static void Add(string path, string text) => Pending.Add((path, text));

    internal static void WriteAll(string directory)
    {
        TypeScriptNamedTypes.Complete(Pending.Select(module => module.Text));
        var typesPath = Path.Combine(directory, $"{TypeScriptNamedTypes.ModuleName}.ts");
        if (TypeScriptNamedTypes.Any && File.Exists(typesPath) && !GeneratedOutput.IsGenerated(typesPath))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"{Path.GetFullPath(typesPath)} is not generated, but the generator writes its named TypeScript types there. Move the file or generate into a dedicated directory.");
        foreach (var (path, text) in Pending)
        {
            var used = TypeScriptNamedTypes.Used(text);
            var imports = used.Count == 0 ? ""
                : $"import type {{ {string.Join(", ", used)} }} from \"./{TypeScriptNamedTypes.ModuleName}.js\";{Environment.NewLine}"
                  + $"export type {{ {string.Join(", ", used)} }} from \"./{TypeScriptNamedTypes.ModuleName}.js\";";
            var resolved = TypeScriptNamedTypes.Resolve(text)
                .Replace(NamedTypeImports + Environment.NewLine, imports.Length == 0 ? "" : imports + Environment.NewLine, StringComparison.Ordinal);
            WriteFile(path, resolved);
        }
        if (TypeScriptNamedTypes.Any)
            WriteFile(typesPath, $"{GeneratedOutput.Header}{Environment.NewLine}{TypeScriptNamedTypes.Module()}");
        else if (File.Exists(typesPath) && GeneratedOutput.IsGenerated(typesPath))
            File.Delete(typesPath);
        Pending.Clear();
    }

    private static void WriteFile(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        if (File.Exists(fullPath) && File.ReadAllText(fullPath) == content) return;
        // Frontend watchers must never observe a truncated module.
        var temporary = Path.Combine(Path.GetDirectoryName(fullPath)!, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

static class GeneratedOutput
{
    internal const string Header = "// <auto-generated />";

    /// <summary>True when a file starts with the header every generator output carries.</summary>
    internal static bool IsGenerated(string path)
    {
        using var reader = new StreamReader(path);
        return reader.ReadLine()?.TrimEnd() == Header;
    }
}

static class CodegenOptions
{
    internal static ReactiveUiFlavor ReactiveUiFlavor { get; set; } = ReactiveUiFlavor.Default;
}

// Every supported command adapter shares input graphs and operation emission.
// Only the framework-specific execution contract remains in its inspector.
sealed record GeneratedCommandPlan(
    bool HasStringArgument,
    bool IsAsync,
    string? LegacyDescriptor = null,
    ReactiveCommandContract? ReactiveContract = null,
    BridgeTypeGraph? InputGraph = null,
    BridgeTypeGraph? ResultGraph = null,
    bool IsPlainICommand = false,
    ToolkitCommandContract? ToolkitContract = null)
{
    internal bool HasArgument => InputGraph is not null || HasStringArgument;
    /// <summary>The generated TypeScript parameter name of the command argument.</summary>
    internal string ParameterName { get; init; } = "input";
    /// <summary>True when the snapshot carries is&lt;Name&gt;Executing.</summary>
    internal bool HasExecutionState => ReactiveContract is not null || ToolkitContract is { IsAsync: true };
    internal static GeneratedCommandPlan Reactive(ReactiveCommandContract contract,
        BridgeTypeGraph? input, BridgeTypeGraph? result) =>
        new(contract.HasInput && contract.Input == typeof(string),
            IsAsync: true, ReactiveContract: contract, InputGraph: input, ResultGraph: result);

    internal static GeneratedCommandPlan Plain(BridgeTypeGraph input) =>
        new(HasStringArgument: false, IsAsync: false, InputGraph: input, IsPlainICommand: true);

    internal string DescriptorFor(PropertyInfo property, string modelType)
    {
        if (LegacyDescriptor is not null) return LegacyDescriptor;
        if (ToolkitContract is { } toolkit)
        {
            var toolkitType = toolkit.Input is null ? null : InputGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(toolkit.Input);
            var readArgument = toolkit.HasInput
                ? $"ReadArgument: e => {{ using var document = global::System.Text.Json.JsonDocument.Parse(e.GetString()); return {property.Name}InputCodec.Read(document.RootElement); }}" : null;
            var encodeArgument = toolkit.HasInput
                ? $"EncodeArgument: argument => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({toolkitType})argument!))" : null;
            // IsRunning can change without CanExecuteChanged (for example with
            // concurrent executions), so publish on the command's own change.
            var subscribeRunning = toolkit.IsAsync
                ? $"Subscribe: (vm, changed) => new ToolkitRunningSubscription(vm.{property.Name}, changed)" : null;
            return ToolkitCommandInspector.DescriptorFor(property, modelType, toolkit, toolkitType, readArgument, encodeArgument, subscribeRunning);
        }
        if (IsPlainICommand)
        {
            var plainGraph = InputGraph ?? throw new InvalidOperationException("Plain ICommand is missing its input graph.");
            var plainInputType = plainGraph.RootCSharpType();
            var name = property.Name[..^"Command".Length];
            return $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{name}\", vm => (object)vm.{property.Name}, ReadArgument: e => {{ using var document = global::System.Text.Json.JsonDocument.Parse(e.GetString()); return {property.Name}InputCodec.Read(document.RootElement); }}, EncodeArgument: argument => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({plainInputType})argument!)))";
        }
        var contract = ReactiveContract ?? throw new InvalidOperationException("Command plan has no descriptor.");
        var inputType = InputGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Input);
        var resultType = ResultGraph?.RootCSharpType() ?? BridgeTypeGraph.CSharpType(contract.Result);
        var commandType = $"global::ReactiveUI.IReactiveCommand<{inputType}, {resultType}>";
        var typed = $"({commandType})vm.{property.Name}";
        var helper = contract.Flavor is ReactiveUiFlavor.SystemReactive
            ? "global::Runic.Application.Views.ReactiveUI.Reactive.ReactiveCommandExecution"
            : "global::Runic.Application.Views.ReactiveUI.ReactiveCommandExecution";
        var input = contract.HasInput ? $"({inputType})argument!"
            : contract.Input.FullName == "System.Reactive.Unit" ? "default(global::System.Reactive.Unit)"
            : "global::ReactiveUI.Primitives.RxVoid.Default";
        var read = contract.HasInput
            ? $", ReadArgument: e => {{ using var document = global::System.Text.Json.JsonDocument.Parse(e.GetString()); return {property.Name}InputCodec.Read(document.RootElement); }}"
            : "";
        var canonical = contract.HasInput
            ? $", EncodeArgument: argument => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({inputType})argument!))"
            : "";
        var canExecute = $", CanExecute: (vm, argument) => {helper}.CanExecute({typed}, {input})";
        var subscribe = $", Subscribe: (vm, changed) => {helper}.Observe({typed}, changed)";
        if (contract.HasResult && contract.Cardinality is BridgeCommandResultCardinality.Stream)
        {
            var streamExecute = $"async (vm, execution, token, argument) => await {helper}.ExecuteStream({typed}, {input}, execution.Stream!, result => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result)), token).ConfigureAwait(false)";
            return $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{contract.Name}\", vm => (object)vm.{property.Name}, CreateStream: () => new global::Runic.Application.Views.BridgeOperationStream(), ExecuteStreamAsync: {streamExecute}{read}{canonical}{canExecute}{subscribe})";
        }
        var executionMethod = contract.Cardinality is BridgeCommandResultCardinality.Last ? "ExecuteLast" : "Execute";
        var execute = contract.HasResult
            ? $"async (vm, token, argument) => {{ var result = await {helper}.{executionMethod}({typed}, {input}, token).ConfigureAwait(false); return global::Runic.Application.Views.BridgeOperationResult.Encode(() => global::Runic.Application.Views.BridgeWire.EncodeCanonical(writer => {property.Name}ResultCodec.Write(writer, result))); }}"
            : $"async (vm, token, argument) => {{ await {helper}.ExecuteCompletion({typed}, {input}, token).ConfigureAwait(false); return global::Runic.Application.Views.BridgeOperationResult.None; }}";
        return $"new global::Runic.Application.Views.CommandDescriptor<{modelType}>(\"{contract.Name}\", vm => (object)vm.{property.Name}, ExecuteResultAsync: {execute}{read}{canonical}{canExecute}{subscribe})";
    }
}
