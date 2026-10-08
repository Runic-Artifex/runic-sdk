using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Compiles small ViewModel assemblies that the generator must reject and
/// checks its diagnostics. Valid shapes live in this project instead, so the
/// project build itself proves that their generated C# compiles.
/// </summary>
internal static class CodegenDiagnosticsTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Text;
        using CommunityToolkit.Mvvm.Input;
        using Runic.Application.Views;

        namespace Fixture;

        public abstract class FixtureModel : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        """;

    internal static async Task RunAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-diagnostics-{Guid.NewGuid():N}");
        try
        {
            async Task Reject(string name, string members, params string[] expected)
            {
                var output = await GenerateAsync(generator, temporaryRoot, name, Preamble + members).ConfigureAwait(false);
                foreach (var text in expected)
                    Require(output.Contains(text, StringComparison.Ordinal),
                        $"{name}: the generator output did not contain '{text}'.\n{output}");
            }

            // Each diagnostic has its own ID and points at the declaration
            // through the fixture's PDB: "<file>(<line>,<column>): error <ID>: ".
            async Task RejectAt(string name, string members, string code, string declaration, params string[] expected)
            {
                var source = Preamble + members;
                var output = await GenerateAsync(generator, temporaryRoot, name, source).ConfigureAwait(false);
                var line = source[..source.IndexOf(declaration, StringComparison.Ordinal)].Count(character => character == '\n') + 1;
                var location = $"{Path.Combine(temporaryRoot, name, "Fixture.cs")}({line},";
                Require(output.Split('\n').Any(text => text.StartsWith(location, StringComparison.Ordinal)
                        && text.Contains($": error {code}: ", StringComparison.Ordinal)),
                    $"{name}: expected 'error {code}' at line {line} of the fixture.\n{output}");
                foreach (var text in expected)
                    Require(output.Contains(text, StringComparison.Ordinal),
                        $"{name}: the generator output did not contain '{text}'.\n{output}");
            }

            await RejectAt("HashSetValue", """
                public sealed class SetViewModel : FixtureModel { public HashSet<string> Tags { get; } = []; }
                public sealed partial class SetWindow(SetViewModel model) : RunicWindow<SetViewModel>(model);
                """, "RUNICBRIDGE003", "public sealed class SetViewModel",
                "System.Collections.Generic.HashSet`1", "is not a supported bridge collection").ConfigureAwait(false);
            // A nested DTO member points at the DTO property, not the ViewModel.
            await RejectAt("NestedValue", """
                public sealed class Row
                {
                    public string Name { get; init; } = "";
                    public object Value { get; init; } = new();
                }
                public sealed class RowViewModel : FixtureModel { public Row Current { get; } = new(); }
                public sealed partial class RowWindow(RowViewModel model) : RunicWindow<RowViewModel>(model);
                """, "RUNICBRIDGE003", "public object Value", "RowViewModel.Current.value").ConfigureAwait(false);
            await RejectAt("InternalWindow", """
                public sealed class HiddenViewModel : FixtureModel { public string Title { get; } = ""; }
                internal sealed partial class HiddenWindow(HiddenViewModel model) : RunicWindow<HiddenViewModel>(model)
                {
                    public string Caption => "hidden";
                }
                """, "RUNICBRIDGE006", "internal sealed partial class HiddenWindow", "Fixture.HiddenWindow: a Runic Window or View must be a public").ConfigureAwait(false);
            await RejectAt("EmptyViewModel", """
                public sealed class EmptyViewModel : FixtureModel
                {
                    internal string Hidden => "";
                }
                public sealed partial class EmptyWindow(EmptyViewModel model) : RunicWindow<EmptyViewModel>(model);
                """, "RUNICBRIDGE007", "internal string Hidden",
                "EmptyViewModel: a ViewModel needs at least one state property, command, or interaction.").ConfigureAwait(false);
            await RejectAt("WriteOnlyState", """
                public sealed class WriteOnlyViewModel : FixtureModel
                {
                    public string Name { set { } }
                }
                public sealed partial class WriteOnlyWindow(WriteOnlyViewModel model) : RunicWindow<WriteOnlyViewModel>(model);
                """, "RUNICBRIDGE008", "public string Name { set { } }",
                "WriteOnlyViewModel.Name: a public getter is required.").ConfigureAwait(false);
            // NavigationRegion<TContent> slots (W230-002): a known content type, get-only.
            await RejectAt("ObjectRegion", """
                #pragma warning disable RUNICNAV001
                public sealed class AnyRegionViewModel : FixtureModel
                {
                    public AnyRegionViewModel(RunicNavigator navigator) => Main = navigator.CreateRegion<object>(this);
                    public NavigationRegion<object> Main { get; }
                }
                public sealed partial class AnyRegionWindow(AnyRegionViewModel model) : RunicWindow<AnyRegionViewModel>(model);
                """, "RUNICBRIDGE008", "public NavigationRegion<object> Main",
                "AnyRegionViewModel.Main: a NavigationRegion<object> slot has no known content.").ConfigureAwait(false);
            await RejectAt("SettableRegion", """
                #pragma warning disable RUNICNAV001
                public interface IRegionPage : INotifyPropertyChanged { }
                public sealed class RegionPageViewModel : FixtureModel, IRegionPage { public string Title => ""; }
                public sealed partial class RegionPageView : RunicView<RegionPageViewModel>;
                public sealed class SettableRegionViewModel : FixtureModel
                {
                    public NavigationRegion<IRegionPage>? Main { get; set; }
                }
                public sealed partial class SettableRegionWindow(SettableRegionViewModel model) : RunicWindow<SettableRegionViewModel>(model);
                """, "RUNICBRIDGE008", "public NavigationRegion<IRegionPage>? Main",
                "SettableRegionViewModel.Main: a NavigationRegion slot must be get-only").ConfigureAwait(false);
            await RejectAt("UnknownRegionContent", """
                #pragma warning disable RUNICNAV001
                public interface IMissingPage : INotifyPropertyChanged { }
                public sealed class MissingRegionViewModel : FixtureModel
                {
                    public NavigationRegion<IMissingPage>? Main { get; }
                }
                public sealed partial class MissingRegionWindow(MissingRegionViewModel model) : RunicWindow<MissingRegionViewModel>(model);
                """, "RUNICBRIDGE008", "public NavigationRegion<IMissingPage>? Main",
                "no ViewModel with a registered View is a IMissingPage").ConfigureAwait(false);
            await RejectAt("UnkeyedCollection", """
                public sealed class Item { public string Label { get; init; } = ""; }
                public sealed class ItemsViewModel : FixtureModel
                {
                    [RunicCollection("Id")] public IReadOnlyList<Item> Items { get; } = [];
                }
                public sealed partial class ItemsWindow(ItemsViewModel model) : RunicWindow<ItemsViewModel>(model);
                """, "RUNICBRIDGE010", "[RunicCollection(\"Id\")]",
                "ItemsViewModel.Items: the collection key 'Id' must name").ConfigureAwait(false);
            await RejectAt("SettableInteraction", """
                public sealed class PromptViewModel : FixtureModel
                {
                    public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; set; } = new();
                }
                public sealed partial class PromptWindow(PromptViewModel model) : RunicWindow<PromptViewModel>(model);
                """, "RUNICBRIDGE011", "public ReactiveUI.Binding.Interaction<string, bool> Confirm",
                "PromptViewModel.Confirm: interactions are application-owned").ConfigureAwait(false);
            // RUNICBRIDGE012: a misplaced, repeated or unsupported failure declaration.
            await RejectAt("FailureOnState", """
                public sealed class StateFailureViewModel : FixtureModel { [RunicFailure(typeof(string))] public string Title { get; set; } = ""; }
                public sealed partial class StateFailureWindow(StateFailureViewModel model) : RunicWindow<StateFailureViewModel>(model);
                """, "RUNICBRIDGE012", "public string Title",
                "StateFailureViewModel.Title: RunicFailure applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method.").ConfigureAwait(false);
            await RejectAt("FailureOnBoth", """
                public sealed record Missing;
                public sealed class BothFailureViewModel : FixtureModel
                {
                    [RunicFailure(typeof(Missing))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                    [RelayCommand, RunicFailure(typeof(Missing))] private void Save() { }
                }
                public sealed partial class BothFailureWindow(BothFailureViewModel model) : RunicWindow<BothFailureViewModel>(model);
                """, "RUNICBRIDGE012", "[RelayCommand, RunicFailure(typeof(Missing))] private void Save()",
                "BothFailureViewModel.SaveCommand: declare RunicFailure once, on the command property or on its [RelayCommand] method, not on both.").ConfigureAwait(false);
            foreach (var (name, type, shown) in new[] { ("FailureException", "InvalidOperationException", "System.InvalidOperationException"),
                ("FailureObject", "object", "System.Object"), ("FailureNullable", "int?", "System.Nullable<System.Int32>") })
                await RejectAt(name, $$"""
                    public sealed class {{name}}ViewModel : FixtureModel
                    {
                        [RunicFailure(typeof({{type}}))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                    }
                    public sealed partial class {{name}}Window({{name}}ViewModel model) : RunicWindow<{{name}}ViewModel>(model);
                    """, "RUNICBRIDGE012", "public IRelayCommand SaveCommand",
                    $"{name}ViewModel.SaveCommand: the failure type {shown} must be a Bridge value type other than object, an exception or Nullable<T>.").ConfigureAwait(false);
            // A declaration on an inherited member is reported too: only the model's own commands are bridged.
            await RejectAt("FailureInherited", """
                public abstract class EditorBase : FixtureModel
                {
                    [RunicFailure(typeof(string))] public IRelayCommand ResetCommand { get; } = new RelayCommand(() => { });
                }
                public sealed class InheritedFailureViewModel : EditorBase { public string Title { get; set; } = ""; }
                public sealed partial class InheritedFailureWindow(InheritedFailureViewModel model) : RunicWindow<InheritedFailureViewModel>(model);
                """, "RUNICBRIDGE012", "public IRelayCommand ResetCommand",
                "InheritedFailureViewModel.ResetCommand: RunicFailure applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method; EditorBase declares it on a member the Bridge does not expose.").ConfigureAwait(false);
            // An override of a declared abstract command keeps the declaration and is valid.
            var overridden = await GenerateValidAsync(generator, temporaryRoot, "FailureOverride", Preamble + """
                public abstract class ResettableBase : FixtureModel
                {
                    [RunicFailure(typeof(string))] public abstract IRelayCommand ResetCommand { get; }
                }
                public sealed class OverrideFailureViewModel : ResettableBase
                {
                    public override IRelayCommand ResetCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class OverrideFailureWindow(OverrideFailureViewModel model) : RunicWindow<OverrideFailureViewModel>(model);
                """).ConfigureAwait(false);
            Require(Directory.GetFiles(overridden, "*OverrideFailureViewModel.Bridge.g.cs").Select(File.ReadAllText)
                    .Any(text => text.Contains("EncodeFailure: failure => failure is global::System.String declared", StringComparison.Ordinal)),
                "An override of a declared abstract command lost its failure codec.");
            // A failure type the Bridge cannot encode is RUNICBRIDGE003 at {Model}.{Command}.failure.
            await RejectAt("FailurePayload", """
                public sealed class Opaque { public object Value { get; init; } = new(); }
                public sealed class PayloadFailureViewModel : FixtureModel
                {
                    [RunicFailure(typeof(Opaque))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class PayloadFailureWindow(PayloadFailureViewModel model) : RunicWindow<PayloadFailureViewModel>(model);
                """, "RUNICBRIDGE003", "public object Value", "PayloadFailureViewModel.SaveCommand.failure.value").ConfigureAwait(false);
            await Reject("ObjectValue", """
                public sealed class ObjectViewModel : FixtureModel { public object Value { get; } = new(); }
                public sealed partial class ObjectWindow(ObjectViewModel model) : RunicWindow<ObjectViewModel>(model);
                """, "System.Object is not an explicitly supported bridge value").ConfigureAwait(false);
            await Reject("FrameworkValue", """
                public sealed class BuilderViewModel : FixtureModel { public StringBuilder Text { get; } = new(); }
                public sealed partial class BuilderWindow(BuilderViewModel model) : RunicWindow<BuilderViewModel>(model);
                """, "System.Text.StringBuilder is not an explicitly supported bridge value").ConfigureAwait(false);
            await Reject("IntegerDictionary", """
                public sealed class LookupViewModel : FixtureModel { public Dictionary<int, string> Lookup { get; } = []; }
                public sealed partial class LookupWindow(LookupViewModel model) : RunicWindow<LookupViewModel>(model);
                """, "is not a supported bridge dictionary").ConfigureAwait(false);
            await Reject("EmptyDto", """
                public sealed class Marker { }
                public sealed class MarkerViewModel : FixtureModel { public Marker Value { get; } = new(); }
                public sealed partial class MarkerWindow(MarkerViewModel model) : RunicWindow<MarkerViewModel>(model);
                """, "Fixture.Marker has no public readable properties").ConfigureAwait(false);
            await Reject("DerivedCollection", """
                public sealed class Names : List<string> { }
                public sealed class NamesViewModel : FixtureModel { public Names Values { get; } = []; }
                public sealed partial class NamesWindow(NamesViewModel model) : RunicWindow<NamesViewModel>(model);
                """, "Fixture.Names is not a supported bridge collection").ConfigureAwait(false);

            // Generated wire, member and route names are checked at build time.
            await Reject("AliasCollision", """
                public sealed class AliasViewModel : FixtureModel
                {
                    [RunicAlias("canSave")] public bool Ready { get; }
                    public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class AliasWindow(AliasViewModel model) : RunicWindow<AliasViewModel>(model);
                """, "error RUNICBRIDGE004:", "generated state name 'canSave' conflicts between Ready and SaveCommand availability").ConfigureAwait(false);

            // Every ViewModel's problem is reported in one run.
            await Reject("SeveralErrors", """
                public sealed class FirstViewModel : FixtureModel { public HashSet<int> Values { get; } = []; }
                public sealed class SecondViewModel : FixtureModel { public object Value { get; } = new(); }
                public sealed partial class FirstWindow(FirstViewModel model) : RunicWindow<FirstViewModel>(model);
                public sealed partial class SecondWindow(SecondViewModel model) : RunicWindow<SecondViewModel>(model);
                """, "FirstViewModel.Values", "SecondViewModel.Value").ConfigureAwait(false);
            // A short ReactiveUI command name was sliced before its suffix check.
            await RejectAt("ShortReactiveCommand", """
                public sealed class GoViewModel : FixtureModel { public ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> Go { get; } = null!; }
                public sealed partial class GoWindow(GoViewModel model) : RunicWindow<GoViewModel>(model);
                """, "RUNICBRIDGE009", "public sealed class GoViewModel", "GoViewModel.Go: Bridge commands must end with Command.").ConfigureAwait(false);
            await Reject("ErrorsCollision", """
                public sealed class ErrorsViewModel : FixtureModel, INotifyDataErrorInfo
                {
                    public string Name { get; } = "";
                    public string[] NameErrors { get; } = [];
                    public bool HasErrors => false;
                    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged { add { } remove { } }
                    public System.Collections.IEnumerable GetErrors(string? propertyName) => Array.Empty<string>();
                }
                public sealed partial class ErrorsWindow(ErrorsViewModel model) : RunicWindow<ErrorsViewModel>(model);
                """, "generated state name 'nameErrors' conflicts").ConfigureAwait(false);
            await Reject("MountRoute", """
                public sealed class MountViewModel : FixtureModel { public IRelayCommand MountCommand { get; } = new RelayCommand(() => { }); }
                public sealed partial class MountWindow(MountViewModel model) : RunicWindow<MountViewModel>(model);
                """, "generated route name 'Mount' conflicts between the View mount route and MountCommand").ConfigureAwait(false);
            await Reject("SnapshotRoute", """
                public sealed class SnapshotViewModel : FixtureModel { public IRelayCommand SnapshotCommand { get; } = new RelayCommand(() => { }); }
                public sealed partial class SnapshotWindow(SnapshotViewModel model) : RunicWindow<SnapshotViewModel>(model);
                """, "name 'snapshot' conflicts between the generated snapshot property and SnapshotCommand").ConfigureAwait(false);
            await Reject("UnmountRoute", """
                public sealed class UnmountViewModel : FixtureModel { public IRelayCommand<string> UnmountCommand { get; } = new RelayCommand<string>(_ => { }); }
                public sealed partial class UnmountWindow(UnmountViewModel model) : RunicWindow<UnmountViewModel>(model);
                """, "generated route name 'Unmount' conflicts between the View unmount route and UnmountCommand").ConfigureAwait(false);
            await Reject("ThenableView", """
                public sealed class ThenViewModel : FixtureModel { public IRelayCommand ThenCommand { get; } = new RelayCommand(() => { }); }
                public sealed partial class ThenWindow(ThenViewModel model) : RunicWindow<ThenViewModel>(model);
                """, "generated view name 'then' conflicts", "thenable").ConfigureAwait(false);
            await Reject("AvailabilityRoute", """
                public sealed class AvailabilityViewModel : FixtureModel
                {
                    public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                    public IRelayCommand CanSaveCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class AvailabilityWindow(AvailabilityViewModel model) : RunicWindow<AvailabilityViewModel>(model);
                """, "generated route name 'CanSave' conflicts between SaveCommand availability query and CanSaveCommand").ConfigureAwait(false);

            // Toolkit commands have no result, so a result cardinality is a mistake.
            await RejectAt("ToolkitResult", """
                public sealed class ResultViewModel : FixtureModel
                {
                    [RunicCommandResult(BridgeCommandResultCardinality.Last)]
                    public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                }
                public sealed partial class ResultWindow(ResultViewModel model) : RunicWindow<ResultViewModel>(model);
                """, "RUNICBRIDGE009", "public IAsyncRelayCommand LoadCommand",
                "LoadCommand: RunicCommandResult selects a ReactiveUI command's result cardinality").ConfigureAwait(false);

            // The contract fingerprint follows the generated wire shape, so a
            // nullable ReactiveUI argument is a contract change for hot reload.
            static string Fingerprint(string name, string commandType)
            {
                using var image = new MemoryStream();
                var emitted = Compile(name, Preamble + $$"""
                    public sealed class EchoViewModel : FixtureModel { public {{commandType}} EchoCommand { get; } = null!; }
                    public sealed partial class EchoWindow(EchoViewModel model) : RunicWindow<EchoViewModel>(model);
                    """).Emit(image);
                Require(emitted.Success, $"{name}: the fingerprint fixture did not compile.");
                var model = System.Reflection.Assembly.Load(image.ToArray()).GetType("Fixture.EchoViewModel")!;
                return Runic.Application.Views.BridgeContractShape.Compute(model);
            }
            Require(Fingerprint("FingerprintPlain", "ReactiveUI.ReactiveCommand<string, string>")
                != Fingerprint("FingerprintNullable", "ReactiveUI.ReactiveCommand<string?, string>"),
                "The contract fingerprint ignored a nullable ReactiveUI command input.");

            // Named types are written to types.ts; a hand-written file there
            // is reported instead of being overwritten.
            var handWritten = Path.Combine(temporaryRoot, "HandWrittenTypes", "ts", "types.ts");
            Directory.CreateDirectory(Path.GetDirectoryName(handWritten)!);
            File.WriteAllText(handWritten, "export type Mine = string;\n");
            await Reject("HandWrittenTypes", """
                public sealed class PlainTitleViewModel : FixtureModel { public string Title { get; } = ""; }
                public sealed partial class PlainTitleWindow(PlainTitleViewModel model) : RunicWindow<PlainTitleViewModel>(model);
                """, "error RUNICBRIDGE004:", "types.ts is not generated").ConfigureAwait(false);
            Require(File.ReadAllText(handWritten) == "export type Mine = string;\n",
                "The generator overwrote a hand-written types.ts.");

            // A file that is not a .NET assembly is a diagnostic, not a crash.
            var invalidDirectory = Path.Combine(temporaryRoot, "InvalidImage");
            Directory.CreateDirectory(invalidDirectory);
            var invalidAssembly = Path.Combine(invalidDirectory, "NotAnAssembly.dll");
            File.WriteAllText(invalidAssembly, "not a portable executable");
            var (invalidExit, invalidOutput) = await RunProcessAsync(generator, invalidAssembly, invalidDirectory).ConfigureAwait(false);
            Require(invalidExit != 0 && invalidOutput.Contains("error RUNICBRIDGE005:", StringComparison.Ordinal),
                $"An unloadable model assembly was not reported as RUNICBRIDGE005.\n{invalidOutput}");

            // A ViewModel with interactions but no content still needs a
            // content session, so DI must not offer a transport-only factory.
            var composition = await GenerateValidAsync(generator, temporaryRoot, "InteractionComposition", Preamble + """
                public sealed class AskViewModel : FixtureModel { public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; } = new(); }
                public sealed class PlainViewModel : FixtureModel { public string Title { get; } = ""; }
                public sealed partial class AskWindow(AskViewModel model) : RunicWindow<AskViewModel>(model);
                public sealed partial class PlainWindow(PlainViewModel model) : RunicWindow<PlainViewModel>(model);
                """, "--di-composition", "Fixture.Composition").ConfigureAwait(false);
            var registration = File.ReadAllText(Path.Combine(composition, "RunicBridgeComposition.g.cs"));
            Require(!registration.Contains("Func<IBridgeTransport, global::Fixture.AskViewModel, global::System.IDisposable>", StringComparison.Ordinal)
                && registration.Contains("Func<IBridgeTransport, WindowContentSession, global::Fixture.AskViewModel, global::System.IDisposable>", StringComparison.Ordinal)
                && registration.Contains("Func<IBridgeTransport, global::Fixture.PlainViewModel, global::System.IDisposable>", StringComparison.Ordinal),
                $"DI composition registered a transport-only factory for an interaction ViewModel.\n{registration}");

            // Bridge generation that the build turned on by default is
            // optional: an assembly with no Window or View (a navigator-only
            // application) succeeds, writes nothing and leaves a marker the
            // targets use to skip the frontend steps. Without --optional the
            // project asked for generation, so the same assembly is an error.
            const string NoViews = "public sealed class PlainService { public int Value => 1; }";
            var skippedOutput = await GenerateValidAsync(generator, temporaryRoot, "OptionalNoViews", Preamble + NoViews,
                "--optional").ConfigureAwait(false);
            Require(File.Exists(Path.Combine(skippedOutput, "RunicBridge.NoViews.marker"))
                    && Directory.GetFiles(skippedOutput, "*.g.cs").Length == 0
                    && !Directory.Exists(Path.Combine(Path.GetDirectoryName(skippedOutput)!, "ts")),
                "An optional generation without Windows or Views did not skip cleanly.");
            await Reject("RequiredNoViews", NoViews, "error RUNICBRIDGE006:", "no Runic Window/View classes found")
                .ConfigureAwait(false);
            // Optional only forgives a missing Window: an invalid View still fails.
            var (optionalViewExit, invalidViewOutput, _) = await RunGeneratorAsync(generator, temporaryRoot, "OptionalInvalidView", Preamble + """
                public sealed class HiddenViewModel : FixtureModel { public string Title { get; } = ""; }
                internal sealed partial class HiddenWindow(HiddenViewModel model) : RunicWindow<HiddenViewModel>(model);
                """, "--optional").ConfigureAwait(false);
            Require(optionalViewExit != 0 && invalidViewOutput.Contains("error RUNICBRIDGE006:", StringComparison.Ordinal),
                $"Optional generation accepted an invalid View.\n{invalidViewOutput}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>Compiles one fixture assembly and returns the failed generator's output.</summary>
    private static async Task<string> GenerateAsync(string generator, string temporaryRoot, string name, string source)
    {
        var (exitCode, text, _) = await RunGeneratorAsync(generator, temporaryRoot, name, source).ConfigureAwait(false);
        Require(exitCode != 0, $"{name}: the generator accepted an invalid ViewModel.\n{text}");
        return text;
    }

    /// <summary>Compiles one fixture assembly and returns the successful generator's C# output directory.</summary>
    private static async Task<string> GenerateValidAsync(string generator, string temporaryRoot, string name, string source,
        params string[] options)
    {
        var (exitCode, text, directory) = await RunGeneratorAsync(generator, temporaryRoot, name, source, options).ConfigureAwait(false);
        Require(exitCode == 0, $"{name}: the generator rejected a valid ViewModel.\n{text}");
        return Path.Combine(directory, "cs");
    }

    private static async Task<(int ExitCode, string Output, string Directory)> RunGeneratorAsync(string generator,
        string temporaryRoot, string name, string source, params string[] options)
    {
        var directory = Path.Combine(temporaryRoot, name);
        Directory.CreateDirectory(directory);
        var assembly = Path.Combine(directory, $"Fixture{name}.dll");
        // Emit a portable PDB over a source file on disk, as a project build
        // does, so diagnostics can report source locations.
        var sourcePath = Path.Combine(directory, "Fixture.cs");
        File.WriteAllText(sourcePath, source);
        var compilation = Compile(name, source, sourcePath);
        Microsoft.CodeAnalysis.Emit.EmitResult emitted;
        using (var image = File.Create(assembly))
        using (var symbols = File.Create(Path.ChangeExtension(assembly, ".pdb")))
            emitted = compilation.Emit(image, symbols, options: new Microsoft.CodeAnalysis.Emit.EmitOptions(
                debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.PortablePdb,
                pdbFilePath: Path.ChangeExtension(assembly, ".pdb")));
        if (!emitted.Success)
            throw new InvalidOperationException($"{name}: the fixture did not compile.\n"
                + string.Join('\n', emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        // Like a bootstrap output directory, place application dependencies
        // (for example ReactiveUI.Binding) beside the inspected assembly.
        foreach (var reference in compilation.GetUsedAssemblyReferences().OfType<PortableExecutableReference>())
            if (reference.FilePath is { } path && Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
                File.Copy(path, Path.Combine(directory, Path.GetFileName(path)), overwrite: true);
        var (exitCode, text) = await RunProcessAsync(generator, assembly, directory, options).ConfigureAwait(false);
        return (exitCode, text, directory);
    }

    private static CSharpCompilation Compile(string name, string source, string path = "")
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(reference => MetadataReference.CreateFromFile(reference));
        return CSharpCompilation.Create($"Fixture{name}",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest), path, System.Text.Encoding.UTF8)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Debug));
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string generator, string assembly,
        string directory, params string[] options)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                ArgumentList = { generator, "--generate", assembly, Path.Combine(directory, "cs"), Path.Combine(directory, "ts") },
            },
        };
        foreach (var option in options) process.StartInfo.ArgumentList.Add(option);
        process.StartInfo.Environment["RUNIC_BRIDGE_CODEGEN_CACHE"] = "0";
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"{assembly}: the generator did not exit within 30 seconds.");
        }
        var text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        Require(!text.Contains("Unhandled exception", StringComparison.Ordinal),
            $"{assembly}: the generator crashed instead of reporting a diagnostic.\n{text}");
        return (process.ExitCode, text);
    }

    private static string FindWorkspaceRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "RunicSdk.Core.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate the Runic SDK workspace root.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
