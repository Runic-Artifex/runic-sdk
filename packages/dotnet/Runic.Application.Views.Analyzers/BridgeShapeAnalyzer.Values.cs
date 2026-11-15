using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The rules of a ViewModel's values (GenerateOne and BridgeTypeGraph.Discover): every graph the generator discovers,
/// RUNICBRIDGE003 and 065; the rows and key of a [RunicCollection] (010) and of a RunicQueryCollection (060); the
/// row editors (050, 051, 052); and the warnings that follow from the graphs, 031, 032 and 053, which the generator
/// leaves to the analyzer (BridgeDiagnosticCodes.AnalyzerWarnings).
/// </summary>
public sealed partial class BridgeShapeAnalyzer
{
    private sealed partial class ViewModelAnalysis
    {
        private const string RowEditorAttribute = "Runic.Application.Views.RunicRowEditorAttribute";
        private const string NoFieldAttribute = "Runic.Application.Views.RunicNoFieldAttribute";

        private bool HonorJsonIgnore => BridgeValueGraph.HonorsJsonIgnore(model.ContainingAssembly);

        // A graph, or null after reporting why it is no Bridge value: at the innermost declaration in hand-written
        // source, else at the member that holds the value, else at the ViewModel.
        private BridgeValueGraph? Graph(ITypeSymbol type, string path, ISymbol origin, ValueDirection direction, bool annotated = true,
            Func<ValueGraphException, ValueGraphException>? explain = null)
        {
            try
            {
                return BridgeValueGraph.Discover(symbols, type, path, origin, HonorJsonIgnore, direction, annotated);
            }
            catch (ValueGraphException error)
            {
                ReportGraph(explain?.Invoke(error) ?? error, origin);
                return null;
            }
        }

        private void ReportGraph(ValueGraphException error, ISymbol origin)
        {
            var cancellation = context.CancellationToken;
            var location = (error.Member is { } member ? SourceLocation(member, cancellation)
                    ?? (ToolkitSource(member) is { } source ? SourceLocation(source, cancellation) : null) : null)
                ?? SourceLocation(origin, cancellation) ?? (ToolkitSource(origin) is { } originSource ? SourceLocation(originSource, cancellation) : null)
                ?? SourceLocation(model, cancellation);
            var rule = error.Code == BridgeRules.Blob.Id ? BridgeRules.Blob : BridgeRules.ValueType;
            var message = error.Message;
            // Diagnostics.Report names the ViewModel when the message does not start with it.
            if (!message.StartsWith(model.MetadataName, StringComparison.Ordinal) && !message.StartsWith(FullName(model), StringComparison.Ordinal))
                message = $"{FullName(model)}: {message}";
            Report(context, rule, location, message);
        }

        // Returns the graphs of the value properties.
        private Dictionary<IPropertySymbol, BridgeValueGraph> CheckValues(List<IPropertySymbol> commands, Dictionary<IPropertySymbol, IMethodSymbol?> sources,
            IPropertySymbol[] declared, IPropertySymbol[] state)
        {
            CheckInteractionValues(declared);
            foreach (var command in commands.Where(command => command.Name.EndsWith("Command", StringComparison.Ordinal)))
                CheckCommandValues(command, sources[command]);
            var editors = RowEditors();
            // An editor of a query collection is reported there, where the generator stops.
            editors = [.. editors.Except(CheckQueryCollectionEditors(editors), SymbolEqualityComparer.Default).OfType<IMethodSymbol>()];
            var values = new Dictionary<IPropertySymbol, BridgeValueGraph>(SymbolEqualityComparer.Default);
            var failed = new HashSet<IPropertySymbol>(SymbolEqualityComparer.Default);
            foreach (var property in state)
            {
                if (property.GetMethod is null) continue;
                if (BridgeSymbols.Is(property.Type, symbols.QueryCollection)) { CheckQueryRows(property); continue; }
                if (!IsValueState(property)) continue;
                var publicSetter = IsPublic(property.SetMethod);
                var editor = BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is not null
                    ? editors.FirstOrDefault(candidate => Edits(candidate, property)) : null;
                var path = $"{Name}.{property.Name}";
                var graph = Graph(property.Type, path, property, ValueDirection.ToPage);
                if (graph is not null && (publicSetter || editor is not null) && !graph.Nodes.Any(node => node.Kind == ValueKind.Blob))
                    graph = Graph(property.Type, path, property, ValueDirection.Both,
                        explain: publicSetter || editor is null ? null : error => new ValueGraphException(error.Path,
                            $"{error.Detail} The rows also come from the page because {Name}.{editor.Name} is their [RunicRowEditor].", error.Member ?? editor, error.Code));
                if (graph is null) failed.Add(property);
                else values.Add(property, graph);
            }
            var incremental = CheckKeyedCollections(state, values, failed);
            CheckCollectionsWithoutKeys(values, incremental);
            var rowEditors = CheckRowEditors(editors, values, incremental);
            foreach (var (collection, _) in rowEditors) WarnRowObjects(collection, values[collection], incremental[collection]);
            WarnUnrecognizedValidation(values);
            return values;
        }

        // A value property: no ViewModel content, region, selection, region collection or query collection (GenerateOne).
        private bool IsValueState(IPropertySymbol property)
        {
            var type = property.Type;
            if (BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicRegionCollection) is not null
                || BridgeSymbols.Is(type, symbols.NavigationRegion) || BridgeSymbols.Is(type, symbols.NavigationSelection)
                || BridgeSymbols.Is(type, symbols.QueryCollection))
                return false;
            if (type.SpecialType != SpecialType.System_Object && HasContentModel(type)) return false;
            if (BridgeSymbols.Is(type, symbols.ReadOnlyList) && type is INamedTypeSymbol { TypeArguments.Length: 1 } list)
            {
                var item = list.TypeArguments[0];
                if (item.SpecialType != SpecialType.System_Object && HasContentModel(item)) return false;
                if (item.SpecialType == SpecialType.System_Object || BridgeSymbols.Implements(item, symbols.NotifyPropertyChanged)
                    && BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is null)
                    return false;
            }
            return true;
        }

        // InteractionCodeEmitter.Discover and DiscoverRequests: an interaction's input goes to the page and its output
        // comes from it; a view request's input goes to the page.
        private void CheckInteractionValues(IPropertySymbol[] declared)
        {
            foreach (var property in declared)
            {
                var contract = BridgeSymbols.Is(property.Type, symbols.ViewQuestion) ? property.Type as INamedTypeSymbol
                    : BridgeSymbols.SelfBasesAndInterfaces(property.Type).OfType<INamedTypeSymbol>().FirstOrDefault(candidate =>
                        BridgeSymbols.MetadataName(candidate) is "ReactiveUI.Binding.IInteraction`2" or "ReactiveUI.Binding.Reactive.IInteraction`2");
                if (contract is { TypeArguments.Length: 2 } && !symbols.IsCommand(property.Type))
                {
                    Graph(contract.TypeArguments[0], $"{Name}.{property.Name}.input", property, ValueDirection.ToPage);
                    Graph(contract.TypeArguments[1], $"{Name}.{property.Name}.output", property, ValueDirection.FromPage);
                }
                else if (BridgeSymbols.Is(property.Type, symbols.ViewRequest) && property.Type is INamedTypeSymbol { TypeArguments.Length: 1 } request)
                    Graph(request.TypeArguments[0], $"{Name}.{property.Name}.input", property, ValueDirection.ToPage);
            }
        }

        // GenerateOne's command plans: the input (or the RunicCommandKey key) comes from the page, the result and the
        // declared failure go to it.
        private void CheckCommandValues(IPropertySymbol command, IMethodSymbol? source)
        {
            ISymbol at = (ISymbol?)source ?? command;
            var shape = ShapeOf(command);
            var path = $"{Name}.{command.Name}";
            var keys = KeyDeclarations(command, source);
            if (keys.Count == 1 && keys[0].Attribute.ConstructorArguments.Length == 1 && keys[0].Attribute.ConstructorArguments[0].Value is string resolverName
                && KeyResolvers(resolverName) is { Count: 1 } resolvers && resolvers[0].Parameters.Length is 1 or 2)
                Graph(resolvers[0].Parameters[0].Type, path + ".key", resolvers[0], ValueDirection.FromPage);
            else if (shape.Input is { } input)
                Graph(input, path + ".input", at, ValueDirection.FromPage);
            else if (!shape.Library && BridgeSymbols.InheritedAttribute(command, BridgeSymbols.RunicCommandInput) is
                { ConstructorArguments.Length: 1 } plain && plain.ConstructorArguments[0].Value is ITypeSymbol plainInput)
                Graph(plainInput, path + ".input", at, ValueDirection.FromPage, annotated: false);
            if (CommandResult(command, source, shape) is { } result) Graph(result, path + ".result", at, ValueDirection.ToPage);
            var failures = Declarations(command, source, BridgeSymbols.RunicFailure);
            if (failures.Count == 1 && failures[0].Attribute.ConstructorArguments.Length == 1
                && failures[0].Attribute.ConstructorArguments[0].Value is ITypeSymbol failure
                && failure.SpecialType != SpecialType.System_Object && !IsException(failure)
                && failure.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T)
                Graph(failure, path + ".failure", failures[0].OnMethod ? source! : command, ValueDirection.ToPage, annotated: false);
        }

        // A ReactiveUI command's TResult (unless RxVoid or Unit); a CommunityToolkit async command's Task<T> method result.
        private static ITypeSymbol? CommandResult(IPropertySymbol command, IMethodSymbol? source, CommandShape shape)
        {
            if (BridgeSymbols.SelfBasesAndInterfaces(command.Type).OfType<INamedTypeSymbol>()
                .FirstOrDefault(candidate => BridgeSymbols.MetadataName(candidate) == ReactiveCommand) is { } reactive)
                return BridgeSymbols.MetadataName(reactive.TypeArguments[1]) is "ReactiveUI.Primitives.RxVoid" or "System.Reactive.Unit"
                    ? null : reactive.TypeArguments[1];
            if (shape.Library && shape.IsAsync && source?.ReturnType is INamedTypeSymbol { IsGenericType: true } task
                && BridgeSymbols.MetadataName(task) == "System.Threading.Tasks.Task`1")
                return task.TypeArguments[0];
            return null;
        }

        // QueryCollectionPlan: the rows of a RunicQueryCollection<TRow, TKey> are DTOs, after its own shape checks.
        private void CheckQueryRows(IPropertySymbol property)
        {
            if (property.Type is not INamedTypeSymbol { TypeArguments.Length: 2 } query || IsPublic(property.SetMethod)
                || BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is not null
                || BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicAppendOnlyText) is not null)
                return;
            var key = query.TypeArguments[1];
            if (key.SpecialType is not (SpecialType.System_String or SpecialType.System_Int32) && BridgeSymbols.MetadataName(key) != "System.Guid") return;
            var row = query.TypeArguments[0];
            if (symbols.ReadOnlyList is null) return;
            var rows = Graph(symbols.ReadOnlyList.Construct(row), $"{Name}.{property.Name}", property, ValueDirection.ToPage, annotated: false);
            if (rows is not null && rows.Root.Element is not { Kind: ValueKind.Dto })
                Report(context, BridgeRules.QueryCollection, property,
                    $"{Name}.{property.Name}: the rows of a RunicQueryCollection are DTOs, not {ReflectionName(BridgeValueGraph.Underlying(row))}.");
        }

        // GenerateOne's [RunicCollection] check (RUNICBRIDGE010): a read-only, nonnullable collection of nonnullable DTO
        // rows with a nonnullable string, Guid, Int32 or Int64 key. Returns the incremental collections and their keys.
        private Dictionary<IPropertySymbol, IPropertySymbol> CheckKeyedCollections(IPropertySymbol[] state,
            Dictionary<IPropertySymbol, BridgeValueGraph> values, HashSet<IPropertySymbol> failed)
        {
            var incremental = new Dictionary<IPropertySymbol, IPropertySymbol>(SymbolEqualityComparer.Default);
            foreach (var property in state)
            {
                if (BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is not { } attribute || failed.Contains(property)
                    || property.GetMethod is null)
                    continue;
                // ViewModel content, regions and query collections report their own shape first.
                if (!values.TryGetValue(property, out var graph) && !IsValueState(property)) continue;
                var publicSetter = IsPublic(property.SetMethod);
                if (publicSetter || graph is null || graph.Root.Kind is not (ValueKind.Array or ValueKind.List) || graph.Root.IsNullable
                    || graph.Root.Element is not { IsNullable: false, Kind: ValueKind.Dto } item)
                {
                    Report(context, BridgeRules.Collection, property,
                        $"{Name}.{property.Name}: RunicCollection requires a nonnullable, read-only collection of nonnullable DTO rows.",
                        publicSetter ? BridgeRules.FixPrivateSetter : null);
                    continue;
                }
                var keyName = attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
                var matches = item.Members.Where(member => member.Property.Name == keyName).ToArray();
                var key = matches.Length == 1 ? matches[0] : null;
                if (key is null || key.Type.IsNullable || !IsKeyType(key.Type.Type))
                {
                    Report(context, BridgeRules.Collection, property,
                        $"{Name}.{property.Name}: the collection key '{keyName}' must name a nonnullable string, Guid, Int32 or Int64 row property.");
                    continue;
                }
                incremental.Add(property, key.Property);
            }
            return incremental;
        }

        private static bool IsKeyType(ITypeSymbol type) => type.SpecialType is SpecialType.System_String or SpecialType.System_Int32 or SpecialType.System_Int64
            || BridgeSymbols.MetadataName(type) == "System.Guid";

        // WarnObservableCollectionsWithoutKeys (RUNICBRIDGE032).
        private void CheckCollectionsWithoutKeys(Dictionary<IPropertySymbol, BridgeValueGraph> values, Dictionary<IPropertySymbol, IPropertySymbol> incremental)
        {
            foreach (var entry in values)
            {
                var (property, graph) = (entry.Key, entry.Value);
                // A [RunicCollection] that is no keyed collection is RUNICBRIDGE010, where the generator stops.
                if (incremental.ContainsKey(property) || BridgeSymbols.InheritedAttribute(property, BridgeSymbols.RunicCollection) is not null
                    || !NotifiesCollectionChanges(property.Type)
                    || graph.Root.Kind is not (ValueKind.Array or ValueKind.List) || graph.Root.Element is not { Kind: ValueKind.Dto } row)
                    continue;
                var key = row.Members.Select(member => member.Property).FirstOrDefault(member => member.Name is "Id" or "Key"
                    && (member.Type.SpecialType is SpecialType.System_String or SpecialType.System_Int32 or SpecialType.System_Int64
                        || BridgeSymbols.MetadataName(member.Type) == "System.Guid"));
                var rowName = row.Type.MetadataName;
                // Keyed by a nonnullable Id or Key of nonnullable rows in a read-only, nonnullable collection, the
                // property passes RUNICBRIDGE010 once it declares [RunicCollection]: a code fix adds it.
                var keyed = key is not null && !IsPublic(property.SetMethod) && !graph.Root.IsNullable && !row.IsNullable
                    && row.Members.FirstOrDefault(member => SymbolEqualityComparer.Default.Equals(member.Property, key)) is { Type.IsNullable: false }
                    && row.Type is INamedTypeSymbol { IsGenericType: false }
                    ? ImmutableDictionary<string, string?>.Empty.Add(BridgeRules.FixKey, BridgeRules.FixKeyed).Add(BridgeRules.NewNameKey, $"{rowName}.{key.Name}")
                    : null;
                OwnedWarning(context, BridgeRules.CollectionWithoutKey, property,
                    $"{Name}.{property.Name}: an observable collection of {rowName} rows that is not a [RunicCollection] "
                    + "publishes every change by encoding and sending the whole list. Declare "
                    + (key is null ? $"[RunicCollection(nameof({rowName}.Key))] with a string, Guid, Int32 or Int64 key property of the rows"
                        : $"[RunicCollection(nameof({rowName}.{key.Name}))]")
                    + $" to publish row changes, or suppress {BridgeRules.CollectionWithoutKey.Id} with NoWarn for a list that stays small.", keyed);
            }
        }

        private static bool NotifiesCollectionChanges(ITypeSymbol type) =>
            BridgeSymbols.SelfBasesAndInterfaces(type).Any(candidate => BridgeSymbols.MetadataName(candidate) == "System.Collections.Specialized.INotifyCollectionChanged");

        // BridgeRowEditors.Declared: the ViewModel's methods with [RunicRowEditor], also inherited ones that are not
        // private, by name.
        private List<IMethodSymbol> RowEditors()
        {
            var editors = new List<IMethodSymbol>();
            for (var owner = model; owner is not null && owner.SpecialType != SpecialType.System_Object; owner = owner.BaseType)
                foreach (var method in owner.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.MethodKind != MethodKind.Ordinary || BridgeSymbols.Attribute(method, RowEditorAttribute) is null) continue;
                    if (!SymbolEqualityComparer.Default.Equals(owner, model) && method.DeclaredAccessibility == Accessibility.Private) continue;
                    if (editors.Any(found => SymbolEqualityComparer.Default.Equals(BaseDefinition(found), BaseDefinition(method)))) continue;
                    editors.Add(method);
                }
            return [.. editors.OrderBy(editor => editor.Name, StringComparer.Ordinal)];
        }

        private static string? EditorCollection(IMethodSymbol editor) =>
            BridgeSymbols.Attribute(editor, RowEditorAttribute) is { ConstructorArguments.Length: 1 } attribute ? attribute.ConstructorArguments[0].Value as string : null;

        // BridgeRowEditors.Edits: the editor names the collection, or, naming none, takes its row type second.
        private static bool Edits(IMethodSymbol editor, IPropertySymbol collection)
        {
            if (BridgeSymbols.Attribute(editor, RowEditorAttribute) is not { } attribute) return false;
            if (attribute.ConstructorArguments.Length == 1) return attribute.ConstructorArguments[0].Value as string == collection.Name;
            return editor.Parameters.Length is 2 or 3 && RowType(collection.Type) is { } row
                && SymbolEqualityComparer.Default.Equals(editor.Parameters[1].Type.WithNullableAnnotation(NullableAnnotation.NotAnnotated),
                    row.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
        }

        private static ITypeSymbol? RowType(ITypeSymbol collection) => collection is IArrayTypeSymbol array ? array.ElementType
            : collection is INamedTypeSymbol { IsGenericType: true } named && BridgeSymbols.MetadataName(named) == "System.Collections.Generic.IEnumerable`1"
                ? named.TypeArguments[0]
            : collection.AllInterfaces.FirstOrDefault(candidate => BridgeSymbols.MetadataName(candidate) == "System.Collections.Generic.IEnumerable`1")?.TypeArguments[0];

        // GenerateOne: an editor that names a query collection, whose rows cannot be edited yet (RUNICBRIDGE050).
        private List<IMethodSymbol> CheckQueryCollectionEditors(List<IMethodSymbol> editors)
        {
            var reported = new List<IMethodSymbol>();
            foreach (var editor in editors)
                if (EditorCollection(editor) is { } named && PublicProperty(named) is { } property
                    && property.Type is INamedTypeSymbol { IsGenericType: true } type && type.OriginalDefinition.MetadataName.StartsWith("RunicQueryCollection", StringComparison.Ordinal))
                {
                    Report(context, BridgeRules.RowEditor, editor,
                        $"{Name}.{editor.Name}: [RunicRowEditor] names {named}, a query collection, whose rows cannot be edited from the page yet.");
                    reported.Add(editor);
                }
            return reported;
        }

        private IPropertySymbol? PublicProperty(string name)
        {
            for (var owner = model; owner is not null; owner = owner.BaseType)
                if (owner.GetMembers(name).OfType<IPropertySymbol>().FirstOrDefault(property => property.DeclaredAccessibility == Accessibility.Public) is { } found)
                    return found;
            return null;
        }

        // GenerateOne's row editor loop (RUNICBRIDGE050, 051, 052). Returns each collection's editor.
        private List<(IPropertySymbol Collection, IMethodSymbol Editor)> CheckRowEditors(List<IMethodSymbol> editors,
            Dictionary<IPropertySymbol, BridgeValueGraph> values, Dictionary<IPropertySymbol, IPropertySymbol> incremental)
        {
            var assigned = new List<(IPropertySymbol Collection, IMethodSymbol Editor)>();
            foreach (var editor in editors)
            {
                var named = EditorCollection(editor);
                var targets = incremental.Keys.Where(property => Edits(editor, property)).ToArray();
                if (targets.Length != 1)
                {
                    Report(context, BridgeRules.RowEditor, editor, targets.Length == 0
                        ? $"{Name}.{editor.Name}: [RunicRowEditor] {(named is null ? "matches no [RunicCollection] property by its row type" : $"names {named}, which is not a [RunicCollection] property of {Name}")}."
                        : $"{Name}.{editor.Name}: [RunicRowEditor] matches {string.Join(" and ", targets.Select(target => target.Name))}; name one, such as [RunicRowEditor(nameof({targets[0].Name}))].");
                    continue;
                }
                var collection = targets[0];
                var key = incremental[collection];
                var row = values[collection].Root.Element!;
                var parameters = editor.Parameters;
                if (editor.IsStatic || editor.IsGenericMethod || editor.ReturnType.SpecialType != SpecialType.System_Void || parameters.Length is not (2 or 3)
                    || !SameType(parameters[0].Type, key.Type) || !SameType(parameters[1].Type, row.Type)
                    || parameters.Length == 3 && BridgeSymbols.MetadataName(parameters[2].Type) != "Runic.Application.Views.RunicRowEdit"
                    || parameters.Any(parameter => parameter.RefKind != RefKind.None)
                    || editor.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                {
                    Report(context, BridgeRules.RowEditor, editor,
                        $"{Name}.{editor.Name}: a [RunicRowEditor] for {collection.Name} must be a public or internal instance method void {editor.Name}({BridgeValueGraph.CSharpType(key.Type)} key, {BridgeValueGraph.CSharpType(row.Type)} row), optionally with a third parameter RunicRowEdit edit.");
                    continue;
                }
                if (CopyOf(row) is { Kind: RowCopyKind.None } refused)
                {
                    Report(context, BridgeRules.RowCopy, editor,
                        $"{Name}.{editor.Name}: the rows of {collection.Name} cannot be copied with one cell changed: {refused.Reason}. Make {BridgeValueGraph.CSharpType(row.Type)} a record, or seal it and give it a copy constructor or a Clone() method.");
                    continue;
                }
                if (assigned.FirstOrDefault(entry => SymbolEqualityComparer.Default.Equals(entry.Collection, collection)) is { Editor: { } first })
                {
                    Report(context, BridgeRules.RowEditors, editor, $"{Name}: {collection.Name} has two [RunicRowEditor] methods, {first.Name} and {editor.Name}.");
                    continue;
                }
                assigned.Add((collection, editor));
            }
            return assigned;
        }

        // Type equality as reflection sees it: nullable reference annotations are not part of a Type.
        private static bool SameType(ITypeSymbol left, ITypeSymbol right) => SymbolEqualityComparer.Default.Equals(
            left.WithNullableAnnotation(NullableAnnotation.NotAnnotated), right.WithNullableAnnotation(NullableAnnotation.NotAnnotated));

        private enum RowCopyKind { None, With, Constructor, Clone, Wire }

        // BridgeFieldLeaf.CopyOf: how an object is copied with one member changed, or why it cannot be copied exactly.
        private static (RowCopyKind Kind, string? Reason) CopyOf(ValueNode node)
        {
            var type = node.Type;
            if (type.IsValueType || type.GetMembers("<Clone>$").OfType<IMethodSymbol>().Any(method => method.DeclaredAccessibility == Accessibility.Public && !method.IsStatic))
                return (RowCopyKind.With, null);
            if (!type.IsSealed) return (RowCopyKind.None, $"{BridgeValueGraph.CSharpType(type)} is a class that is not sealed, so a derived row would lose its own members");
            if (type is INamedTypeSymbol named && named.InstanceConstructors.Any(constructor => constructor.DeclaredAccessibility == Accessibility.Public
                && constructor.Parameters.Length == 1 && SameType(constructor.Parameters[0].Type, type)))
                return (RowCopyKind.Constructor, null);
            if (PublicInstanceMembers(type).OfType<IMethodSymbol>().Any(method => method.Name == "Clone" && method.Parameters.Length == 0
                && SameType(method.ReturnType, type)))
                return (RowCopyKind.Clone, null);
            var hidden = PublicInstanceMembers(type).OfType<IPropertySymbol>()
                .Where(property => property.SetMethod?.DeclaredAccessibility == Accessibility.Public && !property.IsIndexer
                    && !node.Members.Any(member => member.Property.Name == property.Name))
                .Select(property => property.Name).Distinct().ToArray();
            return hidden.Length > 0
                ? (RowCopyKind.None, $"{BridgeValueGraph.CSharpType(type)} has settable members the page does not see ({string.Join(", ", hidden)}) and no copy constructor or Clone()")
                : (RowCopyKind.Wire, null);
        }

        private static IEnumerable<ISymbol> PublicInstanceMembers(ITypeSymbol type)
        {
            for (var current = type; current is not null; current = current.BaseType)
                foreach (var member in current.GetMembers())
                    if (!member.IsStatic && member.DeclaredAccessibility == Accessibility.Public && !member.IsOverride) yield return member;
        }

        // BridgeFieldLeaf.Of with its skipped callback (RUNICBRIDGE053): an object two levels deep inside an editable
        // row whose own copy is refused has no fields below it.
        private void WarnRowObjects(IPropertySymbol collection, BridgeValueGraph graph, IPropertySymbol key)
        {
            var owner = graph.Root.Element!;
            if (CopyOf(owner).Kind == RowCopyKind.None) return;
            Visit(owner, 0, []);

            void Visit(ValueNode node, int depth, List<ValueNode> chain)
            {
                var copy = CopyOf(node).Kind;
                var parameters = node.Constructor?.Parameters ?? [];
                foreach (var member in node.Members)
                {
                    if (!member.Readable || IsAbsent(member) || SymbolEqualityComparer.Default.Equals(member.Property, key)) continue;
                    if (BridgeValueGraph.InheritedAttribute(member.Property, NoFieldAttribute) is not null) continue;
                    var settable = copy switch
                    {
                        RowCopyKind.With => member.Writable,
                        RowCopyKind.Constructor or RowCopyKind.Clone => member.Writable && member.Property.SetMethod?.IsInitOnly != true,
                        _ => member.Writable || parameters.Any(parameter => string.Equals(parameter.Name, member.Property.Name, StringComparison.OrdinalIgnoreCase)),
                    };
                    if (!settable) continue;
                    if (member.Type.Kind != ValueKind.Dto || depth >= 2 || member.Type == owner || chain.Contains(member.Type)) continue;
                    if (CopyOf(member.Type) is { Kind: RowCopyKind.None } refused)
                    {
                        OwnedWarning(context, BridgeRules.RowObjectCopy, member.Property,
                            $"{Name}.{collection.Name}: {member.Property.ContainingType.MetadataName}.{member.Property.Name} has no fields below it: {refused.Reason}. Mark it [RunicNoField], make its type a record, or seal it and give it a copy constructor or a Clone() method.");
                        continue;
                    }
                    Visit(member.Type, depth + 1, [.. chain, member.Type]);
                }
            }
        }

        // A nonnullable JsonElement member, absent from the object when it holds no value (MarkOptionalMembers).
        private static bool IsAbsent(ValueMember member) => member.Type is { Kind: ValueKind.Json, IsNullable: false }
            && BridgeSymbols.MetadataName(member.Type.Type) == "System.Text.Json.JsonElement";

        // WarnUnrecognizedValidatable (RUNICBRIDGE031): the ViewModel and the types of its values that implement an
        // IValidatableViewModel of neither ReactiveUI.Validation flavor.
        private void WarnUnrecognizedValidation(Dictionary<IPropertySymbol, BridgeValueGraph> values)
        {
            var types = new List<ITypeSymbol> { model };
            foreach (var node in values.Values.SelectMany(graph => graph.Nodes))
            {
                var type = node.IsNullable && node.Type.IsValueType ? symbols.Compilation.GetSpecialType(SpecialType.System_Nullable_T).Construct(node.Type) : node.Type;
                if (!types.Contains(type, SymbolEqualityComparer.Default)) types.Add(type);
            }
            foreach (var type in types)
                foreach (var unrecognized in type.AllInterfaces.Concat(type.TypeKind == TypeKind.Interface && type is INamedTypeSymbol self ? [self] : [])
                    .Where(candidate => candidate.Name == "IValidatableViewModel"
                        && BridgeSymbols.MetadataName(candidate) is not ("ReactiveUI.Validation.Abstractions.IValidatableViewModel" or "ReactiveUI.Validation.Reactive.Abstractions.IValidatableViewModel")))
                    OwnedWarning(context, BridgeRules.UnrecognizedValidation, type,
                        $"{Name}{(SymbolEqualityComparer.Default.Equals(type, model) ? "" : $" ({ReflectionName(type)})")}: {BridgeValueGraph.TypeIdentity(unrecognized)} is not the IValidatableViewModel of "
                        + "RunicArtifex.ReactiveUI.Validation or RunicArtifex.ReactiveUI.Validation.Reactive, so the Bridge does not read "
                        + "or observe its ValidationContext. Implement INotifyDataErrorInfo to report its errors.");
        }
    }
}
