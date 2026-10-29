using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using TUnit.Core;

namespace Runic.Platform.Analyzers.Tests;

// RUNICPLAT001: DesktopMenuItem(Title, CommandId) arguments that look swapped, and the code fix that swaps and names them.
public sealed class DesktopMenuItemArgumentTests
{
    [Test]
    [Arguments("new DesktopMenuItem(\"file.new\", \"New Window\")", "is not a valid command id")]
    [Arguments("new DesktopMenuItem(\"file.open\", \"Open\")", "looks like a title")]
    [Arguments("new DesktopMenuItem(\"edit.find-next\", \"Find Next…\")", "is not a valid command id")]
    [Arguments("new DesktopMenuItem(\"app.quit\", \"Quit\") { Shortcut = null }", "looks like a title")]
    [Arguments("new DesktopMenuItem(CommandId: \"Settings\", Title: \"app.settings\")", "looks like a title")]
    public async Task ReportsLikelySwappedArguments(string creation, string reason)
    {
        var diagnostics = await AnalyzeAsync(creation);
        Require(diagnostics.Length == 1 && diagnostics[0].Id == "RUNICPLAT001"
            && diagnostics[0].GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains(reason, StringComparison.Ordinal)
            && diagnostics[0].Severity == DiagnosticSeverity.Warning,
            $"{creation}: {string.Join("; ", diagnostics.Select(diagnostic => diagnostic.ToString()))}");
    }

    [Test]
    [Arguments("new DesktopMenuItem(\"New Window\", \"file.new\")")]
    [Arguments("new DesktopMenuItem(\"Quit\", \"quit\")")]
    [Arguments("new DesktopMenuItem(\"v1.2\", \"Version\")")]
    [Arguments("new DesktopMenuItem(\"e.g.\", \"Example\")")]
    [Arguments("new DesktopMenuItem(title, \"file.new\")")]
    [Arguments("new DesktopMenuItem(\"file.new\", commandId)")]
    [Arguments("new DesktopMenuItem(\"file.new\", \"file.open\")")]
    public async Task AcceptsTitlesAndCommandIdsInOrder(string creation)
    {
        var diagnostics = await AnalyzeAsync(creation);
        Require(diagnostics.IsEmpty, $"{creation}: {string.Join("; ", diagnostics.Select(diagnostic => diagnostic.ToString()))}");
    }

    [Test]
    [Arguments("new DesktopMenuItem(\"file.new\", \"New Window\")", "new DesktopMenuItem(Title: \"New Window\", CommandId: \"file.new\")")]
    [Arguments("new DesktopMenuItem(\"app.quit\", \"Quit\") { IsEnabled = false }", "new DesktopMenuItem(Title: \"Quit\", CommandId: \"app.quit\") { IsEnabled = false }")]
    [Arguments("new DesktopMenuItem(CommandId: \"Settings\", Title: \"app.settings\")", "new DesktopMenuItem(CommandId: \"app.settings\", Title: \"Settings\")")]
    public async Task CodeFixSwapsAndNamesTheArguments(string creation, string expected)
    {
        var document = Document(creation);
        var diagnostic = (await AnalyzeAsync(document)).Single();
        List<CodeAction> actions = [];
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new DesktopMenuItemArgumentCodeFix().RegisterCodeFixesAsync(context);
        var operation = (await actions.Single().GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var text = (await operation.ChangedSolution.GetDocument(document.Id)!.GetTextAsync()).ToString();
        Require(text.Contains(expected, StringComparison.Ordinal), $"The fix wrote:{Environment.NewLine}{text}");
        Require((await AnalyzeAsync(operation.ChangedSolution.GetDocument(document.Id)!)).IsEmpty, "The fixed code is still reported.");
    }

    private static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string creation) => AnalyzeAsync(Document(creation));

    private static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Document document)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Require(errors.Length == 0, string.Join("; ", errors.Select(error => error.ToString())));
        return await compilation.WithAnalyzers([new DesktopMenuItemArgumentAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    private static Document Document(string creation)
    {
        var source = $$"""
            using Runic.Platform;
            public static class Menus
            {
                public static DesktopMenuItem Create(string title, string commandId) => {{creation}};
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(DesktopMenuItem).Assembly.Location));
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Menus", "Menus",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: references.DistinctBy(reference => reference.Display)));
        return workspace.AddDocument(project.Id, "Menus.cs", SourceText.From(source));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
