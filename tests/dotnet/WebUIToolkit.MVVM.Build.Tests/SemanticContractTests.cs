using System;
using System.Linq;
using WebUIToolkit.MVVM.Build.Compiler;

namespace WebUIToolkit.MVVM.Build.Tests;

internal static class SemanticContractTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("semantic model is canonical and validation shares its target ID", ModelIsCanonical);
        runner.Add("semantic model distinguishes an absent command value from nullable data", DistinguishesAbsentFromNull);
        runner.Add("semantic duplicate-ID diagnostic maps both declarations", DuplicateIdMapsBothDeclarations);
        runner.Add("semantic validation-target diagnostic has an exact span", InvalidValidationTargetHasExactSpan);
    }

    private static void ModelIsCanonical()
    {
        BindingSemanticResult result = BindingCompiler.Compile(ParserContractTests.ValidSource, "settings.wutmvvm");
        Assert.False(result.HasErrors, ParserContractTests.DiagnosticsText(result.Diagnostics));
        BindingSemanticModel model = result.Model ?? throw new InvalidOperationException("Expected a semantic model.");
        Assert.Equal("webuitoolkit.mvvm/1", model.ProtocolIdentity);
        BindingContractModel contract = Assert.Single(model.Contracts);
        Assert.Equal("settings", contract.Name);
        Assert.Equal(4, contract.Members.Count);
        AssertSequence(
            [(1, BindingMemberKind.Property), (1, BindingMemberKind.Validation), (2, BindingMemberKind.Collection), (3, BindingMemberKind.Command)],
            contract.Members.Select(static member => (member.Id, member.Kind)).ToArray());
    }

    private static void DistinguishesAbsentFromNull()
    {
        BindingSemanticResult result = BindingCompiler.Compile(ParserContractTests.ValidSource);
        BindingContractModel contract = Assert.Single(result.Model?.Contracts ??
            throw new InvalidOperationException(ParserContractTests.DiagnosticsText(result.Diagnostics)));
        BindingMemberModel property = contract.Members.Single(static member => member.Kind == BindingMemberKind.Property);
        BindingMemberModel command = contract.Members.Single(static member => member.Kind == BindingMemberKind.Command);

        Assert.Equal("System.String?", property.ValueType,
            "A nullable CLR value is still a present typed value in the contract.");
        Assert.Equal(null, command.ParameterType,
            "The grammar token 'none' denotes an absent command argument, not a null JSON value.");
        Assert.Equal(null, command.ResultType,
            "The grammar token 'none' denotes an absent command result, not a null JSON value.");
    }

    private static void DuplicateIdMapsBothDeclarations()
    {
        const string source = """
            protocol webuitoolkit.mvvm/1;
            contract "duplicate" model Example.Model {
              property 7 first: System.String => First readonly;
              property 7 second: System.String => Second readonly;
            }
            """;
        BindingSemanticResult result = BindingCompiler.Compile(source, "duplicate-id.wutmvvm");
        BindingDiagnostic diagnostic = ParserContractTests.Find(result.Diagnostics, BindingDiagnosticIds.DuplicateMemberId);
        int firstOffset = source.IndexOf("7 first", StringComparison.Ordinal);
        int duplicateOffset = source.IndexOf("7 second", StringComparison.Ordinal);

        Assert.Equal("Member ID '7' is declared more than once for kind 'Property' in this contract.", diagnostic.Message);
        Assert.Equal(duplicateOffset, diagnostic.Span.Start.Offset);
        Assert.Equal(1, diagnostic.Span.Length);
        BindingSourceSpan related = diagnostic.RelatedSpan ??
            throw new InvalidOperationException("Duplicate declarations must map the first declaration as a related span.");
        Assert.Equal(firstOffset, related.Start.Offset);
        Assert.Equal(1, related.Length);
    }

    private static void InvalidValidationTargetHasExactSpan()
    {
        const string source = """
            protocol webuitoolkit.mvvm/1;
            contract "invalid" model Example.Model {
              validation errors for missing => GetErrors;
            }
            """;
        BindingSemanticResult result = BindingCompiler.Compile(source, "invalid-target.wutmvvm");
        BindingDiagnostic diagnostic = ParserContractTests.Find(result.Diagnostics, BindingDiagnosticIds.InvalidValidationTarget);
        int offset = source.IndexOf("missing", StringComparison.Ordinal);

        Assert.Equal("A validation member must target a declared property or collection.", diagnostic.Message);
        Assert.Equal(offset, diagnostic.Span.Start.Offset);
        Assert.Equal("missing".Length, diagnostic.Span.Length);
        Assert.Equal("invalid-target.wutmvvm", diagnostic.Span.LogicalPath);
    }

    private static void AssertSequence<T>(T[] expected, T[] actual)
    {
        Assert.Equal(expected.Length, actual.Length, "Sequence lengths differ");
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index], actual[index], $"Sequence differs at index {index}");
        }
    }
}
