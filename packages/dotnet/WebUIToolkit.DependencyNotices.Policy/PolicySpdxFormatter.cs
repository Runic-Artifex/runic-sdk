using System;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Policy;

internal static class PolicySpdxFormatter
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
