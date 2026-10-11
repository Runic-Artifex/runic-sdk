using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Reads the <c>///</c> comment of a partial property's defining declaration
/// from source. For a partial property the compiler writes the implementation
/// part's comment to the XML documentation file, and MVVM source generators
/// document their implementation as <c>&lt;inheritdoc/&gt;</c>, so the summary
/// the developer wrote on <c>[ObservableProperty] public partial T X { get; }</c>
/// or <c>[Reactive] public partial T X { get; }</c> is only in source.
/// </summary>
internal static class SourceDocumentation
{
    private static readonly Dictionary<string, SyntaxTree?> Trees = new(StringComparer.Ordinal);

    /// <summary>The documentation of the declaring part of a partial property, as a member element, or null.</summary>
    internal static XElement? PartialProperty(PropertyInfo property)
    {
        if (property.DeclaringType is not { } type) return null;
        foreach (var path in SourceLocator.Documents(type))
        {
            if (Tree(path) is not { } tree) continue;
            foreach (var declaration in tree.GetRoot().DescendantNodes().OfType<PropertyDeclarationSyntax>())
            {
                if (declaration.Identifier.ValueText != property.Name
                    || !declaration.Modifiers.Any(SyntaxKind.PartialKeyword)
                    || !IsDefinition(declaration)
                    || !Declares(declaration.Parent as TypeDeclarationSyntax, type)) continue;
                return Element(declaration);
            }
        }
        return null;
    }

    // The defining part has accessors without bodies; the implementation part has bodies.
    private static bool IsDefinition(PropertyDeclarationSyntax declaration) =>
        declaration.ExpressionBody is null && declaration.AccessorList is { } accessors
        && accessors.Accessors.All(accessor => accessor.Body is null && accessor.ExpressionBody is null);

    // Compares the containing type declarations with the reflected declaring
    // types, innermost first, and the namespace with the outermost type's.
    private static bool Declares(TypeDeclarationSyntax? declaration, Type type)
    {
        Type? current = type;
        SyntaxNode? node = declaration;
        while (current is not null)
        {
            if (node is not TypeDeclarationSyntax syntax || syntax.Identifier.ValueText != SimpleName(current)
                || syntax.Arity != (current.IsGenericType ? current.GetGenericArguments().Length - (current.DeclaringType?.GetGenericArguments().Length ?? 0) : 0))
                return false;
            node = syntax.Parent;
            current = current.DeclaringType;
        }
        var namespaces = new List<string>();
        for (; node is not null; node = node.Parent)
            if (node is BaseNamespaceDeclarationSyntax @namespace) namespaces.Insert(0, @namespace.Name.ToString());
            else if (node is TypeDeclarationSyntax) return false;
        return string.Join('.', namespaces) == (type.Namespace ?? "");
    }

    private static string SimpleName(Type type)
    {
        var tick = type.Name.IndexOf('`', StringComparison.Ordinal);
        return tick >= 0 ? type.Name[..tick] : type.Name;
    }

    private static XElement? Element(PropertyDeclarationSyntax declaration)
    {
        var comment = declaration.GetLeadingTrivia()
            .Select(trivia => trivia.GetStructure())
            .OfType<DocumentationCommentTriviaSyntax>()
            .LastOrDefault();
        if (comment is null) return null;
        // The comment's text without the /// exterior is the XML content of its member element.
        var text = string.Join('\n', comment.ToFullString().ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.TrimStart() is var trimmed && trimmed.StartsWith("///", StringComparison.Ordinal) ? trimmed[3..] : line));
        try
        {
            return XElement.Parse("<member>" + text + "</member>", LoadOptions.PreserveWhitespace);
        }
        // Malformed XML in a comment (CS1570) has no summary to copy.
        catch (XmlException)
        {
            return null;
        }
    }

    private static SyntaxTree? Tree(string path)
    {
        if (Trees.TryGetValue(path, out var tree)) return tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(File.ReadAllText(path),
                CSharpParseOptions.Default.WithDocumentationMode(DocumentationMode.Parse).WithLanguageVersion(LanguageVersion.Preview),
                path);
        }
        // Documentation is optional; an unreadable file only means no comment.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            tree = null;
        }
        Trees.Add(path, tree);
        return tree;
    }
}
