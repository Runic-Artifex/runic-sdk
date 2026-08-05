using System;
using RunicToolkit.MVVM.Build.Compiler;

namespace RunicToolkit.MVVM.Build.Tests;

internal static class ParserContractTests
{
    public static void Register(TestRunner runner)
    {
        runner.Add("parser accepts the closed binding vocabulary", AcceptsClosedVocabulary);
        runner.Add("parser retains exact zero-based UTF-16 diagnostic spans", RetainsExactDiagnosticSpans);
        runner.Add("parser keeps diagnostic paths logical and checkout independent", KeepsLogicalPaths);
    }

    private static void AcceptsClosedVocabulary()
    {
        BindingParseResult result = BindingParser.Parse(ValidSource, "bindings/settings.rtkmvvm");

        Assert.False(result.HasErrors, DiagnosticsText(result.Diagnostics));
        Assert.Equal("runic.toolkit.mvvm/1", result.Syntax.Protocol?.Identity);
        BindingContractSyntax contract = Assert.Single(result.Syntax.Contracts);
        Assert.Equal("settings", contract.Name);
        Assert.Equal("Example.SettingsViewModel", contract.ModelType);
        Assert.Equal(4, contract.Members.Count);

        PropertyBindingSyntax property = (PropertyBindingSyntax)contract.Members[0];
        Assert.Equal(1, property.Id);
        Assert.Equal("System.String?", property.ValueType);
        Assert.Equal(BindingAccess.ReadWrite, property.Access);

        CollectionBindingSyntax collection = (CollectionBindingSyntax)contract.Members[1];
        Assert.Equal("Example.Item[]", collection.ItemType);

        CommandBindingSyntax command = (CommandBindingSyntax)contract.Members[2];
        Assert.Equal("none", command.ParameterType);
        Assert.Equal("none", command.ResultType);

        ValidationBindingSyntax validation = (ValidationBindingSyntax)contract.Members[3];
        Assert.Equal("serverName", validation.TargetName);
        Assert.Equal(null, validation.Id);
    }

    private static void RetainsExactDiagnosticSpans()
    {
        const string source = "protocol runic.toolkit.mvvm/1;\r\ncontract \"settings\" model Example.Model {\r\n@\r\n}\r\n";
        const string path = "diagnostics/exact-span.rtkmvvm";
        BindingParseResult result = BindingParser.Parse(source, path);
        BindingDiagnostic diagnostic = Find(result.Diagnostics, BindingDiagnosticIds.UnexpectedToken);
        int offset = source.IndexOf('@', StringComparison.Ordinal);

        Assert.Equal("Expected a property, collection, command, or validation declaration, but found '@'.", diagnostic.Message);
        Assert.Equal(path, diagnostic.Span.LogicalPath);
        Assert.Equal(offset, diagnostic.Span.Start.Offset);
        Assert.Equal(2, diagnostic.Span.Start.Line);
        Assert.Equal(0, diagnostic.Span.Start.Column);
        Assert.Equal(offset + 1, diagnostic.Span.End.Offset);
        Assert.Equal(2, diagnostic.Span.End.Line);
        Assert.Equal(1, diagnostic.Span.End.Column);
        Assert.Equal(1, diagnostic.Span.Length);
    }

    private static void KeepsLogicalPaths()
    {
        const string malformed = "protocol unknown/9; contract \"x\" model X {}";
        BindingSemanticResult first = BindingCompiler.Compile(malformed, "one/contract.rtkmvvm");
        BindingSemanticResult second = BindingCompiler.Compile(malformed, "other/contract.rtkmvvm");
        BindingDiagnostic firstDiagnostic = Find(first.Diagnostics, BindingDiagnosticIds.ProtocolMismatch);
        BindingDiagnostic secondDiagnostic = Find(second.Diagnostics, BindingDiagnosticIds.ProtocolMismatch);

        Assert.Equal(firstDiagnostic.Id, secondDiagnostic.Id);
        Assert.Equal(firstDiagnostic.Message, secondDiagnostic.Message);
        Assert.Equal(firstDiagnostic.Span.Start, secondDiagnostic.Span.Start);
        Assert.Equal(firstDiagnostic.Span.End, secondDiagnostic.Span.End);
        Assert.Equal("one/contract.rtkmvvm", firstDiagnostic.Span.LogicalPath);
        Assert.Equal("other/contract.rtkmvvm", secondDiagnostic.Span.LogicalPath);
    }

    internal const string ValidSource = """
        protocol runic.toolkit.mvvm/1;
        contract "settings" model Example.SettingsViewModel {
          property 1 serverName: System.String? => ServerName readwrite;
          collection 2 items: Example.Item[] => Items readonly;
          command 3 save: none -> none => Save;
          validation serverNameErrors for serverName => GetErrors;
        }
        """;

    internal static BindingDiagnostic Find(
        System.Collections.Generic.IReadOnlyList<BindingDiagnostic> diagnostics,
        string id)
    {
        BindingDiagnostic? found = null;
        foreach (BindingDiagnostic diagnostic in diagnostics)
        {
            if (string.Equals(diagnostic.Id, id, StringComparison.Ordinal))
            {
                if (found is not null)
                {
                    throw new InvalidOperationException($"Expected one {id} diagnostic, but found more than one.");
                }

                found = diagnostic;
            }
        }

        return found ?? throw new InvalidOperationException($"Expected diagnostic {id}; actual: {DiagnosticsText(diagnostics)}");
    }

    internal static string DiagnosticsText(System.Collections.Generic.IReadOnlyList<BindingDiagnostic> diagnostics)
    {
        var builder = new System.Text.StringBuilder();
        foreach (BindingDiagnostic diagnostic in diagnostics)
        {
            builder.Append(diagnostic.Id).Append(' ').Append(diagnostic.Message).AppendLine();
        }

        return builder.ToString();
    }
}
