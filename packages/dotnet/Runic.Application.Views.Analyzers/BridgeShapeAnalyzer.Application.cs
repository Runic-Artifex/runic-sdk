using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The rules that compare a ViewModel or View with the rest of the application (Program.cs, the View discovery loop,
/// the name checks and WarnContentWithoutView), the ReactiveUI adapter check (ReactiveUICodegenConvention) and the
/// warnings about declarations that reach nothing (ApplyOperationRetirement, ApplyOperationClose and the
/// RunicPresentationScoped loop of GenerateOne).
/// </summary>
/// <remarks>
/// The warnings are the analyzer's own (BridgeDiagnosticCodes.AnalyzerWarnings in the generator): when the compile runs
/// this analyzer, the generator leaves the warnings of the project's declarations to it, so each is reported once, by
/// the compiler. <see cref="OwnedWarning"/> therefore reports every one the generator would report for a declaration of
/// this compilation, also one that only generated code declares, and none for another assembly's declarations, which
/// the generator keeps.
/// </remarks>
public sealed partial class BridgeShapeAnalyzer
{
    // The ViewModels and Views of the application, and what follows from comparing them, once per compilation.
    private sealed class BridgeApplication
    {
        private readonly BridgeSymbols _symbols;
        private readonly Lazy<Dictionary<INamedTypeSymbol, List<string>>> _missingViews;

        internal BridgeApplication(BridgeSymbols symbols, CancellationToken cancellationToken)
        {
            _symbols = symbols;
            Views = symbols.FindViews(cancellationToken);
            Models = new HashSet<INamedTypeSymbol>(Views.Keys, SymbolEqualityComparer.Default);
            _missingViews = new Lazy<Dictionary<INamedTypeSymbol, List<string>>>(() => FindMissingViews(cancellationToken));
        }

        /// <summary>The concrete Windows and Views by ViewModel, in the generator's order.</summary>
        internal Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> Views { get; }

        internal HashSet<INamedTypeSymbol> Models { get; }

        /// <summary>The RUNICBRIDGE013 messages for each ViewModel without a View that a content slot admits.</summary>
        internal Dictionary<INamedTypeSymbol, List<string>> MissingViews => _missingViews.Value;

        // The Views the generator accepts: public, top-level, non-generic classes with a valid or no contract.
        internal IEnumerable<INamedTypeSymbol> ValidViews(INamedTypeSymbol model) => Views[model].Where(view =>
            view.TypeKind == TypeKind.Class && view.DeclaredAccessibility == Accessibility.Public && view.ContainingType is null
            && !view.IsGenericType && TryContract(view, out _));

        // Program.cs WarnContentWithoutView, for the ViewModels of this compilation: a candidate ViewModel without a
        // View is in the same assembly as the ViewModel whose slot admits it.
        private Dictionary<INamedTypeSymbol, List<string>> FindMissingViews(CancellationToken cancellationToken)
        {
            const int MissingViewCandidateLimit = 8;
            var found = new Dictionary<INamedTypeSymbol, List<string>>(SymbolEqualityComparer.Default);
            var assembly = _symbols.Compilation.Assembly;
            var candidates = TopLevelTypes(assembly.GlobalNamespace)
                .Where(type => type.TypeKind == TypeKind.Class && type.DeclaredAccessibility == Accessibility.Public && !type.IsAbstract
                    && !type.IsGenericType && BridgeSymbols.Implements(type, _symbols.NotifyPropertyChanged) && !Views.ContainsKey(type))
                .OrderBy(type => BridgeSymbols.MetadataName(type), StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) return found;
            var windowModels = Views.Where(entry => entry.Value.Any(_symbols.IsWindow)).Select(entry => entry.Key).ToArray();
            foreach (var model in Views.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SymbolEqualityComparer.Default.Equals(model.ContainingAssembly, assembly) || model.IsGenericType) continue;
                foreach (var (property, slotType, region) in ContentSlots(model))
                {
                    if (slotType.SpecialType == SpecialType.System_Object || IsFrameworkBase(slotType) || IsFrameworkNamespace(slotType)
                        || windowModels.Append(model).Any(owner => _symbols.IsAssignable(owner, slotType)))
                        continue;
                    var cases = Models.Where(other => !SymbolEqualityComparer.Default.Equals(other, model) && _symbols.IsAssignable(other, slotType)).ToArray();
                    var missing = candidates.Where(type => !SymbolEqualityComparer.Default.Equals(type, model) && _symbols.IsAssignable(type, slotType)
                        && !cases.Any(other => _symbols.IsAssignable(type, other))).ToArray();
                    if (missing.Length > MissingViewCandidateLimit || !TryContract(property, out var contract)) continue;
                    var slot = region ? "pushing it to this navigation region" : "presenting it in this slot";
                    foreach (var candidate in missing)
                    {
                        if (!found.TryGetValue(candidate, out var messages)) found.Add(candidate, messages = []);
                        messages.Add($"{model.MetadataName}.{property.Name}: {BridgeSymbols.MetadataName(candidate)} is assignable to {ReflectionName(slotType)} but has no Runic View"
                            + (contract is null ? "" : $" with contract '{contract}'")
                            + $", so {slot} throws NotSupportedException at runtime. Add"
                            + $" 'public sealed partial class {PublicName(candidate)}View : RunicView<{candidate.MetadataName}>;'"
                            + (contract is null ? "" : $" with [RunicViewContract(\"{contract}\")]")
                            + $", or suppress {BridgeRules.MissingView.Id} with NoWarn if it is never presented here.");
                    }
                }
            }
            return found;
        }

        // GenerateOne's content properties, region slots and content collections that pass its checks, with the type
        // a ViewModel must have to be presented there. Commands, interactions and view members are not state.
        private IEnumerable<(IPropertySymbol Property, ITypeSymbol Slot, bool Region)> ContentSlots(INamedTypeSymbol model)
        {
            foreach (var property in BridgedProperties(model))
            {
                var type = property.Type;
                if (property.GetMethod is null || _symbols.IsCommand(type) || BridgeSymbols.Is(type, _symbols.ViewQuestion)
                    || BridgeSymbols.IsReactiveInteraction(type) || BridgeSymbols.Is(type, _symbols.ViewRequest)
                    || BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicRegionCollection) is not null
                    || BridgeSymbols.Is(type, _symbols.QueryCollection) || BridgeSymbols.Is(type, _symbols.NavigationSelection))
                    continue;
                var publicSetter = IsPublic(property.SetMethod);
                if (BridgeSymbols.Is(type, _symbols.NavigationRegion) && type is INamedTypeSymbol { TypeArguments.Length: 1 } region)
                {
                    var content = region.TypeArguments[0];
                    if (!publicSetter && content.SpecialType != SpecialType.System_Object && Admits(model, content))
                        yield return (property, content, true);
                    continue;
                }
                if (type.SpecialType != SpecialType.System_Object && Admits(model, type))
                {
                    if (!publicSetter) yield return (property, type, false);
                    continue;
                }
                if (BridgeSymbols.Is(type, _symbols.ReadOnlyList) && type is INamedTypeSymbol { TypeArguments.Length: 1 } list
                    && list.TypeArguments[0] is var item && item.SpecialType != SpecialType.System_Object && Admits(model, item) && !publicSetter)
                    yield return (property, item, false);
            }
        }

        private bool Admits(INamedTypeSymbol model, ITypeSymbol slot) =>
            Models.Any(other => !SymbolEqualityComparer.Default.Equals(other, model) && _symbols.IsAssignable(other, slot));

        // BridgeContractShape.IsFrameworkBase: a library base class, or a type in a System.* namespace.
        private static bool IsFrameworkBase(ITypeSymbol type) =>
            BridgeSymbols.MetadataName(type) is "ReactiveUI.ReactiveObject" or "ReactiveUI.Validation.Helpers.ReactiveValidationObject"
                or "ReactiveUI.Validation.Reactive.Helpers.ReactiveValidationObject" or "CommunityToolkit.Mvvm.ComponentModel.ObservableObject"
            || Namespace(type)?.StartsWith("System.", StringComparison.Ordinal) == true;

        // The System namespace or one of BridgeMvvmConventions' FrameworkNamespaces.
        private static bool IsFrameworkNamespace(ITypeSymbol type) => Namespace(type) is { } ns
            && (ns == "System" || FrameworkNamespaces.Any(root => ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal)));

        private static readonly string[] FrameworkNamespaces = ["ReactiveUI", "CommunityToolkit"];

        private static string? Namespace(ITypeSymbol type) =>
            type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

        private static IEnumerable<INamedTypeSymbol> TopLevelTypes(INamespaceSymbol root)
        {
            var pending = new Stack<INamespaceSymbol>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var ns = pending.Pop();
                foreach (var member in ns.GetMembers())
                {
                    if (member is INamespaceSymbol child) pending.Push(child);
                    else if (member is INamedTypeSymbol type) yield return type;
                }
            }
        }
    }

    // Program.cs ContractFor: the [RunicViewContract] name of a View or a content slot, null without one. An invalid
    // name is RUNICBRIDGE006, so the member has no contract the other rules could compare.
    private static bool TryContract(ISymbol member, out string? contract)
    {
        contract = null;
        if (BridgeSymbols.Attribute(member, BridgeSymbols.RunicViewContract) is not { } attribute) return true;
        if (attribute.ConstructorArguments.Length != 1 || !IsValidContract(attribute.ConstructorArguments[0].Value as string)) return false;
        contract = (string)attribute.ConstructorArguments[0].Value!;
        return true;
    }

    private static string PublicName(INamedTypeSymbol model) => model.MetadataName.EndsWith("ViewModel", StringComparison.Ordinal)
        ? model.MetadataName.Substring(0, model.MetadataName.Length - "ViewModel".Length) : model.MetadataName;

    private static string PageKind(string name, string? contract) =>
        LowerFirst(name) + (contract is null ? "" : char.ToUpperInvariant(contract[0]) + contract.Substring(1));

    // Program.cs, the View discovery loop: a second View of one ViewModel with the same contract (RUNICBRIDGE006),
    // reported at the later View; and the presentation kinds of the Views (RUNICBRIDGE004), unique across the
    // application, reported at the last View of a kind when the ViewModel names are unique.
    private static void AnalyzeApplicationView(SymbolAnalysisContext context, INamedTypeSymbol view, BridgeApplication application)
    {
        if (ModelOf(application, view) is not { } model) return;
        var views = application.ValidViews(model).ToList();
        var index = views.FindIndex(candidate => SymbolEqualityComparer.Default.Equals(candidate, view));
        if (index < 0) return;
        TryContract(view, out var contract);
        if (views.Take(index).Any(earlier => TryContract(earlier, out var other) && string.Equals(other, contract, StringComparison.OrdinalIgnoreCase)))
        {
            Report(context, BridgeRules.View, view,
                $"{FullName(view)}: {FullName(model)} already has a Runic View with contract '{contract ?? "default"}'.");
            return;
        }
        if (NameCollisions(application).Any()) return;
        foreach (var (owner, message) in FileCollisions(application))
            if (SymbolEqualityComparer.Default.Equals(owner, view)) Report(context, BridgeRules.NameCollision, view, message);
        var kinds = application.Views.Keys.SelectMany(owner => application.ValidViews(owner)
                .Where(candidate => IsFirstOfContract(application, owner, candidate))
                .Select(candidate => (Kind: PageKind(PublicName(owner), ContractOf(candidate)), View: candidate)))
            .GroupBy(entry => entry.Kind, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1 && SymbolEqualityComparer.Default.Equals(group.Last().View, view));
        if (kinds is not null)
            Report(context, BridgeRules.NameCollision, view,
                $"Bridge presentation kinds must be unique across ViewModels and View contracts: '{kinds.Key}' is used by {string.Join(" and ", kinds.Select(entry => entry.View.MetadataName))}.");
    }

    private static INamedTypeSymbol? ModelOf(BridgeApplication application, INamedTypeSymbol view) =>
        application.Views.FirstOrDefault(entry => entry.Value.Contains(view, SymbolEqualityComparer.Default)).Key;

    private static string? ContractOf(INamedTypeSymbol view) => TryContract(view, out var contract) ? contract : null;

    // A View that the generator keeps: no earlier View of the ViewModel has its contract.
    private static bool IsFirstOfContract(BridgeApplication application, INamedTypeSymbol model, INamedTypeSymbol view)
    {
        var contract = ContractOf(view);
        return application.ValidViews(model).TakeWhile(candidate => !SymbolEqualityComparer.Default.Equals(candidate, view))
            .All(earlier => !string.Equals(ContractOf(earlier), contract, StringComparison.OrdinalIgnoreCase));
    }

    // Bridge ViewModel names are unique within an application (RUNICBRIDGE004), by public name, ignoring case.
    private static IEnumerable<IGrouping<string, INamedTypeSymbol>> NameCollisions(BridgeApplication application) =>
        application.Views.Keys.GroupBy(PublicName, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1);

    private static void AnalyzeApplicationModel(SymbolAnalysisContext context, INamedTypeSymbol model, BridgeApplication application)
    {
        foreach (var group in NameCollisions(application).Where(group => SymbolEqualityComparer.Default.Equals(group.Last(), model)))
            Report(context, BridgeRules.NameCollision, model,
                $"Bridge ViewModel names must be unique within an application: {string.Join(" and ", group.Select(FullName))} both use '{group.Key}'.");
        if (!NameCollisions(application).Any())
            foreach (var (owner, message) in FileCollisions(application))
                if (SymbolEqualityComparer.Default.Equals(owner, model)) Report(context, BridgeRules.NameCollision, model, message);
    }

    // Program.cs, the generated files (RUNICBRIDGE004): each ViewModel's {FullName}.Bridge.g.cs, {name}.ts and
    // {name}.mock.ts, each View's {FullName}.View.g.cs and the named types' types.ts, unique ignoring case. Reported at
    // the first ViewModel that writes one, else at the first View. The shared clients' modules (RUNICBRIDGE036) stay
    // the generator's.
    private static IEnumerable<(INamedTypeSymbol Owner, string Message)> FileCollisions(BridgeApplication application)
    {
        var models = application.Views.Keys.Where(model => !model.IsGenericType).ToArray();
        var views = models.SelectMany(model => application.ValidViews(model).Where(view => IsFirstOfContract(application, model, view))).ToArray();
        var outputs = models.Select(model => (Path: FullName(model) + ".Bridge.g.cs", Owner: (INamedTypeSymbol?)model, Name: FullName(model)))
            .Concat(views.Select(view => (Path: FullName(view) + ".View.g.cs", Owner: (INamedTypeSymbol?)view, Name: FullName(view))))
            .Concat(models.Select(model => (Path: LowerFirst(PublicName(model)) + ".ts", Owner: (INamedTypeSymbol?)model, Name: FullName(model))))
            .Concat(models.Select(model => (Path: LowerFirst(PublicName(model)) + ".mock.ts", Owner: (INamedTypeSymbol?)model, Name: FullName(model))))
            .Append((Path: "types.ts", Owner: null, Name: "the generated named types"));
        foreach (var group in outputs.GroupBy(output => output.Path, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
        {
            var owner = models.FirstOrDefault(model => group.Any(output => output.Name == FullName(model)))
                ?? group.Select(output => output.Owner).FirstOrDefault(candidate => candidate is not null);
            if (owner is not null)
                yield return (owner, $"generated file '{group.Key}' would be written by {string.Join(" and ", group.Select(output => output.Name))}. Rename one type.");
        }
    }

    // RUNICBRIDGE013 at the ViewModel without a View, from that type's own analysis so the IDE shows it in its file.
    private static void ReportMissingViews(SymbolAnalysisContext context, INamedTypeSymbol type, BridgeApplication application)
    {
        if (!application.MissingViews.TryGetValue(type, out var messages)) return;
        foreach (var message in messages) OwnedWarning(context, BridgeRules.MissingView, type, message);
    }

    /// <summary>
    /// Reports a warning that the generator leaves to this analyzer: at the declaration in hand-written source, else at
    /// the CommunityToolkit member a generated property comes from, else at the containing type, else in generated code.
    /// A declaration of another assembly is the generator's.
    /// </summary>
    private static void OwnedWarning(SymbolAnalysisContext context, DiagnosticDescriptor rule, ISymbol symbol, string message)
    {
        if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, context.Compilation.Assembly)) return;
        var cancellation = context.CancellationToken;
        var location = SourceLocation(symbol, cancellation)
            ?? (ToolkitSource(symbol) is { } source ? SourceLocation(source, cancellation) : null)
            ?? (symbol.ContainingType is { } owner ? SourceLocation(owner, cancellation) : null)
            ?? symbol.Locations.FirstOrDefault(candidate => candidate.IsInSource);
        if (location is not null) context.ReportDiagnostic(Diagnostic.Create(rule, location, message));
    }

    // The [RelayCommand] method of a generated command property, or the [ObservableProperty] field of a generated property.
    private static ISymbol? ToolkitSource(ISymbol symbol)
    {
        if (symbol is not IPropertySymbol { ContainingType: { } owner } property) return null;
        foreach (var member in owner.GetMembers())
        {
            if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary } method
                && BridgeSymbols.Attribute(method, BridgeSymbols.RelayCommand) is not null
                && ToolkitCommandName(method.Name) + "Command" == property.Name)
                return method;
            if (member is IFieldSymbol field && BridgeSymbols.Attribute(field, BridgeSymbols.ObservableProperty) is not null
                && ToolkitPropertyName(field.Name) == property.Name)
                return field;
        }
        return null;
    }

    // CommunityToolkit's [ObservableProperty] field naming: name, _name or m_name becomes Name.
    private static string ToolkitPropertyName(string field)
    {
        var name = field.StartsWith("m_", StringComparison.Ordinal) ? field.Substring(2) : field.TrimStart('_');
        return name.Length == 0 ? field : char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    private sealed partial class ViewModelAnalysis
    {
        // ReactiveUICodegenConvention.InspectCommand and InspectInteraction (RUNICBRIDGE025): a ReactiveUI member needs
        // the Runic ReactiveUI adapter whose helpers the generated Bridge calls, and an interaction of its flavor.
        private void CheckReactiveAdapter(List<IPropertySymbol> commands, IPropertySymbol[] declared)
        {
            var adapter = symbols.ReactiveAdapter;
            foreach (var command in commands)
                if (adapter == BridgeSymbols.ReactiveFlavor.None && BridgeSymbols.IsReactiveCommand(command.Type)
                    && command.Name.EndsWith("Command", StringComparison.Ordinal))
                    Report(context, BridgeRules.ReactiveUiAdapter, command,
                        $"{Name}.{command.Name} is a ReactiveUI command, but the project references no Runic ReactiveUI adapter, whose helpers the generated Bridge calls. Reference Runic.Application.Views.ReactiveUI, or Runic.Application.Views.ReactiveUI.Reactive for ReactiveUI's System.Reactive flavor.");
            foreach (var property in declared)
            {
                if (symbols.IsCommand(property.Type) || !BridgeSymbols.IsReactiveInteraction(property.Type)) continue;
                var flavor = BridgeSymbols.IsSystemReactiveInteraction(property.Type)
                    ? BridgeSymbols.ReactiveFlavor.SystemReactive : BridgeSymbols.ReactiveFlavor.Primitives;
                if (adapter == BridgeSymbols.ReactiveFlavor.None)
                    Report(context, BridgeRules.ReactiveUiAdapter, property,
                        $"{Name}.{property.Name} is a ReactiveUI interaction, but the project references no Runic ReactiveUI adapter, whose helpers the generated Bridge calls. Reference {AdapterFor(flavor)}.");
                else if (flavor != adapter)
                    Report(context, BridgeRules.ReactiveUiAdapter, property,
                        $"{Name}.{property.Name} is a {FlavorName(flavor)} ReactiveUI interaction, but the project references {AdapterFor(adapter)}, which uses {FlavorName(adapter)}. Use one ReactiveUI flavor: reference {AdapterFor(flavor)} instead, or declare the interaction with the {FlavorName(adapter)} ReactiveUI.Binding interface.");
            }
        }

        private static string AdapterFor(BridgeSymbols.ReactiveFlavor flavor) => flavor == BridgeSymbols.ReactiveFlavor.SystemReactive
            ? "Runic.Application.Views.ReactiveUI.Reactive" : "Runic.Application.Views.ReactiveUI";

        private static string FlavorName(BridgeSymbols.ReactiveFlavor flavor) => flavor == BridgeSymbols.ReactiveFlavor.SystemReactive
            ? "System.Reactive" : "ReactiveUI.Primitives";

        // Program.cs ApplyOperationRetirement and ApplyOperationClose (RUNICBRIDGE028, RUNICBRIDGE039): a value that is
        // not one of the enum's, and a declaration that reaches no operation (an asynchronous command).
        private void CheckOperationDeclaration(List<IPropertySymbol> commands, string attribute, string attributeName, DiagnosticDescriptor rule)
        {
            bool? Declared(ISymbol member)
            {
                if (BridgeSymbols.Attribute(member, attribute) is not { ConstructorArguments.Length: 1 } declaration
                    || declaration.ConstructorArguments[0] is not { Value: int value } argument) return null;
                if (argument.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration && enumeration.GetMembers().OfType<IFieldSymbol>()
                    .Any(field => field.HasConstantValue && Convert.ToInt32(field.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == value))
                    return true;
                OwnedWarning(context, rule, member, $"{Name}.{member.MetadataName}: {value} is not a {attributeName} value; the declaration is ignored.");
                return null;
            }
            for (var owner = model; owner is not null && owner.SpecialType != SpecialType.System_Object; owner = owner.BaseType)
                if (Declared(owner) is true) break;
            var operations = commands.Where(command => BridgeSymbols.IsAsyncCommand(command.Type)).ToList();
            foreach (var command in operations)
                for (var owner = command.ContainingType; owner is not null && owner.SpecialType != SpecialType.System_Object; owner = owner.BaseType)
                    if (owner.GetMembers(command.Name).OfType<IPropertySymbol>().FirstOrDefault() is { } declaration && Declared(declaration) is true)
                        break;
            foreach (var property in model.GetMembers().OfType<IPropertySymbol>())
                if (BridgeSymbols.Attribute(property, attribute) is not null && !operations.Any(command => command.Name == property.Name))
                    OwnedWarning(context, rule, property,
                        $"{Name}.{property.Name}: {attributeName} applies only to a Bridge command that starts an operation (an asynchronous CommunityToolkit or ReactiveUI command); it has no effect here.");
            if (operations.Count == 0 && BridgeSymbols.Attribute(model, attribute) is not null)
                OwnedWarning(context, rule, model,
                    $"{Name}: {attributeName} on the class applies to its operations (asynchronous Bridge commands), but it has none; it has no effect here.");
        }

        // GenerateOne, the RunicPresentationScoped loop (RUNICBRIDGE034): on the ViewModel and its base classes, the
        // attribute belongs to a bridged command, its [RelayCommand] method or a base property that a command overrides.
        private void CheckPresentationScoped(List<IPropertySymbol> commands, Dictionary<IPropertySymbol, IMethodSymbol?> sources)
        {
            for (var owner = model; owner is not null && owner.SpecialType != SpecialType.System_Object; owner = owner.BaseType)
                foreach (var member in owner.GetMembers())
                {
                    if (member is not (IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
                        || BridgeSymbols.Attribute(member, BridgeSymbols.RunicPresentationScoped) is null)
                        continue;
                    if (member is IPropertySymbol property && (commands.Contains(property, SymbolEqualityComparer.Default)
                        || commands.Any(command => Overrides(command, property))))
                        continue;
                    if (member is IMethodSymbol method && SymbolEqualityComparer.Default.Equals(owner, model) && IsCommandSource(method, sources))
                        continue;
                    OwnedWarning(context, BridgeRules.PresentationScoped, member,
                        $"{Name}.{member.Name}: RunicPresentationScoped applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method"
                        + (SymbolEqualityComparer.Default.Equals(owner, model) ? "" : $"; {owner.MetadataName} declares it on a member the Bridge does not expose")
                        + "; it has no effect here.");
                }
        }

        private static bool Overrides(IPropertySymbol command, IPropertySymbol baseProperty)
        {
            for (var current = command.OverriddenProperty; current is not null; current = current.OverriddenProperty)
                if (SymbolEqualityComparer.Default.Equals(current, baseProperty)) return true;
            return false;
        }
    }
}
