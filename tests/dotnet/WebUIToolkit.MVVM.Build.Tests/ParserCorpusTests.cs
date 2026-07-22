using WebUIToolkit.MVVM.Build.Compiler;

namespace WebUIToolkit.MVVM.Build.Tests;

internal static class ParserCorpusTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("parser corpus covers composite CLR type spellings", CompositeTypeSpellings);
        runner.Add("parser corpus rejects invalid protocol and type contracts", InvalidContracts);
    }

    private static void CompositeTypeSpellings()
    {
        string[] types =
        [
            "System.String",
            "System.String?",
            "Example.Item[]",
            "System.Collections.Generic.Dictionary<System.String,Example.Item?>",
        ];

        for (int index = 0; index < types.Length; index++)
        {
            string source = $"protocol webuitoolkit.mvvm/1; contract \"case{index}\" model Example.Model {{ property 1 value: {types[index]} => Value readonly; }}";
            BindingSemanticResult result = BindingCompiler.Compile(source, $"corpus/type-{index}.wutmvvm");
            Assert.False(result.HasErrors,
                $"Expected type '{types[index]}' to compile. {ParserContractTests.DiagnosticsText(result.Diagnostics)}");
        }
    }

    private static void InvalidContracts()
    {
        (string Source, string DiagnosticId)[] cases =
        [
            ("contract \"x\" model X {}", BindingDiagnosticIds.ProtocolRequired),
            ("protocol webuitoolkit.mvvm/2; contract \"x\" model X {}", BindingDiagnosticIds.ProtocolMismatch),
            ("protocol webuitoolkit.mvvm/1; contract \"x\" model X { property 0 value: X => Value readonly; }", BindingDiagnosticIds.InvalidMemberId),
            ("protocol webuitoolkit.mvvm/1; contract \"x\" model X { property 1 value: none => Value readonly; }", BindingDiagnosticIds.InvalidTypeName),
        ];

        for (int index = 0; index < cases.Length; index++)
        {
            BindingSemanticResult result = BindingCompiler.Compile(cases[index].Source, $"corpus/invalid-{index}.wutmvvm");
            _ = ParserContractTests.Find(result.Diagnostics, cases[index].DiagnosticId);
            Assert.True(result.HasErrors, "Every invalid corpus case must fail compilation.");
            Assert.Equal(null, result.Model, "Invalid input must not produce a dispatchable semantic model.");
        }
    }
}
