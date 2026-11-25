using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Runic.Application.Views;

namespace Runic.Application.Views.Analyzers.Tests;

// An application project in memory: a Window over MainViewModel, a View over PageViewModel and the members under test,
// analyzed with the build properties of a project that generates its own Bridges.
internal static class AnalyzerHarness
{
    internal static readonly Dictionary<string, string> Generating = new() { ["RunicApplicationFrontendGenerationEnabled"] = "true" };

    internal static string AppSource(string members, string types = "") => $$"""
        #nullable enable
        using System.Collections.Generic;
        using System.Collections.ObjectModel;
        using System.ComponentModel;
        using System.Threading.Tasks;
        using System.Windows.Input;
        using CommunityToolkit.Mvvm.Input;
        using Runic.Application.Views;
        using Runic.Navigation;
        namespace App;
        public sealed partial class MainWindow : RunicWindow<MainViewModel>;
        public sealed partial class MainViewModel : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            public int Count { get; set; }
        {{members}}
        }
        public interface IPage : INotifyPropertyChanged;
        public sealed class PageViewModel : IPage
        {
            public event PropertyChangedEventHandler? PropertyChanged;
            public string Title { get; } = "";
        }
        public sealed partial class PageView : RunicView<PageViewModel>;
        public sealed record Row(string Id, string Name);
        {{types}}
        """;

    // The library types the rules name and the analyzer never references: stand-ins with the same metadata names.
    private const string Libraries = """
        namespace CommunityToolkit.Mvvm.Input
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class RelayCommandAttribute : System.Attribute { public string? CanExecute { get; set; } }
            public interface IRelayCommand : System.Windows.Input.ICommand;
            public interface IAsyncRelayCommand : IRelayCommand;
            public interface IRelayCommand<in T> : IRelayCommand;
            public interface IAsyncRelayCommand<in T> : IAsyncRelayCommand, IRelayCommand<T>;
        }
        namespace ReactiveUI
        {
            public interface IReactiveCommand<TParam, TResult> : System.Windows.Input.ICommand;
            public abstract class ReactiveCommand<TParam, TResult> : IReactiveCommand<TParam, TResult>
            {
                public event System.EventHandler? CanExecuteChanged;
                public bool CanExecute(object? parameter) => true;
                public void Execute(object? parameter) { }
            }
        }
        namespace ReactiveUI.Binding
        {
            public interface IInteraction<TInput, TOutput>;
            public sealed class Interaction<TInput, TOutput> : IInteraction<TInput, TOutput>;
        }
        namespace ReactiveUI.Binding.Reactive
        {
            public interface IInteraction<TInput, TOutput>;
            public sealed class Interaction<TInput, TOutput> : IInteraction<TInput, TOutput>;
        }
        """;

    // The Runic ReactiveUI adapter the application references (RUNICBRIDGE025): an empty assembly of that name.
    internal const string PrimitivesAdapter = "Runic.Application.Views.ReactiveUI";
    internal const string ReactiveAdapter = "Runic.Application.Views.ReactiveUI.Reactive";

    private static IEnumerable<MetadataReference> RuntimeReferences() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(RunicView<>).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Runic.Navigation.NavigationSelection).Assembly.Location))
            .DistinctBy(reference => reference.Display);

    /// <summary>
    /// A referenced assembly built from <paramref name="source"/>, as a metadata image, so that its types have no
    /// source location in the application's compilation, as a referenced project's or package's do.
    /// </summary>
    internal static MetadataReference Library(string name, string source)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            RuntimeReferences(), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Require(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        return MetadataReference.CreateFromImage(image.ToArray(), filePath: name + ".dll");
    }

    internal static Document Document(string source, string? generated = null, string? adapter = PrimitivesAdapter,
        IEnumerable<MetadataReference>? libraries = null)
    {
        var references = RuntimeReferences().Concat(libraries ?? []);
        if (adapter is not null)
            references = references.Append(CSharpCompilation.Create(adapter, options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary))
                .ToMetadataReference());
        var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "App", "App",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            metadataReferences: references.DistinctBy(reference => reference.Display)));
        project = workspace.AddDocument(project.Id, "Libraries.cs", SourceText.From(Libraries)).Project;
        // A source generator's output, such as CommunityToolkit's command properties.
        if (generated is not null)
            project = project.AddDocument("MainViewModel.g.cs", SourceText.From("// <auto-generated/>\n#nullable enable\nnamespace App;\n" + generated)).Project;
        var document = project.AddDocument("App.cs", SourceText.From(source));
        workspace.TryApplyChanges(document.Project.Solution);
        return workspace.CurrentSolution.GetDocument(document.Id)!;
    }

    internal static Task<ImmutableArray<Diagnostic>> AnalyzeAsync(string source, string? generated = null,
        IReadOnlyDictionary<string, string>? properties = null, string? adapter = PrimitivesAdapter) =>
        AnalyzeAsync(Document(source, generated, adapter), properties);

    internal static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(Document document, IReadOnlyDictionary<string, string>? properties = null)
    {
        var compilation = (await document.Project.GetCompilationAsync())!;
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        Require(errors.Length == 0, string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        var options = new AnalyzerOptions([], new BuildProperties(properties ?? Generating));
        return await compilation.WithAnalyzers([new BridgeShapeAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
    }

    internal static string Describe(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(diagnostic => diagnostic.ToString()));

    internal static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(CultureInfo.InvariantCulture);

    /// <summary>Applies the single code fix offered for the single diagnostic and returns the fixed App.cs.</summary>
    internal static async Task<(string Text, Document Fixed, string Title)> FixAsync(Document document)
    {
        var diagnostic = (await AnalyzeAsync(document)).Single();
        List<CodeAction> actions = [];
        var context = new CodeFixContext(document, diagnostic, (action, _) => actions.Add(action), CancellationToken.None);
        await new BridgeShapeCodeFix().RegisterCodeFixesAsync(context);
        var action = actions.Single();
        var operation = (await action.GetOperationsAsync(CancellationToken.None)).OfType<ApplyChangesOperation>().Single();
        var fixedDocument = operation.ChangedSolution.GetDocument(document.Id)!;
        return ((await fixedDocument.GetTextAsync()).ToString(), fixedDocument, action.Title);
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BuildProperties(IReadOnlyDictionary<string, string> properties) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(properties.ToDictionary(
            property => "build_property." + property.Key, property => property.Value));

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;

        private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
        {
            internal static readonly Options Empty = new([]);

            public override bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) =>
                values.TryGetValue(key, out value);
        }
    }
}
