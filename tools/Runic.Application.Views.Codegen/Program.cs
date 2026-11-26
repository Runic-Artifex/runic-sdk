using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Windows.Input;
using Runic.Application.Views;

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
    // D082: the web bundle that a build writes for another machine's publish.
    else if (args.Length == 2 && args[0] == "--web-bundle-write")
    {
        WebBundle.Write(args[1]);
    }
    else if (args.Length >= 1 && args[0] == "--generate")
    {
        var aot = false;
        var optional = false;
        var analyzerWarnings = false;
        var clientLibrary = false;
        var clientModules = new List<ClientModuleMapping>();
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
                // The compile that follows runs Runic.Application.Views.Analyzers, which reports the warnings of
                // BridgeDiagnosticCodes.AnalyzerWarnings for this assembly's own declarations (Diagnostics.Warn).
                case "--analyzer-warnings": analyzerWarnings = true; break;
                // Library mode (#1318, ClientModules): the TypeScript is also an importable module, and the Views are
                // required.
                case "--client-library": clientLibrary = true; break;
                // Application mode (#1318, ClientModules): <assembly>=<specifier> of a client library whose Views the
                // application adds and whose generated TypeScript it imports instead of writing copies.
                case "--client-module" when i + 1 < args.Length:
                    clientModules.Add(ClientModules.ParseMapping(args[++i]));
                    break;
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
        // The TypeScript clients are the emitter's (@runic-artifex/views/codegen, R7 G6): it reads the contract IR that
        // this generation writes beside the C#.
        if (positional.Length != 3)
            throw new ArgumentException("Usage: BridgeCodegen --generate <model.dll> <C# output dir> [--aot] [--optional] [--client-library] [--client-module <assembly>=<specifier>]... [--di-composition <namespace.type>] [--path-map <map>] [--framework <shared framework>]...");
        // A client library generates its Views' clients for other applications, so it must declare them.
        if (clientLibrary) optional = false;
        foreach (var group in clientModules.GroupBy(mapping => mapping.Assembly, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ClientModuleItem,
                $"{group.Key} is mapped more than once by RunicApplicationFrontendClientModule ({string.Join(" and ", group.Select(mapping => mapping.Specifier))}). Keep one item per assembly.");

        string[] cacheArguments = [$"aot={aot}", $"composition={compositionType}", $"path-map={pathMap}", $"analyzer-warnings={analyzerWarnings}",
            $"frameworks={string.Join(";", frameworks.Order(StringComparer.OrdinalIgnoreCase))}",
            .. MvvmCodegenConventions.All.SelectMany(convention => convention.CacheArguments),
            .. (clientLibrary ? (string[])["client-library=true"] : []),
            .. (clientModules.Count > 0
                ? (string[])[$"client-modules={string.Join(";", clientModules.Select(mapping => $"{mapping.Assembly}={mapping.Specifier}").Order(StringComparer.Ordinal))}"]
                : [])];
        if (BridgeGenerationCache.TryHit(positional[1], positional[2], cacheArguments, out var cachedWarnings))
        {
            foreach (var warning in cachedWarnings) Console.Error.WriteLine(warning);
            Console.WriteLine("Bridge generation is up to date.");
            return;
        }
        BridgeLoading.UseSharedFrameworks(frameworks, positional[1]);
        var assembly = Assembly.LoadFrom(Path.GetFullPath(positional[1]));
        if (analyzerWarnings) Diagnostics.AnalyzerAssembly = assembly;
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
        // Application mode: the mapped client libraries' Views, whose ViewModels' TypeScript the libraries own.
        if (clientModules.Count > 0)
        {
            ClientModules.Discover(assembly, positional[1], clientModules, viewTypes, ViewModelFor, model => LowerFirst(PublicName(model)));
            if (Diagnostics.HasErrors) return;
        }
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
            // navigator) builds without bridge output. Remove stale generated C#
            // and the IR, and leave a marker so the build skips the TypeScript
            // emitter and the frontend steps (the build removes the clients the
            // emitter wrote before).
            Directory.CreateDirectory(positional[2]);
            foreach (var path in Directory.GetFiles(positional[2], "*.g.cs"))
                if (GeneratedOutput.IsGenerated(path)) File.Delete(path);
            File.WriteAllText(Path.Combine(positional[2], "RunicBridge.NoViews.marker"), "");
            BridgeSourceMap.Remove(positional[2]);
            ContractIr.Remove(positional[2]);
            ClientSymbols.Remove(positional[2]);
            Console.WriteLine($"{assembly.GetName().Name}: no Runic Window/View classes found; skipping Bridge generation.");
            return;
        }
        if (models.Length == 0)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.View,
                $"{assembly.GetName().Name}: no Runic Window/View classes found, but Bridge generation is required here. " +
                "Declare a Window (public sealed partial class MainWindow : RunicWindow<MainViewModel>). A project that only uses " +
                "another project's generated Bridges, such as a test project, sets RunicApplicationFrontendGenerationEnabled=false.");
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

        // [RunicSharedClient] interfaces that the ViewModels implement (#839).
        var sharedClients = SharedClients.Discover(models, presentationKinds);
        if (Diagnostics.HasErrors) return;
        ClientModules.AssignSharedClients(sharedClients);
        if (Diagnostics.HasErrors) return;

        Directory.CreateDirectory(positional[2]);
        // View partials and ViewModel bridges use distinct suffixes and full
        // type names: MainWindow and MainWindowViewModel, or two ItemViews in
        // different namespaces, must not overwrite each other's file.
        // A mapped client library's own build writes its View partials. The
        // emitter checks the TypeScript files the same way.
        var csharpOutputs = models.Select(entry => (Path: BridgeFileName(entry.Model), Owner: entry.Model.FullName!))
            .Concat(viewTypes.Values.SelectMany(variants => variants).Where(view => view.Assembly == assembly)
                .Select(view => (Path: ViewFileName(view), Owner: view.FullName!)))
            .Append((Path: BridgeSharedCodecs.FileName, Owner: "the shared Bridge codecs"))
            .Concat(BridgeSupportFiles.All.Select(file => (Path: file, Owner: "the shared Bridge command helpers")))
            .Concat(clientLibrary ? [(Path: ClientModules.LibraryMarkerFile, Owner: "the client library marker")] : [])
            .ToArray();
        foreach (var group in csharpOutputs
            .GroupBy(output => output.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
                $"generated file '{group.Key}' would be written by {string.Join(" and ", group.Select(output => output.Owner))}. Rename one type.",
                models.FirstOrDefault(entry => group.Any(output => output.Owner == entry.Model.FullName)).Model);
        var expectedCsharp = csharpOutputs.Select(output => Path.GetFullPath(Path.Combine(positional[2], output.Path)))
            .ToHashSet(StringComparer.Ordinal);
        // Remove only stale generator output.
        foreach (var path in Directory.GetFiles(positional[2], "*.g.cs"))
            if (!Path.GetFileName(path).StartsWith("RunicBridgeComposition", StringComparison.Ordinal)
                && !expectedCsharp.Contains(Path.GetFullPath(path)) && GeneratedOutput.IsGenerated(path)) File.Delete(path);
        var contentRequired = new HashSet<Type>();
        foreach (var entry in models)
        {
            // Report every ViewModel's problem in one build rather than one
            // ViewModel per build.
            try
            {
                Diagnostics.InjectInternalErrorForTests(entry.Model);
                // The Bridge of a ViewModel that a mapped client library owns is generated as any other, since this
                // application's content switches construct it; its TypeScript is the library's.
                if (GenerateOne(entry.Model, Path.Combine(positional[2], BridgeFileName(entry.Model)), entry.Name, models, viewTypes))
                    contentRequired.Add(entry.Model);
                if (viewTypes.TryGetValue(entry.Model, out var views))
                    foreach (var view in views.Where(view => view.Assembly == assembly))
                        GenerateViewPartial(Path.Combine(positional[2], ViewFileName(view)), view, entry.Model, entry.Name);
            }
            catch (Exception error) when (Diagnostics.IsReportable(error))
            {
                Diagnostics.Report(error, entry.Model);
            }
        }
        // The Bridges generated so far, as before, even when another ViewModel failed.
        BridgeSharedCodecs.WriteAll(positional[2]);
        if (Diagnostics.HasErrors) return;
        SharedClients.Check(sharedClients);
        if (Diagnostics.HasErrors) return;
        if (clientLibrary) ClientModules.WriteLibraryMarker(positional[2]);
        var compositionPath = Path.Combine(positional[2], "RunicBridgeComposition.g.cs");
        if (compositionType is null)
        {
            if (File.Exists(compositionPath)) File.Delete(compositionPath);
        }
        else GenerateCompositionRegistration(compositionPath, compositionType, models, contentRequired,
            viewTypes.Values.SelectMany(variants => variants).Where(view => !IsWindow(view))
                .OrderBy(view => view.FullName, StringComparer.Ordinal));
        // The development source map, beside the generated C# in obj; recorded by the cache like the C#.
        BridgeSourceMap.Write(positional[2]);
        // The contract IR (specs/application/contract-ir.md), beside it. A mapping that owns no View imports nothing.
        ContractIr.Write(positional[2], ContractIr.Serialize(assembly.GetName().Name!, clientLibrary,
            [.. clientModules.Where(mapping => models.Any(entry => ClientModules.OwnerOf(entry.Model) == mapping))],
            SharedClients.ContractIr(ContractIr.PublishedAs), BridgeSourceMap.Serialize()));
        // The assemblies without a PDB, for the emitter's RUNICBRIDGE076.
        ClientSymbols.Write(positional[2], models.Select(entry => entry.Model), sharedClients.Select(shared => shared.Interface));
        BridgeGenerationCache.Save(positional[1], positional[2], cacheArguments, Diagnostics.Warnings);
    }
    else
    {
        throw new ArgumentException("Usage: BridgeCodegen --generate <model.dll> <C# output dir> [options], --composition-stub <type> <output.cs> [--references <file>] or --list-diagnostics [<file>]");
    }
}
catch (Exception error) when (Diagnostics.IsReportable(error))
{
    Diagnostics.Report(error);
}
finally
{
    // An error stops the build before the compiler, so the analyzer will not report the warnings left to it.
    if (Diagnostics.HasErrors) Diagnostics.WriteAnalyzerWarnings();
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

// D072.12: the construction of a development-only ViewModel's Bridge ([RunicDevelopmentOnly] on the ViewModel). A trimmed
// or NativeAOT publish turns RunicDevelopmentOnly.IsSupported off, a constant there, so the Bridge and what only it
// reaches are removed, and opening or presenting the ViewModel throws with its name.
static string ConstructBridge(Type model, string construction) =>
    model.GetCustomAttribute<RunicDevelopmentOnlyAttribute>(true) is null ? construction
        : $"({BridgeTableSupport.IsSupported} ? {construction} : throw global::Runic.Application.Views.RunicDevelopmentOnly.Removed(typeof(global::{model.FullName})))";

static void AppendNamespace(StringBuilder source, string? ns)
{
    if (ns is not null) source.AppendLine($"namespace {ns};");
}

// The ViewModel's bridged properties: public, declared, not [RunicIgnore], in explicit publication only the included
// ones (BridgePublication, #1320), and not indexers. The name-only selection comes first: the signature of a left-out
// property, which GetIndexParameters resolves, may need an assembly the generator cannot load, such as a WPF
// ImageSource. BridgeContractShape selects the same properties, so the Hot Reload fingerprint agrees.
static PropertyInfo[] BridgedProperties(Type model) =>
    BridgePublication.Candidates(model)
        .Where(property => property.GetIndexParameters().Length == 0)
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

// RunicOperationClose (#900): read as RunicOperationRetirement is, a declaration on a command property, else on the
// ViewModel class or a base class. Only an operation carries it; RUNICBRIDGE039 warns about a declaration that reaches
// none. Default emits nothing, so a Bridge without one is generated as before, and the TypeScript and contract hash
// never change.
static void ApplyOperationClose(Type model, IReadOnlyList<PropertyInfo> commands, Dictionary<PropertyInfo, GeneratedCommandPlan> commandPlans)
{
    const string Attribute = "Runic.Application.Views.RunicOperationCloseAttribute";
    const BindingFlags AllDeclared = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.Public | BindingFlags.NonPublic;
    RunicOperationClose? Declared(MemberInfo member)
    {
        if (BridgeAttributeNames.Find(member, Attribute)?.FixedArguments?.Invoke() is not [var argument]) return null;
        var value = Convert.ToInt32(argument, System.Globalization.CultureInfo.InvariantCulture);
        if (Enum.IsDefined(typeof(RunicOperationClose), value)) return (RunicOperationClose)value;
        Diagnostics.Warn(BridgeDiagnosticCodes.OperationClose,
            $"{model.Name}.{member.Name}: {value} is not a RunicOperationClose value; the declaration is ignored.", member);
        return null;
    }
    // A property declaration, also one on the base property that a command overrides.
    RunicOperationClose? OnProperty(PropertyInfo property)
    {
        for (var owner = property.DeclaringType; owner is not null && owner != typeof(object); owner = owner.BaseType)
            if (owner.GetProperty(property.Name, AllDeclared) is { } declared && Declared(declared) is { } value) return value;
        return null;
    }
    RunicOperationClose? onClass = null;
    for (var owner = model; owner is not null && owner != typeof(object) && onClass is null; owner = owner.BaseType)
        onClass = Declared(owner);
    var operations = commands.Where(command => commandPlans[command].IsAsync).ToHashSet();
    foreach (var command in operations)
        if ((OnProperty(command) ?? onClass) is { } close and not RunicOperationClose.Default)
            commandPlans[command] = commandPlans[command] with { Close = close };
    // A declaration that reaches no operation: on the ViewModel's own members, so a base class shared by several
    // ViewModels is reported once per ViewModel that declares it.
    foreach (var property in model.GetProperties(AllDeclared).OrderBy(property => property.MetadataToken))
    {
        if (!BridgeAttributeNames.Has(property, Attribute) || operations.Any(command => command.Name == property.Name)) continue;
        Diagnostics.Warn(BridgeDiagnosticCodes.OperationClose,
            $"{model.Name}.{property.Name}: RunicOperationClose applies only to a Bridge command that starts an operation "
            + "(an asynchronous CommunityToolkit or ReactiveUI command); it has no effect here.", property);
    }
    if (operations.Count == 0 && BridgeAttributeNames.Has(model, Attribute))
        Diagnostics.Warn(BridgeDiagnosticCodes.OperationClose,
            $"{model.Name}: RunicOperationClose on the class applies to its operations (asynchronous Bridge commands), but it has none; it has no effect here.", model);
}

// A RunicQueryCollection<TRow, TKey> member (query-collections/1): get-only, without the attributes of other collections,
// with DTO rows and a string, Guid or Int32 key. Its rows are discovered as a list of TRow, for the row codec and decoder.
static (BridgeTypeGraph Rows, Type Key) QueryCollectionPlan(Type model, PropertyInfo property, Type row, Type key, bool honorJsonIgnore)
{
    if (property.SetMethod?.IsPublic == true)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.QueryCollection,
            $"{model.Name}.{property.Name}: a RunicQueryCollection must be get-only; the page changes its query with setQuery.", property);
    if (property.GetCustomAttribute<RunicCollectionAttribute>(true) is not null || property.GetCustomAttribute<RunicAppendOnlyTextAttribute>(true) is not null)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.QueryCollection,
            $"{model.Name}.{property.Name}: a RunicQueryCollection has its own keys; remove [RunicCollection] or [RunicAppendOnlyText].", property);
    if (key != typeof(string) && key != typeof(Guid) && key != typeof(int))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.QueryCollection,
            $"{model.Name}.{property.Name}: a RunicQueryCollection key is a string, Guid or Int32, not {key.Name}.", property);
    var rows = BridgeTypeGraph.Discover(typeof(IReadOnlyList<>).MakeGenericType(row), rootPath: $"{model.Name}.{property.Name}",
        origin: property, honorJsonIgnore: honorJsonIgnore, direction: BridgeValueDirection.ToPage);
    if (rows.Root.Element is not { Kind: BridgeWireKind.Dto })
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.QueryCollection,
            $"{model.Name}.{property.Name}: the rows of a RunicQueryCollection are DTOs, not {row.Name}.", property);
    return (rows, key);
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
                && (member.PropertyType == typeof(string) || member.PropertyType == typeof(Guid) || member.PropertyType == typeof(int)
                    || member.PropertyType == typeof(long)));
        Diagnostics.Warn(BridgeDiagnosticCodes.CollectionWithoutKey,
            $"{model.Name}.{property.Name}: an observable collection of {row.NonNullableType.Name} rows that is not a [RunicCollection] "
            + "publishes every change by encoding and sending the whole list. Declare "
            + (key is null ? $"[RunicCollection(nameof({row.NonNullableType.Name}.Key))] with a string, Guid, Int32 or Int64 key property of the rows"
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
    cs.AppendLine("        " + ConstructBridge(model, $"new {BridgeTypeName(model, shortName)}(transport,\n"
        + $"            DataContext ?? throw new global::System.InvalidOperationException(\"{view.Name} has no DataContext.\"),\n"
        + "            route, content)") + ";");
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
    // RUNICBRIDGE038: the runtime checks the generated-code contract before any Bridge type loads, so a library that
    // another major release generated fails here with a rebuild message. The lambdas below construct the Bridges only
    // when a Window or View attaches one, so JIT compiling this method loads none of them.
    cs.AppendLine($"        global::Runic.Application.Views.Generated.BridgeGeneratedCode.RequireContract({Runic.Application.Views.Generated.BridgeGeneratedCode.ContractVersion}, typeof({parts[^1]}).Assembly);");
    foreach (var (model, name) in models)
    {
        var modelType = $"global::{model.FullName}";
        var bridgeType = BridgeTypeName(model, name);
        // GenerateOne decided whether the bridge constructor requires a content
        // session (ViewModel content, interactions or view requests). Only offer the
        // transport-only factory when it can actually construct the bridge.
        if (!contentRequired.Contains(model))
        {
            cs.AppendLine($"        services.AddScoped<global::System.Func<global::Runic.Application.Views.IBridgeTransport, {modelType}, global::System.IDisposable>>(");
            cs.AppendLine($"            _ => (transport, vm) => {ConstructBridge(model, $"new {bridgeType}(transport, vm)")});");
        }
        cs.AppendLine($"        services.AddScoped<global::System.Func<global::Runic.Application.Views.IBridgeTransport, global::Runic.Application.Views.WindowContentSession, {modelType}, global::System.IDisposable>>(");
        cs.AppendLine($"            _ => (transport, content, vm) => {ConstructBridge(model, $"new {bridgeType}(transport, vm, content: content)")});");
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
static bool GenerateOne(Type model, string csharpPath, string shortName,
    (Type Model, string Name)[]? knownModels = null,
    IReadOnlyDictionary<Type, List<Type>>? viewTypes = null)
{
    var plan = PlanView(model, shortName, knownModels ?? [(model, shortName)], viewTypes);
    // The View in the language-neutral contract IR (specs/application/contract-ir.md), written from the plan alone. Its
    // fingerprint (runic.views.fingerprint/1) is a hash of the View's IR, so the contracts that contain it, the
    // interactions' and view requests' and the operations' {view}:{fingerprint}, are known once the IR is.
    var view = ContractIrView(plan);
    var fingerprint = ContractIr.Fingerprint(view);
    ContractIr.Seal(view, fingerprint);
    plan.Seal(fingerprint);
    ContractIr.AddView(view);
    return EmitView(plan, csharpPath);
}

// Discovers and validates one ViewModel's Bridge contract (ViewPlan): what the C# emission and the contract IR, from
// which the emitter writes the TypeScript, read. Reports problems by throwing; writes nothing.
static ViewPlan PlanView(Type model, string shortName, (Type Model, string Name)[] knownModels,
    IReadOnlyDictionary<Type, List<Type>>? viewTypes)
{
    if (!typeof(INotifyPropertyChanged).IsAssignableFrom(model))
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName} must implement INotifyPropertyChanged.", model);
    if (model.IsNested || !model.IsClass || !model.IsPublic)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.FullName}: a Bridge ViewModel must be a public, top-level class.", model);

    PublicationChecks.Check(model, knownModels);
    var declared = BridgedProperties(model);
    // The reflection shape hash of the assembly's Views, which Hot Reload recomputes to see whether an edit changed the
    // contract; clients and operations use the IR fingerprint (GenerateOne).
    var contractShape = BridgeContractShape.Compute(model);
    var honorJsonIgnore = BridgeDtoContract.HonorsJsonIgnore(model.Assembly);
    var interactions = InteractionCodeEmitter.Discover(declared, model.Name, honorJsonIgnore);
    var requests = InteractionCodeEmitter.DiscoverRequests(declared, model.Name, honorJsonIgnore);
    var commands = declared.Where(property => typeof(ICommand).IsAssignableFrom(property.PropertyType)
        || MvvmCodegenConventions.All.Any(convention => InspectCommand(model, property, () => convention.InspectCommand(property)) is not null)).ToArray();
    var properties = declared.Except(commands).Except(interactions.Select(plan => plan.Property))
        .Except(requests.Select(plan => plan.Property))
        .Where(property => !IsCommandAvailability(model, property, commands)).ToArray();
    var duplicateWireName = properties.GroupBy(WireName, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
    if (duplicateWireName is not null)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
            $"{model.Name}: state properties {string.Join(" and ", duplicateWireName.Select(property => property.Name))} share the generated wire name '{duplicateWireName.Key}'.",
            duplicateWireName.Last());
    // [RunicSharedClient] (#839): the shared clients of the interfaces the ViewModel implements.
    var sharedClients = SharedClients.For(model);
    var nullability = new NullabilityInfoContext();
    if (properties.Length == 0 && commands.Length == 0 && interactions.Length == 0 && requests.Length == 0)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.ViewModel,
            $"{model.Name}: a ViewModel needs at least one state property, command, or interaction.", model);
    var contentProperties = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var contentCollections = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    // NavigationRegion<TContent> slots: content presented from the region's Current.
    var regionSlots = new HashSet<PropertyInfo>();
    // NavigationSelection slots: state whose wire value is the selection's SelectedIndex, or null for -1.
    var selectionSlots = new HashSet<PropertyInfo>();
    // field-paths/1: a query collection's rows come from a row source page by page; editing them is not supported yet.
    // The check goes by the type's name, before the collection's own checks, so it applies as soon as
    // RunicQueryCollection<TRow, TKey> exists.
    foreach (var editor in Runic.Application.Views.Generated.BridgeRowEditors.Declared(model))
        if (editor.GetCustomAttribute<RunicRowEditorAttribute>(true)!.Collection is { } named && model.GetProperty(named) is { } namedProperty
            && namedProperty.PropertyType.IsGenericType && namedProperty.PropertyType.GetGenericTypeDefinition().Name.StartsWith("RunicQueryCollection", StringComparison.Ordinal))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RowEditor,
                $"{model.Name}.{editor.Name}: [RunicRowEditor] names {named}, a query collection, whose rows cannot be edited from the page yet.", editor);
    // Region collections (region-collections/1): a [RunicRegionCollection] NavigationSelection presented with every
    // region's key and content, and the selected index.
    var regionCollections = new Dictionary<PropertyInfo, (Type Model, string Name)[]>();
    var valueProperties = new Dictionary<PropertyInfo, BridgeTypeGraph>();
    // RunicQueryCollection<TRow, TKey> members (query-collections/1): the graph of their rows, as a list of TRow, and the key.
    var queryCollections = new Dictionary<PropertyInfo, (BridgeTypeGraph Rows, Type Key)>();
    foreach (var property in properties)
    {
        if (property.GetMethod is null)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                $"{model.Name}.{property.Name}: a public getter is required.", property);
        if (BridgeContractShape.RegionCollection(property) is { } regionCollection)
        {
            if (!BridgeContractShape.IsNavigationSelection(property.PropertyType))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RegionCollection,
                    $"{model.Name}.{property.Name}: RunicRegionCollection applies to a NavigationSelection property, not to a {property.PropertyType.Name}.", property);
            if (property.SetMethod?.IsPublic == true)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RegionCollection,
                    $"{model.Name}.{property.Name}: a region collection must be get-only; its Add, SelectAsync and RemoveAsync, not the web view, change it.", property);
            var collectionContent = regionCollection.ContentType;
            if (collectionContent == typeof(object))
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RegionCollection,
                    $"{model.Name}.{property.Name}: a region collection of object has no known content. Name an interface or base class that the regions' ViewModels share.", property);
            var collectionModels = knownModels.Where(entry => entry.Model != model && collectionContent.IsAssignableFrom(entry.Model))
                .OrderByDescending(entry => InheritanceDepth(entry.Model))
                .ThenBy(entry => entry.Model.FullName, StringComparer.Ordinal)
                .ToArray();
            if (collectionModels.Length == 0)
                throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RegionCollection,
                    $"{model.Name}.{property.Name}: no ViewModel with a registered View is a {collectionContent.Name}, so the region collection can present nothing.", property);
            regionCollections.Add(property, collectionModels);
            continue;
        }
        if (BridgeContractShape.QueryCollectionArguments(property.PropertyType) is { } query)
        {
            queryCollections.Add(property, QueryCollectionPlan(model, property, query.Row, query.Key, honorJsonIgnore));
            continue;
        }
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
        // field-paths/1: the rows of an editable collection also come from the page, rebuilt with one cell changed.
        var fromPage = property.SetMethod?.IsPublic == true || property.GetCustomAttribute<RunicCollectionAttribute>(true) is not null
            && Runic.Application.Views.Generated.BridgeRowEditors.For(model, property) is not null;
        BridgeTypeGraph valueGraph;
        try
        {
            // blobs/1: a property that holds a RunicBlob only goes to the page, whatever its setter (PageSettable).
            valueGraph = BridgeTypeGraph.Discover(property.PropertyType,
                nullability.Create(property), $"{model.Name}.{property.Name}", property, honorJsonIgnore, BridgeValueDirection.ToPage);
            if (fromPage && !valueGraph.Nodes.Any(node => node.Kind == BridgeWireKind.Blob))
                valueGraph = BridgeTypeGraph.Discover(property.PropertyType,
                    nullability.Create(property), $"{model.Name}.{property.Name}", property, honorJsonIgnore, BridgeValueDirection.Both);
        }
        catch (BridgeTypeGraphException error) when (property.SetMethod?.IsPublic != true && fromPage)
        {
            var editor = Runic.Application.Views.Generated.BridgeRowEditors.For(model, property)!;
            throw new BridgeTypeGraphException(error.Path,
                $"{error.Detail} The rows also come from the page because {model.Name}.{editor.Name} is their [RunicRowEditor].", error.Member ?? editor);
        }
        valueProperties.Add(property, valueGraph);
    }
    bool PageSettable(PropertyInfo property) => ViewPlan.IsPageSettable(property, valueProperties);
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
        if (key is null || key.Type.IsNullable || key.Type.NonNullableType != typeof(string) && key.Type.NonNullableType != typeof(Guid) && key.Type.NonNullableType != typeof(int)
            && key.Type.NonNullableType != typeof(long))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.Collection,
                $"{model.Name}.{property.Name}: the collection key '{attribute.KeyProperty}' must name a nonnullable string, Guid, Int32 or Int64 row property.", property);
        incrementalCollections.Add(property, key.Property);
    }
    WarnObservableCollectionsWithoutKeys(model, valueProperties, incrementalCollections);
    // field-paths/1: [RunicRowEditor] methods make a keyed collection's cells writable from the page.
    var rowEditors = new Dictionary<PropertyInfo, MethodInfo>();
    foreach (var editor in Runic.Application.Views.Generated.BridgeRowEditors.Declared(model))
    {
        var named = editor.GetCustomAttribute<RunicRowEditorAttribute>(true)!.Collection;
        var targets = incrementalCollections.Keys.Where(property => Runic.Application.Views.Generated.BridgeRowEditors.Edits(editor, property)).ToArray();
        if (targets.Length != 1)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RowEditor, targets.Length == 0
                ? $"{model.Name}.{editor.Name}: [RunicRowEditor] {(named is null ? "matches no [RunicCollection] property by its row type" : $"names {named}, which is not a [RunicCollection] property of {model.Name}")}."
                : $"{model.Name}.{editor.Name}: [RunicRowEditor] matches {string.Join(" and ", targets.Select(target => target.Name))}; name one, such as [RunicRowEditor(nameof({targets[0].Name}))].", editor);
        var collection = targets[0];
        var key = incrementalCollections[collection];
        var rowType = valueProperties[collection].Root.Element!.NonNullableType;
        var parameters = editor.GetParameters();
        if (editor.IsStatic || editor.IsGenericMethodDefinition || editor.ReturnType != typeof(void) || parameters.Length is not (2 or 3)
            || parameters[0].ParameterType != key.PropertyType || parameters[1].ParameterType != rowType
            || parameters.Length == 3 && parameters[2].ParameterType != typeof(RunicRowEdit)
            || !(editor.IsPublic || editor.IsAssembly || editor.IsFamilyOrAssembly))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RowEditor,
                $"{model.Name}.{editor.Name}: a [RunicRowEditor] for {collection.Name} must be a public or internal instance method void {editor.Name}({BridgeTypeGraph.CSharpType(key.PropertyType)} key, {BridgeTypeGraph.CSharpType(rowType)} row), optionally with a third parameter RunicRowEdit edit.", editor);
        // RUNICBRIDGE052: a row that cannot be copied exactly would lose what the page does not see.
        if (BridgeFieldLeaf.CopyOf(valueProperties[collection].Root.Element!) is { Kind: BridgeFieldCopy.None } refused)
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RowCopy,
                $"{model.Name}.{editor.Name}: the rows of {collection.Name} cannot be copied with one cell changed: {refused.Reason}. Make {BridgeTypeGraph.CSharpType(rowType)} a record, or seal it and give it a copy constructor or a Clone() method.", editor);
        if (!rowEditors.TryAdd(collection, editor))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.RowEditors,
                $"{model.Name}: {collection.Name} has two [RunicRowEditor] methods, {rowEditors[collection].Name} and {editor.Name}.", editor);
    }
    // Append-only text (#300): a read-only string state property.
    var textFields = properties.Where(property => property.GetCustomAttribute<RunicAppendOnlyTextAttribute>(true) is not null).ToArray();
    foreach (var property in textFields)
        if (property.PropertyType != typeof(string) || property.SetMethod?.IsPublic == true || !valueProperties.ContainsKey(property))
            throw new BridgeDiagnosticException(BridgeDiagnosticCodes.State,
                $"{model.Name}.{property.Name}: RunicAppendOnlyText requires a read-only string state property.", property);
    // Text members (text-splice/1): string state properties without a custom codec, other than append-only text
    // fields. Frames and setters may change them with text splices.
    var textMembers = properties.Where(property => !textFields.Contains(property) && !selectionSlots.Contains(property)
        && valueProperties.TryGetValue(property, out var graph) && graph.Root.Kind == BridgeWireKind.String).ToArray();
    var hasContent = contentProperties.Count > 0 || contentCollections.Count > 0 || regionCollections.Count > 0;
    if (viewTypes is not null) WarnContentWithoutView(model, contentProperties, contentCollections, regionSlots, viewTypes);
    var contentBindings = contentProperties.Concat(contentCollections).Concat(regionCollections).ToArray();
    ClientModules.CheckContent(model, contentBindings);
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
    ApplyOperationClose(model, commands, commandPlans);
    // D072.13: a development-only state property or command ([RunicDevelopmentOnly], ViewPlan.DevelopmentOnly) is left
    // out of a trimmed or NativeAOT publish. RUNICBRIDGE072 where the attribute has no effect: an interaction or view
    // request has no such form.
    foreach (var member in interactions.Select(plan => plan.Property).Concat(requests.Select(plan => plan.Property)))
        if (member.GetCustomAttribute<RunicDevelopmentOnlyAttribute>(true) is not null)
            Diagnostics.Warn(BridgeDiagnosticCodes.DevelopmentOnly,
                $"{model.Name}.{member.Name}: RunicDevelopmentOnly applies to a ViewModel, a state property or a command, not to an interaction or view request; it has no effect here. Mark the ViewModel instead.", member);
    // Commands scoped to their presentation (presentation-epoch/1), by wire member: the client sends them with the
    // epoch of the state its presentation received.
    // RUNICBRIDGE034: like RunicCommandKey, RunicPresentationScoped belongs to a bridged command or its [RelayCommand]
    // method; anywhere else it would silently leave the command unscoped.
    for (var owner = model; owner is not null && owner != typeof(object); owner = owner.BaseType)
        foreach (var member in owner.GetProperties(AllDeclared).Concat<MemberInfo>(owner.GetMethods(AllDeclared)).OrderBy(member => member.MetadataToken))
            if (BridgeAttributeNames.Has(member, "Runic.Application.Views.RunicPresentationScopedAttribute") && !failureOwners.Contains(member)
                && !OverriddenByCommand(member))
                Diagnostics.Warn(BridgeDiagnosticCodes.PresentationScoped,
                    $"{model.Name}.{member.Name}: RunicPresentationScoped applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method"
                    + (owner == model ? "" : $"; {owner.Name} declares it on a member the Bridge does not expose")
                    + "; it has no effect here.", member);
    var scopedCommands = commands.Where(command => BridgeCommandSources.IsPresentationScoped(model, command))
        .Select(command => LowerFirst(command.Name[..^"Command".Length])).ToArray();

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
    // C3: an object that reports validation in progress (IRunicPendingValidation) has the validation member too.
    var hasValidation = hasErrors || validationContextFlavors.Length > 0 || valueProperties.Values.Any(graph =>
        graph.Nodes.Any(node => typeof(INotifyDataErrorInfo).IsAssignableFrom(node.Type)))
        || validationTypes.Any(type => typeof(Runic.Application.Views.IRunicPendingValidation).IsAssignableFrom(type));
    if (hasValidation && properties.FirstOrDefault(property => WireName(property) == "validation") is { } validationProperty)
        throw new BridgeDiagnosticException(BridgeDiagnosticCodes.NameCollision,
            $"{model.Name}.{validationProperty.Name}: validation is reserved for generated validation state. Alias the model property.", validationProperty);
    // Checked writes use a WindowContentSession-owned provider. The window slice
    // supports the scalar codecs that the ordinary Bridge already serializes
    // without reflection.
    var checkedProperties = properties
        .Where(property => PageSettable(property) && valueProperties.ContainsKey(property)).ToArray();
    // field-paths/1: the field paths below settable object members and below the rows of editable collections.
    var nestedPaths = checkedProperties
        .Where(property => valueProperties[property].Root.Kind == BridgeWireKind.Dto)
        .Select(property => (Property: property, Leaves: BridgeFieldLeaf.Of(valueProperties[property].Root, null, null)))
        .Where(entry => entry.Leaves.Count > 0).ToDictionary(entry => entry.Property, entry => entry.Leaves);
    var rowPaths = rowEditors.Keys
        .Select(property => (Property: property, Leaves: BridgeFieldLeaf.Of(valueProperties[property].Root.Element!, incrementalCollections[property],
            // RUNICBRIDGE053: an object inside an editable row that cannot be copied has no fields below it.
            (member, reason) => Diagnostics.Warn(BridgeDiagnosticCodes.RowObjectCopy,
                $"{model.Name}.{property.Name}: {member.DeclaringType?.Name}.{member.Name} has no fields below it: {reason}. Mark it [RunicNoField], make its type a record, or seal it and give it a copy constructor or a Clone() method.", member))))
        .ToDictionary(entry => entry.Property, entry => entry.Leaves);
    var hasFieldPaths = nestedPaths.Count > 0 || rowPaths.Values.Any(leaves => leaves.Count > 0);
    // blobs/1: a client whose command inputs or settable members take a RunicUpload gets upload().
    var usesUploads = commandPlans.Values.Any(plan => plan.InputGraph?.Nodes.Any(node => node.Kind == BridgeWireKind.Upload) == true)
        || valueProperties.Any(entry => PageSettable(entry.Key) && entry.Value.Nodes.Any(node => node.Kind == BridgeWireKind.Upload));
    InteractionCodeEmitter.ValidatePublicSurface(model.Name, properties, commands, commandPlans, interactions, requests,
        checkedProperties, WireName, hasErrors, hasValidation);
    // Interactions and view requests are attached to the presentations of a WindowContentSession.
    var presented = interactions.Length > 0 || requests.Length > 0;
    return new ViewPlan
    {
        Model = model,
        ShortName = shortName,
        Prefix = prefix,
        KnownModels = knownModels,
        ViewTypes = viewTypes,
        ContractShape = contractShape,
        Nullability = nullability,
        Interactions = interactions,
        Requests = requests,
        Commands = commands,
        Properties = properties,
        SharedClients = sharedClients,
        ContentProperties = contentProperties,
        ContentCollections = contentCollections,
        RegionSlots = regionSlots,
        SelectionSlots = selectionSlots,
        RegionCollections = regionCollections,
        ContentBindings = contentBindings,
        ValueProperties = valueProperties,
        QueryCollections = queryCollections,
        IncrementalCollections = incrementalCollections,
        RowEditors = rowEditors,
        TextFields = textFields,
        TextMembers = textMembers,
        HasContent = hasContent,
        CommandPlans = commandPlans,
        CommandSources = commandSources,
        ScopedCommands = scopedCommands,
        HasErrors = hasErrors,
        ValidationContextFlavors = validationContextFlavors,
        HasValidation = hasValidation,
        CheckedProperties = checkedProperties,
        NestedPaths = nestedPaths,
        RowPaths = rowPaths,
        HasFieldPaths = hasFieldPaths,
        UsesUploads = usesUploads,
        Presented = presented,
    };
}

// Emits one planned ViewModel's C# Bridge; the emitter writes its TypeScript from the contract IR. Returns true when the
// generated bridge requires a WindowContentSession.
static bool EmitView(ViewPlan view, string csharpPath)
{
    var model = view.Model;
    var shortName = view.ShortName;
    var prefix = view.Prefix;
    var knownModels = view.KnownModels;
    var viewTypes = view.ViewTypes;
    var contractFingerprint = view.ContractFingerprint;
    var nullability = view.Nullability;
    var interactions = view.Interactions;
    var requests = view.Requests;
    var commands = view.Commands;
    var properties = view.Properties;
    var sharedClients = view.SharedClients;
    var contentProperties = view.ContentProperties;
    var contentCollections = view.ContentCollections;
    var regionSlots = view.RegionSlots;
    var selectionSlots = view.SelectionSlots;
    var regionCollections = view.RegionCollections;
    var contentBindings = view.ContentBindings;
    var valueProperties = view.ValueProperties;
    var queryCollections = view.QueryCollections;
    var incrementalCollections = view.IncrementalCollections;
    var rowEditors = view.RowEditors;
    var textFields = view.TextFields;
    var textMembers = view.TextMembers;
    var hasContent = view.HasContent;
    var commandPlans = view.CommandPlans;
    var commandSources = view.CommandSources;
    var scopedCommands = view.ScopedCommands;
    var hasErrors = view.HasErrors;
    var validationContextFlavors = view.ValidationContextFlavors;
    var hasValidation = view.HasValidation;
    var checkedProperties = view.CheckedProperties;
    var nestedPaths = view.NestedPaths;
    var rowPaths = view.RowPaths;
    var hasFieldPaths = view.HasFieldPaths;
    var usesUploads = view.UsesUploads;
    var presented = view.Presented;
    var fullType = $"global::{model.FullName}";
    var needsCheckedWriter = checkedProperties.Length > 0;
    bool PageSettable(PropertyInfo property) => view.PageSettable(property);
    // D072.13: a development-only member's table records and arms are marked, and the client describes it as possibly
    // absent or unavailable.
    bool DevelopmentOnly(PropertyInfo member) => view.DevelopmentOnly(member);
    // A region slot presents its Current, whose nullability (always nullable)
    // is the slot's wire and TypeScript nullability.
    string ContentAccess(PropertyInfo property) =>
        regionSlots.Contains(property) ? $"vm.{property.Name}?.Current" : $"vm.{property.Name}";
    // A selection slot's wire value: SelectedIndex, or null for -1 and for a null selection.
    static string SelectionIndex(PropertyInfo property) =>
        $"(vm.{property.Name} is {{ SelectedIndex: var index }} && index >= 0 ? index : (int?)null)";
    var cs = new StringBuilder();
    cs.AppendLine("// <auto-generated />");
    cs.AppendLine("#nullable enable");
    cs.AppendLine("using Runic.Application.Views;");
    AppendNamespace(cs, BridgeNamespace);
    // The codec aliases go here once every codec is known (BridgeCodecAliases).
    var codecs = new BridgeCodecAliases();
    var codecAliasPosition = cs.Length;
    cs.AppendLine($"internal sealed class {shortName}Bridge : global::Runic.Application.Views.Generated.ViewModelBridge");
    cs.AppendLine("{");
    if (presented)
        cs.AppendLine("    private readonly global::System.IDisposable? _rootInteractionMount;");
    if (hasContent)
    {
        cs.AppendLine("    private readonly global::Runic.Application.Views.WindowContentSession _content;");
        cs.AppendLine($"    private readonly {fullType} _owner;");
    }
    // S4: the descriptors are data, read once per ViewModel type (BridgeTableSupport); the constructor passes them on.
    var table = new BridgeTableBuilder(fullType);
    BridgeSupportFiles.Use(BridgeSupportFiles.Tables, BridgeTableSupport.SupportFile());
    cs.AppendLine($"    internal {shortName}Bridge(global::Runic.Application.Views.IBridgeTransport transport, {fullType} vm, string? routePrefix = null, global::Runic.Application.Views.WindowContentSession? content = null) : base(");
    cs.AppendLine($"        transport, vm, typeof({fullType}), routePrefix ?? \"{prefix}\",");
    cs.AppendLine(hasContent ? "        Table.CreateState(content ?? throw new global::System.ArgumentNullException(nameof(content)))," : "        Table.State,");
    cs.AppendLine($"        Table.Properties, Table.Checked, Table.Commands, \"{contractFingerprint}\", content,");
    cs.AppendLine(presented ? "        Interactions," : "        null,");
    cs.AppendLine(validationContextFlavors.Length > 0
        ? "        dataSubscriptions: Table.Data, collections: Table.Collections, validationReader: ValidationReader)"
        : "        dataSubscriptions: Table.Data, collections: Table.Collections)");
    cs.AppendLine("    {");
    cs.AppendLine($"        ContractShape = \"{view.ContractShape}\";");
    if (hasContent) cs.AppendLine("        _content = content!; _owner = vm;");
    if (presented)
    {
        cs.AppendLine("        global::System.ArgumentNullException.ThrowIfNull(content);");
        cs.AppendLine($"        _rootInteractionMount = routePrefix is null ? content.AttachRootInteractionPresentation(\"{prefix}\") : null;");
    }
    cs.AppendLine("    }");
    cs.AppendLine();
    var propertyIds = properties.Select((property, index) => (property, index)).ToDictionary(entry => entry.property, entry => entry.index);
    string Id(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    // P records: every property, in order.
    foreach (var property in properties)
    {
        using var developmentOnly = table.DevelopmentOnly(DevelopmentOnly(property));
        var id = propertyIds[property];
        var flags = new StringBuilder();
        // A setter reads the request's value with the member's codec. Observed members have none.
        var observed = regionSlots.Contains(property) || regionCollections.ContainsKey(property) || queryCollections.ContainsKey(property)
            || selectionSlots.Contains(property);
        if (!observed && PageSettable(property) && valueProperties.ContainsKey(property))
        {
            flags.Append('S');
            table.Do("Set", id, $"vm.{property.Name} = {property.Name}ValueCodec.Read(value)");
        }
        if (regionSlots.Contains(property))
        {
            // The runtime observes the region and binds the window session.
            table.Return("Get", id, $"vm.{property.Name}?.Current");
            flags.Append('O');
        }
        else if (selectionSlots.Contains(property))
        {
            // The runtime observes the selection's SelectedIndex.
            table.Return("Get", id, SelectionIndex(property));
            flags.Append('O');
        }
        else table.Return("Get", id, $"vm.{property.Name}");
        if (regionCollections.ContainsKey(property))
        {
            // The runtime observes the selection, its regions and their Current, and answers region requests.
            flags.Append('O').Append('R');
            if (BridgeContractShape.RegionCollection(property)!.PageCanClose) flags.Append('C');
        }
        else if (queryCollections.ContainsKey(property))
        {
            // The runtime observes the collection's query, total and version, and pages read its rows with query-rows.
            flags.Append('O').Append('Q');
            table.Do("WriteItem", id, $"{property.Name}RowsCodec.WriteItem(writer, item)");
        }
        if (flags.ToString().Contains('O')) table.Return("Observe", id, $"vm.{property.Name}");
        // A frame is one publication unit, so the text's change and the validation it causes arrive together.
        else if (textFields.Contains(property)) flags.Append('A');
        else if (textMembers.Contains(property))
        {
            flags.Append('T');
            if (property.SetMethod?.IsPublic == true) flags.Append('X');
        }
        table.Record("P", property.Name, WireName(property), Id(id), flags.Length == 0 ? "-" : flags.ToString());
    }
    // C records: checked writes. A text setter of a text member that is not checked shares the SetValue arm.
    foreach (var property in checkedProperties)
    {
        using var developmentOnly = table.DevelopmentOnly(DevelopmentOnly(property));
        var id = propertyIds[property];
        var type = valueProperties[property].RootCSharpType();
        table.Return("ReadValue", id, $"{property.Name}ValueCodec.Read(element)");
        table.Do("WriteValue", id, $"{property.Name}ValueCodec.Write(writer, ({type})value!)");
        table.Do("SetValue", id, $"vm.{property.Name} = ({type})value!");
        if (nestedPaths.TryGetValue(property, out var nested))
            table.Return("FieldPaths", id, BridgeFieldLeaf.CSharpPaths(nested, property.Name + "ValueCodec"));
        table.Record("C", property.Name, WireName(property), Id(id), nested is null ? "-" : "F");
    }
    foreach (var property in textMembers.Where(property => property.SetMethod?.IsPublic == true && !checkedProperties.Contains(property)
        && !textFields.Contains(property)))
    {
        using var developmentOnly = table.DevelopmentOnly(DevelopmentOnly(property));
        table.Do("SetValue", propertyIds[property], $"vm.{property.Name} = (string)value!");
    }
    // K records: the commands, in order.
    for (var index = 0; index < commands.Length; index++)
    {
        using var developmentOnly = table.DevelopmentOnly(DevelopmentOnly(commands[index]));
        commandPlans[commands[index]].Describe(table, index, commands[index]);
    }
    // L records: keyed collections.
    foreach (var (property, key) in incrementalCollections)
    {
        using var developmentOnly = table.DevelopmentOnly(DevelopmentOnly(property));
        // Protocol 4: a frame is one publication unit with the collection's indexed changes and every member that
        // changed with them, validation included, so a ViewModel with validation publishes frames too.
        var id = propertyIds[property];
        var itemType = BridgeTypeGraph.CSharpType(valueProperties[property].Root.Element!.NonNullableType);
        var keyAccess = $"(({itemType})item!).{key.Name}";
        table.Do("WriteItem", id, $"{property.Name}ValueCodec.WriteItem(writer, item)");
        table.Return("Key", id, key.PropertyType == typeof(string) ? keyAccess
            : key.PropertyType == typeof(Guid) ? keyAccess + ".ToString(\"D\")"
            : keyAccess + ".ToString(global::System.Globalization.CultureInfo.InvariantCulture)");
        var flags = "-";
        // field-paths/1: a row editor receives the copied row with its key, and the edit when it takes one.
        if (rowEditors.TryGetValue(property, out var editor))
        {
            table.Return("FieldPaths", id, BridgeFieldLeaf.CSharpPaths(rowPaths[property], property.Name + "ValueCodec"));
            table.Do("UpdateRow", id, $"vm.{editor.Name}((({itemType})row!).{key.Name}, ({itemType})row!{(editor.GetParameters().Length == 3 ? ", edit" : "")})");
            flags = editor.GetCustomAttribute<RunicRowEditorAttribute>(true)!.Conflicts == RunicRowConflicts.Row ? "EW" : "E";
        }
        table.Record("L", WireName(property), Id(id), flags);
    }
    // D records: the data members. A selection is observed through its descriptor; its members are not data.
    BridgeDataSubscriptionEmitter.AppendTable(table, valueProperties.Where(entry => !selectionSlots.Contains(entry.Key))
        .Select(entry => (entry.Key, propertyIds[entry.Key], entry.Value)), DevelopmentOnly);
    if (presented)
    {
        // Interactions and view requests: their factories take typed delegates, so they stay code, created once per
        // ViewModel type.
        cs.AppendLine("    private static readonly global::Runic.Application.Views.Generated.BridgeInteractionDescriptor[] Interactions =");
        cs.AppendLine("    [");
        foreach (var descriptor in InteractionCodeEmitter.CSharpDescriptors(interactions, fullType)
            .Concat(InteractionCodeEmitter.CSharpRequestDescriptors(requests, fullType)))
            cs.AppendLine($"        {descriptor},");
        cs.AppendLine("    ];");
        cs.AppendLine();
    }
    if (validationContextFlavors.Length > 0)
    {
        cs.AppendLine();
        AppendValidationContextReader(cs, validationContextFlavors);
    }
    // Codecs: the shared codec of each value's type (BridgeSharedCodecs), named by an alias. A member with field paths
    // keeps a class of its own for its setters.
    foreach (var (property, graph) in valueProperties)
    {
        if (nestedPaths.TryGetValue(property, out var nestedLeaves))
            BridgeCodecAliases.AppendFieldPathCodec(cs, property.Name + "ValueCodec", graph, graph.Root, nestedLeaves);
        else if (rowPaths.TryGetValue(property, out var rowLeaves) && rowLeaves.Count > 0)
            BridgeCodecAliases.AppendFieldPathCodec(cs, property.Name + "ValueCodec", graph, graph.Root.Element!, rowLeaves);
        else
        {
            codecs.Add(property.Name + "ValueCodec", graph);
            continue;
        }
        cs.AppendLine();
    }
    foreach (var (property, plan) in queryCollections)
        codecs.Add(property.Name + "RowsCodec", plan.Rows);
    foreach (var (command, plan) in commandPlans)
    {
        if (plan.InputGraph is { } input) codecs.Add(command.Name + "InputCodec", input);
        if (plan.ResultGraph is { } result) codecs.Add(command.Name + "ResultCodec", result);
        if (plan.FailureGraph is { } failure) codecs.Add(command.Name + "FailureCodec", failure);
    }
    InteractionCodeEmitter.AddCSharpCodecs(codecs, interactions);
    InteractionCodeEmitter.AddCSharpRequestCodecs(codecs, requests);
    foreach (var convention in MvvmCodegenConventions.All)
        convention.UseSupportFiles(commandPlans.Values);
    // The state members in order (specs/application, "State and revisions"). Each writes its value; the runtime writes
    // the member names, and the revision and the checked fields' versions beside the state.
    var stateMembers = new List<(string Member, string Body)>();
    // The state members of development-only properties and commands, by wire name.
    var developmentOnlyState = new HashSet<string>(properties.Where(DevelopmentOnly)
        .SelectMany(property => new[] { WireName(property), WireName(property) + "Errors" }), StringComparer.Ordinal);
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
                    writer.AppendLine($"            var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => {ConstructBridge(pageModel, $"new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content)")}{contractArgument}));");
                else
                    writer.AppendLine($"            var reference = content.Present(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => {ConstructBridge(pageModel, $"new {bridgeType}(transport, current, route, content)")});");
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
        else if (regionCollections.TryGetValue(property, out var regionModels))
        {
            // {regions: [{key, content}], selected}: each region's Current is presented like an item of a content
            // collection, so content shown by no region any more is pruned.
            var writer = new StringBuilder();
            writer.AppendLine("var active = new global::System.Collections.Generic.HashSet<string>(global::System.StringComparer.Ordinal);");
            writer.AppendLine($"var selection = vm.{property.Name};");
            writer.AppendLine("writer.WriteStartObject();");
            writer.AppendLine("writer.WriteStartArray(\"regions\");");
            writer.AppendLine("foreach (var region in selection?.Regions ?? [])");
            writer.AppendLine("{");
            writer.AppendLine("    writer.WriteStartObject();");
            writer.AppendLine("    writer.WriteString(\"key\", global::Runic.Application.Views.Generated.BridgeRegionCollections.Key(region));");
            writer.AppendLine("    writer.WritePropertyName(\"content\");");
            writer.AppendLine("    switch (region.Current)");
            writer.AppendLine("    {");
            writer.AppendLine("        case null: writer.WriteNullValue(); break;");
            foreach (var (pageModel, pageName) in regionModels)
            {
                var pageType = $"global::{pageModel.FullName}";
                var bridgeType = BridgeTypeName(pageModel, pageName);
                var contract = ContractFor(property);
                Type? selectedView = null;
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
                    writer.AppendLine($"            var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => {ConstructBridge(pageModel, $"new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content)")}{contractArgument}));");
                else
                    writer.AppendLine($"            var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => {ConstructBridge(pageModel, $"new {bridgeType}(transport, current, route, content)")});");
                writer.AppendLine("            active.Add(reference.Id);");
                writer.AppendLine("            writer.WriteStartObject();");
                writer.AppendLine("            writer.WriteString(\"kind\", reference.Kind);");
                writer.AppendLine("            writer.WriteString(\"id\", reference.Id);");
                writer.AppendLine("            writer.WriteEndObject();");
                writer.AppendLine("            break;");
                writer.AppendLine("        }");
            }
            writer.AppendLine($"        default: {{ var unregistered = region.Current!; throw new global::System.NotSupportedException({UnregisteredContentMessage(model, property, true)}); }}");
            writer.AppendLine("    }");
            writer.AppendLine("    writer.WriteEndObject();");
            writer.AppendLine("}");
            writer.AppendLine("writer.WriteEndArray();");
            writer.AppendLine("if (selection is { SelectedIndex: >= 0 and var selected }) writer.WriteNumber(\"selected\", selected);");
            writer.AppendLine("else writer.WriteNull(\"selected\");");
            writer.AppendLine("writer.WriteEndObject();");
            writer.AppendLine($"content.PruneCollection(vm, \"{property.Name}\", active);");
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
                    writer.AppendLine($"            var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => content.AttachPresentation<global::{selectedView.FullName}, {pageType}>(current, route, (presentationTransport, presentationModel, presentationRoute) => {ConstructBridge(pageModel, $"new {bridgeType}(presentationTransport, presentationModel, presentationRoute, content)")}{contractArgument}));");
                else
                    writer.AppendLine($"            var reference = content.PresentItem(vm, \"{property.Name}\", \"{pageKind}\", page, (transport, current, route) => {ConstructBridge(pageModel, $"new {bridgeType}(transport, current, route, content)")});");
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
        else if (queryCollections.ContainsKey(property))
            stateMembers.Add((jsonName, $"global::Runic.Application.Views.Generated.BridgeQueryWire.WriteState(writer, vm.{property.Name})"));
        else if (valueProperties.ContainsKey(property))
            stateMembers.Add((jsonName, $"{property.Name}ValueCodec.Write(writer, {(selectionSlots.Contains(property) ? SelectionIndex(property) : $"vm.{property.Name}")})"));
        else throw new InvalidOperationException($"{model.Name}.{property.Name}: no generated value codec.");
        if (hasErrors) stateMembers.Add((jsonName + "Errors", $"WriteStateErrors(writer, vm, \"{property.Name}\")"));
    }
    if (hasValidation)
        stateMembers.Add(("validation", validationContextFlavors.Length > 0
            ? "global::Runic.Application.Views.Generated.BridgeValidation.Write(writer, vm, Table.Data, ReadValidationContextErrors)"
            : "global::Runic.Application.Views.Generated.BridgeValidation.Write(writer, vm, Table.Data)"));
    // The development source map (#951): each member the generated client and the Bridge frames name, with the C#
    // that runs for it.
    BridgeSourceMap.RecordViewModel(model, shortName, LowerFirst(shortName), $"{model.FullName}:{contractFingerprint}",
        viewTypes is not null && viewTypes.TryGetValue(model, out var mappedViews)
            ? mappedViews.Select(view => PageKind(shortName, ContractFor(view))) : [PageKind(shortName, null)]);
    foreach (var property in properties)
    {
        BridgeSourceMap.RecordState(model, WireName(property), property);
        if (PageSettable(property)) BridgeSourceMap.RecordSetter(model, WireName(property), property);
    }
    foreach (var command in commands)
        BridgeSourceMap.RecordCommand(model, LowerFirst(command.Name[..^"Command".Length]), command, commandSources[command]);
    foreach (var plan in interactions)
        BridgeSourceMap.RecordInteraction(model, LowerFirst(plan.Property.Name), plan.Property, request: false);
    foreach (var plan in requests)
        BridgeSourceMap.RecordInteraction(model, LowerFirst(plan.Property.Name), plan.Property, request: true);
    // Availability without an argument is state; a command with an argument
    // is queried through can<Name>(argument). Execution state needs no
    // argument and is written for every asynchronous command that has it.
    foreach (var command in commands)
    {
        var plan = commandPlans[command];
        var name = command.Name[..^"Command".Length];
        var members = plan.Command is { } library ? library.StateMembers(command, name, plan)
            : plan.HasArgument ? [] : [MvvmCommand.CanExecute(command, name)];
        foreach (var (member, value) in members)
        {
            if (DevelopmentOnly(command)) developmentOnlyState.Add(member);
            stateMembers.Add((member, $"writer.WriteBooleanValue({value})"));
            BridgeSourceMap.RecordCommandState(model, member, command);
        }
    }
    // S records: the state members in order, each written by its WriteState arm. A writer of presented content uses the
    // Bridge's window session, which its arm names content.
    for (var slot = 0; slot < stateMembers.Count; slot++)
    {
        var (member, writer) = stateMembers[slot];
        using var developmentOnly = table.DevelopmentOnly(developmentOnlyState.Contains(member));
        table.Record("S", member, Id(slot));
        if (!writer.Contains('\n')) table.Do("WriteState", slot, writer);
        else table.Arm("WriteState", slot, (writer.Contains("content", StringComparison.Ordinal) ? "var content = session!;\n" : "") + writer.TrimEnd('\n') + "\nreturn;");
    }
    table.AppendTable(cs);
    cs.AppendLine();
    table.AppendMembers(cs);
    if (hasErrors)
    {
        cs.AppendLine();
        cs.AppendLine("    private static void WriteStateErrors(global::System.Text.Json.Utf8JsonWriter writer, global::System.ComponentModel.INotifyDataErrorInfo vm, string property) =>");
        cs.AppendLine("        global::Runic.Application.Views.Generated.BridgeValidation.WriteMessages(writer, vm.GetErrors(property));");
    }
    if (hasContent || presented)
    {
        cs.AppendLine("    public override void Dispose()");
        cs.AppendLine("    {");
        if (presented) cs.AppendLine("        _rootInteractionMount?.Dispose();");
        cs.AppendLine("        base.Dispose();");
        if (hasContent) cs.AppendLine("        _content.ClearOwner(_owner);");
        cs.AppendLine("    }");
    }
    cs.AppendLine("}");

    cs.Insert(codecAliasPosition, codecs.Directives());
    // Written with the shared codecs, which are named once every Bridge is generated.
    BridgeSharedCodecs.WriteLater(csharpPath, cs.ToString());
    Console.WriteLine($"Generated {shortName} bridge from compiled {model.Name}: {properties.Length} properties, {commands.Length} commands.");
    return hasContent || presented;
}

// The View in the contract IR (specs/application/contract-ir.md), from its plan. Names are UpperCamelCase: the C#
// member name with an uppercase first letter, which the emitter appends to set, write, can, start and recover.
static JsonObject ContractIrView(ViewPlan view)
{
    var model = view.Model;
    static string UpperFirst(string text) => char.ToUpperInvariant(text[0]) + text[1..];
    static JsonObject Named(string name, string wire, string? doc)
    {
        var item = new JsonObject { ["name"] = UpperFirst(name), ["wire"] = wire };
        if (!string.IsNullOrWhiteSpace(doc)) item["doc"] = doc;
        return item;
    }
    JsonArray Pages(PropertyInfo slot, (Type Model, string Name)[] pages) => new([.. pages.Select(page => (JsonNode)new JsonObject
    {
        ["view"] = page.Model.FullName,
        ["name"] = page.Name,
        ["kind"] = PageKind(page.Name, ContractFor(slot)),
    })]);
    // field-paths/1: the paths below a settable object member or the rows of an editable collection, with the
    // constraints of the member each path ends at.
    static JsonArray FieldPaths(IEnumerable<BridgeFieldLeaf> leaves) => new([.. leaves.Select(leaf =>
    {
        var item = new JsonObject { ["path"] = new JsonArray([.. leaf.Path.Select(segment => (JsonNode)segment)]) };
        if (FieldConstraintSet.Of(leaf.Chain[^1].Property) is { } constraints) item["constraints"] = constraints.Json();
        return (JsonNode)item;
    })]);

    var members = new JsonArray();
    foreach (var property in view.Properties)
    {
        var item = Named(property.Name, WireName(property), XmlDocumentation.Summary(property));
        ContractIr.Publish(model, property, command: false, WireName(property));
        SharedClients.Record(model, property, settable: view.PageSettable(property));
        if (view.ContentProperties.TryGetValue(property, out var pages))
        {
            item["kind"] = "content";
            item["pages"] = Pages(property, pages);
            if (view.IsNullableContent(property)) item["nullable"] = true;
            if (view.RegionSlots.Contains(property)) item["region"] = true;
        }
        else if (view.ContentCollections.TryGetValue(property, out var collectionPages))
        {
            item["kind"] = "content-list";
            item["pages"] = Pages(property, collectionPages);
            if (view.IsNullableContent(property)) item["nullable"] = true;
        }
        else if (view.RegionCollections.TryGetValue(property, out var regionPages))
        {
            item["kind"] = "region-collection";
            item["pages"] = Pages(property, regionPages);
            item["pageCanClose"] = BridgeContractShape.RegionCollection(property)!.PageCanClose;
        }
        else if (view.QueryCollections.TryGetValue(property, out var query))
        {
            item["kind"] = "query-collection";
            item["rows"] = ContractIr.Value(query.Rows);
            // The mock's default key: a row member named Id or Key of the collection's key type.
            if (query.Rows.Root.Element!.Members.FirstOrDefault(member => member.Property.Name is "Id" or "Key"
                && member.Type.NonNullableType == query.Key && !member.Type.IsNullable) is { } defaultKey)
                item["defaultKey"] = defaultKey.WireName;
        }
        else
        {
            var graph = view.ValueProperties[property];
            item["kind"] = view.SelectionSlots.Contains(property) ? "selection" : "value";
            item["value"] = ContractIr.Value(graph);
            if (view.PageSettable(property))
            {
                item["settable"] = true;
                item["declaringType"] = property.DeclaringType!.Name.Split('`')[0];
            }
            if (view.CheckedProperties.Contains(property))
            {
                item["checked"] = true;
                if (FieldConstraintSet.Of(property) is { } constraints) item["constraints"] = constraints.Json();
            }
            if (view.TextMembers.Contains(property)) item["text"] = true;
            if (view.TextFields.Contains(property)) item["appendOnlyText"] = true;
            if (view.IncrementalCollections.TryGetValue(property, out var key))
                item["collectionKey"] = graph.Root.Element!.Members.Single(member => member.Property == key).WireName;
            if (view.RowEditors.ContainsKey(property)) item["rowEditor"] = true;
            if (view.NestedPaths.TryGetValue(property, out var nested)) item["fieldPaths"] = FieldPaths(nested);
            else if (view.RowPaths.TryGetValue(property, out var rows) && rows.Count > 0) item["fieldPaths"] = FieldPaths(rows);
        }
        if (view.DevelopmentOnly(property)) item["developmentOnly"] = true;
        members.Add(item);
    }

    var commands = new JsonArray();
    foreach (var command in view.Commands)
    {
        var plan = view.CommandPlans[command];
        var name = command.Name[..^"Command".Length];
        var item = Named(name, LowerFirst(name), CommandDocumentation(command, view.CommandSources[command], plan.Command));
        ContractIr.Publish(model, command, command: true, LowerFirst(name));
        SharedClients.Record(model, command);
        if (plan.HasArgument) item["parameter"] = plan.ParameterName;
        if (plan.InputGraph is { } input) item["input"] = ContractIr.Value(input);
        else if (plan.HasStringArgument) item["input"] = ContractIr.UntypedString();
        if (plan.ResultGraph is { } result) item["result"] = ContractIr.Value(result);
        if (plan.FailureGraph is { } failure) item["failure"] = ContractIr.Value(failure);
        if (plan.IsAsync) item["async"] = true;
        if (plan.Command?.Cardinality is RunicCommandResultCardinality.Stream) item["stream"] = true;
        if (plan.HasExecutionState) item["executionState"] = true;
        if (plan.Key is not null) item["keyed"] = true;
        if (view.ScopedCommands.Contains(LowerFirst(name))) item["scoped"] = true;
        if (view.DevelopmentOnly(command)) item["developmentOnly"] = true;
        commands.Add(item);
    }

    var ir = new JsonObject
    {
        ["id"] = model.FullName,
        ["name"] = view.ShortName,
        ["route"] = view.Prefix,
        // Filled in once the View's IR is complete (ContractIr.Seal).
        ["fingerprint"] = "",
        ["kinds"] = new JsonArray([.. (view.ViewTypes is not null && view.ViewTypes.TryGetValue(model, out var views)
            ? views.Select(ContractFor) : [null]).Select(contract => (JsonNode)PageKind(view.ShortName, contract))]),
    };
    if (XmlDocumentation.Summary(model) is { } modelDoc) ir["doc"] = modelDoc;
    // D072.12: a development-only ViewModel's Bridge is left out of a trimmed or NativeAOT publish.
    if (model.GetCustomAttribute<RunicDevelopmentOnlyAttribute>(true) is not null) ir["developmentOnly"] = true;
    // Application mode (#1318): a mapped client library owns the client of this View.
    if (ClientModules.OwnerOf(model) is { } owner) ir["owner"] = owner.Assembly;
    if (view.HasErrors) ir["memberErrors"] = true;
    if (view.HasValidation) ir["validation"] = true;
    if (view.UsesUploads) ir["uploads"] = true;
    if (view.SharedClients.Count > 0)
        ir["sharedClients"] = new JsonArray([.. view.SharedClients.Select(shared => (JsonNode)shared.Interface.FullName!)]);
    ir["members"] = members;
    ir["commands"] = commands;
    foreach (var plan in view.Interactions)
        SharedClients.Record(model, plan.Property, unsupported: "an interaction, which a shared client does not include. Remove it from the interface.");
    foreach (var plan in view.Requests)
        SharedClients.Record(model, plan.Property, unsupported: "a view request, which a shared client does not include. Remove it from the interface.");
    if (view.Interactions.Length > 0)
        ir["interactions"] = new JsonArray([.. view.Interactions.Select(plan =>
        {
            var item = Named(plan.Property.Name, LowerFirst(plan.Property.Name), XmlDocumentation.Summary(plan.Property));
            item["contract"] = "";
            item["input"] = ContractIr.Value(plan.Input);
            item["output"] = ContractIr.Value(plan.Output);
            return (JsonNode)item;
        })]);
    if (view.Requests.Length > 0)
        ir["requests"] = new JsonArray([.. view.Requests.Select(plan =>
        {
            var item = Named(plan.Property.Name, LowerFirst(plan.Property.Name), XmlDocumentation.Summary(plan.Property));
            item["contract"] = "";
            item["input"] = ContractIr.Value(plan.Input);
            return (JsonNode)item;
        })]);
    return ir;
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
        // translation-text/1: a component, or its text, that carries a text reference gives the error its text.
        cs.AppendLine("                var message = validation.Text?.ToSingleLine(\" \") ?? string.Empty;");
        cs.AppendLine("                var text = validation is global::Runic.Application.Views.IRunicTextReferenceSource { Text: { } own } ? own");
        cs.AppendLine("                    : validation.Text is global::Runic.Application.Views.IRunicTextReferenceSource { Text: { } carried } ? carried : null;");
        cs.AppendLine("                errors.Add(text is null ? new global::System.ComponentModel.DataAnnotations.ValidationResult(message, properties) : new global::Runic.Application.Views.RunicValidationResult(message, text, properties));");
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
    return ClientMemberNames.IsReservedParameterName(candidate) ? candidate + "Value" : candidate;
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

static void WriteIfChanged(string path, string content) => GeneratedFile.WriteIfChanged(path, content);

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
    /// <summary>The frontend, or its JavaScript workspace, declares dependencies but has no lock file for the default install.</summary>
    internal const string FrontendLock = "RUNICBRIDGE026";
    /// <summary>A RunicCommandKey declaration is misplaced, or its resolver is missing or has an unsupported shape.</summary>
    internal const string CommandKey = "RUNICBRIDGE027";
    /// <summary>Warning: a RunicOperationRetirement declaration reaches no operation.</summary>
    internal const string OperationRetirement = "RUNICBRIDGE028";
    /// <summary>Warning: a RunicOperationClose declaration reaches no operation.</summary>
    internal const string OperationClose = "RUNICBRIDGE039";
    /// <summary>Warning: RunicApplicationFrontendPackageManager is not npm, pnpm or bun.</summary>
    internal const string PackageManager = "RUNICBRIDGE029";
    /// <summary>Warning: RunicApplicationFrontendDelivery=Embedded cannot apply, so the build copies the frontend.</summary>
    internal const string FrontendDelivery = "RUNICBRIDGE030";
    /// <summary>Warning: a type implements an IValidatableViewModel the Bridge does not recognize.</summary>
    internal const string UnrecognizedValidation = "RUNICBRIDGE031";
    /// <summary>Warning: an observable collection of DTO rows is not a RunicCollection.</summary>
    internal const string CollectionWithoutKey = "RUNICBRIDGE032";
    /// <summary>Warning: the .NET SDK or Bun is older than the Runic release supports.</summary>
    internal const string Toolchain = "RUNICBRIDGE033";
    /// <summary>Warning: a RunicPresentationScoped declaration reaches no Bridge command.</summary>
    internal const string PresentationScoped = "RUNICBRIDGE034";
    /// <summary>A ViewRequest or ViewQuestion member is not a get-only property.</summary>
    internal const string ViewMember = "RUNICBRIDGE035";
    /// <summary>A ViewModel does not match a [RunicSharedClient] interface it implements, or the declaration is invalid.</summary>
    internal const string SharedClient = "RUNICBRIDGE036";
    /// <summary>RunicApplicationFrontendTranslations has no Runic Translations ESM to copy, or its directory holds other files.</summary>
    internal const string FrontendTranslations = "RUNICBRIDGE037";
    // Reported by the Runic.Application.Views runtime (RunicBridgeContractException.DiagnosticId).
    /// <summary>Generated Bridges were built against another Runic release than the running runtime.</summary>
    internal const string ContractMismatch = "RUNICBRIDGE038";
    /// <summary>A [RunicRowEditor] method matches no keyed collection, or several, or has an unsupported signature.</summary>
    internal const string RowEditor = "RUNICBRIDGE050";
    /// <summary>A keyed collection has two [RunicRowEditor] methods.</summary>
    internal const string RowEditors = "RUNICBRIDGE051";
    /// <summary>The rows of a [RunicRowEditor] collection cannot be copied with one cell changed.</summary>
    internal const string RowCopy = "RUNICBRIDGE052";
    /// <summary>Warning: an object inside an editable row cannot be copied, so it has no fields below it.</summary>
    internal const string RowObjectCopy = "RUNICBRIDGE053";
    /// <summary>A RunicQueryCollection member has an unsupported shape.</summary>
    internal const string QueryCollection = "RUNICBRIDGE060";

    /// <summary>
    /// The warnings that Runic.Application.Views.Analyzers (BridgeWarningRules) reports for every declaration in the
    /// project's source that the generator would report, with the same message. With --analyzer-warnings the
    /// generator leaves them to the analyzer (Diagnostics.AnalyzerAssembly).
    /// </summary>
    internal static readonly HashSet<string> AnalyzerWarnings = [MissingView, OperationRetirement, PresentationScoped, OperationClose,
        UnrecognizedValidation, CollectionWithoutKey, RowObjectCopy];
    /// <summary>A RunicBlob is read from the page: a command input, an interaction result or a checked field.</summary>
    internal const string Blob = "RUNICBRIDGE065";
    /// <summary>A [RunicRegionCollection] is not on a get-only NavigationSelection property, or names an unsupported content type.</summary>
    internal const string RegionCollection = "RUNICBRIDGE070";
    /// <summary>A region collection's generated select or close method collides with another client member.</summary>
    internal const string RegionCollectionName = "RUNICBRIDGE071";
    /// <summary>A [RunicDevelopmentOnly] declaration has no effect: it is on an interaction or view request.</summary>
    internal const string DevelopmentOnly = "RUNICBRIDGE072";
    /// <summary>A ReactiveUI interaction's [RunicInteractionTimeout] is neither Timeout.Infinite nor 1 ms to 24 hours (sdk#1146).</summary>
    internal const string InteractionTimeout = "RUNICBRIDGE073";
    /// <summary>A [RunicInteractionTimeout] is on a view question, which takes its timeout in its constructor (sdk#1146).</summary>
    internal const string ViewQuestionTimeout = "RUNICBRIDGE074";
    /// <summary>Warning from the build targets: RunicDevelopmentOnlyViews or RunicBridgeBlobs is neither auto nor true.</summary>
    internal const string AutoPropertyValue = "RUNICBRIDGE075";
    /// <summary>Warning: a generated client loses declaration map entries or comments because an assembly has no PDB (#1298).</summary>
    internal const string ClientWithoutPdb = "RUNICBRIDGE076";
    /// <summary>A ViewModel property is marked both [RunicInclude] and [RunicIgnore] (#1320).</summary>
    internal const string IncludeIgnored = "RUNICBRIDGE077";
    /// <summary>Warning: [RunicInclude] on a ViewModel member that the Bridge cannot publish has no effect (#1320).</summary>
    internal const string IncludeUnpublishable = "RUNICBRIDGE078";
    /// <summary>A RunicApplicationFrontendClientModule names an assembly that is not referenced or not a client library (#1318).</summary>
    internal const string ClientModuleLibrary = "RUNICBRIDGE079";
    /// <summary>A ViewModel whose Views a mapped client library declares also has Views in another assembly (#1318).</summary>
    internal const string ClientModuleViews = "RUNICBRIDGE080";
    /// <summary>A mapped client library's ViewModel presents content of another owner, or a shared client has mixed owners (#1318).</summary>
    internal const string ClientModuleOwner = "RUNICBRIDGE081";
    /// <summary>A RunicApplicationFrontendClientModule item is malformed: no Specifier, a relative one, or a duplicate Include (#1318).</summary>
    internal const string ClientModuleItem = "RUNICBRIDGE082";
    /// <summary>Warning: a mapped client library declares no View, so its RunicApplicationFrontendClientModule does nothing (#1318).</summary>
    internal const string ClientModuleEmpty = "RUNICBRIDGE083";
    /// <summary>The TypeScript emitter cannot run: no Node.js or Bun, or no emitter in the frontend's @runic-artifex/views (reported by the build and its launcher).</summary>
    internal const string ClientEmitter = "RUNICBRIDGE084";
    /// <summary>A package of a former ID is in the package graph (old-name guards, D079.11; reported by Runic.Sdk.RenamedNames.targets).</summary>
    internal const string FormerPackage = "RUNICBRIDGE085";
    /// <summary>An environment variable of a former name is set (old-name guards, D079.11; reported by Runic.Sdk.RenamedNames.targets).</summary>
    internal const string FormerEnvironmentVariable = "RUNICBRIDGE086";
    /// <summary>The frontend is below a JavaScript workspace that does not list it (reported by the Views targets).</summary>
    internal const string FrontendWorkspaceMember = "RUNICBRIDGE087";
    /// <summary>A file that a frontend package exports does not exist after its build (reported by the Views targets).</summary>
    internal const string FrontendPackageExports = "RUNICBRIDGE088";
    /// <summary>A workspace member declares another package manager than its workspace (reported by the Views targets).</summary>
    internal const string FrontendWorkspacePackageManager = "RUNICBRIDGE089";
    /// <summary>The frontend's package manager or JavaScript runtime does not run (reported by the Views targets).</summary>
    internal const string FrontendPackageManagerMissing = "RUNICBRIDGE090";
    /// <summary>RunicApplicationFrontendInstallMode is not Auto, Always or Never (reported by the Views targets).</summary>
    internal const string FrontendInstallMode = "RUNICBRIDGE091";
    /// <summary>A web bundle cannot be written: the frontend build is off (D082).</summary>
    internal const string WebBundleWrite = "RUNICBRIDGE092";

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
        (FrontendLock, "error", "The frontend, or its JavaScript workspace, declares dependencies but has no lock file for the default install"),
        (CommandKey, "error", "A [RunicCommandKey] declaration is misplaced, or its resolver is missing or has an unsupported shape"),
        (OperationRetirement, "warning", "A [RunicOperationRetirement] declaration reaches no operation"),
        (PackageManager, "warning", "RunicApplicationFrontendPackageManager is not npm, pnpm or bun"),
        (FrontendDelivery, "warning", "RunicApplicationFrontendDelivery is unknown, or Embedded cannot apply"),
        (UnrecognizedValidation, "warning", "A type implements an IValidatableViewModel that is not ReactiveUI.Validation's"),
        (CollectionWithoutKey, "warning", "An observable collection of DTO rows is not a [RunicCollection]"),
        (Toolchain, "warning", "The .NET SDK or Bun is older than this release supports"),
        (PresentationScoped, "warning", "A [RunicPresentationScoped] declaration reaches no Bridge command"),
        (ViewMember, "error", "A ViewRequest or ViewQuestion member is not a get-only property"),
        (SharedClient, "error", "A ViewModel does not publish a member of a [RunicSharedClient] interface alike, or the interface cannot have a shared client"),
        (FrontendTranslations, "error", "The build has no Runic Translations ESM for the page, or its directory holds other files"),
        (ContractMismatch, "error", "Generated Bridges were built against another Runic release than the running runtime"),
        (OperationClose, "warning", "A [RunicOperationClose] declaration reaches no operation"),
        (RowEditor, "error", "A [RunicRowEditor] method matches no single keyed collection or has an unsupported signature"),
        (RowEditors, "error", "A keyed collection has more than one [RunicRowEditor] method"),
        (RowCopy, "error", "The rows of a [RunicRowEditor] collection cannot be copied with one cell changed"),
        (RowObjectCopy, "warning", "An object inside an editable row cannot be copied, so it has no fields below it"),
        (QueryCollection, "error", "A RunicQueryCollection member has an unsupported shape"),
        (Blob, "error", "A RunicBlob is read from the page: a command input, an interaction result or a checked field"),
        (RegionCollection, "error", "A [RunicRegionCollection] is not on a get-only NavigationSelection property, or its content type is unsupported"),
        (RegionCollectionName, "error", "A region collection's select or close client method collides with another client member"),
        (DevelopmentOnly, "warning", "A [RunicDevelopmentOnly] declaration on an interaction or view request has no effect"),
        (InteractionTimeout, "error", "A ReactiveUI interaction's [RunicInteractionTimeout] is not a timeout"),
        (ViewQuestionTimeout, "error", "A [RunicInteractionTimeout] is on a view question"),
        (AutoPropertyValue, "warning", "RunicDevelopmentOnlyViews or RunicBridgeBlobs is neither auto nor true"),
        (ClientWithoutPdb, "warning", "A generated client loses its declaration map or comments because an assembly has no PDB"),
        (IncludeIgnored, "error", "A ViewModel property is marked both [RunicInclude] and [RunicIgnore]"),
        (IncludeUnpublishable, "warning", "A [RunicInclude] declaration on a ViewModel member that the Bridge cannot publish has no effect"),
        (ClientModuleLibrary, "error", "A RunicApplicationFrontendClientModule names an assembly that is not referenced or not built as a client library"),
        (ClientModuleViews, "error", "A mapped client library's ViewModel also has Views in another assembly"),
        (ClientModuleOwner, "error", "A mapped client library's ViewModel presents content of another owner, or a shared client's implementers have different owners"),
        (ClientModuleItem, "error", "A RunicApplicationFrontendClientModule item has no Specifier, a relative one, or a duplicate Include"),
        (ClientModuleEmpty, "warning", "A mapped client library declares no View, so its RunicApplicationFrontendClientModule item does nothing"),
        (ClientEmitter, "error", "The TypeScript emitter cannot run: no Node.js or Bun, or no emitter in the frontend's @runic-artifex/views"),
        (FormerPackage, "error", "A package of a former ID is in the package graph"),
        (FormerEnvironmentVariable, "error", "An environment variable of a former name is set"),
        (FrontendWorkspaceMember, "error", "The frontend is below a JavaScript workspace that does not list it"),
        (FrontendPackageExports, "error", "A file that a frontend package exports does not exist after its build"),
        (FrontendWorkspacePackageManager, "warning", "A workspace member declares another package manager than its workspace"),
        (FrontendPackageManagerMissing, "error", "The frontend's package manager or JavaScript runtime does not run"),
        (FrontendInstallMode, "warning", "RunicApplicationFrontendInstallMode is not Auto, Always or Never"),
        (WebBundleWrite, "error", "A web bundle cannot be written for a project whose frontend build is off"),
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

    // Warnings left to the analyzer, written only if an error stops the build before the compiler runs it.
    private static readonly List<string> AnalyzerOwnedWarnings = [];

    /// <summary>
    /// The model assembly when the compile that follows runs Runic.Application.Views.Analyzers (--analyzer-warnings).
    /// That analyzer reports the warnings of <see cref="BridgeDiagnosticCodes.AnalyzerWarnings"/> for the declarations
    /// of this assembly, the project's own source, while you type and in the build, so the generator leaves them to it:
    /// the compiler then reports each once, and #pragma, .editorconfig severities, NoWarn and TreatWarningsAsErrors
    /// apply as to any compiler warning. Declarations in other assemblies, which the analyzer cannot see, stay here.
    /// </summary>
    internal static Assembly? AnalyzerAssembly { get; set; }

    internal static void WriteAnalyzerWarnings()
    {
        foreach (var line in AnalyzerOwnedWarnings) Console.Error.WriteLine(line);
        AnalyzerOwnedWarnings.Clear();
    }

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
        if (AnalyzerAssembly is not null && member is not null && member.Module.Assembly == AnalyzerAssembly
            && BridgeDiagnosticCodes.AnalyzerWarnings.Contains(code))
        {
            AnalyzerOwnedWarnings.Add(line);
            return;
        }
        ReportedWarnings.Add(line);
        Console.Error.WriteLine(line);
    }

    /// <summary>Writes an MSBuild canonical warning about a file, such as a generated client; generation continues.</summary>
    internal static void WarnAt(string code, string file, string message)
    {
        var line = $"{file}: warning {code}: {WithHelpLink(code, message)}";
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
            BridgeTypeGraphException graph => graph.Code ?? BridgeDiagnosticCodes.ValueType,
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
/// The names that a generated client reserves: what the generator checks a ViewModel's commands and parameters
/// against, so that the client the emitter writes from the contract IR compiles.
/// </summary>
static class ClientMemberNames
{
    // Identifiers generated client methods use, and JavaScript reserved
    // words. A command parameter with such a name gets a "Value" suffix.
    internal static readonly HashSet<string> ReservedParameterNames = new(StringComparer.Ordinal)
    {
        "view", "route", "mount", "requestId", "bridgeWire", "BridgeError", "JSON", "String", "Number", "Object",
        "globalThis", "hydrate", "bridgeContract", "collectionDefinitions", "interactionDefinitions", "requestDefinitions", "checkedFieldNames",
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

    /// <summary>The operation's RunicOperationClose; Default is not emitted.</summary>
    internal RunicOperationClose Close { get; init; }

    internal static GeneratedCommandPlan Plain(BridgeTypeGraph input) =>
        new(HasStringArgument: false, IsAsync: false, InputGraph: input, IsPlainICommand: true);

    // The command's K record and arms in its Bridge's table (S4): the convention's, or a plain ICommand's input; the
    // declared failure's encoder, the canonical JSON of the declared type or null so that the runtime reports any other
    // value as failed; the key resolver; retirement and close.
    internal void Describe(BridgeTableBuilder table, int id, PropertyInfo property)
    {
        var command = new BridgeCommandTable(table, id);
        table.Return("Command", id, $"vm.{property.Name}");
        if (Command is { } library) library.Describe(command, property, this);
        else if (IsPlainICommand)
        {
            var plainGraph = InputGraph ?? throw new InvalidOperationException("Plain ICommand is missing its input graph.");
            command.Return("ReadInput", $"{property.Name}InputCodec.Read(input)");
            command.Return("EncodeArgument", $"global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}InputCodec.Write(writer, ({plainGraph.RootCSharpType()})argument!))");
        }
        else throw new InvalidOperationException("Command plan has no descriptor.");
        if (FailureGraph is not null)
            command.Return("EncodeFailure", $"failure is {FailureGraph.RootCSharpType()} declared ? global::Runic.Application.Views.Generated.BridgeWire.EncodeCanonical(writer => {property.Name}FailureCodec.Write(writer, declared)) : null");
        Key?.Describe(command);
        var name = property.Name[..^"Command".Length];
        // The member name of command, can and start requests (specs/application, "Routes and members").
        table.Record("K", name, char.ToLowerInvariant(name[0]) + name[1..], id.ToString(System.Globalization.CultureInfo.InvariantCulture), command.Flags,
            Retirement is RunicOperationRetirement.Keep ? "-" : ((int)Retirement).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Close is RunicOperationClose.Default ? "-" : ((int)Close).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}

// The resolver of a RunicCommandKey command (#472): a ViewModel method from the key to the command's
// parameter, or null when the key names no object.
sealed record GeneratedKeyPlan(MethodInfo Resolver, BridgeTypeGraph KeyGraph, Type Parameter, bool IsAsync, bool TakesToken)
{
    // The command's Resolve arm, or its ResolveAsync arm in the async switch method, which keeps a failure the task's.
    internal void Describe(BridgeCommandTable command)
    {
        var target = Resolver.IsStatic ? command.ModelType : "vm";
        var key = $"({KeyGraph.RootCSharpType()})key!";
        if (IsAsync) command.Return("ResolveAsync", $"{target}.{Resolver.Name}({key}{(TakesToken ? ", token" : "")})");
        else command.Return("Resolve", $"{target}.{Resolver.Name}({key})");
    }
}

// field-paths/1: one field path below a member or a row: its wire names and the members it passes through. A path
// names a member that the page reads and that the copy of its object can change, down to three objects deep; an
// object member has its own descriptor and those of its members. [RunicNoField] leaves a member out.
internal sealed record BridgeFieldLeaf(string[] Path, BridgeTypeMember[] Chain)
{
    internal BridgeTypeNode Node => Chain[^1].Type;

    internal static List<BridgeFieldLeaf> Of(BridgeTypeNode owner, PropertyInfo? key, Action<PropertyInfo, string>? skipped)
    {
        var leaves = new List<BridgeFieldLeaf>();
        if (CopyOf(owner).Kind == BridgeFieldCopy.None) return leaves;
        Visit(owner, [], []);
        return leaves;

        void Visit(BridgeTypeNode node, string[] prefix, BridgeTypeMember[] chain)
        {
            var copy = CopyOf(node).Kind;
            var parameters = node.Constructor?.GetParameters() ?? [];
            foreach (var member in node.Members)
            {
                if (!member.Readable || member.Absent || member.Property == key) continue;
                if (member.Property.GetCustomAttribute<RunicNoFieldAttribute>(true) is not null) continue;
                var settable = copy switch
                {
                    BridgeFieldCopy.With => member.Writable,
                    BridgeFieldCopy.Constructor or BridgeFieldCopy.Clone => member.Writable && !IsInitOnly(member.Property),
                    _ => member.Writable || parameters.Any(parameter => string.Equals(parameter.Name, member.Property.Name, StringComparison.OrdinalIgnoreCase)),
                };
                if (!settable) continue;
                string[] path = [.. prefix, member.WireName];
                BridgeTypeMember[] next = [.. chain, member];
                leaves.Add(new(path, next));
                if (member.Type.Kind != BridgeWireKind.Dto || prefix.Length >= 2 || member.Type == owner || chain.Any(step => step.Type == member.Type)) continue;
                if (CopyOf(member.Type) is { Kind: BridgeFieldCopy.None } refused)
                {
                    skipped?.Invoke(member.Property, refused.Reason!);
                    continue;
                }
                Visit(member.Type, path, next);
            }
        }
    }

    // How an object of a node is copied with one member changed, or why it cannot be copied exactly: a record or a
    // struct with `with`, a sealed class with its copy constructor or Clone(), or a sealed class whose every settable
    // member is on the wire through its codec. A class that is not sealed would lose a derived type's members.
    internal static (BridgeFieldCopy Kind, string? Reason) CopyOf(BridgeTypeNode node)
    {
        var type = node.NonNullableType;
        if (type.IsValueType || type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null) return (BridgeFieldCopy.With, null);
        if (!type.IsSealed) return (BridgeFieldCopy.None, $"{BridgeTypeGraph.CSharpType(type)} is a class that is not sealed, so a derived row would lose its own members");
        if (type.GetConstructor([type]) is { IsPublic: true }) return (BridgeFieldCopy.Constructor, null);
        if (type.GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) is { } clone && clone.ReturnType == type)
            return (BridgeFieldCopy.Clone, null);
        var hidden = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0
                && !node.Members.Any(member => member.Property.Name == property.Name))
            .Select(property => property.Name).ToArray();
        return hidden.Length > 0
            ? (BridgeFieldCopy.None, $"{BridgeTypeGraph.CSharpType(type)} has settable members the page does not see ({string.Join(", ", hidden)}) and no copy constructor or Clone()")
            : (BridgeFieldCopy.Wire, null);
    }

    private static bool IsInitOnly(PropertyInfo property) =>
        property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit") == true;

    internal static string CSharpPaths(IReadOnlyList<BridgeFieldLeaf> leaves, string codec) =>
        $"[{string.Join(", ", leaves.Select((leaf, index) => $"new([{string.Join(", ", leaf.Path.Select(segment => JsonSerializer.Serialize(segment)))}], {codec}.SetPath{index})"))}]";
}

internal enum BridgeFieldCopy { None, With, Constructor, Clone, Wire }
