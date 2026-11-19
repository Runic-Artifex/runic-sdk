using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The names of a ViewModel's generated client and its declarations about commands: the state wire names
/// (Program.cs WireName and GenerateOne, RUNICBRIDGE004 and 008), the client surface
/// (InteractionCodeEmitter.ValidatePublicSurface, RUNICBRIDGE004), the region collections' select and close methods
/// (RUNICBRIDGE071), and the RunicFailure and RunicCommandKey declarations of a command (FailureGraph and CommandKey,
/// RUNICBRIDGE012 and 027).
/// </summary>
public sealed partial class BridgeShapeAnalyzer
{
    private sealed partial class ViewModelAnalysis
    {
        private const string ToolkitInputCommand = "CommunityToolkit.Mvvm.Input.IRelayCommand`1";
        private const string ToolkitAsyncInputCommand = "CommunityToolkit.Mvvm.Input.IAsyncRelayCommand`1";
        private const string ReactiveCommand = "ReactiveUI.IReactiveCommand`2";
        private const string RunicAlias = "Runic.Application.Views.RunicAliasAttribute";
        private const string JsonPropertyName = "System.Text.Json.Serialization.JsonPropertyNameAttribute";
        private const string NotifyDataErrorInfo = "System.ComponentModel.INotifyDataErrorInfo";
        private const string PendingValidation = "Runic.Application.Views.IRunicPendingValidation";
        private const string RunicBlob = "Runic.Application.Views.RunicBlob";

        // A command's library shape, as the MVVM conventions inspect it: whether the client passes an argument,
        // whether it starts an operation, and the parameter a RunicCommandKey resolver must return.
        private readonly struct CommandShape(bool library, bool hasInput, ITypeSymbol? input, bool isAsync)
        {
            internal bool Library { get; } = library;
            internal bool HasInput { get; } = hasInput;
            internal ITypeSymbol? Input { get; } = input;
            internal bool IsAsync { get; } = isAsync;
        }

        private static CommandShape ShapeOf(IPropertySymbol command)
        {
            var type = command.Type;
            var candidates = BridgeSymbols.SelfBasesAndInterfaces(type).OfType<INamedTypeSymbol>().ToArray();
            if (candidates.FirstOrDefault(candidate => BridgeSymbols.MetadataName(candidate) == ReactiveCommand) is { } reactive)
            {
                var input = reactive.TypeArguments[0];
                var hasInput = BridgeSymbols.MetadataName(input) is not ("ReactiveUI.Primitives.RxVoid" or "System.Reactive.Unit")
                    && !IsProgress(input);
                return new CommandShape(true, hasInput, hasInput ? input : null, true);
            }
            if (candidates.FirstOrDefault(candidate => BridgeSymbols.MetadataName(candidate) == ToolkitAsyncInputCommand) is { } asyncInput)
                return IsProgress(asyncInput.TypeArguments[0])
                    ? new CommandShape(true, false, null, true)
                    : new CommandShape(true, true, asyncInput.TypeArguments[0], true);
            if (BridgeSymbols.IsAsyncCommand(type)) return new CommandShape(true, false, null, true);
            if (candidates.FirstOrDefault(candidate => BridgeSymbols.MetadataName(candidate) == ToolkitInputCommand) is { } syncInput)
                return new CommandShape(true, true, syncInput.TypeArguments[0], false);
            if (BridgeSymbols.IsToolkitCommand(type)) return new CommandShape(true, false, null, false);
            return new CommandShape(false, false, null, false);
        }

        // An async command's IProgress<double> parameter, which the Bridge binds to the running operation
        // (RunicOperationProgress.Current, #1180): it is no page input (ToolkitCommandContract.TakesProgress).
        private static bool IsProgress(ITypeSymbol type) =>
            type is INamedTypeSymbol { IsGenericType: true } named && named.TypeArguments.Length == 1
            && named.TypeArguments[0].SpecialType == SpecialType.System_Double
            && BridgeSymbols.MetadataName(named.OriginalDefinition) == "System.IProgress`1";

        // GeneratedCommandPlan.HasArgument: a library input, a RunicCommandInput or a RunicCommandKey key.
        private static bool HasArgument(IPropertySymbol command, IMethodSymbol? source, CommandShape shape) =>
            shape.HasInput || BridgeSymbols.InheritedAttribute(command, BridgeSymbols.RunicCommandInput) is not null
            || KeyDeclarations(command, source).Count > 0;

        // Program.cs WireName: [RunicAlias], else [JsonPropertyName], else the name with a lowercase first letter.
        private static string WireName(IPropertySymbol property) =>
            (BridgeSymbols.InheritedAttribute(property, RunicAlias) ?? BridgeSymbols.InheritedAttribute(property, JsonPropertyName))
                is { ConstructorArguments.Length: 1 } attribute && attribute.ConstructorArguments[0].Value is string name
                ? name : LowerFirst(property.Name);

        // The state properties of GenerateOne: the bridged properties that are no command, interaction or view request,
        // and no availability of a command without an argument ([RelayCommand(CanExecute = nameof(CanSave))] Save).
        private IPropertySymbol[] StateProperties(IPropertySymbol[] declared, List<IPropertySymbol> commands,
            Dictionary<IPropertySymbol, IMethodSymbol?> sources) =>
            declared.Where(property => !commands.Contains(property, SymbolEqualityComparer.Default)
                    && !BridgeSymbols.Is(property.Type, symbols.ViewQuestion) && !BridgeSymbols.IsReactiveInteraction(property.Type)
                    && !BridgeSymbols.Is(property.Type, symbols.ViewRequest)
                    && !IsCommandAvailability(property, commands, sources))
                .ToArray();

        private static bool IsCommandAvailability(IPropertySymbol property, List<IPropertySymbol> commands, Dictionary<IPropertySymbol, IMethodSymbol?> sources) =>
            property.Type.SpecialType == SpecialType.System_Boolean && commands.Any(command =>
                command.Name.EndsWith("Command", StringComparison.Ordinal)
                && WireName(property) == "can" + command.Name.Substring(0, command.Name.Length - "Command".Length)
                && ShapeOf(command) is { Library: true, HasInput: false }
                && sources.TryGetValue(command, out var source) && source is not null
                && BridgeSymbols.Attribute(source, BridgeSymbols.RelayCommand) is { } relay
                && relay.NamedArguments.Any(argument => argument.Key == "CanExecute" && argument.Value.Value as string == property.Name));

        // GenerateOne: every state property has a wire name (RUNICBRIDGE008), and no two share one (RUNICBRIDGE004).
        // Returns whether they are all usable, as the generator, which stops at the first, then checks the client.
        private bool CheckWireNames(IPropertySymbol[] state)
        {
            var usable = true;
            // A blank [RunicAlias] throws when the generator reads it; a blank [JsonPropertyName] is this rule.
            foreach (var property in state.Where(property => string.IsNullOrWhiteSpace(WireName(property))
                && BridgeSymbols.InheritedAttribute(property, RunicAlias) is null))
            {
                Report(context, BridgeRules.State, property,
                    $"{property.ContainingType.MetadataName}.{property.Name}: a bridge wire name is required.");
                usable = false;
            }
            foreach (var group in state.Where(property => !string.IsNullOrWhiteSpace(WireName(property)))
                .GroupBy(WireName, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                // The last may be a CommunityToolkit [ObservableProperty]: then at its field, else at the ViewModel.
                var last = group.Last();
                Report(context, BridgeRules.NameCollision,
                    SourceLocation(last, context.CancellationToken)
                        ?? (ToolkitSource(last) is { } field ? SourceLocation(field, context.CancellationToken) : null)
                        ?? SourceLocation(model, context.CancellationToken),
                    $"{Name}: state properties {string.Join(" and ", group.Select(property => property.Name))} share the generated wire name '{group.Key}'.");
                usable = false;
            }
            return usable;
        }

        // InteractionCodeEmitter.ValidatePublicSurface and the reserved validation member (RUNICBRIDGE004): the
        // generated state, client, interaction and request members are unique. The validation state follows from the
        // ViewModel and from the types of its values (GenerateOne's hasValidation).
        private void CheckClientSurface(IPropertySymbol[] state, List<IPropertySymbol> commands, Dictionary<IPropertySymbol, IMethodSymbol?> sources,
            IPropertySymbol[] declared, Dictionary<IPropertySymbol, BridgeValueGraph> values)
        {
            static bool Is(ITypeSymbol type, params string[] names) => BridgeSymbols.SelfBasesAndInterfaces(type).Any(candidate => names.Contains(BridgeSymbols.MetadataName(candidate)));
            var hasErrors = Is(model, NotifyDataErrorInfo);
            var valueTypes = values.Values.SelectMany(graph => graph.Nodes).Select(node => node.Type).ToArray();
            var hasValidation = hasErrors
                || valueTypes.Prepend(model).Any(type => Is(type, PendingValidation, "ReactiveUI.Validation.Abstractions.IValidatableViewModel", "ReactiveUI.Validation.Reactive.Abstractions.IValidatableViewModel"))
                || valueTypes.Any(type => Is(type, NotifyDataErrorInfo));
            if (hasValidation && state.FirstOrDefault(property => WireName(property) == "validation") is { } validation)
            {
                Report(context, BridgeRules.NameCollision, validation,
                    $"{Name}.{validation.Name}: validation is reserved for generated validation state. Alias the model property.");
                return;
            }
            var interactions = declared.Where(property => BridgeSymbols.Is(property.Type, symbols.ViewQuestion) || BridgeSymbols.IsReactiveInteraction(property.Type)).ToArray();
            var requests = declared.Where(property => BridgeSymbols.Is(property.Type, symbols.ViewRequest)).ToArray();
            foreach (var group in interactions.GroupBy(property => LowerFirst(property.Name), StringComparer.Ordinal).Where(group => group.Count() > 1))
                foreach (var duplicate in group.Skip(1))
                    Report(context, BridgeRules.NameCollision, duplicate, $"{Name}.{duplicate.Name}: duplicate generated interaction name '{group.Key}'.");
            foreach (var group in requests.GroupBy(property => LowerFirst(property.Name), StringComparer.Ordinal).Where(group => group.Count() > 1))
                foreach (var duplicate in group.Skip(1))
                    Report(context, BridgeRules.NameCollision, duplicate, $"{Name}.{duplicate.Name}: duplicate generated view request name '{group.Key}'.");
            var named = commands.Where(command => command.Name.EndsWith("Command", StringComparison.Ordinal)).ToArray();
            void Collision(string message) => Report(context, BridgeRules.NameCollision, model, message);
            var surface = new MemberNames(Name, "state", Collision);
            if (hasValidation) surface.Add("validation", "the generated validation state");
            foreach (var property in state)
            {
                surface.Add(WireName(property), property.Name);
                if (hasErrors) surface.Add(WireName(property) + "Errors", property.Name + " errors");
            }
            foreach (var command in named)
            {
                var name = CommandBaseName(command);
                var shape = ShapeOf(command);
                if (!HasArgument(command, sources[command], shape)) surface.Add("can" + name, command.Name + " availability");
                if (shape.IsAsync) surface.Add("is" + name + "Executing", command.Name + " execution state");
            }
            var view = new MemberNames(Name, "view", Collision);
            view.Add("snapshot", "the generated snapshot property");
            view.Add("subscribe", "the generated subscribe method");
            view.Add("dispose", "the generated dispose method");
            if (interactions.Length > 0) view.Add("interactions", "the generated interactions property");
            if (requests.Length > 0) view.Add("requests", "the generated requests property");
            foreach (var property in state.Where(property => IsPublic(property.SetMethod))) view.Add("set" + property.Name, property.Name + " setter");
            var checkedProperties = state.Where(IsChecked).ToArray();
            foreach (var property in checkedProperties) view.Add("write" + property.Name, property.Name + " checked writer");
            if (checkedProperties.Length > 0) view.Add("fieldBaseline", "the generated checked-field baseline method");
            foreach (var command in named)
            {
                var name = CommandBaseName(command);
                var shape = ShapeOf(command);
                view.Add(LowerFirst(name), command.Name);
                if (HasArgument(command, sources[command], shape)) view.Add("can" + name, command.Name + " availability query");
                if (shape.IsAsync)
                {
                    view.Add("start" + name, command.Name + " operation starter");
                    view.Add("recover" + name, command.Name + " operation recovery");
                }
            }
            // A second interaction of a name is reported above, where the generator stops.
            foreach (var interaction in interactions.GroupBy(property => LowerFirst(property.Name), StringComparer.Ordinal).Select(group => group.First()))
                view.Add(LowerFirst(interaction.Name), interaction.Name + " interaction");
            foreach (var request in requests.GroupBy(property => LowerFirst(property.Name), StringComparer.Ordinal).Select(group => group.First()))
                view.Add(LowerFirst(request.Name), request.Name + " view request");
            // GenerateOne's client members of the region collections (RUNICBRIDGE071).
            var members = new HashSet<string>(["snapshot", "subscribe", "dispose", "interactions", "fieldBaseline"], StringComparer.Ordinal);
            foreach (var property in state.Where(property => IsPublic(property.SetMethod))) members.Add("set" + property.Name);
            foreach (var property in checkedProperties) members.Add("write" + property.Name);
            foreach (var command in named)
            {
                var name = CommandBaseName(command);
                members.Add(LowerFirst(name));
                members.Add("can" + name);
                if (ShapeOf(command).IsAsync) { members.Add("start" + name); members.Add("recover" + name); }
            }
            foreach (var property in state)
            {
                if (BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicRegionCollection) is not { } attribute
                    || !BridgeSymbols.Is(property.Type, symbols.NavigationSelection))
                    continue;
                var canClose = !attribute.NamedArguments.Any(argument => argument.Key == "PageCanClose" && argument.Value.Value is false);
                foreach (var action in canClose ? new[] { "select", "close" } : ["select"])
                {
                    var method = action + property.Name;
                    if (members.Add(method)) continue;
                    Report(context, BridgeRules.RegionCollectionName, property,
                        $"{Name}.{property.Name}: the region collection's client method {method} collides with another member of the generated client. Rename the property or the other member.");
                    break;
                }
            }
        }

        // A checked property: the page sets it with a public setter, it is a value (no ViewModel content, region,
        // query collection or region collection), and it holds no RunicBlob, which only goes to the page.
        private bool IsChecked(IPropertySymbol property)
        {
            if (!IsPublic(property.SetMethod) || property.GetMethod is null) return false;
            var type = property.Type;
            if (BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicRegionCollection) is not null
                || BridgeSymbols.Is(type, symbols.QueryCollection) || BridgeSymbols.Is(type, symbols.NavigationRegion))
                return false;
            if (type.SpecialType != SpecialType.System_Object && HasContentModel(type)) return false;
            if (BridgeSymbols.Is(type, symbols.ReadOnlyList) && type is INamedTypeSymbol { TypeArguments.Length: 1 } list
                && list.TypeArguments[0].SpecialType != SpecialType.System_Object && HasContentModel(list.TypeArguments[0]))
                return false;
            return !HoldsBlob(type, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default));
        }

        // A RunicBlob anywhere in the value: the type, an element or type argument, or a public property of a DTO.
        private static bool HoldsBlob(ITypeSymbol type, HashSet<ITypeSymbol> seen)
        {
            if (!seen.Add(type)) return false;
            if (BridgeSymbols.MetadataName(type) == RunicBlob) return true;
            if (type is IArrayTypeSymbol array) return HoldsBlob(array.ElementType, seen);
            if (type is not INamedTypeSymbol named) return false;
            if (named.TypeArguments.Any(argument => HoldsBlob(argument, seen))) return true;
            if (named.ContainingNamespace is { } ns && (ns.ToDisplayString() == "System" || ns.ToDisplayString().StartsWith("System.", StringComparison.Ordinal)))
                return false;
            for (var current = named; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
                foreach (var member in current.GetMembers().OfType<IPropertySymbol>())
                    if (!member.IsStatic && member.DeclaredAccessibility == Accessibility.Public && HoldsBlob(member.Type, seen)) return true;
            return false;
        }

        private static string CommandBaseName(IPropertySymbol command) => command.Name.Substring(0, command.Name.Length - "Command".Length);

        // InteractionCodeEmitter.MemberNames: the first owner of a name keeps it; a second one is reported at the ViewModel.
        private sealed class MemberNames(string modelName, string surface, Action<string> collision)
        {
            private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

            internal void Add(string name, string owner)
            {
                if (_owners.TryGetValue(name, out var previous))
                    collision($"{modelName}: generated {surface} name '{name}' conflicts between {previous} and {owner}. Rename one member or add an explicit bridge alias.");
                else _owners.Add(name, owner);
            }
        }

        // BridgeCommandSources.FailureDeclarations and KeyDeclarations: on the property (or the property it overrides),
        // then on its [RelayCommand] method.
        private static List<(bool OnMethod, AttributeData Attribute)> Declarations(IPropertySymbol command, IMethodSymbol? source, string attribute)
        {
            var declarations = new List<(bool, AttributeData)>();
            if (BridgeSymbols.InheritedAttribute(command, attribute) is { } onProperty) declarations.Add((false, onProperty));
            if (source is not null && BridgeSymbols.Attribute(source, attribute) is { } onMethod) declarations.Add((true, onMethod));
            return declarations;
        }

        private static List<(bool OnMethod, AttributeData Attribute)> KeyDeclarations(IPropertySymbol command, IMethodSymbol? source) =>
            Declarations(command, source, BridgeSymbols.RunicCommandKey);

        // Program.cs FailureGraph (RUNICBRIDGE012): one declaration, of a type other than object, an exception or Nullable<T>.
        private void CheckFailure(IPropertySymbol command, IMethodSymbol? source)
        {
            var declarations = Declarations(command, source, BridgeSymbols.RunicFailure);
            if (declarations.Count == 0) return;
            ISymbol at = (ISymbol?)source ?? command;
            if (declarations.Count > 1)
            {
                Report(context, BridgeRules.Failure, at,
                    $"{Name}.{command.Name}: declare RunicFailure once, on the command property or on its [RelayCommand] method, not on both.");
                return;
            }
            var (onMethod, attribute) = declarations[0];
            if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not ITypeSymbol failure) return;
            if (failure.SpecialType == SpecialType.System_Object || IsException(failure)
                || failure.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                Report(context, BridgeRules.Failure, onMethod ? source! : command,
                    $"{Name}.{command.Name}: the failure type {CSharpType(failure)} must be a Bridge value type other than object, an exception or Nullable<T>.");
        }

        private static bool IsException(ITypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
                if (BridgeSymbols.MetadataName(current) == "System.Exception") return true;
            return false;
        }

        // Program.cs CommandKey (RUNICBRIDGE027): one declaration, not beside RunicCommandInput, on a command that takes a
        // parameter, naming one public or internal method that takes the key and returns the command's parameter.
        private void CheckCommandKey(IPropertySymbol command, IMethodSymbol? source)
        {
            var declarations = KeyDeclarations(command, source);
            if (declarations.Count == 0) return;
            var name = $"{Name}.{command.Name}";
            ISymbol at = (ISymbol?)source ?? command;
            if (declarations.Count > 1)
            {
                Report(context, BridgeRules.CommandKey, at,
                    $"{name}: declare RunicCommandKey once, on the command property or on its [RelayCommand] method, not on both.");
                return;
            }
            var (onMethod, attribute) = declarations[0];
            ISymbol location = onMethod ? source! : command;
            if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not string resolverName) return;
            var shape = ShapeOf(command);
            if (BridgeSymbols.InheritedAttribute(command, BridgeSymbols.RunicCommandInput) is not null)
            {
                Report(context, BridgeRules.CommandKey, location,
                    $"{name}: RunicCommandKey replaces RunicCommandInput, whose key is the resolver's parameter. Declare one of them.");
                return;
            }
            if (shape is { Library: true, HasInput: false })
            {
                Report(context, BridgeRules.CommandKey, location, $"{name}: RunicCommandKey resolves a command's parameter, but this command takes none.");
                return;
            }
            var methods = KeyResolvers(resolverName);
            if (methods.Count == 0)
            {
                Report(context, BridgeRules.CommandKey, location,
                    $"{name}: the RunicCommandKey resolver '{resolverName}' is not a method of {Name}.");
                return;
            }
            if (methods.Count > 1)
            {
                Report(context, BridgeRules.CommandKey, methods[methods.Count - 1],
                    $"{name}: the RunicCommandKey resolver {Name}.{resolverName} is overloaded. Name a method with one signature.");
                return;
            }
            var resolver = methods[0];
            if (resolver.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                Report(context, BridgeRules.CommandKey, resolver,
                    $"{name}: the RunicCommandKey resolver {Name}.{resolverName} must be public or internal, so the generated Bridge can call it.");
                return;
            }
            var returned = resolver.ReturnType;
            var isAsync = returned is INamedTypeSymbol { IsGenericType: true } task
                && BridgeSymbols.MetadataName(task) is "System.Threading.Tasks.Task`1" or "System.Threading.Tasks.ValueTask`1";
            var resolved = isAsync ? ((INamedTypeSymbol)returned).TypeArguments[0] : returned;
            var parameters = resolver.Parameters;
            var takesToken = isAsync && parameters.Length == 2 && BridgeSymbols.MetadataName(parameters[1].Type) == "System.Threading.CancellationToken";
            if (resolver.IsGenericMethod || parameters.Length != (takesToken ? 2 : 1) || parameters[0].RefKind != RefKind.None
                || BridgeSymbols.MetadataName(parameters[0].Type) == "System.Threading.CancellationToken")
            {
                Report(context, BridgeRules.CommandKey, resolver,
                    $"{name}: the RunicCommandKey resolver {Name}.{resolverName} must take the key as its only parameter, and an asynchronous resolver may add a CancellationToken.");
                return;
            }
            var underlying = Underlying(resolved);
            var parameter = shape.Input ?? underlying;
            if (resolved.SpecialType == SpecialType.System_Void || IsTask(resolved) || resolver.ReturnsByRef || resolver.ReturnsByRefReadonly
                || !IsAssignableFrom(parameter, underlying))
                Report(context, BridgeRules.CommandKey, resolver,
                    $"{name}: the RunicCommandKey resolver {Name}.{resolverName} returns {CSharpType(returned)}; it must return the command's parameter{(shape.Library ? $" {CSharpType(parameter)}" : "")}, or null when the key names no object, directly or as Task<T> or ValueTask<T>.");
        }

        // Type.IsAssignableFrom, which also accepts a boxing conversion (an int key for an object parameter).
        private bool IsAssignableFrom(ITypeSymbol destination, ITypeSymbol source)
        {
            var conversion = symbols.Compilation.ClassifyCommonConversion(source, destination);
            return conversion.IsIdentity || conversion.IsImplicit && (conversion.IsReference || source.IsValueType && !destination.IsValueType);
        }

        private static ITypeSymbol Underlying(ITypeSymbol type) =>
            type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable ? nullable.TypeArguments[0] : type;

        private static bool IsTask(ITypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
                if (BridgeSymbols.MetadataName(current) == "System.Threading.Tasks.Task") return true;
            return false;
        }

        // BridgeCommandSources.KeyResolvers: the methods of that name the ViewModel declares or inherits, most derived
        // first, without the base definitions of overrides.
        private List<IMethodSymbol> KeyResolvers(string name)
        {
            var methods = new List<IMethodSymbol>();
            for (var owner = model; owner is not null && owner.SpecialType != SpecialType.System_Object; owner = owner.BaseType)
                foreach (var method in owner.GetMembers(name).OfType<IMethodSymbol>().Where(method => method.MethodKind == MethodKind.Ordinary))
                    if (!methods.Any(found => SymbolEqualityComparer.Default.Equals(BaseDefinition(found), BaseDefinition(method))))
                        methods.Add(method);
            return methods;
        }

        private static IMethodSymbol BaseDefinition(IMethodSymbol method)
        {
            while (method.OverriddenMethod is { } overridden) method = overridden;
            return method.OriginalDefinition;
        }

        // BridgeTypeGraph.CSharpType without "global::": the full name, nested types joined by '.', with type arguments.
        private static string CSharpType(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array) return CSharpType(array.ElementType) + "[]";
            if (type is not INamedTypeSymbol named) return type.Name;
            var name = BridgeSymbols.MetadataName(named.IsGenericType ? named.OriginalDefinition : named).Replace('+', '.');
            if (!named.IsGenericType) return name;
            var tick = name.IndexOf('`');
            return (tick < 0 ? name : name.Substring(0, tick)) + "<" + string.Join(", ", named.TypeArguments.Select(CSharpType)) + ">";
        }
    }
}
