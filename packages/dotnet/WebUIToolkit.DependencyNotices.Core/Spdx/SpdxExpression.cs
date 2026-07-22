using System;

namespace WebUIToolkit.DependencyNotices.Spdx;

/// <summary>Represents a node in an SPDX license expression.</summary>
public abstract record SpdxExpressionNode;

/// <summary>Represents an SPDX license identifier or a locally defined license reference.</summary>
public sealed record SpdxLicenseIdentifierNode : SpdxExpressionNode
{
    /// <summary>Creates a license identifier node.</summary>
    /// <param name="identifier">The identifier exactly as it appeared in the parsed expression.</param>
    public SpdxLicenseIdentifierNode(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        Identifier = identifier;
    }

    /// <summary>Gets the identifier exactly as it appeared in the parsed expression.</summary>
    public string Identifier { get; }
}

/// <summary>Represents a license qualified by an SPDX exception.</summary>
public sealed record SpdxWithExceptionNode : SpdxExpressionNode
{
    /// <summary>Creates a license exception node.</summary>
    /// <param name="license">The license to which the exception applies.</param>
    /// <param name="exceptionIdentifier">The SPDX exception identifier.</param>
    public SpdxWithExceptionNode(SpdxLicenseIdentifierNode license, string exceptionIdentifier)
    {
        ArgumentNullException.ThrowIfNull(license);
        ArgumentException.ThrowIfNullOrWhiteSpace(exceptionIdentifier);
        License = license;
        ExceptionIdentifier = exceptionIdentifier;
    }

    /// <summary>Gets the license to which the exception applies.</summary>
    public SpdxLicenseIdentifierNode License { get; }

    /// <summary>Gets the SPDX exception identifier.</summary>
    public string ExceptionIdentifier { get; }
}

/// <summary>Represents the conjunction of two SPDX license expressions.</summary>
public sealed record SpdxAndNode : SpdxExpressionNode
{
    /// <summary>Creates a conjunction node.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public SpdxAndNode(SpdxExpressionNode left, SpdxExpressionNode right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        Left = left;
        Right = right;
    }

    /// <summary>Gets the left operand.</summary>
    public SpdxExpressionNode Left { get; }

    /// <summary>Gets the right operand.</summary>
    public SpdxExpressionNode Right { get; }
}

/// <summary>Represents the disjunction of two SPDX license expressions.</summary>
public sealed record SpdxOrNode : SpdxExpressionNode
{
    /// <summary>Creates a disjunction node.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="right">The right operand.</param>
    public SpdxOrNode(SpdxExpressionNode left, SpdxExpressionNode right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        Left = left;
        Right = right;
    }

    /// <summary>Gets the left operand.</summary>
    public SpdxExpressionNode Left { get; }

    /// <summary>Gets the right operand.</summary>
    public SpdxExpressionNode Right { get; }
}

/// <summary>Contains an SPDX expression's original text, parsed tree, and canonical text.</summary>
public sealed class SpdxExpression
{
    /// <summary>Creates a parsed SPDX expression.</summary>
    /// <param name="original">The original expression text.</param>
    /// <param name="root">The root of the parsed syntax tree.</param>
    public SpdxExpression(string original, SpdxExpressionNode root)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(root);

        Original = original;
        Root = root;
        Canonical = SpdxExpressionFormatter.Format(root);
    }

    /// <summary>Gets the input text exactly as supplied to the parser.</summary>
    public string Original { get; }

    /// <summary>Gets the immutable root node of the expression tree.</summary>
    public SpdxExpressionNode Root { get; }

    /// <summary>Gets the expression formatted with canonical spacing and minimal parentheses.</summary>
    public string Canonical { get; }
}

internal static class SpdxExpressionFormatter
{
    public static string Format(SpdxExpressionNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return Format(node, 0);
    }

    private static string Format(SpdxExpressionNode node, int parentPrecedence)
    {
        int precedence = GetPrecedence(node);
        string value = node switch
        {
            SpdxLicenseIdentifierNode license => license.Identifier,
            SpdxWithExceptionNode with => $"{with.License.Identifier} WITH {with.ExceptionIdentifier}",
            SpdxAndNode and => $"{Format(and.Left, precedence)} AND {Format(and.Right, precedence)}",
            SpdxOrNode or => $"{Format(or.Left, precedence)} OR {Format(or.Right, precedence)}",
            _ => throw new ArgumentException("Unsupported SPDX expression node type.", nameof(node)),
        };

        return precedence < parentPrecedence ? $"({value})" : value;
    }

    private static int GetPrecedence(SpdxExpressionNode node) => node switch
    {
        SpdxOrNode => 1,
        SpdxAndNode => 2,
        SpdxWithExceptionNode => 3,
        SpdxLicenseIdentifierNode => 4,
        _ => throw new ArgumentException("Unsupported SPDX expression node type.", nameof(node)),
    };
}
