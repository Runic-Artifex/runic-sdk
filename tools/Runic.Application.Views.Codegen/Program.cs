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
                    throw new InvalidOperationException($"{type.FullName}: a Runic Window or View must be a public, top-level concrete class.");
                if (!viewTypes.TryGetValue(model, out var variants))
                    viewTypes.Add(model, variants = []);
                var contract = ContractFor(type);
                if (variants.Any(existing => string.Equals(ContractFor(existing), contract, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{model.FullName}: duplicate Runic View contract '{contract ?? "default"}'.");
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
            throw new InvalidOperationException($"{assembly.GetName().Name}: no Runic Window/View classes found.");
        foreach (var group in models.GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeNameCollisionException($"Bridge ViewModel names must be unique within an application: {string.Join(" and ", group.Select(entry => entry.Model.FullName))} both use '{group.Key}'.");
        var presentationKinds = models.SelectMany(entry => viewTypes.TryGetValue(entry.Model, out var variants)
            ? variants.Select(view => PageKind(entry.Name, ContractFor(view)))
            : [PageKind(entry.Name, null)]).ToArray();
        foreach (var group in presentationKinds.GroupBy(kind => kind, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeNameCollisionException($"Bridge presentation kinds must be unique across ViewModels and View contracts: '{group.Key}' is used more than once.");
        if (aot)
        {
            var unsupported = models.FirstOrDefault(entry => ToolkitCommandInspector.HasUnsupportedAotValidation(entry.Model));
            if (unsupported.Model is not null)
                throw new AotValidationException($"{unsupported.Model.FullName} inherits CommunityToolkit.Mvvm.ObservableValidator. Its validation failed in this prototype's Native AOT probe; use a framework-dependent publish until an AOT-safe validator is verified.");
        }

        Directory.CreateDirectory(positional[2]);
        Directory.CreateDirectory(positional[3]);
        // View partials and ViewModel bridges use distinct suffixes and full
        // type names: MainWindow and MainWindowViewModel, or two ItemViews in
        // different namespaces, must not overwrite each other's file.
        var csharpOutputs = models.Select(entry => (Path: BridgeFileName(entry.Model), Owner: entry.Model.FullName!))
            .Concat(viewTypes.Values.SelectMany(variants => variants).Select(view => (Path: ViewFileName(view), Owner: view.FullName!)))
            .ToArray();
        var typescriptOutputs = models.Select(entry => (Path: $"{LowerFirst(entry.Name)}.ts", Owner: entry.Model.FullName!)).ToArray();
        foreach (var group in csharpOutputs.Concat(typescriptOutputs)
            .GroupBy(output => output.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeNameCollisionException($"generated file '{group.Key}' would be written by {string.Join(" and ", group.Select(output => output.Owner))}. Rename one type.");
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
        var compositionPath = Path.Combine(positional[2], "RunicBridgeComposition.g.cs");
        if (compositionType is null)
        {
            if (File.Exists(compositionPath)) File.Delete(compositionPath);
        }
        else GenerateCompositionRegistration(compositionPath, compositionType, models, contentRequired);
        BridgeGenerationCache.Save(positional[1], positional[2], positional[3], cacheArguments);
    }
    else
    {
        if (args.Length is not (4 or 5))
            throw new ArgumentException("Usage: BridgeCodegen <model.dll> <ViewModel type> <output.cs> <output.ts> [public name]");
        var model = Assembly.LoadFrom(Path.GetFullPath(args[0])).GetType(args[1], throwOnError: true)!;
        GenerateOne(model, args[2], args[3], args.Length == 5 ? args[4] : PublicName(model));
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
        throw new InvalidOperationException($"{member.Name}: a View contract must contain only letters and digits and start with a letter.");
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
        throw new InvalidOperationException($"{view.FullName}: a Runic View must be a closed, named class.");
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

static void GenerateCompositionRegistration(string path, string compositionType, (Type Model, string Name)[] models,
    IReadOnlySet<Type> contentRequired)
{
    var parts = CompositionParts(compositionType);

    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection;");
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
        throw new InvalidOperationException($"{model.FullName} must implement INotifyPropertyChanged.");
    if (model.IsNested || !model.IsClass || !model.IsPublic)
        throw new InvalidOperationException("This prototype supports public, top-level ViewModel classes.");

    var declared = model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
        .Where(property => property.GetIndexParameters().Length == 0
            && !property.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Runic.Application.Views.RunicIgnoreAttribute"))
        .OrderBy(property => property.MetadataToken)
        .ToArray();
    var contractFingerprint = BridgeContractShape.Compute(model);
    var interactions = InteractionCodeEmitter.Discover(declared, model.Name, contractFingerprint);
    var commands = declared.Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)
        || ReactiveCommandInspector.InspectContract(property, CodegenOptions.ReactiveUiFlavor) is not null).ToArray();
    var properties = declared.Except(commands).Except(interactions.Select(plan => plan.Property)).ToArray();
    if (properties.Any(property => WireName(property) == "revision"))
        throw new NotSupportedException($"{model.Name}.Revision conflicts with the generated Bridge revision field.");
    var duplicateWireName = properties.GroupBy(WireName, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
    if (duplicateWireName is not null)
        throw new NotSupportedException($"{model.Name}: state properties share the generated wire name '{duplicateWireName.Key}'.");
    var nullability = new NullabilityInfoContext();
    if (properties.Length == 0 && commands.Length == 0 && interactions.Length == 0)
        throw new InvalidOperationException("A ViewModel needs at least one state property, command, or interaction.");
    var contentProperties = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var contentCollections = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var valueProperties = new Dictionary<PropertyInfo, BridgeTypeGraph>();
    foreach (var property in properties)
    {
        if (property.GetMethod is null) throw new NotSupportedException($"{property.Name}: a public getter is required.");
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
                throw new NotSupportedException($"{property.Name}: ViewModel content must be set by .NET, not the web view.");
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
                    throw new NotSupportedException($"{property.Name}: ViewModel collections must be set by .NET, not the web view.");
                contentCollections.Add(property, itemModels);
                continue;
            }
            if (itemType == typeof(object) || typeof(INotifyPropertyChanged).IsAssignableFrom(itemType))
                throw new NotSupportedException($"{model.Name}.{property.Name}: a ViewModel collection needs a specific item interface or base class with a registered View.");
        }
        valueProperties.Add(property, BridgeTypeGraph.Discover(property.PropertyType,
            nullability.Create(property), $"{model.Name}.{property.Name}"));
    }
    var hasContent = contentProperties.Count > 0 || contentCollections.Count > 0;
    var contentBindings = contentProperties.Concat(contentCollections).ToArray();
    if ((hasContent || interactions.Length > 0) && registerGlobally)
        throw new InvalidOperationException($"{model.Name} contains ViewModel content or interactions. Generate it with --no-registry and attach it through a window content session.");
    var fullType = $"global::{model.FullName}";
    var commandPlans = new Dictionary<PropertyInfo, GeneratedCommandPlan>();
    foreach (var command in commands)
    {
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            throw new NotSupportedException($"{command.Name}: Bridge commands must end with Command.");
        var toolkit = ToolkitCommandInspector.InspectContract(command);
        var reactive = ReactiveCommandInspector.InspectContract(command, CodegenOptions.ReactiveUiFlavor);
        var plainInput = command.GetCustomAttribute<RunicCommandInputAttribute>(true);
        var plan = toolkit is { } existing
            ? new GeneratedCommandPlan(false, existing.IsAsync, ToolkitContract: existing,
                InputGraph: existing.Input is { } toolkitInput ? BridgeTypeGraph.Discover(toolkitInput,
                    ContractNullability.Argument(command, nullability, [toolkitInput], 0),
                    $"{model.Name}.{command.Name}.input") : null)
            : reactive is { } contract
                ? GeneratedCommandPlan.Reactive(contract,
                    contract.HasInput ? BridgeTypeGraph.Discover(contract.Input,
                        ContractNullability.Argument(command, nullability, [contract.Input, contract.Result], 0),
                        $"{model.Name}.{command.Name}.input") : null,
                    contract.HasResult ? BridgeTypeGraph.Discover(contract.Result,
                        ContractNullability.Argument(command, nullability, [contract.Input, contract.Result], 1),
                        $"{model.Name}.{command.Name}.result") : null)
                : plainInput is not null
                    ? GeneratedCommandPlan.Plain(BridgeTypeGraph.Discover(plainInput.Input,
                        rootPath: $"{model.Name}.{command.Name}.input"))
                : throw new NotSupportedException($"{command.Name}: unsupported CommunityToolkit or ReactiveUI command shape.");
        if (plan.ReactiveContract is null && command.GetCustomAttribute<RunicCommandResultAttribute>(true) is not null)
            throw new NotSupportedException($"{command.Name}: RunicCommandResult selects a ReactiveUI command's result cardinality; CommunityToolkit and plain commands have no result value.");
        commandPlans.Add(command, plan);
    }
    var operationPlans = commands.Where(command => commandPlans[command].IsAsync).Select(command =>
    {
        var plan = commandPlans[command];
        return new OperationTypeScriptPlan(command.Name[..^"Command".Length],
            plan.InputGraph?.TypeScriptType() ?? "never",
            plan.ResultGraph?.TypeScriptType() ?? "never",
            plan.ResultGraph?.EmitTypeScriptDecoder("value") ?? "undefined as never",
            plan.InputGraph?.EncodeTypeScript("input") ?? "undefined",
            plan.HasArgument,
            plan.ReactiveContract?.Cardinality is BridgeCommandResultCardinality.Stream);
    }).ToArray();

    if (shortName.Length == 0 || !shortName.All(char.IsLetterOrDigit) || !char.IsLetter(shortName[0]))
        throw new ArgumentException("The public Bridge name must be a C#/TypeScript identifier.");
    var prefix = LowerFirst(shortName);
    var hasErrors = typeof(INotifyDataErrorInfo).IsAssignableFrom(model);
    var hasValidation = hasErrors || valueProperties.Values.Any(graph =>
        graph.Nodes.Any(node => typeof(INotifyDataErrorInfo).IsAssignableFrom(node.Type)));
    if (hasValidation && properties.Any(property => WireName(property) == "validation"))
        throw new NotSupportedException($"{model.Name}: validation is reserved for generated validation state. Alias the model property.");
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
    cs.AppendLine("        dataSubscriptions: DataMetadata");
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
                        throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract ?? "default"}'.");
                }
                else if (contract is not null)
                    throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract}'.");
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
                        throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract ?? "default"}'.");
                }
                else if (contract is not null)
                    throw new InvalidOperationException($"{model.Name}.{property.Name}: no {pageModel.Name} View has contract '{contract}'.");
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

    var ts = new StringBuilder();
    ts.AppendLine("// <auto-generated />");
    foreach (var (pageName, contracts) in contentBindings.SelectMany(entry => entry.Value.Select(page => (page.Name, Contract: ContractFor(entry.Key))))
        .GroupBy(entry => entry.Name).Select(group => (group.Key, Contracts: group.Select(entry => entry.Contract).Distinct())))
    {
        var members = string.Join(", ", contracts.SelectMany(contract =>
        {
            var variant = char.ToUpperInvariant(PageKind(pageName, contract)[0]) + PageKind(pageName, contract)[1..];
            return new[] { $"page{variant}", $"type {variant}PageReference" };
        }));
        ts.AppendLine($"import {{ {members} }} from \"./{LowerFirst(pageName)}.js\";");
    }
    if (hasContent) ts.AppendLine();
    BridgeTypeScriptWireEmitter.AppendRuntime(ts);
    if (hasValidation) ValidationTypeScriptEmitter.AppendRuntime(ts);
    ts.AppendLine($"export interface {shortName}State {{");
    if (hasValidation) ts.AppendLine("  readonly validation: BridgeValidationState;");
    ts.AppendLine("  readonly revision: number;");
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
        ts.AppendLine("export type FieldBaseline<T> = { readonly value: T; readonly version: number };");
        ts.AppendLine("export type FieldWriteOptions<T> = { readonly requestId: string; readonly baseline: FieldBaseline<T> };");
        ts.AppendLine("export type FieldWriteReceipt<T> =");
        ts.AppendLine("  | { readonly kind: \"applied\"; readonly snapshot: FieldBaseline<T>; readonly validation?: string }");
        ts.AppendLine("  | { readonly kind: \"committed-with-error\"; readonly snapshot: FieldBaseline<T>; readonly message: string }");
        ts.AppendLine("  | { readonly kind: \"rejected\"; readonly message: string }");
        ts.AppendLine("  | { readonly kind: \"conflict\"; readonly incoming: FieldBaseline<T>; readonly message: string };");
        ts.AppendLine($"export interface {shortName}CheckedFields {{");
        foreach (var property in checkedProperties)
            ts.AppendLine($"  readonly {TsPropertyName(WireName(property))}: {TsPropertyType(property)};");
        ts.AppendLine("}");
        ts.AppendLine();
    }
    ts.AppendLine($"export interface {shortName}View {{");
    ts.AppendLine($"  readonly snapshot: {shortName}State;");
    ts.AppendLine($"  subscribe(listener: (state: {shortName}State) => void): () => void;");
    ts.AppendLine("  dispose(): void;");
    if (interactions.Length > 0) ts.AppendLine($"  readonly interactions: {shortName}Interactions;");
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
        ts.AppendLine($"  set{property.Name}(value: {TsPropertyType(property)}): Promise<{shortName}State>;");
    foreach (var property in checkedProperties)
        ts.AppendLine($"  write{property.Name}(value: {TsPropertyType(property)}, options: FieldWriteOptions<{TsPropertyType(property)}>): Promise<FieldWriteReceipt<{TsPropertyType(property)}>>;");
    if (needsCheckedWriter)
        ts.AppendLine($"  fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K): FieldBaseline<{shortName}CheckedFields[K]>;");
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var input = plan.HasArgument ? $"argument: {CommandInputType(plan)}" : "";
        ts.AppendLine($"  {LowerFirst(command.Name[..^"Command".Length])}({input}): Promise<{shortName}State>;");
        if (plan.HasArgument) ts.AppendLine($"  can{command.Name[..^"Command".Length]}(argument: {CommandInputType(plan)}): Promise<boolean>;");
    }
    foreach (var command in commands.Where(command => commandPlans[command].IsAsync))
    {
        var plan = commandPlans[command];
        var operationName = command.Name[..^"Command".Length];
        var input = plan.HasArgument ? $"argument: {CommandInputType(plan)}" : "";
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
        var mapName = $"page{functionName}References";
        var finalizerName = $"page{functionName}Finalizer";
        ts.AppendLine($"export interface {referenceType} {{");
        ts.AppendLine($"  readonly kind: \"{ownKind}\";");
        ts.AppendLine($"  connect(): Promise<{shortName}View>;");
        ts.AppendLine("}");
        ts.AppendLine($"const {mapName} = new Map<string, WeakRef<{referenceType}>>();");
        ts.AppendLine($"const {finalizerName} = new FinalizationRegistry<{{ id: string; reference: WeakRef<{referenceType}> }}>(entry => {{");
        ts.AppendLine($"  if ({mapName}.get(entry.id) === entry.reference) {mapName}.delete(entry.id);");
        ts.AppendLine("});");
        ts.AppendLine($"export function page{functionName}(id: string): {referenceType} {{");
        ts.AppendLine($"  let reference = {mapName}.get(id)?.deref();");
        ts.AppendLine("  if (reference) return reference;");
        ts.AppendLine($"  reference = {{ kind: \"{ownKind}\", connect: () => connect{shortName}At(`content${{id}}`, true) }};");
        ts.AppendLine("  const weak = new WeakRef(reference);");
        ts.AppendLine($"  {mapName}.set(id, weak);");
        ts.AppendLine($"  {finalizerName}.register(reference, {{ id, reference: weak }});");
        ts.AppendLine("  return reference;");
        ts.AppendLine("}");
        ts.AppendLine();
    }
    ts.AppendLine("export type BridgeErrorKind = \"rejected\" | \"cancelled\" | \"failed\" | \"disconnected\" | \"timeout\";");
    ts.AppendLine("export class BridgeError extends Error {");
    ts.AppendLine("  constructor(readonly kind: BridgeErrorKind, message: string) { super(message); this.name = \"BridgeError\"; }");
    ts.AppendLine("}");
    OperationTypeScriptEmitter.AppendDefinitions(ts, operationPlans, shortName);
    // The public state is the decoded TypeScript contract. The transport is
    // JSON, so every graph-backed field must remain unknown until hydrate
    // validates and converts it (for example Int64 strings to bigint).
    if (hasContent || needsCheckedWriter || valueProperties.Count > 0 || hasValidation)
    {
        var names = contentBindings.Select(entry => $"\"{WireName(entry.Key)}\"")
            .Concat(valueProperties.Keys.Select(property => $"\"{WireName(property)}\""))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hasValidation) names.Add("\"validation\"");
        if (names.Count == 0) names.Add("never");
        ts.AppendLine($"type WireState = Omit<{shortName}State, {string.Join(" | ", names)}> & {{");
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
        if (needsCheckedWriter) ts.AppendLine("  const { __runicFields: _runicFields, ...state } = wire;");
        ts.AppendLine("  return {");
        ts.AppendLine(needsCheckedWriter ? "    ...state," : "    ...wire,");
        if (hasValidation) ts.AppendLine("    validation: decodeBridgeValidation(wire.validation),");
        foreach (var (property, graph) in valueProperties)
        {
            var field = WireName(property);
            ts.AppendLine($"    {TsPropertyName(field)}: {graph.EmitTypeScriptDecoder(TsAccess("wire", field))},");
        }
        foreach (var (property, pages) in contentProperties)
        {
            var field = WireName(property);
            var expression = string.Join(" : ", pages.Select(page =>
                $"{TsAccess("wire", field)}.kind === \"{PageKind(page.Name, ContractFor(property))}\" ? page{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}({TsAccess("wire", field)}.id)"));
            expression += $" : (() => {{ throw new BridgeError(\"failed\", \"Unknown {field} kind.\"); }})()";
            if (nullability.Create(property).ReadState == NullabilityState.Nullable)
                expression = $"{TsAccess("wire", field)} === null ? null : {expression}";
            ts.AppendLine($"    {TsPropertyName(field)}: {expression},");
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
            ts.AppendLine($"    {TsPropertyName(field)}: {hydrated},");
        }
        ts.AppendLine("  };");
        ts.AppendLine("}");
    }
    else
    {
        ts.AppendLine($"type WireState = {shortName}State;");
        ts.AppendLine($"function hydrate(wire: WireState): {shortName}State {{ return wire; }}");
    }
    ts.AppendLine($"interface BridgeReply {{ readonly ok: boolean; readonly state: WireState | null; readonly error: {{ readonly kind: BridgeErrorKind; readonly message: string }} | null; }}");
    if (needsCheckedWriter)
        ts.AppendLine("interface FieldWriteReply extends BridgeReply { readonly receipt: unknown; }");
    ts.AppendLine("interface RunicBridgeClient {");
    ts.AppendLine("  isConnected(): boolean;");
    ts.AppendLine("  call(name: string, ...args: unknown[]): Promise<string>;");
    ts.AppendLine("  onReconnect?(listener: () => void): () => void;");
    ts.AppendLine("}");
    ts.AppendLine("type BridgeWindow = Window & { __runicBridge?: RunicBridgeClient };");
    ts.AppendLine();
    ts.AppendLine("async function waitForBridge(timeoutMilliseconds = 5_000): Promise<RunicBridgeClient> {");
    ts.AppendLine("  const hostWindow = window as BridgeWindow;");
    ts.AppendLine("  const deadline = Date.now() + timeoutMilliseconds;");
    ts.AppendLine("  while (!hostWindow.__runicBridge?.isConnected()) {");
    ts.AppendLine("    if (Date.now() >= deadline) throw new BridgeError(\"timeout\", \"The Bridge did not connect to .NET in time.\");");
    ts.AppendLine("    await new Promise<void>((resolve) => window.setTimeout(resolve, 50));");
    ts.AppendLine("  }");
    ts.AppendLine("  return hostWindow.__runicBridge;");
    ts.AppendLine("}");
    ts.AppendLine();
    ts.AppendLine("interface SharedLease {");
    ts.AppendLine("  disposed: boolean;");
    ts.AppendLine("  current: unknown | undefined;");
    ts.AppendLine("  readonly listeners: Set<(state: unknown) => void>;");
    ts.AppendLine("  mounted: boolean;");
    ts.AppendLine("  readonly mountToken: string | undefined;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedEntry {");
    ts.AppendLine("  readonly contract: string;");
    ts.AppendLine("  readonly route: string;");
    ts.AppendLine("  readonly bridge: RunicBridgeClient;");
    ts.AppendLine("  readonly routeEntry: SharedRoute;");
    ts.AppendLine("  readonly leases: Set<SharedLease>;");
    ts.AppendLine("  readonly hydrate: (wire: unknown) => unknown;");
    ts.AppendLine("  readonly accept: (wire: unknown) => unknown;");
    ts.AppendLine("  current: unknown | undefined;");
    ts.AppendLine("  wire: unknown | undefined;");
    ts.AppendLine("  revision: number | undefined;");
    ts.AppendLine("  initializing: Promise<void> | undefined;");
    ts.AppendLine("  active: boolean;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedRoute {");
    ts.AppendLine("  readonly route: string;");
    ts.AppendLine("  readonly callbackName: string;");
    ts.AppendLine("  readonly bridge: RunicBridgeClient;");
    ts.AppendLine("  readonly generation: symbol;");
    ts.AppendLine("  readonly entries: Map<string, SharedEntry>;");
    ts.AppendLine("  readonly callback: (state: unknown) => unknown;");
    ts.AppendLine("  readonly previousCallback: unknown;");
    ts.AppendLine("  active: boolean;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedRuntime {");
    ts.AppendLine("  bridge: RunicBridgeClient | undefined;");
    ts.AppendLine("  generation: symbol;");
    ts.AppendLine("  mountSession: string;");
    ts.AppendLine("  readonly routes: Map<string, SharedRoute>;");
    ts.AppendLine("  readonly operations: Map<string, SharedOperation>;");
    ts.AppendLine("  reconnect?: (() => void) | undefined;");
    ts.AppendLine("}");
    ts.AppendLine("interface SharedOperation {");
    ts.AppendLine("  readonly contract: string;");
    ts.AppendLine("  readonly requestId: string;");
    ts.AppendLine("  state: \"pending\" | \"accepted\" | \"uncertain\";");
    ts.AppendLine("  admission: Promise<unknown> | undefined;");
    ts.AppendLine("  handle: unknown | undefined;");
    ts.AppendLine("}");
    ts.AppendLine("const sharedRuntimeKey = Symbol.for(\"runic.views.generated-client-runtime\");");
    ts.AppendLine("function sharedRuntimeFor(bridge: RunicBridgeClient): SharedRuntime {");
    ts.AppendLine("  const hostWindow = window as unknown as Record<symbol, unknown>;");
    ts.AppendLine("  let runtime = hostWindow[sharedRuntimeKey] as SharedRuntime | undefined;");
    ts.AppendLine("  if (!runtime) {");
    ts.AppendLine("    runtime = { bridge, generation: Symbol(), mountSession: globalThis.crypto.randomUUID(), routes: new Map(), operations: new Map() };");
    ts.AppendLine("    hostWindow[sharedRuntimeKey] = runtime;");
    ts.AppendLine("    watchReconnect(runtime, bridge);");
    ts.AppendLine("    return runtime;");
    ts.AppendLine("  }");
    ts.AppendLine("  if (runtime.bridge === bridge) {");
    ts.AppendLine("    // HMR can retain a runtime created by an earlier generated client.");
    ts.AppendLine("    const legacy = runtime as SharedRuntime & { operations?: Map<string, SharedOperation> };");
    ts.AppendLine("    legacy.operations ??= new Map();");
    ts.AppendLine("    watchReconnect(runtime, bridge);");
    ts.AppendLine("    return runtime;");
    ts.AppendLine("  }");
    ts.AppendLine("  runtime.reconnect?.();");
    ts.AppendLine("  runtime.reconnect = undefined;");
    ts.AppendLine("  const callbacks = window as unknown as Record<string, unknown>;");
    ts.AppendLine("  for (const route of runtime.routes.values()) {");
    ts.AppendLine("    route.active = false;");
    ts.AppendLine("    if (callbacks[route.callbackName] === route.callback) callbacks[route.callbackName] = route.previousCallback;");
    ts.AppendLine("  }");
    ts.AppendLine("  runtime.routes.clear();");
    ts.AppendLine("  runtime.operations.clear();");
    ts.AppendLine("  runtime.bridge = bridge;");
    ts.AppendLine("  runtime.generation = Symbol();");
    ts.AppendLine("  runtime.mountSession = globalThis.crypto.randomUUID();");
    ts.AppendLine("  watchReconnect(runtime, bridge);");
    ts.AppendLine("  return runtime;");
    ts.AppendLine("}");
    ts.AppendLine("// A transport reconnect keeps this page, while .NET released the former");
    ts.AppendLine("// connection's View mounts and its publications were lost. Re-read each");
    ts.AppendLine("// live route and re-acknowledge each mounted presentation.");
    ts.AppendLine("function watchReconnect(runtime: SharedRuntime, bridge: RunicBridgeClient): void {");
    ts.AppendLine("  runtime.reconnect ??= bridge.onReconnect?.(() => resumeAfterReconnect(runtime, bridge));");
    ts.AppendLine("}");
    ts.AppendLine("function resumeAfterReconnect(runtime: SharedRuntime, bridge: RunicBridgeClient): void {");
    ts.AppendLine("  if (runtime.bridge !== bridge) return;");
    ts.AppendLine("  for (const route of runtime.routes.values()) {");
    ts.AppendLine("    if (!route.active || route.bridge !== bridge) continue;");
    ts.AppendLine("    for (const entry of route.entries.values()) {");
    ts.AppendLine("      if (!entry.active) continue;");
    ts.AppendLine("      void bridge.call(`${route.route}Snapshot`).then(json => {");
    ts.AppendLine("        const reply = JSON.parse(json) as { readonly state?: unknown };");
    ts.AppendLine("        if (entry.active && reply.state !== null && reply.state !== undefined) entry.accept(reply.state);");
    ts.AppendLine("      }).catch(() => {});");
    ts.AppendLine("      for (const lease of entry.leases)");
    ts.AppendLine("        if (lease.mounted && lease.mountToken && !lease.disposed) void remountLease(bridge, route.route, lease, lease.mountToken);");
    ts.AppendLine("    }");
    ts.AppendLine("  }");
    ts.AppendLine("}");
    ts.AppendLine("async function remountLease(bridge: RunicBridgeClient, route: string, lease: SharedLease, token: string): Promise<void> {");
    ts.AppendLine("  // .NET answers \"ignored\" while the former connection still owns the token.");
    ts.AppendLine("  for (let attempt = 0; attempt < 20 && !lease.disposed; attempt++) {");
    ts.AppendLine("    let reply: string;");
    ts.AppendLine("    try { reply = await bridge.call(`${route}Mount`, token); } catch { return; }");
    ts.AppendLine("    if (reply !== \"ignored\") return;");
    ts.AppendLine("    await new Promise<void>((resolve) => window.setTimeout(resolve, 250));");
    ts.AppendLine("  }");
    ts.AppendLine("}");
    // A throwing subscriber or an undecodable push must not stop delivery to
    // other subscribers or unwind into the .NET callback that pushed state.
    ts.AppendLine("function reportBridgeError(error: unknown): void {");
    ts.AppendLine("  const report = (globalThis as { reportError?: (error: unknown) => void }).reportError;");
    ts.AppendLine("  if (typeof report === \"function\") report(error); else console.error(error);");
    ts.AppendLine("}");
    ts.AppendLine("function sharedRouteFor(runtime: SharedRuntime, bridge: RunicBridgeClient, route: string): SharedRoute {");
    ts.AppendLine("  const callbackName = `__${route}Changed`;");
    ts.AppendLine("  const callbacks = window as unknown as Record<string, unknown>;");
    ts.AppendLine("  const existing = runtime.routes.get(route);");
    ts.AppendLine("  if (existing?.active && existing.bridge === bridge && existing.generation === runtime.generation) {");
    ts.AppendLine("    callbacks[callbackName] = existing.callback;");
    ts.AppendLine("    return existing;");
    ts.AppendLine("  }");
    ts.AppendLine("  let sharedRoute: SharedRoute;");
    ts.AppendLine("  sharedRoute = {");
    ts.AppendLine("    route, callbackName, bridge, generation: runtime.generation, entries: new Map(), previousCallback: callbacks[callbackName], active: true,");
    ts.AppendLine("    callback(state) {");
    ts.AppendLine("      let accepted: unknown;");
    ts.AppendLine("      for (const entry of sharedRoute.entries.values()) {");
    ts.AppendLine("        try { accepted = entry.accept(state); }");
    ts.AppendLine("        catch (error) { reportBridgeError(error); }");
    ts.AppendLine("      }");
    ts.AppendLine("      return accepted;");
    ts.AppendLine("    },");
    ts.AppendLine("  };");
    ts.AppendLine("  runtime.routes.set(route, sharedRoute);");
    ts.AppendLine("  callbacks[callbackName] = sharedRoute.callback;");
    ts.AppendLine("  return sharedRoute;");
    ts.AppendLine("}");
    ts.AppendLine($"const bridgeContract = \"{model.FullName}:{contractFingerprint}\";");
    ts.AppendLine();
    ts.AppendLine($"export function connect{shortName}(): Promise<{shortName}View> {{ return connect{shortName}At(\"{prefix}\", {(interactions.Length > 0 ? "true" : "false")}); }}");
    ts.AppendLine($"async function connect{shortName}At(route: string, needsMount = false): Promise<{shortName}View> {{");
    ts.AppendLine("  const bridge = await waitForBridge();");
    ts.AppendLine("  const runtime = sharedRuntimeFor(bridge);");
    ts.AppendLine("  const contractId = `${bridgeContract}:${route}`;");
    ts.AppendLine("  const routeEntry = sharedRouteFor(runtime, bridge, route);");
    ts.AppendLine("  if (routeEntry.entries.size !== 0 && !routeEntry.entries.has(contractId))");
    ts.AppendLine("    throw new BridgeError(\"failed\", \"This route already has an incompatible Bridge contract.\");");
    ts.AppendLine("  let entry = routeEntry.entries.get(contractId);");
    ts.AppendLine("  if (!entry) {");
    ts.AppendLine("    let created: SharedEntry;");
    ts.AppendLine("    created = {");
    ts.AppendLine("      contract: contractId, route, bridge, routeEntry, leases: new Set(), hydrate: wire => hydrate(wire as WireState), current: undefined, wire: undefined, revision: undefined, initializing: undefined, active: true,");
    ts.AppendLine("      accept(wire) {");
    ts.AppendLine("        const next = wire as WireState;");
    ts.AppendLine("        if (!created.active || !routeEntry.active) return created.current ?? created.hydrate(next);");
    ts.AppendLine("        if (created.current === undefined || created.revision === undefined || next.revision >= created.revision) {");
    ts.AppendLine("          // Decode first: a state that fails validation must not advance the revision.");
    ts.AppendLine("          const current = created.hydrate(next);");
    ts.AppendLine("          created.revision = next.revision;");
    ts.AppendLine("          created.wire = next;");
    ts.AppendLine("          created.current = current;");
    ts.AppendLine("          for (const lease of created.leases) if (!lease.disposed) {");
    ts.AppendLine("            lease.current = current;");
    ts.AppendLine("            for (const listener of lease.listeners) {");
    ts.AppendLine("              try { listener(current); }");
    ts.AppendLine("              catch (error) { reportBridgeError(error); }");
    ts.AppendLine("            }");
    ts.AppendLine("          }");
    ts.AppendLine("        }");
    ts.AppendLine("        return created.current;");
    ts.AppendLine("      },");
    ts.AppendLine("    };");
    ts.AppendLine("    routeEntry.entries.set(contractId, created);");
    ts.AppendLine("    const initialization = (async () => {");
    ts.AppendLine("      let reply: string;");
    ts.AppendLine("      try { reply = await bridge.call(`${route}Snapshot`); }");
    ts.AppendLine("      catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The Bridge call could not complete.\"); }");
    ts.AppendLine("      unpack(reply, created);");
    ts.AppendLine("    })();");
    ts.AppendLine("    created.initializing = initialization;");
    ts.AppendLine("    initialization.then(");
    ts.AppendLine("      () => { if (created.initializing === initialization) created.initializing = undefined; },");
    ts.AppendLine("      () => { if (created.initializing === initialization) created.initializing = undefined; },");
    ts.AppendLine("    );");
    ts.AppendLine("    entry = created;");
    ts.AppendLine("  }");
    ts.AppendLine("  const shared = entry;");
    ts.AppendLine("  const mountToken = needsMount ? `${runtime.mountSession}:${globalThis.crypto.randomUUID()}` : undefined;");
    ts.AppendLine("  const lease: SharedLease = { disposed: false, current: undefined, listeners: new Set(), mounted: false, mountToken };");
    ts.AppendLine("  shared.leases.add(lease);");
    ts.AppendLine("  function isLive(): boolean {");
    ts.AppendLine("    return shared.active && routeEntry.active && runtime.bridge === bridge && routeEntry.generation === runtime.generation;");
    ts.AppendLine("  }");
    ts.AppendLine($"  function unpack(json: string, target: SharedEntry = shared): {shortName}State {{");
    ts.AppendLine("    let reply: BridgeReply;");
    ts.AppendLine("    try { reply = JSON.parse(json) as BridgeReply; }");
    ts.AppendLine("    catch { throw new BridgeError(\"failed\", \"The Bridge returned an invalid response.\"); }");
    ts.AppendLine("    if (reply === null || typeof reply !== \"object\") throw new BridgeError(\"failed\", \"The Bridge returned an invalid response.\");");
    ts.AppendLine("    let state: unknown;");
    ts.AppendLine("    try { state = reply.state === null ? undefined : target.accept(reply.state); }");
    ts.AppendLine("    catch { throw new BridgeError(\"failed\", \"The Bridge returned an invalid state.\"); }");
    ts.AppendLine("    if (!reply.ok) throw new BridgeError(reply.error?.kind ?? \"failed\", reply.error?.message ?? \"The call failed.\");");
    ts.AppendLine("    if (state === undefined) throw new BridgeError(\"failed\", \"The Bridge returned no state.\");");
    ts.AppendLine($"    return state as {shortName}State;");
    ts.AppendLine("  }");
    ts.AppendLine($"  async function invoke(name: string, ...args: unknown[]): Promise<{shortName}State> {{");
    ts.AppendLine("    if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
    ts.AppendLine("    let reply: string;");
    ts.AppendLine("    try { reply = await bridge.call(name, ...args); }");
    ts.AppendLine("    catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The Bridge call could not complete.\"); }");
    ts.AppendLine("    if (lease.disposed || !isLive()) throw new BridgeError(\"disconnected\", \"This view was disposed. Reconnect for the current state.\");");
    ts.AppendLine("    return unpack(reply);");
    ts.AppendLine("  }");
    if (needsCheckedWriter) CheckedWriteTypeScriptEmitter.AppendRuntime(ts);
    OperationTypeScriptEmitter.AppendRuntime(ts, operationPlans, shortName);
    var interactionRuntime = InteractionCodeEmitter.TypeScriptRuntime(interactions, shortName);
    if (interactions.Length > 0)
        ts.AppendLine("  let disposeInteractions: (() => void) | undefined;");
    ts.AppendLine("  function dispose(): void {");
    ts.AppendLine("    if (lease.disposed) return;");
    ts.AppendLine("    lease.disposed = true;");
    if (interactions.Length > 0)
        ts.AppendLine("    disposeInteractions?.();");
    ts.AppendLine("    if (lease.mounted && lease.mountToken) void bridge.call(`${route}Unmount`, lease.mountToken).catch(() => {});");
    ts.AppendLine("    lease.listeners.clear();");
    ts.AppendLine("    shared.leases.delete(lease);");
    ts.AppendLine("    if (shared.leases.size !== 0 || routeEntry.entries.get(contractId) !== shared) return;");
    ts.AppendLine("    shared.active = false;");
    ts.AppendLine("    routeEntry.entries.delete(contractId);");
    ts.AppendLine("    if (routeEntry.entries.size !== 0) return;");
    ts.AppendLine("    routeEntry.active = false;");
    ts.AppendLine("    if (runtime.routes.get(route) === routeEntry) runtime.routes.delete(route);");
    ts.AppendLine("    const callbacks = window as unknown as Record<string, unknown>;");
    ts.AppendLine("    if (callbacks[routeEntry.callbackName] === routeEntry.callback) callbacks[routeEntry.callbackName] = routeEntry.previousCallback;");
    ts.AppendLine("  }");
    ts.AppendLine("  try { await shared.initializing; }");
    ts.AppendLine("  catch (error) { dispose(); throw error; }");
    ts.AppendLine("  if (!isLive()) { dispose(); throw new BridgeError(\"disconnected\", \"The Bridge session changed during connection.\"); }");
    ts.AppendLine("  lease.current = shared.current;");
    ts.AppendLine("  if (mountToken) {");
    ts.AppendLine("    try {");
    ts.AppendLine("      lease.mounted = true;");
    ts.AppendLine("      const acknowledged = await bridge.call(`${route}Mount`, mountToken);");
    ts.AppendLine("      if (acknowledged !== \"ok\") throw new BridgeError(\"disconnected\", \"The View mount was not accepted.\");");
    ts.AppendLine("    } catch (cause) {");
    ts.AppendLine("      dispose();");
    ts.AppendLine("      throw cause instanceof BridgeError ? cause : new BridgeError(\"disconnected\", \"The View mount could not complete.\");");
    ts.AppendLine("    }");
    ts.AppendLine("  }");
    ts.AppendLine("  if (!isLive()) { dispose(); throw new BridgeError(\"disconnected\", \"The Bridge session changed during connection.\"); }");
    if (interactions.Length > 0)
    {
        ts.Append(interactionRuntime.Setup);
        ts.AppendLine("  async function awaitInteractionCapabilities(): Promise<void> {");
        ts.AppendLine("    if (interactionHandlers.size === 0) return;");
        ts.AppendLine("    try { await interactionCapabilitySync; }");
        ts.AppendLine("    catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The interaction handler could not be registered.\"); }");
        ts.AppendLine("    if (interactionUnavailable) throw new BridgeError(\"disconnected\", \"The interaction presentation is no longer available.\");");
        ts.AppendLine("  }");
        ts.AppendLine("  disposeInteractions = () => {");
        ts.AppendLine(interactionRuntime.Dispose);
        ts.AppendLine("  };");
    }
    else
        ts.AppendLine("  async function awaitInteractionCapabilities(): Promise<void> { }");
    if (needsCheckedWriter)
    {
        ts.AppendLine($"  function fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K): FieldBaseline<{shortName}CheckedFields[K]> {{");
        ts.AppendLine("    if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError(\"disconnected\", \"ViewModel is not connected.\");");
        ts.AppendLine("    switch (field) {");
        foreach (var property in checkedProperties)
        {
            var wireName = WireName(property);
            ts.AppendLine($"      case \"{wireName}\": {{");
            ts.AppendLine($"        const version = {TsAccess("(shared.wire as WireState | undefined)?.__runicFields", wireName)}?.version;");
            ts.AppendLine("        if (typeof version !== \"number\" || !Number.isSafeInteger(version) || version < 0) throw new BridgeError(\"failed\", \"The checked field baseline is unavailable for this connection.\");");
            ts.AppendLine($"        return {{ value: {TsAccess($"(lease.current as {shortName}State)", wireName)}, version }} as FieldBaseline<{shortName}CheckedFields[K]>;");
            ts.AppendLine("      }");
        }
        ts.AppendLine("    }");
        ts.AppendLine("    throw new BridgeError(\"failed\", \"The requested checked field is unavailable.\");");
        ts.AppendLine("  }");
    }
    ts.AppendLine("  return {");
    ts.AppendLine("    get snapshot() {");
    ts.AppendLine("      if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError(\"disconnected\", \"ViewModel is not connected.\");");
    ts.AppendLine($"      return lease.current as {shortName}State;");
    ts.AppendLine("    },");
    ts.AppendLine("    subscribe(listener) {");
    ts.AppendLine("      if (lease.disposed || !isLive() || lease.current === undefined) throw new BridgeError(\"disconnected\", \"ViewModel is not connected.\");");
    ts.AppendLine($"      const typed = listener as (state: unknown) => void;");
    ts.AppendLine("      lease.listeners.add(typed);");
    ts.AppendLine("      // The caller sees a failing initial delivery and gets no unsubscribe, so do not retain it.");
    ts.AppendLine("      try { typed(lease.current); }");
    ts.AppendLine("      catch (error) { lease.listeners.delete(typed); throw error; }");
    ts.AppendLine("      return () => lease.listeners.delete(typed);");
    ts.AppendLine("    },");
    ts.AppendLine("    dispose,");
    if (interactions.Length > 0)
        ts.AppendLine(interactionRuntime.ViewMember);
    if (needsCheckedWriter)
    {
        ts.AppendLine("    fieldBaseline,");
    }
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
        ts.AppendLine($"      return invoke(`${{route}}Set{property.Name}`, {argument});");
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
        ts.AppendLine($"      return invokeFieldWrite<{TsPropertyType(property)}>(`${{route}}Write{property.Name}`, JSON.stringify({{ requestId: options.requestId, expectedVersion: baseline.version, expectedValue: {encodedBaseline}, value: {encodedValue} }}), value => {valueGraph.EmitTypeScriptDecoder("value")});");
        ts.AppendLine("    },");
    }
    foreach (var command in commands)
    {
        var name = command.Name[..^"Command".Length];
        var plan = commandPlans[command];
        var argument = plan.InputGraph is { } inputGraph
            ? $", JSON.stringify({inputGraph.EncodeTypeScript("argument")})"
            : plan.HasStringArgument ? ", JSON.stringify(argument)" : "";
        ts.AppendLine($"    async {LowerFirst(name)}({(plan.HasArgument ? "argument" : "")}) {{");
        ts.AppendLine("      await awaitInteractionCapabilities();");
        ts.AppendLine($"      return invoke(`${{route}}{name}`{argument});");
        ts.AppendLine("    },");
        if (plan.HasArgument)
        {
            ts.AppendLine($"    async can{name}(argument) {{");
            ts.AppendLine("      if (lease.disposed || !isLive() || !bridge.isConnected()) throw new BridgeError(\"disconnected\", \"The Bridge is disconnected.\");");
            ts.AppendLine("      let reply: string;");
            ts.AppendLine($"      try {{ reply = await bridge.call(`${{route}}Can{name}`{argument}); }}");
            ts.AppendLine("      catch { throw new BridgeError(bridge.isConnected() ? \"failed\" : \"disconnected\", \"The command availability query could not complete.\"); }");
            ts.AppendLine("      if (reply === \"true\") return true;");
            ts.AppendLine("      if (reply === \"false\") return false;");
            ts.AppendLine("      throw new BridgeError(\"failed\", \"The command availability query returned an invalid response.\");");
            ts.AppendLine("    },");
        }
    }
    OperationTypeScriptEmitter.AppendClientMethods(ts, operationPlans);
    ts.AppendLine("  };");
    ts.AppendLine("}");

    WriteIfChanged(csharpPath, cs.ToString());
    WriteIfChanged(typescriptPath, ts.ToString());
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

}

static string LowerFirst(string text) => char.ToLowerInvariant(text[0]) + text[1..];

static string WireName(PropertyInfo property)
{
    var name = property.GetCustomAttribute<RunicAliasAttribute>(true)?.Name
        ?? property.GetCustomAttribute<JsonPropertyNameAttribute>(true)?.Name
        ?? LowerFirst(property.Name);
    if (string.IsNullOrWhiteSpace(name))
        throw new NotSupportedException($"{property.DeclaringType?.Name}.{property.Name}: a bridge wire name is required.");
    return name;
}

static bool IsTypeScriptIdentifier(string name) => name.Length > 0
    && (char.IsLetter(name[0]) || name[0] is '_' or '$')
    && name.Skip(1).All(character => char.IsLetterOrDigit(character) || character is '_' or '$');

static string TsPropertyName(string name) => IsTypeScriptIdentifier(name)
    ? name
    : JsonSerializer.Serialize(name);

static string TsAccess(string target, string name) => IsTypeScriptIdentifier(name)
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

sealed class AotValidationException(string message) : Exception(message);

sealed class BridgeNameCollisionException(string message) : NotSupportedException(message);

/// <summary>
/// Writes MSBuild-recognised errors. Codes: RUNICBRIDGE001 unsupported model
/// or option, 002 Native AOT validation, 003 unsupported bridge value type,
/// 004 generated name collision, 005 the model assembly could not be loaded.
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
            AotValidationException => "RUNICBRIDGE002",
            BridgeTypeGraphException => "RUNICBRIDGE003",
            BridgeNameCollisionException => "RUNICBRIDGE004",
            ReflectionTypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException
                or TypeLoadException => "RUNICBRIDGE005",
            _ => "RUNICBRIDGE001",
        };
        var message = error is ReflectionTypeLoadException load
            ? $"{error.Message} {string.Join(" ", load.LoaderExceptions.OfType<Exception>().Select(inner => inner.Message).Distinct())}"
            : error.Message;
        // Reflection has no source location; name the ViewModel instead.
        if (model is not null && !message.StartsWith(model.Name, StringComparison.Ordinal)
            && !message.StartsWith(model.FullName ?? model.Name, StringComparison.Ordinal))
            message = $"{model.FullName}: {message}";
        Console.Error.WriteLine($"error {code}: {message}");
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
