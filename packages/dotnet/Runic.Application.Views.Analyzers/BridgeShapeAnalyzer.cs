using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The Bridge shape rules that need only the source: the names, accessors and attributes of the ViewModels that a
/// Window or View of this project presents, and of the Views themselves. The Bridge generator runs after a bootstrap
/// compile and is skipped in design-time builds, so without this analyzer these errors appear only after a build.
/// </summary>
/// <remarks>
/// It reports only what the generator reports for the same member, with the same ID and message, and only where the
/// generator runs: in a project whose build generates its own Bridges (RunicApplicationFrontendGenerationEnabled,
/// without RunicApplicationFrontendModelAssembly). In a build the generator runs before CoreCompile and stops the
/// build at these errors, so a build reports each one once, from the generator; the analyzer's copy is the one the
/// IDE shows while you type. Rules that need the compiled assembly stay the generator's alone.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed partial class BridgeShapeAnalyzer : DiagnosticAnalyzer
{
    internal const string GenerationEnabledProperty = "build_property.RunicApplicationFrontendGenerationEnabled";
    internal const string ModelAssemblyProperty = "build_property.RunicApplicationFrontendModelAssembly";

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => BridgeRules.All;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        // Generated code is analyzed and may carry a warning: the generator leaves the warnings of the project's own
        // declarations to this analyzer (OwnedWarning), so one that only generated code declares is reported there.
        // The errors are reported only at hand-written source (SourceLocation).
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var options = start.Options.AnalyzerConfigOptionsProvider.GlobalOptions;
            if (!options.TryGetValue(GenerationEnabledProperty, out var enabled)
                || !string.Equals(enabled.Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return;
            if (options.TryGetValue(ModelAssemblyProperty, out var modelAssembly) && !string.IsNullOrWhiteSpace(modelAssembly))
                return;
            if (BridgeSymbols.Create(start.Compilation) is not { } symbols) return;
            var cancellation = start.CancellationToken;
            var application = new Lazy<BridgeApplication>(() => new BridgeApplication(symbols, cancellation));
            start.RegisterSymbolAction(symbol => Analyze(symbol, symbols, application), SymbolKind.NamedType);
        });
    }

    private static void Analyze(SymbolAnalysisContext context, BridgeSymbols symbols, Lazy<BridgeApplication> application)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (!type.IsAbstract && symbols.ViewModelOf(type) is not null)
        {
            AnalyzeView(context, type);
            AnalyzeApplicationView(context, type, application.Value);
        }
        if (application.Value.Models.Contains(type))
        {
            new ViewModelAnalysis(context, symbols, type, application.Value).Run();
            AnalyzeApplicationModel(context, type, application.Value);
        }
        ReportMissingViews(context, type, application.Value);
    }

    // Program.cs, the View discovery loop and ContractFor.
    private static void AnalyzeView(SymbolAnalysisContext context, INamedTypeSymbol view)
    {
        if (view.IsGenericType) return;
        if (view.TypeKind != TypeKind.Class || view.DeclaredAccessibility != Accessibility.Public || view.ContainingType is not null)
        {
            Report(context, BridgeRules.View, view, $"{FullName(view)}: a Runic Window or View must be a public, top-level concrete class.",
                view.ContainingType is null && view.TypeKind == TypeKind.Class ? BridgeRules.FixPublic : null);
            return;
        }
        if (BridgeSymbols.Attribute(view, BridgeSymbols.RunicViewContract) is { } attribute
            && attribute.ConstructorArguments.Length == 1
            && !IsValidContract(attribute.ConstructorArguments[0].Value as string))
            Report(context, BridgeRules.View, view,
                $"{view.MetadataName}: a View contract must contain only letters and digits and start with a letter.");
    }

    private static bool IsValidContract(string? contract) =>
        !string.IsNullOrWhiteSpace(contract) && char.IsLetter(contract![0]) && contract.All(char.IsLetterOrDigit);

    /// <summary>The checks of one ViewModel, in the order GenerateOne makes them.</summary>
    private sealed partial class ViewModelAnalysis(SymbolAnalysisContext context, BridgeSymbols symbols, INamedTypeSymbol model,
        BridgeApplication application)
    {
        private HashSet<INamedTypeSymbol> models => application.Models;

        private string Name => model.MetadataName;

        internal void Run()
        {
            if (model.IsGenericType) return;
            if (!BridgeSymbols.Implements(model, symbols.NotifyPropertyChanged))
                Report(context, BridgeRules.ViewModel, model, $"{FullName(model)} must implement INotifyPropertyChanged.");
            if (model.ContainingType is not null || model.TypeKind != TypeKind.Class || model.DeclaredAccessibility != Accessibility.Public)
            {
                Report(context, BridgeRules.ViewModel, model, $"{FullName(model)}: a Bridge ViewModel must be a public, top-level class.",
                    model.ContainingType is null && model.TypeKind == TypeKind.Class ? BridgeRules.FixPublic : null);
                if (model.TypeKind != TypeKind.Class) return;
            }

            var declared = BridgedProperties();
            if (declared.Length == 0 && !HasToolkitMembers())
                Report(context, BridgeRules.ViewModel, model, $"{Name}: a ViewModel needs at least one state property, command, or interaction.");

            var commands = new List<IPropertySymbol>();
            foreach (var property in declared)
            {
                if (symbols.IsCommand(property.Type)) commands.Add(property);
                else if (BridgeSymbols.Is(property.Type, symbols.ViewQuestion)) CheckGetOnly(property, "view question");
                else if (BridgeSymbols.IsReactiveInteraction(property.Type)) CheckInteraction(property);
                else if (BridgeSymbols.Is(property.Type, symbols.ViewRequest)) CheckGetOnly(property, "view request");
                else CheckState(property);
            }
            var sources = new Dictionary<IPropertySymbol, IMethodSymbol?>(SymbolEqualityComparer.Default);
            foreach (var command in commands)
                CheckCommand(command, sources[command] = SourceMethod(command));
            foreach (var command in commands)
            {
                CheckFailure(command, sources[command]);
                CheckCommandKey(command, sources[command]);
            }
            var state = StateProperties(declared, commands, sources);
            if (CheckWireNames(state)) CheckClientSurface(state, commands, sources, declared);
            CheckCommandAttributes(commands, sources, BridgeSymbols.RunicFailure, BridgeRules.Failure, "RunicFailure");
            CheckCommandAttributes(commands, sources, BridgeSymbols.RunicCommandKey, BridgeRules.CommandKey, "RunicCommandKey");
            CheckReactiveAdapter(commands, declared);
            CheckOperationDeclaration(commands, BridgeSymbols.RunicOperationRetirement, "RunicOperationRetirement", BridgeRules.OperationRetirement);
            CheckOperationDeclaration(commands, BridgeSymbols.RunicOperationClose, "RunicOperationClose", BridgeRules.OperationClose);
            CheckPresentationScoped(commands, sources);
        }

        private IPropertySymbol[] BridgedProperties() => BridgeShapeAnalyzer.BridgedProperties(model);

        // CommunityToolkit members whose generated properties may not exist yet, as while the IDE's generators run.
        private bool HasToolkitMembers() => model.GetMembers().Any(member =>
            BridgeSymbols.Attribute(member, BridgeSymbols.ObservableProperty) is not null
            || BridgeSymbols.Attribute(member, BridgeSymbols.RelayCommand) is not null);

        // Program.cs, the command loop (RUNICBRIDGE009), and InteractionCodeEmitter.ValidatePublicSurface (RUNICBRIDGE004).
        private void CheckCommand(IPropertySymbol command, IMethodSymbol? source)
        {
            ISymbol at = (ISymbol?)source ?? command;
            if (!command.Name.EndsWith("Command", StringComparison.Ordinal))
            {
                Report(context, BridgeRules.Command, at, $"{Name}.{command.Name}: Bridge commands must end with Command.",
                    BridgeRules.FixRename, command.Name + "Command");
                return;
            }
            // A command of neither library takes its parameter from RunicCommandInput or RunicCommandKey.
            var toolkit = BridgeSymbols.IsToolkitCommand(command.Type);
            var reactive = BridgeSymbols.IsReactiveCommand(command.Type);
            if (!toolkit && !reactive
                && BridgeSymbols.InheritedAttribute(command, BridgeSymbols.RunicCommandInput) is null
                && BridgeSymbols.InheritedAttribute(command, BridgeSymbols.RunicCommandKey) is null
                && (source is null || BridgeSymbols.Attribute(source, BridgeSymbols.RunicCommandKey) is null))
                Report(context, BridgeRules.Command, at,
                    $"{Name}.{command.Name}: unsupported command shape. Use a CommunityToolkit IRelayCommand or IAsyncRelayCommand, a ReactiveUI ReactiveCommand, or an ICommand with RunicCommandInput or RunicCommandKey.");
            else if (!reactive && BridgeSymbols.InheritedAttribute(command, BridgeSymbols.RunicCommandResult) is not null)
                Report(context, BridgeRules.Command, at,
                    $"{Name}.{command.Name}: RunicCommandResult selects a ReactiveUI command's result cardinality; a CommunityToolkit command has the single result of its Task<T> method, and a plain command has none.");
            var name = command.Name.Substring(0, command.Name.Length - "Command".Length);
            if (name.Length == 0 || !BridgeRules.ReservedClientMemberNames.TryGetValue(LowerFirst(name), out var reserved)) return;
            Report(context, BridgeRules.NameCollision, at,
                $"{Name}.{command.Name}: the generated client member '{LowerFirst(name)}' {reserved.Reason}. Rename the command, for example to {reserved.Suggestion}Command (client member '{LowerFirst(reserved.Suggestion)}').",
                BridgeRules.FixRename, source is null ? reserved.Suggestion + "Command" : RenamedSource(source.Name, reserved.Suggestion));
        }

        // BridgeCommandSources.SourceMethod: the [RelayCommand] method that CommunityToolkit generated the command from.
        private IMethodSymbol? SourceMethod(IPropertySymbol command)
        {
            if (!command.Name.EndsWith("Command", StringComparison.Ordinal)) return null;
            var name = command.Name.Substring(0, command.Name.Length - "Command".Length);
            return model.GetMembers().OfType<IMethodSymbol>()
                .Where(method => method.MethodKind == MethodKind.Ordinary && BridgeSymbols.Attribute(method, BridgeSymbols.RelayCommand) is not null)
                .FirstOrDefault(method => ToolkitCommandName(method.Name) == name);
        }

        // InteractionCodeEmitter.Discover: a ReactiveUI interaction.
        private void CheckInteraction(IPropertySymbol property)
        {
            if (property.GetMethod is null)
                Report(context, BridgeRules.Interaction, property, $"{Name}.{property.Name}: an interaction needs a public getter.");
            else if (IsPublic(property.SetMethod))
                Report(context, BridgeRules.Interaction, property,
                    $"{Name}.{property.Name}: interactions are application-owned and cannot have a public setter.", BridgeRules.FixPrivateSetter);
        }

        // InteractionCodeEmitter.RequireGetOnly: a ViewRequest or ViewQuestion.
        private void CheckGetOnly(IPropertySymbol property, string kind)
        {
            if (!IsPublic(property.GetMethod))
                Report(context, BridgeRules.ViewMember, property,
                    $"{Name}.{property.Name}: a {kind} needs a public getter. Expose it as a get-only property, such as {{ get; }} = new(...).");
            else if (IsPublic(property.SetMethod))
                Report(context, BridgeRules.ViewMember, property,
                    $"{Name}.{property.Name}: a {kind} is application-owned and cannot have a public setter. Expose it as a get-only property, such as {{ get; }} = new(...).",
                    BridgeRules.FixPrivateSetter);
        }

        // Program.cs GenerateOne, the state property loop, and QueryCollectionPlan; then the RunicCollection and
        // RunicAppendOnlyText checks. Each property reports its first problem, as the generator does.
        private void CheckState(IPropertySymbol property)
        {
            if (property.GetMethod is null) return;
            var type = property.Type;
            var publicSetter = IsPublic(property.SetMethod);
            if (BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicRegionCollection) is { } regionCollection)
            {
                if (!BridgeSymbols.Is(type, symbols.NavigationSelection))
                    Report(context, BridgeRules.RegionCollection, property,
                        $"{Name}.{property.Name}: RunicRegionCollection applies to a NavigationSelection property, not to a {ReflectionName(type)}.");
                else if (publicSetter)
                    Report(context, BridgeRules.RegionCollection, property,
                        $"{Name}.{property.Name}: a region collection must be get-only; its Add, SelectAsync and RemoveAsync, not the web view, change it.",
                        BridgeRules.FixPrivateSetter);
                else if (regionCollection.ConstructorArguments.Length == 1
                    && regionCollection.ConstructorArguments[0].Value is ITypeSymbol { SpecialType: SpecialType.System_Object })
                    Report(context, BridgeRules.RegionCollection, property,
                        $"{Name}.{property.Name}: a region collection of object has no known content. Name an interface or base class that the regions' ViewModels share.");
                else if (regionCollection.ConstructorArguments.Length == 1 && regionCollection.ConstructorArguments[0].Value is ITypeSymbol content)
                {
                    if (!HasContentModel(content))
                        Report(context, BridgeRules.RegionCollection, property,
                            $"{Name}.{property.Name}: no ViewModel with a registered View is a {ReflectionName(content)}, so the region collection can present nothing.");
                    else CheckSlotContract(property, content);
                }
                return;
            }
            if (BridgeSymbols.Is(type, symbols.QueryCollection) && type is INamedTypeSymbol { TypeArguments.Length: 2 } query)
            {
                var key = query.TypeArguments[1];
                if (publicSetter)
                    Report(context, BridgeRules.QueryCollection, property,
                        $"{Name}.{property.Name}: a RunicQueryCollection must be get-only; the page changes its query with setQuery.", BridgeRules.FixPrivateSetter);
                else if (BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is not null
                    || BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicAppendOnlyText) is not null)
                    Report(context, BridgeRules.QueryCollection, property,
                        $"{Name}.{property.Name}: a RunicQueryCollection has its own keys; remove [RunicCollection] or [RunicAppendOnlyText].");
                else if (key.SpecialType is not (SpecialType.System_String or SpecialType.System_Int32) && BridgeSymbols.MetadataName(key) != "System.Guid")
                    Report(context, BridgeRules.QueryCollection, property,
                        $"{Name}.{property.Name}: a RunicQueryCollection key is a string, Guid or Int32, not {ReflectionName(key)}.");
                return;
            }
            if (BridgeSymbols.Is(type, symbols.NavigationRegion) && type is INamedTypeSymbol { TypeArguments.Length: 1 } region)
            {
                var content = region.TypeArguments[0];
                if (publicSetter)
                    Report(context, BridgeRules.State, property,
                        $"{Name}.{property.Name}: a NavigationRegion slot must be get-only; its navigator, not the web view, changes the content.",
                        BridgeRules.FixPrivateSetter);
                else if (content.SpecialType == SpecialType.System_Object)
                    Report(context, BridgeRules.State, property,
                        $"{Name}.{property.Name}: a NavigationRegion<object> slot has no known content. Use an interface or base class that the region's ViewModels share.");
                else if (!HasContentModel(content))
                    Report(context, BridgeRules.State, property,
                        $"{Name}.{property.Name}: no ViewModel with a registered View is a {ReflectionName(content)}, so the NavigationRegion slot can present nothing.");
                else CheckSlotContract(property, content);
                return;
            }
            if (BridgeSymbols.Is(type, symbols.NavigationSelection))
            {
                if (publicSetter)
                    Report(context, BridgeRules.State, property,
                        $"{Name}.{property.Name}: a NavigationSelection slot must be get-only; its SelectAsync, not the web view, changes the selection. Select through a command.",
                        BridgeRules.FixPrivateSetter);
                return;
            }
            // ViewModel content: a slot that another presented ViewModel fits.
            if (type.SpecialType != SpecialType.System_Object && models.Any(other => !SymbolEqualityComparer.Default.Equals(other, model) && IsAssignable(other, type)))
            {
                if (publicSetter)
                    Report(context, BridgeRules.State, property, $"{Name}.{property.Name}: ViewModel content must be set by .NET, not the web view.",
                        BridgeRules.FixPrivateSetter);
                else CheckSlotContract(property, type);
                return;
            }
            if (BridgeSymbols.Is(type, symbols.ReadOnlyList) && type is INamedTypeSymbol { TypeArguments.Length: 1 } list)
            {
                var item = list.TypeArguments[0];
                if (item.SpecialType != SpecialType.System_Object && HasContentModel(item))
                {
                    if (publicSetter)
                        Report(context, BridgeRules.State, property, $"{Name}.{property.Name}: ViewModel collections must be set by .NET, not the web view.",
                            BridgeRules.FixPrivateSetter);
                    else CheckSlotContract(property, item);
                    return;
                }
                // A list of ViewModels that no View presents. A [RunicCollection] of ViewModel rows is reported as a value.
                if (item.SpecialType == SpecialType.System_Object || BridgeSymbols.Implements(item, symbols.NotifyPropertyChanged)
                    && BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is null)
                {
                    Report(context, BridgeRules.State, property,
                        $"{Name}.{property.Name}: a ViewModel collection needs a specific item interface or base class with a registered View.");
                    return;
                }
            }
            // A value property. Only the setter is certain from the source: whether the rows are DTOs is the generator's.
            if (publicSetter && BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is not null && HasDataRows(type))
                Report(context, BridgeRules.Collection, property,
                    $"{Name}.{property.Name}: RunicCollection requires a nonnullable, read-only collection of nonnullable DTO rows.",
                    BridgeRules.FixPrivateSetter);
            // A bool may be a command's availability, which is not a state property.
            if (type.SpecialType != SpecialType.System_Boolean
                && BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicAppendOnlyText) is not null
                && (type.SpecialType != SpecialType.System_String || publicSetter))
                Report(context, BridgeRules.State, property,
                    $"{Name}.{property.Name}: RunicAppendOnlyText requires a read-only string state property.",
                    type.SpecialType == SpecialType.System_String ? BridgeRules.FixPrivateSetter : null);
        }

        // A collection whose rows are not ViewModels: the generator would report ViewModel rows with another message.
        private bool HasDataRows(ITypeSymbol type)
        {
            var item = type is IArrayTypeSymbol array ? array.ElementType
                : BridgeSymbols.SelfBasesAndInterfaces(type).OfType<INamedTypeSymbol>()
                    .FirstOrDefault(candidate => BridgeSymbols.Is(candidate, symbols.Enumerable))?.TypeArguments[0];
            return item is not null && item.SpecialType != SpecialType.System_Object && item.TypeKind != TypeKind.TypeParameter
                && !BridgeSymbols.Implements(item, symbols.NotifyPropertyChanged);
        }

        // Program.cs, the RunicFailure and RunicCommandKey loops, for the ViewModel's own members: either belongs to a
        // Bridge command property or its [RelayCommand] method. A base class's members are left to the generator.
        private void CheckCommandAttributes(List<IPropertySymbol> commands, Dictionary<IPropertySymbol, IMethodSymbol?> sources,
            string attribute, DiagnosticDescriptor rule, string attributeName)
        {
            foreach (var member in model.GetMembers())
            {
                if (member is not (IPropertySymbol or IMethodSymbol { MethodKind: MethodKind.Ordinary })
                    || BridgeSymbols.Attribute(member, attribute) is null)
                    continue;
                if (member is IPropertySymbol property && commands.Contains(property, SymbolEqualityComparer.Default)) continue;
                if (member is IMethodSymbol method && IsCommandSource(method, sources)) continue;
                Report(context, rule, member,
                    $"{Name}.{member.Name}: {attributeName} applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method.");
            }
        }

        // A [RelayCommand] method of a bridged command, or one whose command CommunityToolkit has not generated yet.
        private bool IsCommandSource(IMethodSymbol method, Dictionary<IPropertySymbol, IMethodSymbol?> sources)
        {
            if (sources.Values.Contains(method, SymbolEqualityComparer.Default)) return true;
            if (BridgeSymbols.Attribute(method, BridgeSymbols.RelayCommand) is null) return false;
            var generated = ToolkitCommandName(method.Name) + "Command";
            return !model.GetMembers(generated).OfType<IPropertySymbol>().Any();
        }

        private bool IsAssignable(ITypeSymbol source, ITypeSymbol destination) => symbols.IsAssignable(source, destination);

        // Program.cs ContractFor and the content switches of the generated Bridge (RUNICBRIDGE006): a slot's
        // [RunicViewContract] is valid, and each ViewModel it admits, most derived first, has a View of that contract.
        private void CheckSlotContract(IPropertySymbol property, ITypeSymbol slot)
        {
            if (!TryContract(property, out var contract))
            {
                Report(context, BridgeRules.View, property,
                    $"{Name}.{property.Name}: a View contract must contain only letters and digits and start with a letter.");
                return;
            }
            var missing = models.Where(other => !SymbolEqualityComparer.Default.Equals(other, model) && IsAssignable(other, slot))
                .OrderByDescending(InheritanceDepth).ThenBy(BridgeSymbols.MetadataName, StringComparer.Ordinal)
                .FirstOrDefault(other => !application.ValidViews(other).Any(view => ContractOf(view) == contract));
            if (missing is not null)
                Report(context, BridgeRules.View, property,
                    $"{Name}.{property.Name}: no {missing.MetadataName} View has contract '{contract ?? "default"}'.");
        }

        private static int InheritanceDepth(INamedTypeSymbol type)
        {
            var depth = 0;
            for (var current = type.BaseType; current is not null; current = current.BaseType) depth++;
            return depth;
        }

        // A ViewModel with a View, other than this one, that the slot type admits.
        private bool HasContentModel(ITypeSymbol slot) =>
            models.Any(other => !SymbolEqualityComparer.Default.Equals(other, model) && IsAssignable(other, slot));
    }

    private static bool IsPublic(IMethodSymbol? accessor) => accessor?.DeclaredAccessibility == Accessibility.Public;

    // Program.cs BridgedProperties: public, declared, instance, not [RunicIgnore] and not indexers.
    internal static IPropertySymbol[] BridgedProperties(INamedTypeSymbol model) => model.GetMembers().OfType<IPropertySymbol>()
        .Where(property => !property.IsStatic && !property.IsIndexer
            && (IsPublic(property.GetMethod) || IsPublic(property.SetMethod))
            && !IsRunicIgnored(property))
        .ToArray();

    private static bool IsRunicIgnored(IPropertySymbol property)
    {
        for (IPropertySymbol? current = property; current is not null; current = current.OverriddenProperty)
            if (BridgeSymbols.Attribute(current, BridgeSymbols.RunicIgnore) is not null) return true;
        return false;
    }

    // CommunityToolkitConventions.CommandName: [RelayCommand] turns Save, SaveAsync or OnSave into SaveCommand.
    internal static string ToolkitCommandName(string method)
    {
        if (method.Length > 2 && method.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(method[2]))
            method = method.Substring(2);
        if (method.Length > "Async".Length && method.EndsWith("Async", StringComparison.Ordinal))
            method = method.Substring(0, method.Length - "Async".Length);
        return method;
    }

    // The [RelayCommand] method name that generates {suggestion}Command, keeping an On prefix and an Async suffix.
    private static string RenamedSource(string method, string suggestion)
    {
        var prefix = method.Length > 2 && method.StartsWith("On", StringComparison.Ordinal) && char.IsUpper(method[2]) ? "On" : "";
        var rest = method.Substring(prefix.Length);
        var suffix = rest.Length > "Async".Length && rest.EndsWith("Async", StringComparison.Ordinal) ? "Async" : "";
        return prefix + suggestion + suffix;
    }

    private static string LowerFirst(string text) => char.ToLowerInvariant(text[0]) + text.Substring(1);

    // Type.FullName of a non-generic type: the namespace, then nested types joined by '+'.
    private static string FullName(INamedTypeSymbol type) => BridgeSymbols.MetadataName(type);

    // Type.Name: the metadata name, with [] for an array.
    private static string ReflectionName(ITypeSymbol type) => type switch
    {
        IArrayTypeSymbol array => ReflectionName(array.ElementType) + "[" + new string(',', array.Rank - 1) + "]",
        INamedTypeSymbol named => named.MetadataName,
        _ => type.Name,
    };

    private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor rule, ISymbol symbol, string message,
        string? fix = null, string? newName = null) =>
        Report(context, rule, SourceLocation(symbol, context.CancellationToken), message, fix, newName);

    private static void Report(SymbolAnalysisContext context, DiagnosticDescriptor rule, Location? location, string message,
        string? fix = null, string? newName = null)
    {
        // A member that only generated code declares has no line to point at; the generator reports it after a build.
        if (location is null) return;
        var properties = ImmutableDictionary<string, string?>.Empty;
        if (fix is not null) properties = properties.Add(BridgeRules.FixKey, fix);
        if (newName is not null) properties = properties.Add(BridgeRules.NewNameKey, newName);
        context.ReportDiagnostic(Diagnostic.Create(rule, location, properties, message));
    }

    // The member's declaration in hand-written source, not in a source generator's or designer's output.
    private static Location? SourceLocation(ISymbol symbol, CancellationToken cancellationToken) =>
        symbol.Locations.FirstOrDefault(location => location.IsInSource && !IsGenerated(location.SourceTree!, cancellationToken));

    private static bool IsGenerated(SyntaxTree tree, CancellationToken cancellationToken)
    {
        var path = tree.FilePath;
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
            return true;
        foreach (var trivia in tree.GetRoot(cancellationToken).GetLeadingTrivia())
            if (trivia.ToString().IndexOf("<auto-generated", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }
}
