using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Simplification;

namespace Runic.Application.Views.Analyzers;

/// <summary>
/// The mechanical fixes of the Bridge shape rules that <see cref="BridgeShapeAnalyzer"/> reports: make a setter that
/// the page must not call private, rename a command, or make a Window, View or ViewModel public. The analyzer names
/// the fix in the diagnostic's properties.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BridgeShapeCodeFix)), Shared]
public sealed class BridgeShapeCodeFix : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        [.. new[] { BridgeRules.NameCollision, BridgeRules.View, BridgeRules.ViewModel, BridgeRules.State, BridgeRules.Command,
            BridgeRules.Collection, BridgeRules.Interaction, BridgeRules.ViewMember, BridgeRules.QueryCollection,
            BridgeRules.RegionCollection, BridgeRules.CollectionWithoutKey }.Select(rule => rule.Id)];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            if (!diagnostic.Properties.TryGetValue(BridgeRules.FixKey, out var fix)) continue;
            var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            switch (fix)
            {
                case BridgeRules.FixPrivateSetter when node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { } property
                    && property.AccessorList?.Accessors.FirstOrDefault(accessor =>
                        accessor.Kind() is SyntaxKind.SetAccessorDeclaration or SyntaxKind.InitAccessorDeclaration) is { } setter:
                    context.RegisterCodeFix(CodeAction.Create("Make the setter private",
                        token => MakePrivateAsync(context.Document, root, setter), BridgeRules.FixPrivateSetter), diagnostic);
                    break;
                case BridgeRules.FixRename when diagnostic.Properties.TryGetValue(BridgeRules.NewNameKey, out var newName) && newName is not null:
                    context.RegisterCodeFix(CodeAction.Create($"Rename to {newName}",
                        token => RenameAsync(context.Document, node, newName, token), BridgeRules.FixRename + newName), diagnostic);
                    break;
                case BridgeRules.FixKeyed when diagnostic.Properties.TryGetValue(BridgeRules.NewNameKey, out var key) && key is not null
                    && node.FirstAncestorOrSelf<PropertyDeclarationSyntax>() is { } keyedProperty:
                    context.RegisterCodeFix(CodeAction.Create($"Declare [RunicCollection(nameof({key}))]",
                        token => KeyAsync(context.Document, root, keyedProperty, key), BridgeRules.FixKeyed), diagnostic);
                    break;
                case BridgeRules.FixPublic when node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } declaration:
                    context.RegisterCodeFix(CodeAction.Create($"Make {declaration.Identifier.ValueText} public",
                        token => MakePublicAsync(context.Document, declaration, token), BridgeRules.FixPublic), diagnostic);
                    break;
            }
        }
    }

    // [RunicCollection(nameof(Row.Id))] before the property's other attributes, on the property's line.
    private static Task<Document> KeyAsync(Document document, SyntaxNode root, PropertyDeclarationSyntax property, string key)
    {
        var attribute = SyntaxFactory.AttributeList(SyntaxFactory.SingletonSeparatedList(
            SyntaxFactory.Attribute(SyntaxFactory.ParseName("global::Runic.Application.Views.RunicCollection")
                    .WithAdditionalAnnotations(Simplifier.Annotation),
                SyntaxFactory.ParseAttributeArgumentList($"(nameof({key}))"))));
        var leading = property.GetLeadingTrivia();
        var fixedProperty = property.WithoutLeadingTrivia()
            .WithAttributeLists(property.AttributeLists.Insert(0, attribute.WithTrailingTrivia(SyntaxFactory.Space)))
            .WithLeadingTrivia(leading);
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(property, fixedProperty)));
    }

    // private set or private init: the class still assigns it, the page no longer can.
    private static Task<Document> MakePrivateAsync(Document document, SyntaxNode root, AccessorDeclarationSyntax setter)
    {
        var keyword = setter.Modifiers.Count > 0 ? setter.Modifiers[0].LeadingTrivia : setter.Keyword.LeadingTrivia;
        var fixedSetter = setter
            .WithKeyword(setter.Keyword.WithLeadingTrivia())
            .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithLeadingTrivia(keyword)
                .WithTrailingTrivia(SyntaxFactory.Space)));
        return Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(setter, fixedSetter)));
    }

    private static async Task<Solution> RenameAsync(Document document, SyntaxNode node, string newName, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var declaration = node.AncestorsAndSelf().FirstOrDefault(candidate => candidate is MethodDeclarationSyntax or PropertyDeclarationSyntax);
        if (model is null || declaration is null || model.GetDeclaredSymbol(declaration, cancellationToken) is not { } symbol)
            return document.Project.Solution;
        return await Renamer.RenameSymbolAsync(document.Project.Solution, symbol, new SymbolRenameOptions(), newName, cancellationToken)
            .ConfigureAwait(false);
    }

    // Every declaration of the type that states an accessibility: partial parts without one follow the others.
    private static async Task<Solution> MakePublicAsync(Document document, TypeDeclarationSyntax declaration, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var solution = document.Project.Solution;
        if (model?.GetDeclaredSymbol(declaration, cancellationToken) is not { } type) return solution;
        foreach (var group in type.DeclaringSyntaxReferences.GroupBy(reference => reference.SyntaxTree))
        {
            if (solution.GetDocument(group.Key) is not { } part) continue;
            var root = await group.Key.GetRootAsync(cancellationToken).ConfigureAwait(false);
            var nodes = group.Select(reference => reference.GetSyntax(cancellationToken)).OfType<TypeDeclarationSyntax>()
                .Where(node => node == declaration || node.Modifiers.Any(IsAccessibility)).ToArray();
            root = root.ReplaceNodes(nodes, (_, node) => Public(node));
            solution = solution.WithDocumentSyntaxRoot(part.Id, root);
        }
        return solution;
    }

    private static TypeDeclarationSyntax Public(TypeDeclarationSyntax declaration)
    {
        var modifiers = declaration.Modifiers;
        var first = modifiers.FirstOrDefault(IsAccessibility);
        if (first.IsKind(SyntaxKind.None))
        {
            // No accessibility (internal by default): public goes before the other modifiers, after the attributes.
            var anchor = modifiers.Count > 0 ? modifiers[0] : declaration.Keyword;
            var token = SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithLeadingTrivia(anchor.LeadingTrivia)
                .WithTrailingTrivia(SyntaxFactory.Space);
            var moved = declaration.ReplaceToken(anchor, anchor.WithLeadingTrivia());
            return moved.WithModifiers(moved.Modifiers.Insert(0, token));
        }
        var replacement = SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTriviaFrom(first);
        var kept = modifiers.Where(modifier => !IsAccessibility(modifier) || modifier == first)
            .Select(modifier => modifier == first ? replacement : modifier);
        return declaration.WithModifiers(SyntaxFactory.TokenList(kept));
    }

    private static bool IsAccessibility(SyntaxToken modifier) => modifier.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.InternalKeyword
        or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.FileKeyword;
}
