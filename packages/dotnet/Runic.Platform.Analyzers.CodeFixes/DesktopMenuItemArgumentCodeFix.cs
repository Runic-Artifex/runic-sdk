using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Runic.Platform.Analyzers;

/// <summary>Swaps the title and command id of a <c>DesktopMenuItem</c> that RUNICPLAT001 reports, and names both.</summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(DesktopMenuItemArgumentCodeFix)), Shared]
public sealed class DesktopMenuItemArgumentCodeFix : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => [DesktopMenuItemArgumentAnalyzer.Id];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root?.FindNode(context.Span, getInnermostNodeForTie: true) is not { } node) return;
        var creation = node.FirstAncestorOrSelf<BaseObjectCreationExpressionSyntax>();
        if (creation?.ArgumentList is null) return;
        context.RegisterCodeFix(
            CodeAction.Create("Swap the title and command id", token => SwapAsync(context.Document, creation, token),
                nameof(DesktopMenuItemArgumentCodeFix)),
            context.Diagnostics);
    }

    private static async Task<Document> SwapAsync(Document document, BaseObjectCreationExpressionSyntax creation, CancellationToken cancellationToken)
    {
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (model?.GetOperation(creation, cancellationToken) is not IObjectCreationOperation operation
            || DesktopMenuItemArgumentAnalyzer.Argument(operation, DesktopMenuItemArgumentAnalyzer.TitleParameter)?.Syntax is not ArgumentSyntax title
            || DesktopMenuItemArgumentAnalyzer.Argument(operation, DesktopMenuItemArgumentAnalyzer.CommandIdParameter)?.Syntax is not ArgumentSyntax commandId)
            return document;
        // The values trade places and both arguments are named, so the order reads in the source from now on.
        var arguments = creation.ArgumentList!.Arguments.Select(argument =>
            argument == title ? Named("Title", commandId.Expression, argument)
            : argument == commandId ? Named("CommandId", title.Expression, argument)
            : argument);
        var fixedCreation = creation.WithArgumentList(creation.ArgumentList.WithArguments(
            SyntaxFactory.SeparatedList(arguments, creation.ArgumentList.Arguments.GetSeparators())));
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        return document.WithSyntaxRoot(root!.ReplaceNode(creation, fixedCreation));
    }

    private static ArgumentSyntax Named(string name, ExpressionSyntax value, ArgumentSyntax replaced) =>
        SyntaxFactory.Argument(SyntaxFactory.NameColon(name), default, value.WithoutTrivia())
            .WithLeadingTrivia(replaced.GetLeadingTrivia())
            .WithTrailingTrivia(replaced.GetTrailingTrivia());
}
