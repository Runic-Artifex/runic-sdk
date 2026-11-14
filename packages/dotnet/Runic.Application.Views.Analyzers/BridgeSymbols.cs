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
        NotifyPropertyChanged = compilation.GetTypeByMetadataName("System.ComponentModel.INotifyPropertyChanged");
        Command = compilation.GetTypeByMetadataName("System.Windows.Input.ICommand");
        ReadOnlyList = compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1");
        Enumerable = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1");
        ViewRequest = compilation.GetTypeByMetadataName("Runic.Application.Views.ViewRequest`1");
        ViewQuestion = compilation.GetTypeByMetadataName("Runic.Application.Views.ViewQuestion`2");
        QueryCollection = compilation.GetTypeByMetadataName("Runic.Application.Views.RunicQueryCollection`2");
        NavigationRegion = compilation.GetTypeByMetadataName("Runic.Navigation.NavigationRegion`1");
        NavigationSelection = compilation.GetTypeByMetadataName("Runic.Navigation.NavigationSelection");
    }

    internal Compilation Compilation { get; }
    internal INamedTypeSymbol RunicView { get; }
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
    internal HashSet<INamedTypeSymbol> FindViewModels(CancellationToken cancellationToken)
    {
        var models = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
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
                    if (!type.IsAbstract && ViewModelOf(type) is { } model) models.Add(model);
                }
            }
        }
        return models;
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
