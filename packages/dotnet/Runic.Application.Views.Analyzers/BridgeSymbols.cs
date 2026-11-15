using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The types the Bridge shape rules name, resolved once per compilation. The generator matches most of them by
/// metadata name; so do these, and the ReactiveUI types, which the analyzer never references, only by name.
/// </summary>
internal sealed class BridgeSymbols
{
    private BridgeSymbols(Compilation compilation, INamedTypeSymbol runicView)
    {
        Compilation = compilation;
        RunicView = runicView;
        RunicWindow = compilation.GetTypeByMetadataName("Runic.Application.Views.RunicWindow`1");
        NotifyPropertyChanged = compilation.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged");
        Command = compilation.GetTypeByMetadataName("System.Windows.Input.ICommand");
        ReadOnlyList = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        Enumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        ViewRequest = compilation.GetTypeByMetadataName("Runic.Application.Views.ViewRequest`1");
        ViewQuestion = compilation.GetTypeByMetadataName("Runic.Application.Views.ViewQuestion`2");
        QueryCollection = compilation.GetTypeByMetadataName("Runic.Application.Views.RunicQueryCollection`2");
        NavigationRegion = compilation.GetTypeByMetadataName("Runic.Navigation.NavigationRegion`1");
        NavigationSelection = compilation.GetTypeByMetadataName("Runic.Navigation.NavigationSelection");
        // The Runic ReactiveUI adapter this compilation references, as the targets pass --reactiveui-flavor from
        // ReferencePath (RUNICBRIDGE023 reports both).
        var references = new HashSet<string>(compilation.ReferencedAssemblyNames.Select(name => name.Name), StringComparer.Ordinal);
        ReactiveAdapter = references.Contains("Runic.Application.Views.ReactiveUI.Reactive") ? ReactiveFlavor.SystemReactive
            : references.Contains("Runic.Application.Views.ReactiveUI") ? ReactiveFlavor.Primitives : ReactiveFlavor.None;
    }

    internal enum ReactiveFlavor { None, Primitives, SystemReactive }

    internal ReactiveFlavor ReactiveAdapter { get; }

    internal Compilation Compilation { get; }
    internal INamedTypeSymbol RunicView { get; }
    internal INamedTypeSymbol? RunicWindow { get; }
    internal INamedTypeSymbol? NotifyPropertyChanged { get; }
    internal INamedTypeSymbol? Command { get; }
    internal INamedTypeSymbol? ReadOnlyList { get; }
    internal INamedTypeSymbol? Enumerable { get; }
    internal INamedTypeSymbol? ViewRequest { get; }
    internal INamedTypeSymbol? ViewQuestion { get; }
    internal INamedTypeSymbol? QueryCollection { get; }
    internal INamedTypeSymbol? NavigationRegion { get; }
    internal INamedTypeSymbol? NavigationSelection { get; }

    internal const string RunicIgnore = "Runic.Application.Views.RunicIgnoreAttribute";
    internal const string RunicViewContract = "Runic.Application.Views.RunicViewContractAttribute";
    internal const string RunicCollection = "Runic.Application.Views.RunicCollectionAttribute";
    internal const string RunicAppendOnlyText = "Runic.Application.Views.RunicAppendOnlyTextAttribute";
    internal const string RunicRegionCollection = "Runic.Application.Views.RunicRegionCollectionAttribute";
    internal const string RunicFailure = "Runic.Application.Views.RunicFailureAttribute";
    internal const string RunicCommandKey = "Runic.Application.Views.RunicCommandKeyAttribute";
    internal const string RunicCommandInput = "Runic.Application.Views.RunicCommandInputAttribute";
    internal const string RunicCommandResult = "Runic.Application.Views.RunicCommandResultAttribute";
    internal const string RelayCommand = "CommunityToolkit.Mvvm.Input.RelayCommandAttribute";
    internal const string ObservableProperty = "CommunityToolkit.Mvvm.ComponentModel.ObservablePropertyAttribute";
    internal const string RunicOperationRetirement = "Runic.Application.Views.RunicOperationRetirementAttribute";
    internal const string RunicOperationClose = "Runic.Application.Views.RunicOperationCloseAttribute";
    internal const string RunicPresentationScoped = "Runic.Application.Views.RunicPresentationScopedAttribute";
    private const string AsyncToolkitCommand = "CommunityToolkit.Mvvm.Input.IAsyncRelayCommand";
    private const string ReactivePrimitivesInteraction = Interaction;
    private const string ReactiveCommand = "ReactiveUI.IReactiveCommand`2";
    private const string ToolkitCommand = "CommunityToolkit.Mvvm.Input.IRelayCommand";
    private const string Interaction = "ReactiveUI.Binding.IInteraction`2";
    private const string ReactiveInteraction = "ReactiveUI.Binding.Reactive.IInteraction`2";

    /// <summary>The symbols of a compilation that references Runic.Application.Views, or null.</summary>
    internal static BridgeSymbols? Create(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Runic.Application.Views.RunicView`1") is { } runicView
            ? new BridgeSymbols(compilation, runicView) : null;

    /// <summary>The ViewModel of a Runic Window or View: the type argument of its RunicView&lt;T&gt; base class.</summary>
    internal INamedTypeSymbol? ViewModelOf(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
            if (current.IsGenericType && SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, RunicView))
                return current.TypeArguments[0] as INamedTypeSymbol;
        return null;
    }

    /// <summary>
    /// The ViewModels that the generator would bridge: those of the concrete Windows and Views this compilation
    /// declares, as the generator finds them in the model assembly. A ViewModel may come from a referenced assembly.
    /// </summary>
    internal HashSet<INamedTypeSymbol> FindViewModels(CancellationToken cancellationToken) =>
        new(FindViews(cancellationToken).Keys, SymbolEqualityComparer.Default);

    /// <summary>
    /// The concrete Windows and Views this compilation declares, by ViewModel, in declaration order (the order of the
    /// syntax trees, then of the declarations), as the generator finds them in the model assembly's type order.
    /// </summary>
    internal Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>> FindViews(CancellationToken cancellationToken)
    {
        var views = new List<INamedTypeSymbol>();
        var pending = new Stack<INamespaceOrTypeSymbol>();
        pending.Push(Compilation.Assembly.GlobalNamespace);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var container = pending.Pop();
            foreach (var member in container.GetMembers())
            {
                if (member is INamespaceSymbol ns) pending.Push(ns);
                else if (member is INamedTypeSymbol type)
                {
                    pending.Push(type);
                    if (!type.IsAbstract && ViewModelOf(type) is not null) views.Add(type);
                }
            }
        }
        var order = new Dictionary<SyntaxTree, int>();
        foreach (var tree in Compilation.SyntaxTrees) order[tree] = order.Count;
        var byModel = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var view in views
            .OrderBy(view => view.Locations.FirstOrDefault(location => location.IsInSource) is { SourceTree: { } tree } location
                ? (order.TryGetValue(tree, out var index) ? index : int.MaxValue, location.SourceSpan.Start) : (int.MaxValue, 0))
            .ThenBy(view => MetadataName(view), StringComparer.Ordinal))
        {
            var model = ViewModelOf(view)!;
            if (!byModel.TryGetValue(model, out var variants)) byModel.Add(model, variants = []);
            variants.Add(view);
        }
        return byModel;
    }

    /// <summary>A Runic Window: a RunicWindow&lt;T&gt; in the base classes (Program.cs IsWindow).</summary>
    internal bool IsWindow(INamedTypeSymbol view)
    {
        for (var current = view.BaseType; current is not null; current = current.BaseType)
            if (Is(current, RunicWindow)) return true;
        return false;
    }

    /// <summary>Type.IsAssignableFrom: an identity or implicit reference conversion from <paramref name="source"/>.</summary>
    internal bool IsAssignable(ITypeSymbol source, ITypeSymbol destination)
    {
        var conversion = Compilation.ClassifyCommonConversion(source, destination);
        return conversion.IsIdentity || conversion.IsImplicit && conversion.IsReference;
    }

    internal static bool Implements(ITypeSymbol type, INamedTypeSymbol? contract) =>
        contract is not null && (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, contract)
            || type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, contract)));

    internal static bool Is(ITypeSymbol type, INamedTypeSymbol? definition) =>
        definition is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, definition);

    /// <summary>A Bridge command: an ICommand, or a ReactiveUI command of either flavor.</summary>
    internal bool IsCommand(ITypeSymbol type) =>
        Implements(type, Command) || IsReactiveCommand(type);

    /// <summary>A CommunityToolkit command: every IRelayCommand and IAsyncRelayCommand, generic or not, is an IRelayCommand.</summary>
    internal static bool IsToolkitCommand(ITypeSymbol type) =>
        SelfBasesAndInterfaces(type).Any(candidate => MetadataName(candidate) == ToolkitCommand);

    /// <summary>A ReactiveUI command of either flavor: IReactiveCommand&lt;TParam, TResult&gt;.</summary>
    internal static bool IsReactiveCommand(ITypeSymbol type) =>
        SelfBasesAndInterfaces(type).Any(candidate => MetadataName(candidate) == ReactiveCommand);

    /// <summary>
    /// A command that starts an operation (GeneratedCommandPlan.IsAsync): a CommunityToolkit IAsyncRelayCommand or a
    /// ReactiveUI command. A plain ICommand, with RunicCommandInput or RunicCommandKey, is synchronous.
    /// </summary>
    internal static bool IsAsyncCommand(ITypeSymbol type) =>
        IsReactiveCommand(type) || SelfBasesAndInterfaces(type).Any(candidate => MetadataName(candidate) == AsyncToolkitCommand);

    /// <summary>The flavor of a ReactiveUI Binding interaction: true for ReactiveUI.Binding.Reactive's (System.Reactive).</summary>
    internal static bool IsSystemReactiveInteraction(ITypeSymbol type) =>
        SelfBasesAndInterfaces(type).Any(candidate => MetadataName(candidate) == ReactiveInteraction)
        && !SelfBasesAndInterfaces(type).Any(candidate => MetadataName(candidate) == ReactivePrimitivesInteraction);

    /// <summary>A ReactiveUI Binding interaction of either flavor.</summary>
    internal static bool IsReactiveInteraction(ITypeSymbol type) =>
        SelfBasesAndInterfaces(type).Any(candidate => MetadataName(candidate) is Interaction or ReactiveInteraction);

    internal static IEnumerable<ITypeSymbol> SelfBasesAndInterfaces(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            yield return current;
        foreach (var candidate in type.AllInterfaces) yield return candidate;
    }

    /// <summary>The metadata name of a type's definition, such as <c>ReactiveUI.IReactiveCommand`2</c>.</summary>
    internal static string MetadataName(ITypeSymbol type)
    {
        var definition = type.OriginalDefinition;
        var name = definition.MetadataName;
        for (var owner = definition.ContainingType; owner is not null; owner = owner.ContainingType)
            name = owner.MetadataName + "+" + name;
        return definition.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + name : name;
    }

    /// <summary>The attribute of the given metadata name declared on the symbol itself.</summary>
    internal static AttributeData? Attribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass is { } type && MetadataName(type) == name);

    /// <summary>The attribute on a property or on the property it overrides, as GetCustomAttribute(inherit: true) finds it.</summary>
    internal static AttributeData? InheritedAttribute(IPropertySymbol property, string name)
    {
        for (IPropertySymbol? current = property; current is not null; current = current.OverriddenProperty)
            if (Attribute(current, name) is { } attribute) return attribute;
        return null;
    }
}
