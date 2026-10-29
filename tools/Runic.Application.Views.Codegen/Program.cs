using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Runic.Application.Views;
using static TypeScriptModuleBindings;

try
{
    if (args.Length is 3 or 5 && args[0] == "--composition-stub" && (args.Length == 3 || args[3] == "--references"))
    {
        GenerateCompositionStub(args[2], args[1], args.Length == 5 ? args[4] : null);
    }
    // The machine-readable RUNICBRIDGE list (docs/runicbridge-diagnostics.json), to standard output or a file.
    else if (args.Length is 1 or 2 && args[0] == "--list-diagnostics")
    {
        var list = BridgeDiagnosticCodes.WriteList();
        if (args.Length == 2) File.WriteAllText(args[1], list);
        else Console.Out.Write(list);
    }
    else if (args.Length >= 1 && args[0] == "--generate")
    {
        var aot = false;
        var optional = false;
        string? compositionType = null;
        var pathMap = "";
        var frameworks = new List<string>();
        var values = new List<string> { args[0] };
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--aot": aot = true; break;
                case "--optional": optional = true; break;
                case "--di-composition" when i + 1 < args.Length:
                    compositionType = args[++i];
                    break;
                // The compiler path map, so PDB documents of a deterministic
                // build resolve to local source files.
                case "--path-map" when i + 1 < args.Length:
                    pathMap = args[++i];
                    SourceLocator.SetPathMap(pathMap);
                    break;
                // A shared framework the application's build references, such as
                // Microsoft.AspNetCore.App (see BridgeLoading.UseSharedFrameworks).
                case "--framework" when i + 1 < args.Length:
                    frameworks.Add(args[++i]);
                    break;
                // MVVM library options, such as --reactiveui-flavor.
                case var option when i + 1 < args.Length && MvvmCodegenConventions.ForOption(option) is { } convention:
                    convention.ApplyOption(option, args[++i]);
                    break;
                default: values.Add(args[i]); break;
            }
        }
        var positional = values.ToArray();
        if (positional.Length != 4)
            throw new ArgumentException("Usage: BridgeCodegen --generate <model.dll> <C# output dir> <TypeScript output dir> [--aot] [--optional] [--di-composition <namespace.type>] [--path-map <map>] [--framework <shared framework>]...");

        string[] cacheArguments = [$"aot={aot}", $"composition={compositionType}", $"path-map={pathMap}",
            $"frameworks={string.Join(";", frameworks.Order(StringComparer.OrdinalIgnoreCase))}",
            .. MvvmCodegenConventions.All.SelectMany(convention => convention.CacheArguments)];
        if (BridgeGenerationCache.TryHit(positional[1], positional[2], positional[3], cacheArguments, out var cachedWarnings))
        {
            foreach (var warning in cachedWarnings) Console.Error.WriteLine(warning);
            Console.WriteLine("Bridge generation is up to date.");
            return;
        }
        BridgeLoading.UseSharedFrameworks(frameworks, positional[1]);
        var assembly = Assembly.LoadFrom(Path.GetFullPath(positional[1]));
        var viewTypes = new Dictionary<Type, List<Type>>();
        foreach (var type in BridgeLoading.InspectableTypes(assembly))
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
        // Without Views, optional generation removes the generated output. Not
        // when types were skipped for a missing assembly: they may be the Views.
        if (models.Length == 0 && optional && BridgeLoading.SkippedFor.Count > 0)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.AssemblyLoad,
                $"{assembly.GetName().Name}: no Runic Window/View classes were found, but types were skipped because {string.Join(", ", BridgeLoading.SkippedFor)} could not be loaded, so the generated output is kept. Make the assembly available in the build output.");
        if (models.Length == 0 && optional)
        {
            // Bridge generation was on by default, not requested: an assembly
            // without Windows or Views (for example one that only uses the
            // navigator) builds without bridge output. Remove stale generated C# and
            // TypeScript (never creating the TypeScript directory) and leave a marker
            // so the build skips the frontend steps.
            Directory.CreateDirectory(positional[2]);
            foreach (var path in Directory.GetFiles(positional[2], "*.g.cs"))
                if (GeneratedOutput.IsGenerated(path)) File.Delete(path);
            if (Directory.Exists(positional[3]))
                foreach (var path in Directory.GetFiles(positional[3], "*.ts"))
                    if (GeneratedOutput.IsGenerated(path)) File.Delete(path);
            File.WriteAllText(Path.Combine(positional[2], "RunicBridge.NoViews.marker"), "");
            Console.WriteLine($"{assembly.GetName().Name}: no Runic Window/View classes found; skipping Bridge generation.");
            return;
        }
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
        // A ViewModel member whose type needs an assembly the generator cannot
        // load (a WPF type) is reported at that member. The contract of each
        // ViewModel covers the application's other ViewModels too, so stop
        // before it would fail for all of them.
        foreach (var entry in models)
        {
            try
            {
                BridgeLoading.CheckSurface(entry.Model);
            }
            catch (Exception error) when (Diagnostics.IsReportable(error))
            {
                Diagnostics.Report(error, entry.Model);
            }
        }
        if (Diagnostics.HasErrors) return;
        if (aot)
        {
            var unsupported = models.Select(entry => (entry.Model, Reason: MvvmCodegenConventions.UnsupportedAotReason(entry.Model)))
                .FirstOrDefault(entry => entry.Reason is not null);
            if (unsupported.Reason is not null)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Aot, unsupported.Reason, unsupported.Model);
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
                $"{Path.GetFullPath(namedTypesPath)} is not generated, but the generator writes its named TypeScript types there. Move the file or set RunicApplicationFrontendGeneratedDirectory to a dedicated directory.");
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
                Diagnostics.InjectInternalErrorForTests(entry.Model);
                if (GenerateOne(entry.Model, Path.Combine(positional[2], BridgeFileName(entry.Model)),
                    Path.Combine(positional[3], $"{LowerFirst(entry.Name)}.ts"), entry.Name, models, viewTypes))
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
        BridgeGenerationCache.Save(positional[1], positional[2], positional[3], cacheArguments, Diagnostics.Warnings);
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

// A set… member exists because its C# property has a public setter, which is
// what any MVVM View may write; name the setter the call runs.
static string SetterDocumentation(PropertyInfo property)
{
    var setter = $"Calls the public C# setter of `{property.DeclaringType!.Name.Split('`')[0]}.{property.Name}`.";
    return XmlDocumentation.Summary(property) is { } summary ? summary + "\n\n" + setter : setter;
}

static string PublicName(Type model) => model.Name.EndsWith("ViewModel", StringComparison.Ordinal)
    ? model.Name[..^"ViewModel".Length] : model.Name;

static string BridgeFileName(Type model) => $"{model.FullName}.Bridge.g.cs";

static string ViewFileName(Type view) => $"{view.FullName}.View.g.cs";

// Generated bridges live in a namespace of their own, not beside their
// ViewModels, so no application type can collide with one. ViewModel public
// names are unique within an application, and so are the bridge names.
const string BridgeNamespace = "Runic.Application.Views.Generated.Bridges";

static string BridgeTypeName(Type model, string shortName) => $"global::{BridgeNamespace}.{shortName}Bridge";

static void AppendNamespace(StringBuilder source, string? ns)
{
    if (ns is not null) source.AppendLine($"namespace {ns};");
}

// The ViewModel's bridged properties: public, declared, not [RunicIgnore] and
// not indexers. [RunicIgnore] is checked first, by name: the signature of an
// ignored property, which GetIndexParameters resolves, may need an assembly
// the generator cannot load, such as a WPF ImageSource.
static PropertyInfo[] BridgedProperties(Type model) =>
    model.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public)
        .Where(property => !BridgeDtoContract.IsRunicIgnored(property) && property.GetIndexParameters().Length == 0)
        .OrderBy(property => property.MetadataToken)
        .ToArray();

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

// The generated runtime failure for content of a type without a generated
// View, as a C# interpolated string over the switch's `unregistered` local.
// It names the slot and the presented type and says how to add the View.
static string UnregisteredContentMessage(Type model, PropertyInfo property, bool region)
{
    var contract = ContractFor(property);
    var slot = region ? $"navigation region {model.Name}.{property.Name}" : $"{model.Name}.{property.Name}";
    return $"$\"The {slot} cannot present {{unregistered.GetType().FullName}}: the Bridge generator found no Runic View"
        + (contract is null ? "" : $" with contract '{contract}'")
        + " for it. Declare one in the project that generates this Bridge, for example"
        + " 'public sealed partial class {(unregistered.GetType().Name.EndsWith(\"ViewModel\", global::System.StringComparison.Ordinal) ? unregistered.GetType().Name[..^9] : unregistered.GetType().Name)}View : RunicView<{unregistered.GetType().Name}>;'"
        + (contract is null ? "" : $" with [RunicViewContract(\\\"{contract}\\\")]")
        + ", and rebuild.\"";
}

// A slot only presents ViewModels with a View, so a pushed ViewModel without
// one compiles and then fails when presented. Warn about the public, top-level
// concrete ViewModels in the application assembly that a slot admits but no
// View presents: none of the slot's switch cases, which include a ViewModel's
// base classes and interfaces with a View, matches them. Types in other assemblies are not checked: their Views, if
// any, are not visible here either.
//
// A warning must name a ViewModel that is meant for the slot, so a broad slot
// type is not checked: a framework type (System, ReactiveUI, CommunityToolkit),
// a type that the slot's owner or a Window's ViewModel also derives from or
// implements (an application-wide ViewModel base), or one that admits more than
// MissingViewCandidateLimit ViewModels without a View. Window ViewModels are
// never candidates: every Window is a RunicView, so they are in viewTypes.
// RunicOperationRetirement (#284): the declaration on a command property, else on the ViewModel class or a base class,
// read by name like RunicFailure. Only an operation (an asynchronous command) carries it; RUNICBRIDGE028 warns about a
// declaration that reaches none. Keep, the default, emits nothing, so a Bridge without one is generated as before.
static void ApplyOperationRetirement(Type model, IReadOnlyList<PropertyInfo> commands, Dictionary<PropertyInfo, GeneratedCommandPlan> commandPlans)
{
    const string Attribute = "Runic.Application.Views.RunicOperationRetirementAttribute";
    const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic;
    RunicOperationRetirement? Declared(MemberInfo member)
    {
        if (BridgeAttributeNames.Find(member, Attribute)?.FixedArguments?.Invoke() is not [var argument]) return null;
        var value = Convert.ToInt32(argument, System.Globalization.CultureInfo.InvariantCulture);
        if (Enum.IsDefined(typeof(RunicOperationRetirement), value)) return (RunicOperationRetirement)value;
        Diagnostics.Warn(BridgeDiagnosticCodes.OperationRetirement,
            $"{model.Name}.{member.Name}: {value} is not a RunicOperationRetirement value; the declaration is ignored.", member);
        return null;
    }
    // A property declaration, also one on the base property that a command overrides.
    RunicOperationRetirement? OnProperty(PropertyInfo property)
    {
        for (var owner = property.DeclaringType; owner is not null && owner != typeof(object); owner = owner.BaseType)
            if (owner.GetProperty(property.Name, AllDeclared) is { } declared && Declared(declared) is { } value) return value;
        return null;
    }
    RunicOperationRetirement? onClass = null;
    for (var owner = model; owner is not null && owner != typeof(object) && onClass is null; owner = owner.BaseType)
        onClass = Declared(owner);
    var operations = commands.Where(command => commandPlans[command].IsAsync).ToHashSet();
    foreach (var command in operations)
        if ((OnProperty(command) ?? onClass) is { } retirement and not RunicOperationRetirement.Keep)
            commandPlans[command] = commandPlans[command] with { Retirement = retirement };
    // A declaration that reaches no operation: on the ViewModel's own members, so a base class shared by several
    // ViewModels is reported once per ViewModel that declares it.
    foreach (var property in model.GetProperties(AllDeclared).OrderBy(property => property.MetadataToken))
    {
        if (!BridgeAttributeNames.Has(property, Attribute) || operations.Any(command => command.Name == property.Name)) continue;
        Diagnostics.Warn(BridgeDiagnosticCodes.OperationRetirement,
            $"{model.Name}.{property.Name}: RunicOperationRetirement applies only to a Bridge command that starts an operation "
            + "(an asynchronous CommunityToolkit or ReactiveUI command); it has no effect here.", property);
    }
    if (operations.Count == 0 && BridgeAttributeNames.Has(model, Attribute))
        Diagnostics.Warn(BridgeDiagnosticCodes.OperationRetirement,
            $"{model.Name}: RunicOperationRetirement on the class applies to its operations (asynchronous Bridge commands), but it has none; it has no effect here.", model);
}

// An observable collection of DTO rows (ObservableCollection<T>, ReadOnlyObservableCollection<T>) that is not a
// [RunicCollection] is published by comparison: every change of a row encodes and sends the whole list again, so such a
// list grows expensive as it grows (RUNICBRIDGE032). A plain list or array is a value the ViewModel replaces and is
// not reported.
static void WarnObservableCollectionsWithoutKeys(Type model, IReadOnlyDictionary<PropertyInfo, BridgeTypeGraph> valueProperties,
    IReadOnlyDictionary<PropertyInfo, PropertyInfo> incrementalCollections)
{
    foreach (var (property, graph) in valueProperties)
    {
        if (incrementalCollections.ContainsKey(property)
            || !typeof(System.Collections.Specialized.INotifyCollectionChanged).IsAssignableFrom(property.PropertyType)
            || graph.Root.Kind is not (BridgeWireKind.Array or BridgeWireKind.List)
            || graph.Root.Element is not { Kind: BridgeWireKind.Dto } row) continue;
        var key = row.Members.Select(member => member.Property)
            .FirstOrDefault(member => member.Name is "Id" or "Key"
                && (member.PropertyType == typeof(string) || member.PropertyType == typeof(Guid) || member.PropertyType == typeof(int)));
        Diagnostics.Warn(BridgeDiagnosticCodes.CollectionWithoutKey,
            $"{model.Name}.{property.Name}: an observable collection of {row.NonNullableType.Name} rows that is not a [RunicCollection] "
            + "publishes every change by encoding and sending the whole list. Declare "
            + (key is null ? $"[RunicCollection(nameof({row.NonNullableType.Name}.Key))] with a string, Guid or Int32 key property of the rows"
                : $"[RunicCollection(nameof({row.NonNullableType.Name}.{key.Name}))]")
            + $" to publish row changes, or suppress {BridgeDiagnosticCodes.CollectionWithoutKey} with NoWarn for a list that stays small.",
            property);
    }
}

static void WarnContentWithoutView(Type model, IReadOnlyDictionary<PropertyInfo, (Type Model, string Name)[]> contentProperties,
    IReadOnlyDictionary<PropertyInfo, (Type Model, string Name)[]> contentCollections, IReadOnlySet<PropertyInfo> regionSlots,
    IReadOnlyDictionary<Type, List<Type>> viewTypes)
{
    const int MissingViewCandidateLimit = 8;
    Type[] types;
    try { types = model.Assembly.GetTypes(); }
    catch (ReflectionTypeLoadException error) { types = [.. error.Types.OfType<Type>()]; }
    var candidates = types.Where(type => type.IsClass && type.IsPublic && !type.IsAbstract && !type.ContainsGenericParameters
            && typeof(INotifyPropertyChanged).IsAssignableFrom(type) && !viewTypes.ContainsKey(type))
        .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
    var windowModels = viewTypes.Where(entry => entry.Value.Any(IsWindow)).Select(entry => entry.Key).Append(model).ToArray();
    foreach (var property in contentProperties.Keys.Concat(contentCollections.Keys))
    {
        var slotType = regionSlots.Contains(property) ? BridgeContractShape.NavigationRegionContent(property.PropertyType)!
            : contentCollections.ContainsKey(property) ? property.PropertyType.GenericTypeArguments[0]
            : property.PropertyType;
        if (slotType == typeof(object) || BridgeContractShape.IsFrameworkBase(slotType) || slotType.Namespace is { } ns
                && (ns == "System" || BridgeMvvmConventions.IsFrameworkNamespace(ns))
            || windowModels.Any(slotType.IsAssignableFrom))
            continue;
        // The generated switch has a type-pattern case for each ViewModel with a
        // View that the slot admits, so a candidate that derives from or
        // implements one of them is presented by that case's View.
        var cases = contentProperties.TryGetValue(property, out var propertyCases) ? propertyCases : contentCollections[property];
        var missing = candidates.Where(type => type != model && slotType.IsAssignableFrom(type)
            && !cases.Any(entry => entry.Model.IsAssignableFrom(type))).ToArray();
        if (missing.Length > MissingViewCandidateLimit) continue;
        var contract = ContractFor(property);
        var slot = regionSlots.Contains(property) ? "pushing it to this navigation region" : "presenting it in this slot";
        foreach (var candidate in missing)
            Diagnostics.Warn(BridgeDiagnosticCodes.MissingView,
                $"{model.Name}.{property.Name}: {candidate.FullName} is assignable to {slotType.Name} but has no Runic View"
                + (contract is null ? "" : $" with contract '{contract}'")
                + $", so {slot} throws NotSupportedException at runtime. Add"
                + $" 'public sealed partial class {PublicName(candidate)}View : RunicView<{candidate.Name}>;'"
                + (contract is null ? "" : $" with [RunicViewContract(\"{contract}\")]")
                + $", or suppress {BridgeDiagnosticCodes.MissingView} with NoWarn if it is never presented here.",
                candidate);
    }
}

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
    cs.AppendLine("/// <summary>Registers this project's generated Runic Views for dependency injection.</summary>");
    AppendGeneratedCodeAttributes(cs, "");
    cs.AppendLine($"public static class {parts[^1]}");
    cs.AppendLine("{");
    cs.AppendLine("    /// <summary>Registers only the generated Bridges, for an application that creates its Views itself. <see cref=\"AddRunicViews\"/> calls it.</summary>");
    cs.AppendLine("    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddRunicBridges(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
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
            cs.AppendLine($"        services.AddScoped<global::System.Func<global::Runic.Application.Views.IBridgeTransport, {modelType}, global::System.IDisposable>>(");
            cs.AppendLine($"            _ => (transport, vm) => new {bridgeType}(transport, vm));");
        }
        cs.AppendLine($"        services.AddScoped<global::System.Func<global::Runic.Application.Views.IBridgeTransport, global::Runic.Application.Views.WindowContentSession, {modelType}, global::System.IDisposable>>(");
        cs.AppendLine($"            _ => (transport, content, vm) => new {bridgeType}(transport, vm, content: content));");
    }
    cs.AppendLine("        return services;");
    cs.AppendLine("    }");
    cs.AppendLine();
    // Views are registered by their concrete generated-time types, so the
    // registrations stay explicit and trimming/AOT safe. ViewModels remain
    // application registrations because their lifetime is an application choice.
    cs.AppendLine("    /// <summary>Registers the generated Bridges, every non-Window View as transient, and the default service-provider View locator.</summary>");
    cs.AppendLine("    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddRunicViews(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
    cs.AppendLine("    {");
    cs.AppendLine("        AddRunicBridges(services);");
    foreach (var view in views)
        cs.AppendLine($"        services.TryAddTransient<global::{view.FullName}>();");
    cs.AppendLine("        services.TryAddScoped<global::Runic.Application.Views.IRunicViewLocator, global::Runic.Application.Views.ServiceProviderViewLocator>();");
    cs.AppendLine("        return services;");
    cs.AppendLine("    }");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

// A referenced project that already generated a composition of the same name
// (a model project named after the application, for example) declares the
// type the application calls. A stub beside it would make every
// AddRunicViews() call ambiguous (CS0121), so none is written. The marker
// names the declaring assembly; the real build reports RUNICBRIDGE018 if this
// project generates a composition of that name as well.
static void GenerateCompositionStub(string path, string compositionType, string? referencesPath)
{
    var parts = CompositionParts(compositionType);
    var marker = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "RunicBridgeComposition.DeclaredBy.txt");
    if (referencesPath is not null && ReferencedDeclaration(referencesPath, parts) is { } declaringAssembly)
    {
        File.Delete(path);
        WriteIfChanged(marker, declaringAssembly + "\n");
        Console.WriteLine($"Runic Bridge: the referenced assembly {declaringAssembly} declares {compositionType}; no stub composition is generated.");
        return;
    }
    File.Delete(marker);
    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Microsoft.Extensions.DependencyInjection;");
    cs.AppendLine($"namespace {string.Join('.', parts[..^1])};");
    AppendGeneratedCodeAttributes(cs, "");
    cs.AppendLine($"public static class {parts[^1]}");
    cs.AppendLine("{");
    cs.AppendLine("    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddRunicBridges(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services;");
    cs.AppendLine("    public static global::Microsoft.Extensions.DependencyInjection.IServiceCollection AddRunicViews(this global::Microsoft.Extensions.DependencyInjection.IServiceCollection services) => services;");
    cs.AppendLine("}");
    WriteIfChanged(path, cs.ToString());
}

// The name of the first referenced assembly that declares a public top-level
// type with the composition's full name. References are read as metadata only,
// so reference assemblies and other target frameworks are fine.
static string? ReferencedDeclaration(string referencesPath, string[] compositionParts)
{
    var typeNamespace = string.Join('.', compositionParts[..^1]);
    var typeName = compositionParts[^1];
    foreach (var reference in File.ReadAllLines(referencesPath))
    {
        if (reference.Length == 0 || !File.Exists(reference)) continue;
        try
        {
            using var stream = File.OpenRead(reference);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public
                    && metadata.StringComparer.Equals(type.Name, typeName)
                    && metadata.StringComparer.Equals(type.Namespace, typeNamespace))
                    return metadata.IsAssembly ? metadata.GetString(metadata.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(reference);
            }
        }
        catch (BadImageFormatException)
        {
        }
    }
    return null;
}

// The markers CommunityToolkit.Mvvm and the ReactiveUI source generators emit: tools and coverage skip the code.
static void AppendGeneratedCodeAttributes(StringBuilder cs, string indent)
{
    var version = typeof(BridgeTypeGraph).Assembly
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(BridgeTypeGraph).Assembly.GetName().Version?.ToString() ?? "0.0.0";
    cs.AppendLine($"{indent}[global::System.CodeDom.Compiler.GeneratedCode(\"Runic.Application.Views.Codegen\", \"{version}\")]");
    cs.AppendLine($"{indent}[global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]");
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
    (Type Model, string Name)[]? knownModels = null,
    IReadOnlyDictionary<Type, List<Type>>? viewTypes = null)
{
    knownModels ??= [(model, shortName)];
    if (!typeof(INotifyPropertyChanged).IsAssignableFrom(model))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName} must implement INotifyPropertyChanged.", model);
    if (model.IsNested || !model.IsClass || !model.IsPublic)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName}: a Bridge ViewModel must be a public, top-level class.", model);

    var declared = BridgedProperties(model);
    var contractFingerprint = BridgeContractShape.Compute(model);
    var honorJsonIgnore = BridgeDtoContract.HonorsJsonIgnore(model.Assembly);
    var interactions = InteractionCodeEmitter.Discover(declared, model.Name, contractFingerprint, honorJsonIgnore);
    var commands = declared.Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)
        || MvvmCodegenConventions.All.Any(convention => InspectCommand(model, property, () => convention.InspectCommand(property)) is not null)).ToArray();
    var properties = declared.Except(commands).Except(interactions.Select(plan => plan.Property))
        .Where(property => !IsCommandAvailability(model, property, commands)).ToArray();
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
    // NavigationRegion<TContent> slots: content presented from the region's Current.
    var regionSlots = new HashSet<PropertyInfo>();
    // NavigationSelection slots: state whose wire value is the selection's SelectedIndex, or null for -1.
    var selectionSlots = new HashSet<PropertyInfo>();
    var valueProperties = new Dictionary<PropertyInfo, BridgeTypeGraph>();
    foreach (var property in properties)
    {
        if (property.GetMethod is null)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                $"{model.Name}.{property.Name}: a public getter is required.", property);
        if (BridgeContractShape.NavigationRegionContent(property.PropertyType) is { } regionContent)
        {
            if (property.SetMethod?.IsPublic == true)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                    $"{model.Name}.{property.Name}: a NavigationRegion slot must be get-only; its navigator, not the web view, changes the content.", property);
            if (regionContent == typeof(object))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                    $"{model.Name}.{property.Name}: a NavigationRegion<object> slot has no known content. Use an interface or base class that the region's ViewModels share.", property);
            var regionModels = knownModels.Where(entry => entry.Model != model && regionContent.IsAssignableFrom(entry.Model))
                .OrderByDescending(entry => InheritanceDepth(entry.Model))
                .ThenBy(entry => entry.Model.FullName, StringComparer.Ordinal)
                .ToArray();
            if (regionModels.Length == 0)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                    $"{model.Name}.{property.Name}: no ViewModel with a registered View is a {regionContent.Name}, so the NavigationRegion slot can present nothing.", property);
            contentProperties.Add(property, regionModels);
            regionSlots.Add(property);
            continue;
        }
        if (BridgeContractShape.IsNavigationSelection(property.PropertyType))
        {
            if (property.SetMethod?.IsPublic == true)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                    $"{model.Name}.{property.Name}: a NavigationSelection slot must be get-only; its SelectAsync, not the web view, changes the selection. Select through a command.", property);
            valueProperties.Add(property, BridgeTypeGraph.Discover(typeof(int?), rootPath: $"{model.Name}.{property.Name}",
                origin: property, honorJsonIgnore: honorJsonIgnore, direction: BridgeValueDirection.ToPage));
            selectionSlots.Add(property);
            continue;
        }
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
            nullability.Create(property), $"{model.Name}.{property.Name}", property, honorJsonIgnore,
            property.SetMethod?.IsPublic == true ? BridgeValueDirection.Both : BridgeValueDirection.ToPage));
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
    WarnObservableCollectionsWithoutKeys(model, valueProperties, incrementalCollections);
    // Append-only text (#300): a read-only string state property.
    var textFields = properties.Where(property => property.GetCustomAttribute<RunicAppendOnlyTextAttribute>(true) is not null).ToArray();
    foreach (var property in textFields)
        if (property.PropertyType != typeof(string) || property.SetMethod?.IsPublic == true || !valueProperties.ContainsKey(property))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                $"{model.Name}.{property.Name}: RunicAppendOnlyText requires a read-only string state property.", property);
    var hasContent = contentProperties.Count > 0 || contentCollections.Count > 0;
    if (viewTypes is not null) WarnContentWithoutView(model, contentProperties, contentCollections, regionSlots, viewTypes);
    // A region slot presents its Current, whose nullability (always nullable)
    // is the slot's wire and TypeScript nullability.
    string ContentAccess(PropertyInfo property) =>
        regionSlots.Contains(property) ? $"vm.{property.Name}?.Current" : $"vm.{property.Name}";
    // A selection slot's wire value: SelectedIndex, or null for -1 and for a null selection.
    static string SelectionIndex(PropertyInfo property) =>
        $"(vm.{property.Name} is {{ SelectedIndex: var index }} && index >= 0 ? index : (int?)null)";
    // DeclaredOnly: NavigationRegion<T>.Current hides the non-generic base's object-typed Current.
    bool IsNullableContent(PropertyInfo property) => (regionSlots.Contains(property)
        ? nullability.Create(property.PropertyType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)!)
        : nullability.Create(property)).ReadState == NullabilityState.Nullable;
    var contentBindings = contentProperties.Concat(contentCollections).ToArray();
    var fullType = $"global::{model.FullName}";
    var commandPlans = new Dictionary<PropertyInfo, GeneratedCommandPlan>();
    var commandSources = commands.ToDictionary(command => command, command => CommandSourceMethod(model, command));
    foreach (var command in commands)
    {
        var source = (MemberInfo?)commandSources[command] ?? command;
        if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command,
                $"{model.Name}.{command.Name}: Bridge commands must end with Command.", source);
        var library = MvvmCodegenConventions.All
            .Select(convention => InspectCommand(model, command, () => convention.InspectCommand(command))).ToArray()
            .FirstOrDefault(candidate => candidate is not null);
        var plainInput = command.GetCustomAttribute<RunicCommandInputAttribute>(true);
        // A RunicCommandKey command sends its resolver's key; its own parameter has no wire form.
        var key = CommandKey(model, command, source, library, plainInput, nullability, honorJsonIgnore);
        var plan = library is { } typed
            ? new GeneratedCommandPlan(key is null && typed.HasStringArgument, typed.IsAsync, Command: typed,
                InputGraph: key?.KeyGraph ?? (typed.Input is { } input ? BridgeTypeGraph.Discover(input,
                    ContractNullability.Argument(command, nullability, typed.ContractArguments, 0),
                    $"{model.Name}.{command.Name}.input", source, honorJsonIgnore, BridgeValueDirection.FromPage) : null),
                ResultGraph: typed.Result is { } result ? BridgeTypeGraph.Discover(result,
                    typed.ResultNullability(command, nullability),
                    $"{model.Name}.{command.Name}.result", source, honorJsonIgnore, BridgeValueDirection.ToPage) : null)
            { Key = key }
            : plainInput is not null
                ? GeneratedCommandPlan.Plain(BridgeTypeGraph.Discover(plainInput.Input,
                    rootPath: $"{model.Name}.{command.Name}.input", origin: source, honorJsonIgnore: honorJsonIgnore,
                    direction: BridgeValueDirection.FromPage))
            : key is not null
                ? GeneratedCommandPlan.Plain(key.KeyGraph) with { Key = key }
            : throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command,
                $"{model.Name}.{command.Name}: unsupported command shape. Use a CommunityToolkit IRelayCommand or IAsyncRelayCommand, a ReactiveUI ReactiveCommand, or an ICommand with RunicCommandInput or RunicCommandKey.", source);
        if (plan.Command?.SelectsResultCardinality != true && command.GetCustomAttribute<RunicCommandResultAttribute>(true) is not null)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Command,
                $"{model.Name}.{command.Name}: RunicCommandResult selects a ReactiveUI command's result cardinality; a CommunityToolkit command has the single result of its Task<T> method, and a plain command has none.", source);
        commandPlans.Add(command, plan with
        {
            ParameterName = CommandParameterName(commandSources[command], plan),
            FailureGraph = FailureGraph(model, command, source),
        });
    }
    // A failure declaration must belong to a bridged command; anything else
    // would silently not reach the client.
    // Base classes are included: only the model's own command properties are
    // bridged, so a declaration on an inherited member never reaches a client.
    var failureOwners = commands.Select(command => (MemberInfo)command)
        .Concat(commandSources.Values.OfType<MethodInfo>()).ToHashSet();
    // Properties and methods only, and RunicFailure by name: GetMembers would
    // load nested types (an iterator's state machine over WPF objects), and
    // GetCustomAttribute resolves every attribute type on the member.
    const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic;
    for (var owner = model; owner is not null && owner != typeof(object); owner = owner.BaseType)
        foreach (var member in owner.GetProperties(AllDeclared).Concat<MemberInfo>(owner.GetMethods(AllDeclared)).OrderBy(member => member.MetadataToken))
            if (BridgeAttributeNames.Has(member, "Runic.Application.Views.RunicFailureAttribute") && !failureOwners.Contains(member)
                && !OverriddenByCommand(member))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Failure,
                    $"{model.Name}.{member.Name}: RunicFailure applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method"
                    + (owner == model ? "." : $"; {owner.Name} declares it on a member the Bridge does not expose."), member);
    // A base property that a bridged command overrides passes its declaration
    // to the override (attributes on properties are inherited), so it is valid.
    // RunicCommandKey, like RunicFailure, belongs to a bridged command or its [RelayCommand] method.
    for (var owner = model; owner is not null && owner != typeof(object); owner = owner.BaseType)
        foreach (var member in owner.GetProperties(AllDeclared).Concat<MemberInfo>(owner.GetMethods(AllDeclared)).OrderBy(member => member.MetadataToken))
            if (BridgeAttributeNames.Has(member, "Runic.Application.Views.RunicCommandKeyAttribute") && !failureOwners.Contains(member)
                && !OverriddenByCommand(member))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
                    $"{model.Name}.{member.Name}: RunicCommandKey applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method"
                    + (owner == model ? "." : $"; {owner.Name} declares it on a member the Bridge does not expose."), member);
    bool OverriddenByCommand(MemberInfo member) => member is PropertyInfo baseProperty && commands.Any(command =>
        command.Name == baseProperty.Name && command.GetMethod is { } getter && baseProperty.GetMethod is { } baseGetter
        && getter.GetBaseDefinition() == baseGetter.GetBaseDefinition() && getter != baseGetter);
    ApplyOperationRetirement(model, commands, commandPlans);
    var operationPlans = commands.Where(command => commandPlans[command].IsAsync).Select(command =>
    {
        var plan = commandPlans[command];
        return new OperationTypeScriptPlan(command.Name[..^"Command".Length],
            plan.InputGraph?.TypeScriptType() ?? "never",
            plan.ResultGraph?.TypeScriptType() ?? "void",
            plan.ResultGraph?.EmitTypeScriptDecoder("value") ?? "undefined",
            plan.InputGraph?.EncodeTypeScript(plan.ParameterName) ?? "undefined",
            plan.HasArgument,
            plan.Command?.Cardinality is RunicCommandResultCardinality.Stream,
            plan.ParameterName,
            plan.FailureGraph?.TypeScriptType(),
            plan.FailureGraph?.EmitTypeScriptDecoder("value"));
    }).ToArray();

    if (shortName.Length == 0 || !shortName.All(char.IsLetterOrDigit) || !char.IsLetter(shortName[0]))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName}: the public Bridge name '{shortName}' must be a C#/TypeScript identifier.", model);
    var prefix = LowerFirst(shortName);
    var hasErrors = typeof(INotifyDataErrorInfo).IsAssignableFrom(model);
    Type[] validationTypes = [model, .. valueProperties.Values.SelectMany(graph => graph.Nodes.Select(node => node.Type)).Distinct()];
    // #478, D053.3: the ReactiveUI.Validation flavors (matched by name; the generator references neither) whose
    // ValidationContext the generated Bridge reads and observes. A ViewModel or DTO that implements
    // IValidatableViewModel has validation without INotifyDataErrorInfo too.
    var validationContextFlavors = ValidationContextFlavors(validationTypes);
    WarnUnrecognizedValidatable(model, validationTypes);
    var hasValidation = hasErrors || validationContextFlavors.Length > 0 || valueProperties.Values.Any(graph =>
        graph.Nodes.Any(node => typeof(INotifyDataErrorInfo).IsAssignableFrom(node.Type)));
    if (hasValidation && properties.FirstOrDefault(property => WireName(property) == "validation") is { } validationProperty)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
            $"{model.Name}.{validationProperty.Name}: validation is reserved for generated validation state. Alias the model property.", validationProperty);
    // Checked writes use a WindowContentSession-owned provider. The window slice
    // supports the scalar codecs that the ordinary Bridge already serializes
    // without reflection.
    var checkedProperties = properties
        .Where(property => property.SetMethod?.IsPublic == true && valueProperties.ContainsKey(property)).ToArray();
    var needsCheckedWriter = checkedProperties.Length > 0;
    InteractionCodeEmitter.ValidatePublicSurface(model.Name, properties, commands, commandPlans, interactions,
        checkedProperties, WireName, hasErrors, hasValidation);

    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Runic.Application.Views;");
    AppendNamespace(cs, BridgeNamespace);
    cs.AppendLine($"internal sealed class {shortName}Bridge : global::Runic.Application.Views.Generated.ViewModelBridge<{fullType}>");
    cs.AppendLine("{");
    if (interactions.Length > 0)
        cs.AppendLine("    private readonly global::System.IDisposable? _rootInteractionMount;");
    if (hasContent)
    {
        cs.AppendLine("    private readonly global::Runic.Application.Views.WindowContentSession _content;");
        cs.AppendLine($"    private readonly {fullType} _owner;");
    }
    cs.AppendLine($"    internal {shortName}Bridge(global::Runic.Application.Views.IBridgeTransport transport, {fullType} vm, string? routePrefix = null, global::Runic.Application.Views.WindowContentSession? content = null) : base(");
    cs.AppendLine($"        transport, vm, routePrefix ?? \"{prefix}\",");
    cs.AppendLine(hasContent ? "        CreateState(content)," : "        State,");
    cs.AppendLine("        [");
    foreach (var property in properties)
    {
        // A setter reads the request's value with the member's codec.
        var setter = property.SetMethod?.IsPublic == true && valueProperties.ContainsKey(property)
            ? $"(vm, value) => vm.{property.Name} = {property.Name}ValueCodec.Read(value)"
            : "null";
        var descriptorNames = $"\"{property.Name}\", \"{WireName(property)}\"";
        if (regionSlots.Contains(property))
        {
            // The runtime observes the region and binds the window session.
            cs.AppendLine($"            new global::Runic.Application.Views.Generated.PropertyDescriptor<{fullType}>({descriptorNames}, vm => vm.{property.Name}?.Current, null, static vm => vm.{property.Name}),");
            continue;
        }
        if (selectionSlots.Contains(property))
        {
            // The runtime observes the selection's SelectedIndex.
            cs.AppendLine($"            new global::Runic.Application.Views.Generated.PropertyDescriptor<{fullType}>({descriptorNames}, vm => {SelectionIndex(property)}, null, static vm => vm.{property.Name}),");
            continue;
        }
        // A frame is one publication unit, so the text's change and the validation it causes arrive together.
        var text = textFields.Contains(property) ? $" {{ AppendOnlyTextField = \"{WireName(property)}\" }}" : "";
        cs.AppendLine($"            new global::Runic.Application.Views.Generated.PropertyDescriptor<{fullType}>({descriptorNames}, vm => vm.{property.Name}, {setter}){text},");
    }
    cs.AppendLine("        ],");
    {
        cs.AppendLine("        [");
        foreach (var property in checkedProperties)
        {
            var type = valueProperties[property].RootCSharpType();
            cs.AppendLine($"            new global::Runic.Application.Views.Generated.CheckedPropertyDescriptor<{fullType}>(\"{property.Name}\", \"{WireName(property)}\", global::Runic.Application.Views.Generated.CheckedFieldValueKind.Json, vm => vm.{property.Name}, (vm, value) => vm.{property.Name} = ({type})value!, readValue: element => {property.Name}ValueCodec.Read(element), writeValue: (writer, value) => {property.Name}ValueCodec.Write(writer, ({type})value!)),");
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
        // Protocol 4: a frame is one publication unit with the collection's indexed changes and every member that
        // changed with them, validation included, so a ViewModel with validation publishes frames too.
        var itemType = BridgeTypeGraph.CSharpType(valueProperties[property].Root.Element!.NonNullableType);
        var keyAccess = $"(({itemType})item!).{key.Name}";
        var keyExpression = key.PropertyType == typeof(string) ? keyAccess
            : key.PropertyType == typeof(Guid) ? keyAccess + ".ToString(\"D\")"
            : keyAccess + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)";
        cs.AppendLine($"            new global::Runic.Application.Views.Generated.BridgeCollectionDescriptor<{fullType}>(\"{WireName(property)}\", static vm => vm.{property.Name}, {property.Name}ValueCodec.WriteItem, static item => {keyExpression}),");
    }
    cs.AppendLine(validationContextFlavors.Length > 0 ? "        ], validationReader: ValidationReader" : "        ]");
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
    cs.AppendLine("    private static readonly global::Runic.Application.Views.Generated.BridgeDataSubscriptionMember[] DataMetadata =");
    // A selection is observed through its descriptor; its members are not data.
    BridgeDataSubscriptionEmitter.AppendMetadata(cs, fullType, valueProperties.Where(entry => !selectionSlots.Contains(entry.Key)));
    cs.AppendLine("    ;");
    if (validationContextFlavors.Length > 0)
    {
        cs.AppendLine();
        AppendValidationContextReader(cs, validationContextFlavors);
    }
    foreach (var (property, graph) in valueProperties)
    {
        graph.AppendCSharpCodec(cs, property.Name + "ValueCodec");
        cs.AppendLine();
    }
    foreach (var (command, plan) in commandPlans)
    {
        if (plan.InputGraph is { } input) { input.AppendCSharpCodec(cs, command.Name + "InputCodec"); cs.AppendLine(); }
        if (plan.ResultGraph is { } result) { result.AppendCSharpCodec(cs, command.Name + "ResultCodec"); cs.AppendLine(); }
        if (plan.FailureGraph is { } failure) { failure.AppendCSharpCodec(cs, command.Name + "FailureCodec"); cs.AppendLine(); }
    }
    InteractionCodeEmitter.AppendCSharpCodecs(cs, interactions);
    foreach (var convention in MvvmCodegenConventions.All)
        convention.AppendSupportTypes(cs, commandPlans.Values);
    // The state members in order (specs/application, "State and revisions"). Each writes its value; the runtime writes
    // the member names, and the revision and the checked fields' versions beside the state.
    var stateMembers = new List<(string Member, string Body)>();
    foreach (var property in properties)
    {
        var jsonName = WireName(property);
        if (contentProperties.TryGetValue(property, out var pageModels))
        {
            var writer = new StringBuilder();
            writer.AppendLine($"if ({ContentAccess(property)} is null) {{ content.Clear(vm, \"{property.Name}\"); writer.WriteNullValue(); }}");
            writer.AppendLine("else");
            writer.AppendLine("{");
            writer.AppendLine($"    switch ({ContentAccess(property)})");
            writer.AppendLine("    {");
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
                writer.AppendLine($"        case {pageType} page:");
                writer.AppendLine("        {");
                if (selectedView is not null)
                    writer.AppendLine($"            var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content){contractArgument}));");
                else
                    writer.AppendLine($"            var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => new {bridgeType}(transport, current, route, content));");
                writer.AppendLine("            writer.WriteStartObject();");
                writer.AppendLine("            writer.WriteString(\"kind\", reference.Kind);");
                writer.AppendLine("            writer.WriteString(\"id\", reference.Id);");
                writer.AppendLine("            writer.WriteEndObject();");
                writer.AppendLine("            break;");
                writer.AppendLine("        }");
            }
            writer.AppendLine($"        default: {{ var unregistered = {ContentAccess(property)}!; throw new global::System.NotSupportedException({UnregisteredContentMessage(model, property, regionSlots.Contains(property))}); }}");
            writer.AppendLine("    }");
            writer.AppendLine("}");
            stateMembers.Add((jsonName, writer.ToString()));
        }
        else if (contentCollections.TryGetValue(property, out var collectionModels))
        {
            var writer = new StringBuilder();
            writer.AppendLine("var active = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);");
            writer.AppendLine($"if (vm.{property.Name} is null) {{ content.PruneCollection(vm, \"{property.Name}\", active); writer.WriteNullValue(); return; }}");
            writer.AppendLine("writer.WriteStartArray();");
            writer.AppendLine($"foreach (var item in vm.{property.Name})");
            writer.AppendLine("{");
            writer.AppendLine("    if (item is null) { writer.WriteNullValue(); continue; }");
            writer.AppendLine("    switch (item)");
            writer.AppendLine("    {");
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
                writer.AppendLine($"        case {pageType} page:");
                writer.AppendLine("        {");
                if (selectedView is not null)
                    writer.AppendLine($"            var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content){contractArgument}));");
                else
                    writer.AppendLine($"            var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => new {bridgeType}(transport, current, route, content));");
                writer.AppendLine("            active.Add(reference.Id);");
                writer.AppendLine("            writer.WriteStartObject();");
                writer.AppendLine("            writer.WriteString(\"kind\", reference.Kind);");
                writer.AppendLine("            writer.WriteString(\"id\", reference.Id);");
                writer.AppendLine("            writer.WriteEndObject();");
                writer.AppendLine("            break;");
                writer.AppendLine("        }");
            }
            writer.AppendLine($"        default: {{ var unregistered = item!; throw new global::System.NotSupportedException({UnregisteredContentMessage(model, property, false)}); }}");
            writer.AppendLine("    }");
            writer.AppendLine("}");
            writer.AppendLine($"content.PruneCollection(vm, \"{property.Name}\", active);");
            writer.AppendLine("writer.WriteEndArray();");
            stateMembers.Add((jsonName, writer.ToString()));
        }
        else if (valueProperties.ContainsKey(property))
            stateMembers.Add((jsonName, $"{property.Name}ValueCodec.Write(writer, {(selectionSlots.Contains(property) ? SelectionIndex(property) : $"vm.{property.Name}")})"));
        else throw new InvalidOperationException($"{model.Name}.{property.Name}: no generated value codec.");
        if (hasErrors) stateMembers.Add((jsonName + "Errors", $"WriteStateErrors(writer, vm, \"{property.Name}\")"));
    }
    if (hasValidation)
        stateMembers.Add(("validation", validationContextFlavors.Length > 0
            ? "global::Runic.Application.Views.Generated.BridgeValidation.Write(writer, vm, DataMetadata, ReadValidationContextErrors)"
            : "global::Runic.Application.Views.Generated.BridgeValidation.Write(writer, vm, DataMetadata)"));
    // Availability without an argument is state; a command with an argument
    // is queried through can<Name>(argument). Execution state needs no
    // argument and is written for every asynchronous command that has it.
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var name = command.Name[..^"Command".Length];
        var members = plan.Command is { } library ? library.StateMembers(command, name, plan)
            : plan.HasArgument ? [] : [MvvmCommand.CanExecute(command, name)];
        foreach (var (member, value) in members) stateMembers.Add((member, $"writer.WriteBooleanValue({value})"));
    }
    var stateMemberType = $"global::Runic.Application.Views.Generated.BridgeStateMember<{fullType}>";
    if (hasContent)
    {
        cs.AppendLine($"    private static {stateMemberType}[] CreateState(global::Runic.Application.Views.WindowContentSession? content)");
        cs.AppendLine("    {");
        cs.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(content);");
        cs.AppendLine("        return");
        AppendStateMembers("        ");
        cs.AppendLine("        ;");
        cs.AppendLine("    }");
    }
    else
    {
        cs.AppendLine($"    private static readonly {stateMemberType}[] State =");
        AppendStateMembers("    ");
        cs.AppendLine("    ;");
    }
    if (hasErrors)
    {
        cs.AppendLine();
        cs.AppendLine("    private static void WriteStateErrors(global::System.Text.Json.Utf8JsonWriter writer, global::System.ComponentModel.INotifyDataErrorInfo vm, string property) =>");
        cs.AppendLine("        global::Runic.Application.Views.Generated.BridgeValidation.WriteMessages(writer, vm.GetErrors(property));");
    }

    // One member per line; a member whose writer has several statements gets a block body.
    void AppendStateMembers(string indent)
    {
        cs.AppendLine($"{indent}[");
        foreach (var (member, body) in stateMembers)
        {
            var name = JsonSerializer.Serialize(member);
            if (!body.Contains('\n'))
            {
                cs.AppendLine($"{indent}    new {stateMemberType}({name}, static (writer, vm) => {body}),");
                continue;
            }
            cs.AppendLine($"{indent}    new {stateMemberType}({name}, (writer, vm) =>");
            cs.AppendLine($"{indent}    {{");
            foreach (var line in body.TrimEnd('\n').Split('\n')) cs.AppendLine($"{indent}        {line}");
            cs.AppendLine($"{indent}    }}),");
        }
        cs.AppendLine($"{indent}]");
    }
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
    if (hasValidation) ts.AppendLine($"  readonly validation: {Ref("BridgeValidationState")};");
    foreach (var property in properties)
    {
        var propertyType = contentProperties.TryGetValue(property, out var pages)
            ? string.Join(" | ", pages.Select(page =>
                $"{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}PageReference"))
                + (IsNullableContent(property) ? " | null" : "")
            : contentCollections.TryGetValue(property, out var collectionPages)
                ? "readonly (" + string.Join(" | ", collectionPages.Select(page =>
                    $"{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}PageReference"))
                    + ")[]" + (nullability.Create(property).ReadState == NullabilityState.Nullable ? " | null" : "")
            : valueProperties.TryGetValue(property, out var valueGraph)
                ? valueGraph.TypeScriptType()
                : throw new InvalidOperationException($"{model.Name}.{property.Name}: no generated TypeScript type.");
        var stateDocumentation = XmlDocumentation.Summary(property);
        if (valueProperties.TryGetValue(property, out var documentedGraph))
            stateDocumentation = BridgeTypeGraph.WithTypeScriptNote(stateDocumentation, documentedGraph.Root);
        XmlDocumentation.Append(ts, "  ", stateDocumentation);
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
    ts.AppendLine($"export interface {clientName} extends {Ref("ViewClient")}<{shortName}State> {{");
    if (interactions.Length > 0) ts.AppendLine($"  readonly interactions: {shortName}Interactions;");
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
    {
        XmlDocumentation.Append(ts, "  ", SetterDocumentation(property));
        ts.AppendLine($"  set{property.Name}(value: {TsPropertyType(property)}): Promise<{shortName}State>;");
    }
    foreach (var property in checkedProperties)
    {
        XmlDocumentation.Append(ts, "  ", XmlDocumentation.Summary(property)
            ?? $"Writes `{property.ReflectedType?.Name}.{property.Name}` with an acknowledged field write, which reports whether .NET committed it.");
        ts.AppendLine($"  write{property.Name}(value: {TsPropertyType(property)}, options: {Ref("FieldWriteOptions")}<{TsPropertyType(property)}>): Promise<{Ref("FieldWriteReceipt")}<{TsPropertyType(property)}>>;");
    }
    if (needsCheckedWriter)
    {
        ts.AppendLine("  /** The value and version of a checked field that a `write…` call is based on. */");
        ts.AppendLine($"  fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K): {Ref("FieldBaseline")}<{shortName}CheckedFields[K]>;");
    }
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var input = plan.HasArgument ? $"{plan.ParameterName}: {CommandInputType(plan)}" : "";
        var documentation = CommandDocumentation(command, commandSources[command], plan.Command);
        // A RunicCommandKey command (#472) sends the key; .NET resolves it to the command's parameter.
        var unresolved = plan.Key is null ? null
            : $"Rejects with a `BridgeError` of kind `rejected` and reason `unresolved-key` when .NET finds no object for `{plan.ParameterName}`.";
        XmlDocumentation.Append(ts, "  ", unresolved is null ? documentation
            : string.IsNullOrWhiteSpace(documentation) ? unresolved : documentation + "\n\n" + unresolved);
        var reply = plan.FailureGraph is { } declaredFailure
            ? $"{Ref("BridgeOutcome")}<{shortName}State, {declaredFailure.TypeScriptType()}>" : $"{shortName}State";
        ts.AppendLine($"  {LowerFirst(command.Name[..^"Command".Length])}({input}): Promise<{reply}>;");
        if (plan.Key is not null)
            XmlDocumentation.Append(ts, "  ", $"Whether `{LowerFirst(command.Name[..^"Command".Length])}` can run for `{plan.ParameterName}`: false when .NET finds no object for it.");
        if (plan.HasArgument) ts.AppendLine($"  can{command.Name[..^"Command".Length]}({input}): Promise<boolean>;");
    }
    foreach (var command in commands.Where(command => commandPlans[command].IsAsync))
    {
        var plan = commandPlans[command];
        var operationName = command.Name[..^"Command".Length];
        var input = plan.HasArgument ? $"{plan.ParameterName}: {CommandInputType(plan)}, " : "";
        ts.AppendLine($"  /**");
        ts.AppendLine($"   * Starts `{operationName}` as an operation that the page can observe, cancel and recover. `options.requestId` names it;");
        ts.AppendLine($"   * the default is a random id. A start with a request id that .NET already admitted returns that operation.");
        if (plan.Key is not null)
            ts.AppendLine($"   * A new start is rejected with reason `unresolved-key` when .NET finds no object for `{plan.ParameterName}`.");
        ts.AppendLine($"   */");
        ts.AppendLine($"  start{operationName}({input}options?: {Ref("BridgeOperationStartOptions")}): Promise<{shortName}{operationName}Operation>;");
        ts.AppendLine($"  /** Returns the `{operationName}` operation started with `requestId`, for example after the page reloaded. */");
        ts.AppendLine($"  recover{operationName}(requestId: string): Promise<{shortName}{operationName}Operation>;");
    }
    ts.AppendLine("}");
    ts.AppendLine();
    var connectAt = Ref($"connect{shortName}At");
    var ownContracts = viewTypes is not null && viewTypes.TryGetValue(model, out var ownViews)
        ? ownViews.Select(ContractFor).ToArray() : [null];
    foreach (var contract in ownContracts)
    {
        var ownKind = PageKind(shortName, contract);
        var functionName = char.ToUpperInvariant(ownKind[0]) + ownKind[1..];
        var referenceType = $"{functionName}PageReference";
        var cacheName = Ref($"page{functionName}References");
        ts.AppendLine($"export interface {referenceType} {{");
        ts.AppendLine($"  readonly kind: \"{ownKind}\";");
        ts.AppendLine($"  connect(): Promise<{clientName}>;");
        ts.AppendLine("}");
        ts.AppendLine($"const {cacheName} = {Ref("viewReferences")}<{referenceType}>(id => ({{ kind: \"{ownKind}\", connect: () => {connectAt}(`content${{id}}`) }}));");
        ts.AppendLine($"export function page{functionName}(id: string): {referenceType} {{ return {cacheName}(id); }}");
        ts.AppendLine();
    }
    // The public state is the decoded TypeScript contract. The client passes one
    // decoder per state member, which validates the member's wire value and
    // converts it (for example int64 strings to bigint), so a frame's `set`
    // change decodes only its member.
    string ContentDecoder(PropertyInfo property, (Type Model, string Name)[] pages, string reference)
    {
        var field = WireName(property);
        return string.Join(" : ", pages.Select(page =>
            $"{reference}.kind === \"{PageKind(page.Name, ContractFor(property))}\" ? page{char.ToUpperInvariant(PageKind(page.Name, ContractFor(property))[0])}{PageKind(page.Name, ContractFor(property))[1..]}({reference}.id)"))
            + $" : (() => {{ throw new {Ref("BridgeError")}(\"failed\", {JsonSerializer.Serialize($"Unknown {field} kind.")}); }})()";
    }
    // A decoder is an expression of `wire`, or a block body.
    var memberDecoders = new List<(string Member, string Decoder)>();
    var blockDecoders = new HashSet<string>(StringComparer.Ordinal);
    if (hasValidation) memberDecoders.Add(("validation", $"{Ref("decodeBridgeValidation")}(wire)"));
    foreach (var property in properties)
    {
        var field = WireName(property);
        if (valueProperties.TryGetValue(property, out var graph))
            memberDecoders.Add((field, graph.EmitTypeScriptDecoder("wire")));
        else if (contentProperties.TryGetValue(property, out var pages))
        {
            var decoder = $"{{ const reference = {Ref("contentReference")}(wire); return {ContentDecoder(property, pages, "reference")}; }}";
            memberDecoders.Add((field, IsNullableContent(property) ? $"{{ if (wire === null) return null; {decoder[2..]}" : decoder));
            blockDecoders.Add(field);
        }
        else if (contentCollections.TryGetValue(property, out var collectionPages))
        {
            var decoder = $"bridgeWire.array(wire, item => {{ const reference = {Ref("contentReference")}(item); return {ContentDecoder(property, collectionPages, "reference")}; }})";
            memberDecoders.Add((field, nullability.Create(property).ReadState == NullabilityState.Nullable ? $"wire === null ? null : {decoder}" : decoder));
        }
        if (hasErrors) memberDecoders.Add((field + "Errors", "bridgeWire.array(wire, item => bridgeWire.string(item))"));
    }
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var name = command.Name[..^"Command".Length];
        if (!plan.HasArgument) memberDecoders.Add(($"can{name}", "bridgeWire.boolean(wire)"));
        if (plan.HasExecutionState) memberDecoders.Add(($"is{name}Executing", "bridgeWire.boolean(wire)"));
    }
    if (hasContent)
    {
        ts.AppendLine($"function {Ref("contentReference")}(wire: unknown): {{ readonly kind: string; readonly id: string }} {{");
        ts.AppendLine("  return bridgeWire.object(wire, value => ({ kind: bridgeWire.string(value[\"kind\"]), id: bridgeWire.string(value[\"id\"]) }));");
        ts.AppendLine("}");
    }
    ts.AppendLine($"const {Ref("members")}: {Ref("BridgeMemberDecoders")}<{shortName}State> = {{");
    foreach (var (member, decoder) in memberDecoders)
        ts.AppendLine(blockDecoders.Contains(member)
            ? $"  {TsLiteralKey(member)}: (wire: unknown) => {decoder},"
            : $"  {TsLiteralKey(member)}: (wire: unknown) => {BridgeTypeGraph.ArrowBody(decoder)},");
    ts.AppendLine("};");
    ts.AppendLine($"const {Ref("bridgeContract")} = \"{model.FullName}:{contractFingerprint}\";");
    InteractionCodeEmitter.AppendTypeScriptDefinitions(ts, interactions);
    if (needsCheckedWriter)
        ts.AppendLine($"const {Ref("checkedFieldNames")} = [{string.Join(", ", checkedProperties.Select(property => JsonSerializer.Serialize(WireName(property))))}];");
    OperationTypeScriptEmitter.AppendCodecs(ts, operationPlans);
    ts.AppendLine();
    var hasDeltaFields = incrementalCollections.Count > 0 || textFields.Length > 0;
    if (hasDeltaFields)
    {
        ts.AppendLine($"const {Ref("collectionDefinitions")} = {Ref("defineCollections")}({{");
        foreach (var (property, key) in incrementalCollections)
        {
            var graph = valueProperties[property];
            var keyMember = graph.Root.Element!.Members.Single(member => member.Property == key);
            ts.AppendLine($"  {TsLiteralKey(WireName(property))}: {Ref("defineCollection")}<{graph.ItemTypeScriptType()}>(wire => {BridgeTypeGraph.ArrowBody(graph.EmitItemTypeScriptDecoder("wire"))}, item => String({TsAccess("item", keyMember.WireName)})),");
        }
        foreach (var property in textFields)
            ts.AppendLine($"  {TsLiteralKey(WireName(property))}: {Ref("defineText")}({(IsNullableString(property) ? "true" : "")}),");
        ts.AppendLine("});");
    }
    var connectOptions = $"contract: {Ref("bridgeContract")}, route, {Shorthand("members")}"
        + (hasDeltaFields ? $", collections: {Ref("collectionDefinitions")}" : "")
        + (needsCheckedWriter ? $", checkedFields: {Ref("checkedFieldNames")}" : "")
        + (interactions.Length > 0 ? $", interactions: {Ref("interactionDefinitions")}, handlers: options?.handlers" : "")
        + (operationPlans.Length > 0 ? $", operations: {Ref("bridgeOperations")}" : "");
    // A View with interactions takes their handlers when it connects, so the producer can ask them from the first
    // request on (specs/application, "Presentations").
    var optionsType = interactions.Length > 0 ? $"{shortName}ConnectOptions" : Ref("BridgeConnectOptions");
    if (interactions.Length > 0)
        InteractionCodeEmitter.AppendConnectOptions(ts, interactions, shortName);
    XmlDocumentation.Append(ts, "", $"Connects a {shortName} client. It waits up to `options.timeout` milliseconds (default 5000) for the\nhost Bridge to connect, then rejects with a `timeout` BridgeError. `options.signal` abandons the connection.\nA `using` declaration disposes the client where the engine has `Symbol.dispose`."
        + (interactions.Length > 0 ? "\n`options.handlers` answers interactions from the first request on." : ""));
    // globalThis.Disposable cannot be shadowed by a named type of the same name.
    ts.AppendLine($"export function connect{shortName}(options?: {optionsType}): Promise<{clientName} & globalThis.Disposable> {{ return {connectAt}(\"{prefix}\", options); }}");
    ts.AppendLine($"async function {connectAt}(route: string, options?: {optionsType}): Promise<{clientName} & globalThis.Disposable> {{");
    ts.AppendLine($"  const view = await {Ref("connectView")}<{shortName}State>({{ {connectOptions}, timeout: options?.timeout, signal: options?.signal }});");
    ts.AppendLine($"  return {Ref("disposable")}<{clientName}>({{");
    ts.AppendLine("    get snapshot() { return view.snapshot; },");
    ts.AppendLine("    subscribe: view.subscribe,");
    ts.AppendLine("    dispose: view.dispose,");
    if (interactions.Length > 0)
        ts.AppendLine($"    interactions: view.interactions as unknown as {shortName}Interactions,");
    if (needsCheckedWriter)
        ts.AppendLine($"    fieldBaseline<K extends keyof {shortName}CheckedFields>(field: K) {{ return view.fieldBaseline(field) as {Ref("FieldBaseline")}<{shortName}CheckedFields[K]>; }},");
    foreach (var property in properties.Where(property => property.SetMethod?.IsPublic == true))
    {
        // A setter sends the member's value as an edit (specs/application, "Edits").
        var member = JsonSerializer.Serialize(WireName(property));
        if (property.PropertyType == typeof(int))
            ts.AppendLine($"    async set{property.Name}(value) {{ if (!Number.isSafeInteger(value)) throw new RangeError(\"Value must be an integer.\"); return view.set({member}, value); }},");
        else
            ts.AppendLine($"    async set{property.Name}(value) {{ return view.set({member}, {valueProperties[property].EncodeTypeScript("value")}); }},");
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
        ts.AppendLine($"      return view.write<{TsPropertyType(property)}>({JsonSerializer.Serialize(WireName(property))}, {{ requestId: options.requestId, expectedVersion: baseline.version, expectedValue: {valueGraph.EncodeTypeScript("baseline.value")}, value: {valueGraph.EncodeTypeScript("value")} }}, value => {BridgeTypeGraph.ArrowBody(valueGraph.EmitTypeScriptDecoder("value"))});");
        ts.AppendLine("    },");
    }
    foreach (var command in commands)
    {
        var name = command.Name[..^"Command".Length];
        var member = JsonSerializer.Serialize(LowerFirst(name));
        var plan = commandPlans[command];
        var parameter = plan.ParameterName;
        var input = plan.InputGraph is { } inputGraph ? inputGraph.EncodeTypeScript(parameter)
            : plan.HasStringArgument ? parameter : null;
        var argument = input is null ? "" : $", {input}";
        ts.AppendLine(plan.FailureGraph is { } failureGraph
            ? $"    async {LowerFirst(name)}({(plan.HasArgument ? parameter : "")}) {{ return view.commandOutcome({member}, value => {BridgeTypeGraph.ArrowBody(failureGraph.EmitTypeScriptDecoder("value"))}{argument}); }},"
            : $"    async {LowerFirst(name)}({(plan.HasArgument ? parameter : "")}) {{ return view.command({member}{argument}); }},");
        if (plan.HasArgument)
            ts.AppendLine($"    async can{name}({parameter}) {{ return view.can({member}{argument}); }},");
    }
    OperationTypeScriptEmitter.AppendClientMethods(ts, operationPlans);
    ts.AppendLine("  });");
    ts.AppendLine("}");


    var body = ts.ToString();
    // Application-facing types and errors come from the package root; the
    // runtime only generated code calls comes from its /generated entry, and
    // only the protocol parts this ViewModel uses are imported.
    var publicImports = new List<string>();
    if (body.Contains($"new {Ref("BridgeError")}(", StringComparison.Ordinal)) publicImports.Add(Import("BridgeError"));
    if (hasValidation) publicImports.Add("type " + Import("BridgeValidationState"));
    publicImports.Add("type " + Import("BridgeConnectOptions"));
    publicImports.Add("type " + Import("ViewClient"));
    if (needsCheckedWriter) publicImports.AddRange(["type " + Import("FieldBaseline"), "type " + Import("FieldWriteOptions"), "type " + Import("FieldWriteReceipt")]);
    if (commandPlans.Values.Any(plan => plan.FailureGraph is not null)) publicImports.Add("type " + Import("BridgeOutcome"));
    if (operationPlans.Any(operation => !operation.IsStream)) publicImports.Add("type " + Import("BridgeOperation"));
    if (operationPlans.Length > 0) publicImports.Add("type " + Import("BridgeOperationStartOptions"));
    if (operationPlans.Any(operation => operation.IsStream)) publicImports.Add("type " + Import("BridgeStreamOperation"));
    if (interactions.Length > 0) publicImports.Add("type " + Import("BridgeInteractionContext"));
    var runtimeImports = new List<string> { Import("connectView"), Import("disposable"), Import("viewReferences"), "type " + Import("BridgeMemberDecoders") };
    if (operationPlans.Length > 0) runtimeImports.Add("type " + Import("BridgeOperationCodec"));
    if (incrementalCollections.Count > 0) runtimeImports.Add(Import("defineCollection"));
    if (hasDeltaFields) runtimeImports.Add(Import("defineCollections"));
    if (textFields.Length > 0) runtimeImports.Add(Import("defineText"));
    if (interactions.Length > 0) runtimeImports.Add(Import("defineInteractions"));
    if (operationPlans.Length > 0) runtimeImports.Add(Import("bridgeOperations"));
    if (hasValidation) runtimeImports.Add(Import("decodeBridgeValidation"));
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
    TypeScriptModules.Add(typescriptPath, ts.ToString(), model);
    TypeScriptModules.Add(MockModulePath(typescriptPath), MockTypeScriptEmitter.Emit(new MockTypeScriptPlan(
        shortName, prefix,
        ownContracts.Select(contract => PageKind(shortName, contract)).ToArray(),
        valueProperties.Select(entry => new MockValueField(WireName(entry.Key), entry.Value)).ToArray(),
        contentBindings.Select(entry => new MockContentField(WireName(entry.Key),
            string.Join(" | ", entry.Value.Select(page => $"{Ref("MockReference")}<{JsonSerializer.Serialize(PageKind(page.Name, ContractFor(entry.Key)))}>")),
            contentCollections.ContainsKey(entry.Key),
            IsNullableContent(entry.Key))).ToArray(),
        MockDefaults(),
        checkedProperties.Select(WireName).ToArray(),
        properties.Where(property => property.SetMethod?.IsPublic == true && valueProperties.ContainsKey(property))
            .Select(property => new MockSetter($"set{property.Name}", WireName(property), TsPropertyType(property),
                checkedProperties.Contains(property) ? $"write{property.Name}" : null)).ToArray(),
        commands.Select(command =>
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            return new MockCommand(LowerFirst(name), LowerFirst(name), plan.HasArgument ? CommandInputType(plan) : null,
                plan.HasArgument ? plan.InputGraph?.EmitTypeScriptDecoder("wire") ?? "bridgeWire.string(wire)" : null,
                plan.HasArgument ? null : $"can{name}", plan.FailureGraph?.TypeScriptType(), plan.FailureGraph?.EncodeTypeScript("typed"),
                plan.Key is null ? null : plan.ParameterName);
        }).ToArray(),
        commands.Where(command => commandPlans[command].IsAsync).Select(command =>
        {
            var plan = commandPlans[command];
            var name = command.Name[..^"Command".Length];
            return new MockOperation(LowerFirst(name), LowerFirst(name), plan.HasArgument ? CommandInputType(plan) : "void",
                plan.ResultGraph?.TypeScriptType() ?? "void", plan.InputGraph?.EmitTypeScriptDecoder("wire"),
                plan.ResultGraph?.EncodeTypeScript("typed"), plan.Command?.Cardinality is RunicCommandResultCardinality.Stream,
                plan.FailureGraph?.TypeScriptType(), plan.FailureGraph?.EncodeTypeScript("typed"));
        }).ToArray(),
        interactions.Select(plan => new MockInteraction(LowerFirst(plan.Property.Name), plan.Input.TypeScriptType(),
            plan.Output.TypeScriptType(), plan.Input.EncodeTypeScript("typed"), plan.Output.EmitTypeScriptDecoder("wire"))).ToArray(),
        incrementalCollections.Select(entry =>
        {
            var graph = valueProperties[entry.Key];
            var keyMember = graph.Root.Element!.Members.Single(member => member.Property == entry.Value);
            return new MockCollection(WireName(entry.Key), graph.ItemTypeScriptType(), graph.EncodeItemTypeScript("typed"),
                graph.EmitItemTypeScriptDecoder("wire"), $"String({TsAccess("item", keyMember.WireName)})");
        }).ToArray(), $"{model.FullName}:{contractFingerprint}"), Path.GetFileNameWithoutExtension(typescriptPath)), model);
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

// The root namespaces of the ReactiveUI.Validation flavors whose IValidatableViewModel one of the
// types implements: ReactiveUI.Validation (RunicArtifex.ReactiveUI.Validation) and
// ReactiveUI.Validation.Reactive (its System.Reactive flavor).
static string[] ValidationContextFlavors(IEnumerable<Type> types) => types
    .SelectMany(ReactiveValidationConventions.FlavorsOf)
    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

// RUNICBRIDGE031: a type implements an IValidatableViewModel of neither flavor, such as one of another
// validation library or a copy in the application. The Bridge cannot read its ValidationContext, so its
// validation reaches the page only through INotifyDataErrorInfo, if at all.
static void WarnUnrecognizedValidatable(Type model, IEnumerable<Type> types)
{
    foreach (var type in types)
        foreach (var unrecognized in ReactiveValidationConventions.Unrecognized(type))
            Diagnostics.Warn(BridgeDiagnosticCodes.UnrecognizedValidation,
                $"{model.Name}{(type == model ? "" : $" ({type.Name})")}: {unrecognized.FullName} is not the IValidatableViewModel of "
                + "RunicArtifex.ReactiveUI.Validation or RunicArtifex.ReactiveUI.Validation.Reactive, so the Bridge does not read "
                + "or observe its ValidationContext. Implement INotifyDataErrorInfo to report its errors.", type);
}

// #478 (D033): a ReactiveValidationObject's GetErrors(null) returns the text of every property
// validation. The reader takes the ValidationContext instead: a validation with properties is a
// ValidationResult naming them, which BridgeValidation leaves to GetErrors(member) when they are
// presented; any other validation reports at the object's path.
static void AppendValidationContextReader(StringBuilder cs, IReadOnlyList<string> flavors)
{
    cs.AppendLine("    private static global::System.Collections.IEnumerable? ReadValidationContextErrors(object owner)");
    cs.AppendLine("    {");
    foreach (var flavor in flavors)
    {
        cs.AppendLine($"        if (owner is global::{flavor}.Abstractions.IValidatableViewModel {{ ValidationContext: {{ }} context }})");
        cs.AppendLine("        {");
        cs.AppendLine("            var errors = new global::System.Collections.Generic.List<global::System.ComponentModel.DataAnnotations.ValidationResult>();");
        cs.AppendLine("            foreach (var validation in context.Validations.Items)");
        cs.AppendLine("            {");
        cs.AppendLine("                if (validation.IsValid) continue;");
        cs.AppendLine($"                var properties = validation is global::{flavor}.Components.Abstractions.IValidatesProperties {{ PropertyCount: > 0 }} validatesProperties");
        cs.AppendLine("                    ? global::System.Linq.Enumerable.ToArray(validatesProperties.Properties) : null;");
        cs.AppendLine("                errors.Add(new(validation.Text?.ToSingleLine(\" \") ?? string.Empty, properties));");
        cs.AppendLine("            }");
        cs.AppendLine("            return errors;");
        cs.AppendLine("        }");
    }
    cs.AppendLine("        return null;");
    cs.AppendLine("    }");
    cs.AppendLine();
    // A rule that names no member raises neither ErrorsChanged nor PropertyChanged; the Bridge observes the context.
    cs.AppendLine("    private static global::System.IDisposable? ObserveValidationContext(object owner, global::System.Action changed)");
    cs.AppendLine("    {");
    foreach (var flavor in flavors)
    {
        // ValidationStatusChange activates the context, which a disposed one may refuse: then there is nothing to observe.
        cs.AppendLine($"        if (owner is global::{flavor}.Abstractions.IValidatableViewModel {{ ValidationContext: {{ }} context }})");
        cs.AppendLine("        {");
        cs.AppendLine("            try { return global::Runic.Application.Views.Generated.BridgeValidation.Observe(context.ValidationStatusChange, changed); }");
        cs.AppendLine("            catch (global::System.ObjectDisposedException) { return null; }");
        cs.AppendLine("        }");
    }
    cs.AppendLine("        return null;");
    cs.AppendLine("    }");
    cs.AppendLine();
    cs.AppendLine("    private static readonly global::Runic.Application.Views.Generated.BridgeValidationReader ValidationReader =");
    cs.AppendLine("        new(ReadValidationContextErrors, ObserveValidationContext);");
    cs.AppendLine();
}

static string MockModulePath(string typescriptPath) =>
    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(typescriptPath))!, $"{Path.GetFileNameWithoutExtension(typescriptPath)}.mock.ts");

// A command's declared failure type (W130-029): at most one [RunicFailure], on
// the command property or its [RelayCommand] method, naming a value type the
// Bridge can encode. Errors in the type's shape are RUNICBRIDGE003 at
// {Model}.{Command}.failure.
// The key resolver of a RunicCommandKey command, or null (#472).
static GeneratedKeyPlan? CommandKey(Type model, PropertyInfo command, MemberInfo source, MvvmCommand? library,
    RunicCommandInputAttribute? plainInput, NullabilityInfoContext nullability, bool honorJsonIgnore)
{
    var declarations = BridgeCommandSources.KeyDeclarations(model, command);
    if (declarations.Count == 0) return null;
    var name = $"{model.Name}.{command.Name}";
    if (declarations.Count > 1)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: declare RunicCommandKey once, on the command property or on its [RelayCommand] method, not on both.", source);
    var (declaredOn, resolverName) = declarations[0];
    MemberInfo location = declaredOn == "method" ? source : command;
    if (plainInput is not null)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: RunicCommandKey replaces RunicCommandInput, whose key is the resolver's parameter. Declare one of them.", location);
    if (library is { Input: null })
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: RunicCommandKey resolves a command's parameter, but this command takes none.", location);
    var methods = BridgeCommandSources.KeyResolvers(model, resolverName);
    if (methods.Count == 0)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: the RunicCommandKey resolver '{resolverName}' is not a method of {model.Name}.", location);
    if (methods.Count > 1)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: the RunicCommandKey resolver {model.Name}.{resolverName} is overloaded. Name a method with one signature.", methods[^1]);
    var resolver = methods[0];
    if (!(resolver.IsPublic || resolver.IsAssembly || resolver.IsFamilyOrAssembly))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: the RunicCommandKey resolver {model.Name}.{resolverName} must be public or internal, so the generated Bridge can call it.", resolver);
    var returned = resolver.ReturnType;
    var isAsync = returned.IsGenericType && (returned.GetGenericTypeDefinition() == typeof(Task<>) || returned.GetGenericTypeDefinition() == typeof(ValueTask<>));
    var resolved = isAsync ? returned.GenericTypeArguments[0] : returned;
    var parameters = resolver.GetParameters();
    var takesToken = isAsync && parameters.Length == 2 && parameters[1].ParameterType == typeof(CancellationToken);
    if (resolver.IsGenericMethodDefinition || parameters.Length != (takesToken ? 2 : 1)
        || parameters[0].ParameterType.IsByRef || parameters[0].IsOut || parameters[0].ParameterType == typeof(CancellationToken))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: the RunicCommandKey resolver {model.Name}.{resolverName} must take the key as its only parameter, and an asynchronous resolver may add a CancellationToken.", resolver);
    var parameter = library?.Input ?? Nullable.GetUnderlyingType(resolved) ?? resolved;
    if (resolved == typeof(void) || typeof(Task).IsAssignableFrom(resolved) || resolved.IsByRef
        || !parameter.IsAssignableFrom(Nullable.GetUnderlyingType(resolved) ?? resolved))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.CommandKey,
            $"{name}: the RunicCommandKey resolver {model.Name}.{resolverName} returns {BridgeTypeGraph.CSharpType(returned).Replace("global::", "", StringComparison.Ordinal)}; it must return the command's parameter{(library is null ? "" : $" {BridgeTypeGraph.CSharpType(parameter).Replace("global::", "", StringComparison.Ordinal)}")}, or null when the key names no object, directly or as Task<T> or ValueTask<T>.", resolver);
    var keyGraph = BridgeTypeGraph.Discover(parameters[0].ParameterType, nullability.Create(parameters[0]),
        $"{name}.key", resolver, honorJsonIgnore, BridgeValueDirection.FromPage);
    return new GeneratedKeyPlan(resolver, keyGraph, parameter, isAsync, takesToken);
}

static BridgeTypeGraph? FailureGraph(Type model, PropertyInfo command, MemberInfo source)
{
    var declarations = BridgeCommandSources.FailureDeclarations(model, command);
    if (declarations.Count == 0) return null;
    if (declarations.Count > 1)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Failure,
            $"{model.Name}.{command.Name}: declare RunicFailure once, on the command property or on its [RelayCommand] method, not on both.", source);
    var (declaredOn, failure) = declarations[0];
    MemberInfo location = declaredOn == "method" ? source : command;
    if (failure == typeof(object) || typeof(Exception).IsAssignableFrom(failure) || Nullable.GetUnderlyingType(failure) is not null)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Failure,
            $"{model.Name}.{command.Name}: the failure type {BridgeTypeGraph.CSharpType(failure).Replace("global::", "", StringComparison.Ordinal)} must be a Bridge value type other than object, an exception or Nullable<T>.", location);
    return BridgeTypeGraph.Discover(failure, rootPath: $"{model.Name}.{command.Name}.failure", origin: location,
        honorJsonIgnore: BridgeDtoContract.HonorsJsonIgnore(model.Assembly), direction: BridgeValueDirection.ToPage);
}

// A property that a command without an argument declares as its availability, such as CanCreate for
// [RelayCommand(CanExecute = nameof(CanCreate))] Create, is that command's can<Name> state already: the
// snapshot writes the command's CanExecute, which reads the property, and a change of the property
// publishes a new snapshot. It is not written a second time under the same name (#305).
static bool IsCommandAvailability(Type model, PropertyInfo property, PropertyInfo[] commands) =>
    property.PropertyType == typeof(bool) && commands.Any(command =>
        command.Name.EndsWith("Command", StringComparison.Ordinal)
        && WireName(property) == "can" + command.Name[..^"Command".Length]
        && MvvmCodegenConventions.All.Select(convention => InspectCommandOrNull(model, command, convention)).FirstOrDefault(candidate => candidate is not null)
            is { Input: null } library
        && library.AvailabilityMember == property.Name);

// A shape error is reported when the command itself is generated.
static MvvmCommand? InspectCommandOrNull(Type model, PropertyInfo command, MvvmCodegenConvention convention)
{
    try
    {
        return convention.InspectCommand(command);
    }
    catch (NotSupportedException)
    {
        return null;
    }
}

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
            (MemberInfo?)CommandSourceMethod(model, command) ?? command);
    }
}

// The method an MVVM library generated the command from: CommunityToolkit
// generates SaveCommand from [RelayCommand] Save, SaveAsync or OnSave. The
// method carries the parameter name, documentation and the source line that
// the generated property lacks. The lookup is shared with BridgeContractShape,
// which fingerprints failure declarations on the method.
static MethodInfo? CommandSourceMethod(Type model, PropertyInfo command) =>
    BridgeCommandSources.SourceMethod(model, command);

// A generated command property's comment only says which method it wraps,
// so such a command uses its method's documentation alone. Without a source
// method, the command's MVVM convention chooses the property's comment.
static string? CommandDocumentation(PropertyInfo command, MethodInfo? source, MvvmCommand? library) =>
    source is not null ? XmlDocumentation.Summary(source)
    : library is not null ? library.PropertyDocumentation(command) : XmlDocumentation.Summary(command);

// The TypeScript parameter of a command with an argument: its source method's
// parameter name, else the argument's C# type name, else "input".
static string CommandParameterName(MethodInfo? source, GeneratedCommandPlan plan)
{
    if (!plan.HasArgument) return "input";
    var candidate = plan.Key is { } key ? key.Resolver.GetParameters()[0].Name
        : source?.GetParameters().FirstOrDefault(parameter => parameter.ParameterType != typeof(CancellationToken))?.Name;
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
    // Generated sources are shared between Windows and Unix checkouts.
    content = content.ReplaceLineEndings("\n");
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
/// Diagnostic IDs. Each one has an entry in docs/diagnostics.md, which every
/// report links to at the release tag, with a row in its overview table, and an entry in All.
/// </summary>
static class BridgeDiagnosticCodes
{
    /// <summary>Invalid generator invocation or build configuration, or output that cannot be written.</summary>
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
    /// <summary>A RunicFailure declaration is misplaced, repeated or names an unsupported type.</summary>
    internal const string Failure = "RUNICBRIDGE012";
    /// <summary>Warning: a ViewModel that a content slot or navigation region admits has no View.</summary>
    internal const string MissingView = "RUNICBRIDGE013";
    /// <summary>Warning: types were skipped because an assembly other than a Windows Desktop one could not be loaded.</summary>
    internal const string SkippedTypes = "RUNICBRIDGE020";
    /// <summary>An internal generator error: an exception that is not one of the diagnostics above.</summary>
    internal const string Internal = "RUNICBRIDGE024";
    /// <summary>A ReactiveUI command or interaction without the Runic ReactiveUI adapter of its flavor.</summary>
    internal const string ReactiveUiAdapter = "RUNICBRIDGE025";
    // Reported by Runic.Application.Views.targets, as are RUNICBRIDGE001 for a missing generator
    // and RUNICBRIDGE024 for a generator run that failed without reporting an error.
    /// <summary>The frontend directory or its package.json is missing.</summary>
    internal const string FrontendMissing = "RUNICBRIDGE014";
    /// <summary>Warning: a project declares Views, but generation is off.</summary>
    internal const string GenerationOff = "RUNICBRIDGE015";
    /// <summary>The frontend install or build command failed without a located error.</summary>
    internal const string FrontendBuild = "RUNICBRIDGE016";
    /// <summary>A build property or item uses a former name.</summary>
    internal const string FormerName = "RUNICBRIDGE017";
    /// <summary>A referenced assembly already declares the generated composition type.</summary>
    internal const string CompositionConflict = "RUNICBRIDGE018";
    /// <summary>Warning: the build cannot tell which host's Views client to copy.</summary>
    internal const string ClientHost = "RUNICBRIDGE019";
    /// <summary>The external node_modules could not be linked.</summary>
    internal const string NodeModulesLink = "RUNICBRIDGE021";
    /// <summary>NoBuild=true finds no generated Bridge code to reuse.</summary>
    internal const string NoBuildReuse = "RUNICBRIDGE022";
    /// <summary>The application references Runic ReactiveUI adapters of both ReactiveUI flavors.</summary>
    internal const string ReactiveUiFlavors = "RUNICBRIDGE023";
    /// <summary>The frontend declares dependencies but has no lock file for the default install.</summary>
    internal const string FrontendLock = "RUNICBRIDGE026";
    /// <summary>A RunicCommandKey declaration is misplaced, or its resolver is missing or has an unsupported shape.</summary>
    internal const string CommandKey = "RUNICBRIDGE027";
    /// <summary>Warning: a RunicOperationRetirement declaration reaches no operation.</summary>
    internal const string OperationRetirement = "RUNICBRIDGE028";
    /// <summary>Warning: RunicApplicationFrontendPackageManager is not npm, pnpm or bun.</summary>
    internal const string PackageManager = "RUNICBRIDGE029";
    /// <summary>Warning: RunicApplicationFrontendDelivery=Embedded cannot apply, so the build copies the frontend.</summary>
    internal const string FrontendDelivery = "RUNICBRIDGE030";
    /// <summary>Warning: a type implements an IValidatableViewModel the Bridge does not recognize.</summary>
    internal const string UnrecognizedValidation = "RUNICBRIDGE031";
    /// <summary>Warning: an observable collection of DTO rows is not a RunicCollection.</summary>
    internal const string CollectionWithoutKey = "RUNICBRIDGE032";

    // Every code with its severity and title, in ID order: the source of the machine-readable list
    // (--list-diagnostics, docs/runicbridge-diagnostics.json). eng/diagnostics.test.mjs checks the
    // list against the docs/diagnostics.md overview table, whose Problem column is the title.
    internal static readonly (string Id, string Severity, string Title)[] All =
    [
        (Invocation, "error", "Invalid generator invocation or build configuration, generated output that cannot be written, or a missing generator"),
        (Aot, "error", "A CommunityToolkit ObservableValidator ViewModel in a Native AOT publish"),
        (ValueType, "error", "A value type that is not a supported bridge value"),
        (NameCollision, "error", "Two generated names collide, or a command's client member is reserved in TypeScript"),
        (AssemblyLoad, "error", "An assembly, a Runic View or a bridged member's type could not be loaded"),
        (View, "error", "No Window or View class, or an invalid class or View contract"),
        (ViewModel, "error", "A ViewModel class has an unsupported shape"),
        (State, "error", "A state property, ViewModel content member or append-only text has an unsupported shape"),
        (Command, "error", "A command has an unsupported name or shape"),
        (Collection, "error", "A [RunicCollection] member is not a keyed collection of DTO rows"),
        (Interaction, "error", "A ReactiveUI interaction is not a get-only property"),
        (Failure, "error", "A [RunicFailure] declaration is misplaced, repeated or names an unsupported type"),
        (MissingView, "warning", "A ViewModel that a content slot admits has no View"),
        (FrontendMissing, "error", "The frontend directory or its package.json is missing"),
        (GenerationOff, "warning", "A project declares Views, but generation is off"),
        (FrontendBuild, "error", "The frontend install or build command failed without a located error"),
        (FormerName, "error", "A build property or item uses a former name"),
        (CompositionConflict, "error", "A referenced assembly already declares the generated composition type"),
        (ClientHost, "warning", "The build cannot tell which host's Views client to copy"),
        (SkippedTypes, "warning", "Types were skipped because an assembly they need could not be loaded"),
        (NodeModulesLink, "error", "The external node_modules could not be linked"),
        (NoBuildReuse, "error", "NoBuild=true finds no generated Bridge code to reuse"),
        (ReactiveUiFlavors, "error", "The application references Runic ReactiveUI adapters of both ReactiveUI flavors"),
        (Internal, "error", "An internal generator error"),
        (ReactiveUiAdapter, "error", "A ReactiveUI command or interaction without the Runic ReactiveUI adapter of its flavor"),
        (FrontendLock, "error", "The frontend declares dependencies but has no lock file for the default install"),
        (CommandKey, "error", "A [RunicCommandKey] declaration is misplaced, or its resolver is missing or has an unsupported shape"),
        (OperationRetirement, "warning", "A [RunicOperationRetirement] declaration reaches no operation"),
        (PackageManager, "warning", "RunicApplicationFrontendPackageManager is not npm, pnpm or bun"),
        (FrontendDelivery, "warning", "RunicApplicationFrontendDelivery is unknown, or Embedded cannot apply"),
        (UnrecognizedValidation, "warning", "A type implements an IValidatableViewModel that is not ReactiveUI.Validation's"),
        (CollectionWithoutKey, "warning", "An observable collection of DTO rows is not a [RunicCollection]"),
    ];

    // Schema version 1: { schemaVersion, diagnostics: [{ id, severity: "error" | "warning", title }] }. A later
    // version only adds members; the set of IDs and of severities is open.
    internal static string WriteList()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartArray("diagnostics");
            foreach (var (id, severity, title) in All)
            {
                writer.WriteStartObject();
                writer.WriteString("id", id);
                writer.WriteString("severity", severity);
                writer.WriteString("title", title);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray()).ReplaceLineEndings("\n") + "\n";
    }
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

    private static readonly List<string> ReportedWarnings = [];

    // The cache records them, so a skipped generation repeats them.
    internal static IReadOnlyList<string> Warnings => ReportedWarnings;

    // Every exception is reported with a code: a failure that is no diagnostic of
    // its own is RUNICBRIDGE024 rather than an unhandled crash. Only a process that
    // cannot continue is left to the runtime.
    internal static bool IsReportable(Exception error) => error is not (OutOfMemoryException or StackOverflowException);

    /// <summary>Writes an MSBuild canonical warning; generation continues.</summary>
    internal static void Warn(string code, string message, MemberInfo? member)
    {
        var location = SourceLocator.Find(member);
        message = WithHelpLink(code, message);
        var line = location is { } at
            ? $"{at.Path}({at.Line},{at.Column}): warning {code}: {message}"
            : $"warning {code}: {message}";
        ReportedWarnings.Add(line);
        Console.Error.WriteLine(line);
    }

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
            // A wrong argument or build property, or output the generator cannot write.
            ArgumentException or IOException or UnauthorizedAccessException => BridgeDiagnosticCodes.Invocation,
            _ => BridgeDiagnosticCodes.Internal,
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
        if (code == BridgeDiagnosticCodes.Internal)
            message = $"internal generator error: {error.GetType().FullName}: {message} Report it with the ViewModel that triggers it and the stack trace that follows.";
        // Name the ViewModel when the message does not already start with it.
        if (model is not null && !message.StartsWith(model.Name, StringComparison.Ordinal)
            && !message.StartsWith(model.FullName ?? model.Name, StringComparison.Ordinal))
            message = $"{model.FullName}: {message}";
        var location = SourceLocator.Find(member);
        message = WithHelpLink(code, message);
        Console.Error.WriteLine(location is { } at
            ? $"{at.Path}({at.Line},{at.Column}): error {code}: {message}"
            : $"error {code}: {message}");
        // Plain lines, which the build logs as messages below the error.
        if (code == BridgeDiagnosticCodes.Internal && error.StackTrace is { } stackTrace)
            Console.Error.WriteLine(stackTrace);
    }

    /// <summary>
    /// Throws an exception that no diagnostic covers for the ViewModel that
    /// RUNIC_BRIDGE_CODEGEN_TEST_INTERNAL_ERROR names, so the generator tests can
    /// check how an internal error is reported. Not a setting.
    /// </summary>
    internal static void InjectInternalErrorForTests(Type model)
    {
        if (Environment.GetEnvironmentVariable("RUNIC_BRIDGE_CODEGEN_TEST_INTERNAL_ERROR") is { Length: > 0 } name
            && string.Equals(name, model.FullName, StringComparison.Ordinal))
            throw new InvalidOperationException("Injected internal error.");
    }

    /// <summary>Appends the diagnostic's catalog entry at this release's tag.</summary>
    internal static string WithHelpLink(string code, string message) =>
        $"{message} See {Runic.Application.Views.Codegen.RunicReleaseLinks.Diagnostic(code)}";
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

    // Generated client members that are valid C# names but break the client
    // object type or object literal, with the reason and a suggested rename.
    // A command is the only member whose bare name becomes a client member.
    internal static readonly Dictionary<string, (string Reason, string Suggestion)> ReservedClientMemberNames = new(StringComparer.Ordinal)
    {
        ["new"] = ("declares a construct signature in a TypeScript object type, so the generated client does not compile", "Create"),
        ["constructor"] = ("replaces the client's Object.prototype.constructor, which frameworks and type checks read", "Construct"),
        ["then"] = ("makes the connected client thenable, so 'await connect…()' calls it instead of resolving to the client", "Continue"),
        ["__proto__"] = ("sets the prototype of the client object literal instead of defining a member", "Prototype"),
        ["toJSON"] = ("replaces the client's JSON.stringify serialization", "Export"),
        ["toString"] = ("replaces the client's string conversion", "Format"),
        ["valueOf"] = ("replaces the client's primitive conversion", "Evaluate"),
    };

    // Encoders declare bridgeNullable<N>, bridgeUnion<N> and
    // bridgeDecodedUnion<N> locals around the argument expression.
    internal static bool IsReservedParameterName(string name) => ReservedParameterNames.Contains(name)
        || name.StartsWith("bridgeNullable", StringComparison.Ordinal) || name.StartsWith("bridgeUnion", StringComparison.Ordinal)
        || name.StartsWith("bridgeDecodedUnion", StringComparison.Ordinal);

    private static readonly List<(string Path, string Text)> Pending = [];

    // Assigns the module's own bindings (TypeScriptModuleBindings) before the
    // named types, which avoid every name the resolved modules declare.
    internal static void Add(string path, string text, MemberInfo? owner = null) =>
        Pending.Add((path, TypeScriptModuleBindings.Resolve(text, path, owner)));

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
        // Match the repository and generated-client LF contract on every host.
        content = content.ReplaceLineEndings("\n");
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

// Every supported command shares input graphs and operation emission. Only the
// library-specific execution contract remains in its MVVM convention module.
sealed record GeneratedCommandPlan(
    bool HasStringArgument,
    bool IsAsync,
    string? LegacyDescriptor = null,
    MvvmCommand? Command = null,
    BridgeTypeGraph? InputGraph = null,
    BridgeTypeGraph? ResultGraph = null,
    bool IsPlainICommand = false,
    BridgeTypeGraph? FailureGraph = null)
{
    internal bool HasArgument => InputGraph is not null || HasStringArgument;
    /// <summary>The generated TypeScript parameter name of the command argument.</summary>
    internal string ParameterName { get; init; } = "input";
    /// <summary>The key resolver of a RunicCommandKey command, whose <see cref="InputGraph"/> is the key's.</summary>
    internal GeneratedKeyPlan? Key { get; init; }
    /// <summary>
    /// The C# type the command's own parameter is cast to: the input's, or the resolved parameter of a
    /// RunicCommandKey command, whose input is the key.
    /// </summary>
    internal string? ParameterCSharpType => Key is { } key ? BridgeTypeGraph.CSharpType(key.Parameter) : InputGraph?.RootCSharpType();
    /// <summary>True when the snapshot carries is&lt;Name&gt;Executing.</summary>
    internal bool HasExecutionState => Command?.HasExecutionState == true;

    /// <summary>The operation's RunicOperationRetirement; Keep, the default, is not emitted.</summary>
    internal RunicOperationRetirement Retirement { get; init; }

    internal static GeneratedCommandPlan Plain(BridgeTypeGraph input) =>
        new(HasStringArgument: false, IsAsync: false, InputGraph: input, IsPlainICommand: true);

    // A declared failure adds the generated encoder: the declared type's
    // canonical JSON, or null so the runtime reports any other value as failed.
    internal string DescriptorFor(PropertyInfo property, string modelType)
    {
        var descriptor = DescriptorCore(property, modelType);
        if (FailureGraph is not null)
        {
            if (!descriptor.EndsWith(')')) throw new InvalidOperationException("A command descriptor must be a constructor call.");
            descriptor = descriptor[..^1] + $", EncodeFailure: failure => failure is {FailureGraph.RootCSharpType()} declared ? global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}FailureCodec.Write(writer, declared)) : null)";
        }
        // The member name of command, can and start requests (specs/application, "Routes and members").
        var initializer = $"WireName = \"{char.ToLowerInvariant(property.Name[0])}{property.Name[1..^"Command".Length]}\"";
        if (Command?.DescriptorInitializer(property, this) is { } library) initializer += ", " + library;
        if (Key is { } key)
            initializer += ", " + key.Initializer(modelType);
        if (Retirement is not RunicOperationRetirement.Keep)
            initializer += $", Retirement = global::Runic.Application.Views.RunicOperationRetirement.{Retirement}";
        return CommandDescriptorSyntax.WithInitializer(descriptor, initializer);
    }

    private string DescriptorCore(PropertyInfo property, string modelType)
    {
        if (LegacyDescriptor is not null) return LegacyDescriptor;
        if (Command is { } library) return library.Descriptor(property, modelType, this);
        if (!IsPlainICommand) throw new InvalidOperationException("Command plan has no descriptor.");
        var plainGraph = InputGraph ?? throw new InvalidOperationException("Plain ICommand is missing its input graph.");
        var plainInputType = plainGraph.RootCSharpType();
        var name = property.Name[..^"Command".Length];
        return $"new global::Runic.Application.Views.Generated.CommandDescriptor<{modelType}>(\"{name}\", vm => (object)vm.{property.Name}, ReadInput: input => {property.Name}InputCodec.Read(input), EncodeArgument: argument => global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({plainInputType})argument!)))";
    }
}

// The resolver of a RunicCommandKey command (#472): a ViewModel method from the key to the command's
// parameter, or null when the key names no object.
sealed record GeneratedKeyPlan(MethodInfo Resolver, BridgeTypeGraph KeyGraph, Type Parameter, bool IsAsync, bool TakesToken)
{
    // The CommandDescriptor's ResolveArgument or ResolveArgumentAsync assignment.
    internal string Initializer(string modelType)
    {
        var target = Resolver.IsStatic ? modelType : "vm";
        var key = $"({KeyGraph.RootCSharpType()})key!";
        return IsAsync
            ? $"ResolveArgumentAsync = async (vm, key, token) => await {target}.{Resolver.Name}({key}{(TakesToken ? ", token" : "")}).ConfigureAwait(false)"
            : $"ResolveArgument = (vm, key) => {target}.{Resolver.Name}({key})";
    }
}

// The conventions describe a command as a constructor call with C# named arguments, such as
// new CommandDescriptor<T>("Save", vm => vm.SaveCommand, ExecuteResultAsync: ..., CanExecute: ...).
// CommandDescriptor<T> takes only the name and the getter; everything else is an init property, so a later
// release can add members without changing its constructor. This rewrites the call into that shape:
// new CommandDescriptor<T>("Save", vm => vm.SaveCommand) { ExecuteResultAsync = ..., CanExecute = ... }.
internal static class CommandDescriptorSyntax
{
    // The init properties, in the order of the former positional parameters after Name and Get.
    private static readonly string[] Positional =
        ["ExecuteAsync", "ReadArgument", "ExecuteResultAsync", "EncodeArgument", "CanExecute", "Subscribe", "CreateStream",
            "ExecuteStreamAsync", "EncodeFailure"];

    internal static string WithInitializer(string call, string? extraInitializer)
    {
        var open = call.IndexOf('(');
        if (open < 0 || !call.EndsWith(')')) throw new InvalidOperationException("A command descriptor must be a constructor call.");
        var arguments = SplitArguments(call[(open + 1)..^1]);
        if (arguments.Count < 2) throw new InvalidOperationException("A command descriptor needs a name and a getter.");
        var assignments = new List<string>();
        for (var index = 2; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var colon = NamedArgumentColon(argument);
            assignments.Add(colon < 0
                ? $"{Positional[index - 2]} = {argument}"
                : $"{argument[..colon].Trim()} = {argument[(colon + 1)..].Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(extraInitializer)) assignments.Add(extraInitializer);
        var constructor = $"{call[..(open + 1)]}{arguments[0]}, {arguments[1]})";
        return assignments.Count == 0 ? constructor : $"{constructor} {{ {string.Join(", ", assignments)} }}";
    }

    // The colon of "Name: value", or -1 for a positional argument.
    private static int NamedArgumentColon(string argument)
    {
        var index = 0;
        while (index < argument.Length && (char.IsLetterOrDigit(argument[index]) || argument[index] == '_')) index++;
        return index > 0 && index < argument.Length && argument[index] == ':' ? index : -1;
    }

    // Splits top-level arguments, skipping commas inside brackets, strings and character literals.
    private static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character is '"' or '\'')
            {
                var verbatim = character == '"' && index > 0 && text[index - 1] == '@';
                for (index++; index < text.Length && text[index] != character; index++)
                    if (text[index] == '\\' && !verbatim) index++;
                continue;
            }
            // Generic argument lists only appear inside parentheses (casts, calls), and '>' is also part of '=>'.
            if (character is '(' or '{' or '[') depth++;
            else if (character is ')' or '}' or ']') depth--;
            else if (character == ',' && depth == 0)
            {
                result.Add(text[start..index].Trim());
                start = index + 1;
            }
        }
        result.Add(text[start..].Trim());
        return result;
    }
}
