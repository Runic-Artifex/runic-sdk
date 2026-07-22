using System;
using WebUIToolkit.DependencyNotices.Spdx;

namespace WebUIToolkit.DependencyNotices.Tests;

internal static class SpdxTests
{
    public static void Register(TestHarness tests)
    {
        tests.Add("SPDX preserves original and canonicalizes whitespace", PreservesOriginalAndCanonicalizesWhitespace);
        tests.Add("SPDX parses WITH before AND before OR", ParsesOperatorPrecedence);
        tests.Add("SPDX honors explicit grouping", HonorsExplicitGrouping);
        tests.Add("SPDX canonical formatting uses minimal parentheses", UsesMinimalParentheses);
        tests.Add("SPDX parses local license references", ParsesLocalLicenseReference);
        tests.Add("SPDX parses document license references", ParsesDocumentLicenseReference);
        tests.Add("SPDX accepts legacy plus identifiers", AcceptsPlusIdentifier);
        tests.Add("SPDX AST records have value equality", AstRecordsHaveValueEquality);
        tests.Add("SPDX rejects empty expressions with an offset", RejectsEmptyExpression);
        tests.Add("SPDX reports missing operands", ReportsMissingOperand);
        tests.Add("SPDX reports missing closing parentheses", ReportsMissingClosingParenthesis);
        tests.Add("SPDX reports unexpected closing parentheses", ReportsUnexpectedClosingParenthesis);
        tests.Add("SPDX rejects lowercase operators", RejectsLowercaseOperator);
        tests.Add("SPDX rejects invalid identifier characters", RejectsInvalidIdentifierCharacters);
        tests.Add("SPDX rejects malformed local references", RejectsMalformedLocalReference);
        tests.Add("SPDX rejects malformed document references", RejectsMalformedDocumentReference);
        tests.Add("SPDX rejects WITH on parenthesized expressions", RejectsWithOnParenthesizedExpression);
        tests.Add("SPDX rejects LicenseRef exceptions", RejectsLicenseReferenceException);
        tests.Add("SPDX rejects plus-suffixed exceptions", RejectsPlusSuffixedException);
        tests.Add("SPDX rejects malformed plus suffixes", RejectsMalformedPlusSuffix);
        tests.Add("SPDX rejects a repeated WITH", RejectsRepeatedWith);
        tests.Add("SPDX rejects null", RejectsNull);
    }

    private static void PreservesOriginalAndCanonicalizesWhitespace()
    {
        const string original = "  MIT\tAND\r\nApache-2.0  ";
        SpdxExpression expression = SpdxParser.Parse(original);

        Assert.Equal(original, expression.Original);
        Assert.Equal("MIT AND Apache-2.0", expression.Canonical);
    }

    private static void ParsesOperatorPrecedence()
    {
        SpdxExpression expression = SpdxParser.Parse("GPL-2.0-only WITH Classpath-exception-2.0 AND MIT OR Apache-2.0");

        Assert.True(expression.Root is SpdxOrNode
        {
            Left: SpdxAndNode
            {
                Left: SpdxWithExceptionNode
                {
                    License.Identifier: "GPL-2.0-only",
                    ExceptionIdentifier: "Classpath-exception-2.0",
                },
                Right: SpdxLicenseIdentifierNode { Identifier: "MIT" },
            },
            Right: SpdxLicenseIdentifierNode { Identifier: "Apache-2.0" },
        });
    }

    private static void HonorsExplicitGrouping()
    {
        SpdxExpression expression = SpdxParser.Parse("MIT AND (Apache-2.0 OR BSD-3-Clause)");

        Assert.True(expression.Root is SpdxAndNode
        {
            Left: SpdxLicenseIdentifierNode { Identifier: "MIT" },
            Right: SpdxOrNode,
        });
        Assert.Equal("MIT AND (Apache-2.0 OR BSD-3-Clause)", expression.Canonical);
    }

    private static void UsesMinimalParentheses()
    {
        Assert.Equal("MIT OR Apache-2.0 AND BSD-3-Clause", SpdxParser.Parse("MIT OR (Apache-2.0 AND BSD-3-Clause)").Canonical);
        Assert.Equal("(MIT OR Apache-2.0) AND BSD-3-Clause", SpdxParser.Parse("(MIT OR Apache-2.0) AND BSD-3-Clause").Canonical);
        Assert.Equal("MIT", SpdxParser.Parse("(((MIT)))").Canonical);
        Assert.Equal("MIT AND Apache-2.0 AND BSD-3-Clause", SpdxParser.Parse("MIT AND (Apache-2.0 AND BSD-3-Clause)").Canonical);
    }

    private static void ParsesLocalLicenseReference()
    {
        SpdxExpression expression = SpdxParser.Parse("LicenseRef-Proprietary.2026 OR MIT");
        Assert.True(expression.Root is SpdxOrNode
        {
            Left: SpdxLicenseIdentifierNode { Identifier: "LicenseRef-Proprietary.2026" },
        });
    }

    private static void ParsesDocumentLicenseReference()
    {
        const string value = "DocumentRef-vendor.spdx:LicenseRef-Custom-1.0";
        SpdxExpression expression = SpdxParser.Parse(value);
        Assert.Equal(value, expression.Canonical);
        Assert.True(expression.Root is SpdxLicenseIdentifierNode { Identifier: value });
    }

    private static void AcceptsPlusIdentifier()
    {
        Assert.Equal("GPL-2.0+", SpdxParser.Parse("GPL-2.0+").Canonical);
    }

    private static void AstRecordsHaveValueEquality()
    {
        SpdxExpressionNode first = new SpdxAndNode(
            new SpdxLicenseIdentifierNode("MIT"),
            new SpdxLicenseIdentifierNode("Apache-2.0"));
        SpdxExpressionNode second = new SpdxAndNode(
            new SpdxLicenseIdentifierNode("MIT"),
            new SpdxLicenseIdentifierNode("Apache-2.0"));

        Assert.Equal(first, second);
        Assert.Equal("MIT AND Apache-2.0", new SpdxExpression("ignored", first).Canonical);
    }

    private static void RejectsEmptyExpression()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("   "));
        Assert.Equal(3, exception.Offset);
        Assert.Equal("license identifier or '('", exception.Expected);
    }

    private static void ReportsMissingOperand()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT AND "));
        Assert.Equal(8, exception.Offset);
        Assert.Equal("license identifier or '('", exception.Expected);
    }

    private static void ReportsMissingClosingParenthesis()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT AND (Apache-2.0 OR BSD-3-Clause"));
        Assert.Equal(35, exception.Offset);
        Assert.Equal("')'", exception.Expected);
    }

    private static void ReportsUnexpectedClosingParenthesis()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT)"));
        Assert.Equal(3, exception.Offset);
        Assert.Equal("end of expression", exception.Expected);
    }

    private static void RejectsLowercaseOperator()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT and Apache-2.0"));
        Assert.Equal(4, exception.Offset);
        Assert.Equal("end of expression", exception.Expected);
    }

    private static void RejectsInvalidIdentifierCharacters()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT/Apache-2.0"));
        Assert.Equal(0, exception.Offset);
        Assert.Equal("valid SPDX license identifier", exception.Expected);
    }

    private static void RejectsMalformedLocalReference()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("LicenseRef-"));
        Assert.Equal(0, exception.Offset);
    }

    private static void RejectsMalformedDocumentReference()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("DocumentRef-vendor:MIT"));
        Assert.Equal(0, exception.Offset);
    }

    private static void RejectsWithOnParenthesizedExpression()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("(GPL-2.0-only) WITH Classpath-exception-2.0"));
        Assert.Equal(15, exception.Offset);
        Assert.Equal("AND, OR, or end of expression", exception.Expected);
    }

    private static void RejectsLicenseReferenceException()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT WITH LicenseRef-Exception"));
        Assert.Equal(9, exception.Offset);
        Assert.Equal("SPDX exception identifier", exception.Expected);
    }

    private static void RejectsPlusSuffixedException()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT WITH LLVM-exception+"));
        Assert.Equal(9, exception.Offset);
        Assert.Equal("SPDX exception identifier", exception.Expected);
    }

    private static void RejectsMalformedPlusSuffix()
    {
        SpdxParseException repeated = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("GPL-2.0++"));
        Assert.Equal(0, repeated.Offset);

        SpdxParseException standalone = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("+"));
        Assert.Equal(0, standalone.Offset);
    }

    private static void RejectsRepeatedWith()
    {
        SpdxParseException exception = Assert.Throws<SpdxParseException>(() => SpdxParser.Parse("MIT WITH LLVM-exception WITH Classpath-exception-2.0"));
        Assert.Equal(24, exception.Offset);
        Assert.Equal("end of expression", exception.Expected);
    }

    private static void RejectsNull()
    {
        _ = Assert.Throws<ArgumentNullException>(() => SpdxParser.Parse(null!));
    }
}
