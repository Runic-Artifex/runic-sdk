using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// Compiles small ViewModel assemblies that the generator must reject and
/// checks its diagnostics. Valid shapes live in this project instead, so the
/// project build itself proves that their generated C# compiles.
/// </summary>
internal sealed partial class CodegenDiagnosticsTests
{
    private const string Preamble = """
        using System;
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Text;
        using CommunityToolkit.Mvvm.Input;
        using Runic.Application.Views;
        using Runic.Navigation;

        namespace Fixture;

        public abstract class FixtureModel : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        """;

    // I-028: the committed machine-readable list is the generator's current output. eng/diagnostics.test.mjs checks
    // it against the catalog.
    [Test]
    public async Task DiagnosticListIsTheGeneratorsOutputAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        using var process = Process.Start(new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            ArgumentList = { generator, "--list-diagnostics" },
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(GeneratorTimeout).ConfigureAwait(false);
        var list = await output.ConfigureAwait(false);
        Require(process.ExitCode == 0, $"--list-diagnostics failed: {await error.ConfigureAwait(false)}");
        var committed = File.ReadAllText(Path.Combine(root, "docs", "runicbridge-diagnostics.json")).ReplaceLineEndings("\n");
        Require(list == committed, "docs/runicbridge-diagnostics.json is stale. Run: dotnet run --project "
            + "tools/Runic.Application.Views.Codegen -- --list-diagnostics docs/runicbridge-diagnostics.json\n" + list);
    }

    // RUNICBRIDGE028 (#284): a RunicOperationRetirement declaration that reaches no operation is a located warning, and
    // the policy reaches the generated descriptor only for an operation and only when it is not Keep.
    [Test]
    public async Task OperationRetirementReachesOperationsOnlyAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-retirement-{Guid.NewGuid():N}");
        try
        {
            const string Source = Preamble + """
                public sealed class RetirementViewModel : FixtureModel
                {
                    [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
                    public IAsyncRelayCommand SaveCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                    [RunicOperationRetirement(RunicOperationRetirement.Keep)]
                    public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                    public IAsyncRelayCommand PlainCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                    [RunicOperationRetirement(RunicOperationRetirement.Drain)]
                    public IRelayCommand ToggleCommand { get; } = new RelayCommand(() => { });
                    [RunicOperationRetirement(RunicOperationRetirement.Drain)]
                    public string Title { get; set; } = "";
                    [RunicOperationRetirement((RunicOperationRetirement)7)]
                    public IAsyncRelayCommand OddCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                }
                [RunicOperationRetirement(RunicOperationRetirement.Drain)]
                public sealed class ClassPolicyViewModel : FixtureModel
                {
                    public IAsyncRelayCommand SaveCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                    [RunicOperationRetirement(RunicOperationRetirement.Keep)]
                    public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                }
                [RunicOperationRetirement(RunicOperationRetirement.Cancel)]
                public sealed class NoOperationViewModel : FixtureModel
                {
                    public IRelayCommand ToggleCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class RetirementWindow : RunicWindow<RetirementViewModel>;
                public sealed partial class ClassPolicyWindow : RunicWindow<ClassPolicyViewModel>;
                public sealed partial class NoOperationWindow : RunicWindow<NoOperationViewModel>;
                """;
            var (exitCode, output, directory) = await RunGeneratorAsync(generator, temporaryRoot, "Retirement", Source).ConfigureAwait(false);
            var warnings = output.Split('\n').Where(text => text.Contains("warning RUNICBRIDGE028:", StringComparison.Ordinal)).ToArray();
            int Line(string declaration) => Source.Split('\n').ToList().FindIndex(text => text.Contains(declaration, StringComparison.Ordinal)) + 1;
            bool At(string declaration, string message) => warnings.Any(text =>
                text.StartsWith($"{Path.Combine(directory, "Fixture.cs")}({Line(declaration)},", StringComparison.Ordinal)
                && text.Contains(message, StringComparison.Ordinal));
            Require(exitCode == 0 && warnings.Length == 4
                    && At("public IRelayCommand ToggleCommand", // the first: RetirementViewModel's
                        "RetirementViewModel.ToggleCommand: RunicOperationRetirement applies only to a Bridge command that starts an operation")
                    && At("public string Title", "RetirementViewModel.Title: RunicOperationRetirement applies only to a Bridge command that starts an operation")
                    && At("public IAsyncRelayCommand OddCommand", "RetirementViewModel.OddCommand: 7 is not a RunicOperationRetirement value")
                    && warnings.Any(text => text.Contains("NoOperationViewModel: RunicOperationRetirement on the class applies to its operations", StringComparison.Ordinal))
                    && warnings.All(text => text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE028")}", StringComparison.Ordinal))
                    && !warnings.Any(text => text.Contains("ClassPolicyViewModel", StringComparison.Ordinal)),
                $"RunicOperationRetirement without an operation was not reported as located RUNICBRIDGE028 warnings.\n{output}");

            string Descriptor(string model, string command) => File.ReadAllLines(Path.Combine(directory, "cs", $"Fixture.{model}.Bridge.g.cs"))
                .Single(line => line.Contains($"(\"{command}\", vm => (object)vm.{command}Command)", StringComparison.Ordinal));
            const string Policy = "Retirement = global::Runic.Application.Views.RunicOperationRetirement.";
            Require(Descriptor("RetirementViewModel", "Save").Contains(Policy + "Cancel", StringComparison.Ordinal)
                    && Descriptor("ClassPolicyViewModel", "Save").Contains(Policy + "Drain", StringComparison.Ordinal)
                    && new[] { ("RetirementViewModel", "Load"), ("RetirementViewModel", "Plain"), ("RetirementViewModel", "Odd"),
                        ("RetirementViewModel", "Toggle"), ("ClassPolicyViewModel", "Load"), ("NoOperationViewModel", "Toggle") }
                        .All(command => !Descriptor(command.Item1, command.Item2).Contains("Retirement = ", StringComparison.Ordinal)),
                "The generated descriptors do not carry exactly the declared non-Keep policies of operations.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // RUNICBRIDGE031 (D053.3): an IValidatableViewModel of neither ReactiveUI.Validation flavor is a located warning,
    // on the ViewModel and on a DTO of its state; the ViewModel gets no validation member from it.
    [Test]
    public async Task UnrecognizedValidatableViewModelIsAWarningAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-validatable-{Guid.NewGuid():N}");
        try
        {
            const string Source = Preamble + """
                public interface IValidatableViewModel { }
                public sealed class OtherAddress : IValidatableViewModel
                {
                    public string City { get; set; } = "";
                }
                public sealed class OtherValidationViewModel : FixtureModel, IValidatableViewModel
                {
                    public string Name { get; set; } = "";
                    public OtherAddress Address { get; } = new();
                }
                public sealed partial class OtherValidationWindow : RunicWindow<OtherValidationViewModel>;
                """;
            var (exitCode, output, directory) = await RunGeneratorAsync(generator, temporaryRoot, "Validatable", Source).ConfigureAwait(false);
            var warnings = output.Split('\n').Where(text => text.Contains("warning RUNICBRIDGE031:", StringComparison.Ordinal)).ToArray();
            int Line(string declaration) => Source.Split('\n').ToList().FindIndex(text => text.Contains(declaration, StringComparison.Ordinal)) + 1;
            bool At(string declaration, string message) => warnings.Any(text =>
                text.StartsWith($"{Path.Combine(directory, "Fixture.cs")}({Line(declaration)},", StringComparison.Ordinal)
                && text.Contains(message, StringComparison.Ordinal));
            Require(exitCode == 0 && warnings.Length == 2
                    && At("public sealed class OtherValidationViewModel", "OtherValidationViewModel: Fixture.IValidatableViewModel is not the IValidatableViewModel of")
                    && At("public sealed class OtherAddress", "OtherValidationViewModel (OtherAddress): Fixture.IValidatableViewModel")
                    && warnings.All(text => text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE031")}", StringComparison.Ordinal)),
                $"An unrecognized IValidatableViewModel was not reported as located RUNICBRIDGE031 warnings.\n{output}");
            var bridge = File.ReadAllText(Path.Combine(directory, "cs", "Fixture.OtherValidationViewModel.Bridge.g.cs"));
            Require(!bridge.Contains("\"validation\"", StringComparison.Ordinal) && !bridge.Contains("ValidationReader", StringComparison.Ordinal),
                "An unrecognized IValidatableViewModel gave the ViewModel a validation member.");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    // RUNICBRIDGE032: an observable collection of DTO rows that is not a [RunicCollection] is a located warning that
    // names the rows' key when they have one; a keyed collection, a plain list and an observable list of strings are not.
    [Test]
    public async Task ObservableRowsWithoutAKeyAreReportedAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-collections-{Guid.NewGuid():N}");
        try
        {
            const string Source = Preamble + """
                public sealed record Task(int Id, string Title);
                public sealed record Note(string Text);
                public sealed class CollectionsViewModel : FixtureModel
                {
                    public System.Collections.ObjectModel.ObservableCollection<Task> Tasks { get; } = [];
                    public System.Collections.ObjectModel.ObservableCollection<Note> Notes { get; } = [];
                    [RunicCollection(nameof(Fixture.Task.Id))]
                    public System.Collections.ObjectModel.ObservableCollection<Task> Keyed { get; } = [];
                    public List<Task> Plain { get; } = [];
                    public System.Collections.ObjectModel.ObservableCollection<string> Tags { get; } = [];
                }
                public sealed partial class CollectionsWindow : RunicWindow<CollectionsViewModel>;
                """;
            var (exitCode, output, directory) = await RunGeneratorAsync(generator, temporaryRoot, "Collections", Source).ConfigureAwait(false);
            var warnings = output.Split('\n').Where(text => text.Contains("warning RUNICBRIDGE032:", StringComparison.Ordinal)).ToArray();
            int Line(string declaration) => Source.Split('\n').ToList().FindIndex(text => text.Contains(declaration, StringComparison.Ordinal)) + 1;
            bool At(string declaration, string message) => warnings.Any(text =>
                text.StartsWith($"{Path.Combine(directory, "Fixture.cs")}({Line(declaration)},", StringComparison.Ordinal)
                && text.Contains(message, StringComparison.Ordinal));
            Require(exitCode == 0 && warnings.Length == 2
                    && At("ObservableCollection<Task> Tasks", "CollectionsViewModel.Tasks: an observable collection of Task rows that is not a [RunicCollection]")
                    && At("ObservableCollection<Task> Tasks", "[RunicCollection(nameof(Task.Id))]")
                    && At("ObservableCollection<Note> Notes", "[RunicCollection(nameof(Note.Key))] with a string, Guid or Int32 key property")
                    && warnings.All(text => text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE032")}", StringComparison.Ordinal)),
                $"Observable rows without a key were not reported as located RUNICBRIDGE032 warnings.\n{output}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Test]
    public async Task GeneratorRejectsUnsupportedModelsWithLocatedDiagnosticsAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0",
            "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-diagnostics-{Guid.NewGuid():N}");
        try
        {
            await RunDtoJsonIgnoreAsync(generator, temporaryRoot).ConfigureAwait(false);
            await RunCompositionAndConfigurationDiagnosticsAsync(generator, temporaryRoot).ConfigureAwait(false);
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
                // Every report links to the ID's catalog entry at the release tag.
                Require(output.Split('\n').Any(text => text.Contains($": error {code}: ", StringComparison.Ordinal)
                        && text.TrimEnd().EndsWith($" See {HelpLink(code)}", StringComparison.Ordinal)),
                    $"{name}: expected 'error {code}' to end with its help link {HelpLink(code)}.\n{output}");
                foreach (var text in expected)
                    Require(output.Contains(text, StringComparison.Ordinal),
                        $"{name}: the generator output did not contain '{text}'.\n{output}");
            }

            await RejectAt("HashSetValue", """
                public sealed class SetViewModel : FixtureModel { public HashSet<string> Tags { get; } = []; }
                public sealed partial class SetWindow : RunicWindow<SetViewModel>;
                """, "RUNICBRIDGE003", "public sealed class SetViewModel",
                "System.Collections.Generic.HashSet`1", "is not a supported bridge collection").ConfigureAwait(false);
            // A JsonObject or JsonArray is not read as a collection; any JSON value is a JsonNode (#304).
            await RejectAt("JsonObjectValue", """
                public sealed class JsonObjectViewModel : FixtureModel { public System.Text.Json.Nodes.JsonObject Data { get; } = []; }
                public sealed partial class JsonObjectWindow : RunicWindow<JsonObjectViewModel>;
                """, "RUNICBRIDGE003", "public sealed class JsonObjectViewModel",
                "System.Text.Json.Nodes.JsonObject is not a bridge value. Declare JsonNode or JsonElement for any JSON value").ConfigureAwait(false);
            // Append-only text is read-only state: the page never writes it (#300).
            await RejectAt("SettableAppendOnlyText", """
                public sealed class SettableLogViewModel : FixtureModel { [RunicAppendOnlyText] public string Log { get; set; } = ""; }
                public sealed partial class SettableLogWindow : RunicWindow<SettableLogViewModel>;
                """, "RUNICBRIDGE008", "public string Log", "SettableLogViewModel.Log: RunicAppendOnlyText requires a read-only string state property.").ConfigureAwait(false);
            await RejectAt("NumberAppendOnlyText", """
                public sealed class CountLogViewModel : FixtureModel { [RunicAppendOnlyText] public int Count => 0; }
                public sealed partial class CountLogWindow : RunicWindow<CountLogViewModel>;
                """, "RUNICBRIDGE008", "public int Count", "CountLogViewModel.Count: RunicAppendOnlyText requires a read-only string state property.").ConfigureAwait(false);
            // A nested DTO member points at the DTO property, not the ViewModel.
            await RejectAt("NestedValue", """
                public sealed class Row
                {
                    public string Name { get; init; } = "";
                    public object Value { get; init; } = new();
                }
                public sealed class RowViewModel : FixtureModel { public Row Current { get; } = new(); }
                public sealed partial class RowWindow : RunicWindow<RowViewModel>;
                """, "RUNICBRIDGE003", "public object Value", "RowViewModel.Current.value").ConfigureAwait(false);
            // A navigation result, typically from an exposed back command, names the mapping that fixes it (W250-014).
            await RejectAt("NavigationResultValue", """
                public sealed class BackViewModel : FixtureModel { public NavigationResult<BackViewModel>? LastBack { get; } }
                public sealed partial class BackWindow : RunicWindow<BackViewModel>;
                """, "RUNICBRIDGE003", "public NavigationResult<BackViewModel>? LastBack",
                "BackViewModel.LastBack", "A NavigationResult holds .NET content and cannot cross the Bridge",
                "return its Outcome (a NavigationOutcome) instead").ConfigureAwait(false);
            await RejectAt("InternalWindow", """
                public sealed class HiddenViewModel : FixtureModel { public string Title { get; } = ""; }
                internal sealed partial class HiddenWindow : RunicWindow<HiddenViewModel>
                {
                    public string Caption => "hidden";
                }
                """, "RUNICBRIDGE006", "internal sealed partial class HiddenWindow", "Fixture.HiddenWindow: a Runic Window or View must be a public").ConfigureAwait(false);
            await RejectAt("EmptyViewModel", """
                public sealed class EmptyViewModel : FixtureModel
                {
                    internal string Hidden => "";
                }
                public sealed partial class EmptyWindow : RunicWindow<EmptyViewModel>;
                """, "RUNICBRIDGE007", "public sealed class EmptyViewModel",
                "EmptyViewModel: a ViewModel needs at least one state property, command, or interaction.").ConfigureAwait(false);
            await RejectAt("WriteOnlyState", """
                public sealed class WriteOnlyViewModel : FixtureModel
                {
                    public string Name { set { } }
                }
                public sealed partial class WriteOnlyWindow : RunicWindow<WriteOnlyViewModel>;
                """, "RUNICBRIDGE008", "public string Name { set { } }",
                "WriteOnlyViewModel.Name: a public getter is required.").ConfigureAwait(false);
            // NavigationRegion<TContent> slots (W230-002): a known content type, get-only.
            await RejectAt("ObjectRegion", """
                public sealed class AnyRegionViewModel : FixtureModel
                {
                    public AnyRegionViewModel(RunicNavigator navigator) => Main = navigator.CreateRegion<object>(this);
                    public NavigationRegion<object> Main { get; }
                }
                public sealed partial class AnyRegionWindow : RunicWindow<AnyRegionViewModel>;
                """, "RUNICBRIDGE008", "public NavigationRegion<object> Main",
                "AnyRegionViewModel.Main: a NavigationRegion<object> slot has no known content.").ConfigureAwait(false);
            await RejectAt("SettableRegion", """
                public interface IRegionPage : INotifyPropertyChanged { }
                public sealed class RegionPageViewModel : FixtureModel, IRegionPage { public string Title => ""; }
                public sealed partial class RegionPageView : RunicView<RegionPageViewModel>;
                public sealed class SettableRegionViewModel : FixtureModel
                {
                    public NavigationRegion<IRegionPage>? Main { get; set; }
                }
                public sealed partial class SettableRegionWindow : RunicWindow<SettableRegionViewModel>;
                """, "RUNICBRIDGE008", "public NavigationRegion<IRegionPage>? Main",
                "SettableRegionViewModel.Main: a NavigationRegion slot must be get-only").ConfigureAwait(false);
            await RejectAt("UnknownRegionContent", """
                public interface IMissingPage : INotifyPropertyChanged { }
                public sealed class MissingRegionViewModel : FixtureModel
                {
                    public NavigationRegion<IMissingPage>? Main { get; }
                }
                public sealed partial class MissingRegionWindow : RunicWindow<MissingRegionViewModel>;
                """, "RUNICBRIDGE008", "public NavigationRegion<IMissingPage>? Main",
                "no ViewModel with a registered View is a IMissingPage").ConfigureAwait(false);
            // NavigationSelection slots (#648): get-only, like a region slot; a command selects.
            await RejectAt("SettableSelection", """
                public sealed class SettableSelectionViewModel : FixtureModel
                {
                    public NavigationSelection? Tabs { get; set; }
                }
                public sealed partial class SettableSelectionWindow : RunicWindow<SettableSelectionViewModel>;
                """, "RUNICBRIDGE008", "public NavigationSelection? Tabs",
                "SettableSelectionViewModel.Tabs: a NavigationSelection slot must be get-only").ConfigureAwait(false);
            await RejectAt("UnkeyedCollection", """
                public sealed class Item { public string Label { get; init; } = ""; }
                public sealed class ItemsViewModel : FixtureModel
                {
                    [RunicCollection("Id")] public IReadOnlyList<Item> Items { get; } = [];
                }
                public sealed partial class ItemsWindow : RunicWindow<ItemsViewModel>;
                """, "RUNICBRIDGE010", "[RunicCollection(\"Id\")]",
                "ItemsViewModel.Items: the collection key 'Id' must name").ConfigureAwait(false);
            await RejectAt("SettableInteraction", """
                public sealed class PromptViewModel : FixtureModel
                {
                    public ReactiveUI.Binding.Interaction<string, bool> Confirm { get; set; } = new();
                }
                public sealed partial class PromptWindow : RunicWindow<PromptViewModel>;
                """, "RUNICBRIDGE011", "public ReactiveUI.Binding.Interaction<string, bool> Confirm",
                "PromptViewModel.Confirm: interactions are application-owned").ConfigureAwait(false);
            // RUNICBRIDGE012: a misplaced, repeated or unsupported failure declaration.
            await RejectAt("FailureOnState", """
                public sealed class StateFailureViewModel : FixtureModel { [RunicFailure(typeof(string))] public string Title { get; set; } = ""; }
                public sealed partial class StateFailureWindow : RunicWindow<StateFailureViewModel>;
                """, "RUNICBRIDGE012", "public string Title",
                "StateFailureViewModel.Title: RunicFailure applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method.").ConfigureAwait(false);
            await RejectAt("FailureOnBoth", """
                public sealed record Missing;
                public sealed class BothFailureViewModel : FixtureModel
                {
                    [RunicFailure(typeof(Missing))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                    [RelayCommand, RunicFailure(typeof(Missing))] private void Save() { }
                }
                public sealed partial class BothFailureWindow : RunicWindow<BothFailureViewModel>;
                """, "RUNICBRIDGE012", "[RelayCommand, RunicFailure(typeof(Missing))] private void Save()",
                "BothFailureViewModel.SaveCommand: declare RunicFailure once, on the command property or on its [RelayCommand] method, not on both.").ConfigureAwait(false);
            foreach (var (name, type, shown) in new[] { ("FailureException", "InvalidOperationException", "System.InvalidOperationException"),
                ("FailureObject", "object", "System.Object"), ("FailureNullable", "int?", "System.Nullable<System.Int32>") })
                await RejectAt(name, $$"""
                    public sealed class {{name}}ViewModel : FixtureModel
                    {
                        [RunicFailure(typeof({{type}}))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                    }
                    public sealed partial class {{name}}Window : RunicWindow<{{name}}ViewModel>;
                    """, "RUNICBRIDGE012", "public IRelayCommand SaveCommand",
                    $"{name}ViewModel.SaveCommand: the failure type {shown} must be a Bridge value type other than object, an exception or Nullable<T>.").ConfigureAwait(false);
            // A declaration on an inherited member is reported too: only the model's own commands are bridged.
            await RejectAt("FailureInherited", """
                public abstract class EditorBase : FixtureModel
                {
                    [RunicFailure(typeof(string))] public IRelayCommand ResetCommand { get; } = new RelayCommand(() => { });
                }
                public sealed class InheritedFailureViewModel : EditorBase { public string Title { get; set; } = ""; }
                public sealed partial class InheritedFailureWindow : RunicWindow<InheritedFailureViewModel>;
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
                public sealed partial class OverrideFailureWindow : RunicWindow<OverrideFailureViewModel>;
                """).ConfigureAwait(false);
            Require(Directory.GetFiles(overridden, "*OverrideFailureViewModel.Bridge.g.cs").Select(File.ReadAllText)
                    .Any(text => text.Contains("EncodeFailure = failure => failure is global::System.String declared", StringComparison.Ordinal)),
                "An override of a declared abstract command lost its failure codec.");
            // A failure type the Bridge cannot encode is RUNICBRIDGE003 at {Model}.{Command}.failure.
            await RejectAt("FailurePayload", """
                public sealed class Opaque { public object Value { get; init; } = new(); }
                public sealed class PayloadFailureViewModel : FixtureModel
                {
                    [RunicFailure(typeof(Opaque))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class PayloadFailureWindow : RunicWindow<PayloadFailureViewModel>;
                """, "RUNICBRIDGE003", "public object Value", "PayloadFailureViewModel.SaveCommand.failure.value").ConfigureAwait(false);
            await RejectKeyDiagnosticsAsync((name, members, code, declaration, expected) => RejectAt(name, members, code, declaration, expected)).ConfigureAwait(false);
            await Reject("ObjectValue", """
                public sealed class ObjectViewModel : FixtureModel { public object Value { get; } = new(); }
                public sealed partial class ObjectWindow : RunicWindow<ObjectViewModel>;
                """, "System.Object is not an explicitly supported bridge value").ConfigureAwait(false);
            await Reject("FrameworkValue", """
                public sealed class BuilderViewModel : FixtureModel { public StringBuilder Text { get; } = new(); }
                public sealed partial class BuilderWindow : RunicWindow<BuilderViewModel>;
                """, "System.Text.StringBuilder is not an explicitly supported bridge value").ConfigureAwait(false);
            await Reject("IntegerDictionary", """
                public sealed class LookupViewModel : FixtureModel { public Dictionary<int, string> Lookup { get; } = []; }
                public sealed partial class LookupWindow : RunicWindow<LookupViewModel>;
                """, "is not a supported bridge dictionary").ConfigureAwait(false);
            await Reject("EmptyDto", """
                public sealed class Marker { }
                public sealed class MarkerViewModel : FixtureModel { public Marker Value { get; } = new(); }
                public sealed partial class MarkerWindow : RunicWindow<MarkerViewModel>;
                """, "Fixture.Marker has no public readable properties").ConfigureAwait(false);
            await Reject("DerivedCollection", """
                public sealed class Names : List<string> { }
                public sealed class NamesViewModel : FixtureModel { public Names Values { get; } = []; }
                public sealed partial class NamesWindow : RunicWindow<NamesViewModel>;
                """, "Fixture.Names is not a supported bridge collection").ConfigureAwait(false);

            // Generated wire and member names are checked at build time.
            await Reject("AliasCollision", """
                public sealed class AliasViewModel : FixtureModel
                {
                    [RunicAlias("canSave")] public bool Ready { get; }
                    public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class AliasWindow : RunicWindow<AliasViewModel>;
                """, "error RUNICBRIDGE004:", "generated state name 'canSave' conflicts between Ready and SaveCommand availability").ConfigureAwait(false);

            // Every ViewModel's problem is reported in one run.
            await Reject("SeveralErrors", """
                public sealed class FirstViewModel : FixtureModel { public HashSet<int> Values { get; } = []; }
                public sealed class SecondViewModel : FixtureModel { public object Value { get; } = new(); }
                public sealed partial class FirstWindow : RunicWindow<FirstViewModel>;
                public sealed partial class SecondWindow : RunicWindow<SecondViewModel>;
                """, "FirstViewModel.Values", "SecondViewModel.Value").ConfigureAwait(false);
            // A short ReactiveUI command name was sliced before its suffix check.
            await RejectAt("ShortReactiveCommand", """
                public sealed class GoViewModel : FixtureModel { public ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> Go { get; } = null!; }
                public sealed partial class GoWindow : RunicWindow<GoViewModel>;
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
                public sealed partial class ErrorsWindow : RunicWindow<ErrorsViewModel>;
                """, "generated state name 'nameErrors' conflicts").ConfigureAwait(false);
            await Reject("SnapshotRoute", """
                public sealed class SnapshotViewModel : FixtureModel { public IRelayCommand SnapshotCommand { get; } = new RelayCommand(() => { }); }
                public sealed partial class SnapshotWindow : RunicWindow<SnapshotViewModel>;
                """, "name 'snapshot' conflicts between the generated snapshot property and SnapshotCommand").ConfigureAwait(false);
            await Reject("ThenableView", """
                public sealed class ThenViewModel : FixtureModel { public IRelayCommand ThenCommand { get; } = new RelayCommand(() => { }); }
                public sealed partial class ThenWindow : RunicWindow<ThenViewModel>;
                """, "error RUNICBRIDGE004:", "ThenViewModel.ThenCommand: the generated client member 'then'", "thenable",
                "Rename the command, for example to ContinueCommand").ConfigureAwait(false);
            // A command's bare name is a client member: `new()` in the client
            // type is a construct signature, so the TypeScript would not compile.
            await RejectAt("NewCommand", """
                public sealed partial class NewViewModel : FixtureModel
                {
                    public IRelayCommand NewCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class NewWindow : RunicWindow<NewViewModel>;
                """, "RUNICBRIDGE004", "public IRelayCommand NewCommand",
                "NewViewModel.NewCommand: the generated client member 'new' declares a construct signature",
                "Rename the command, for example to CreateCommand (client member 'create')").ConfigureAwait(false);
            await Reject("ConstructorCommand", """
                public sealed class ConstructorViewModel : FixtureModel { public IRelayCommand<string> ConstructorCommand { get; } = new RelayCommand<string>(_ => { }); }
                public sealed partial class ConstructorWindow : RunicWindow<ConstructorViewModel>;
                """, "error RUNICBRIDGE004:", "the generated client member 'constructor'").ConfigureAwait(false);
            await Reject("ToJsonCommand", """
                public sealed class ToJsonViewModel : FixtureModel { public IAsyncRelayCommand ToJSONCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask); }
                public sealed partial class ToJsonWindow : RunicWindow<ToJsonViewModel>;
                """, "error RUNICBRIDGE004:", "the generated client member 'toJSON'").ConfigureAwait(false);
            // I-003: the bridge is generated in Runic.Application.Views.Generated.Bridges, so an application type named
            // like it (#258) no longer collides with it.
            const string BridgeNamesake = """
                public sealed class CounterBridge { }
                public sealed class CounterViewModel : FixtureModel { public int Count { get; } }
                public sealed partial class CounterWindow : RunicWindow<CounterViewModel>;
                """;
            var namesake = await GenerateValidAsync(generator, temporaryRoot, "BridgeTypeName", Preamble + BridgeNamesake)
                .ConfigureAwait(false);
            var namesakeBridge = File.ReadAllText(Path.Combine(namesake, "Fixture.CounterViewModel.Bridge.g.cs"));
            Require(namesakeBridge.Contains("namespace Runic.Application.Views.Generated.Bridges;", StringComparison.Ordinal)
                    && namesakeBridge.Contains("internal sealed class CounterBridge : ", StringComparison.Ordinal),
                $"The bridge is not generated in Runic.Application.Views.Generated.Bridges:\n{namesakeBridge}");
            var namesakeErrors = Compile("BridgeTypeNameConsumer", Preamble + BridgeNamesake)
                .AddSyntaxTrees(Directory.GetFiles(namesake, "*.g.cs").Select(path =>
                    CSharpSyntaxTree.ParseText(File.ReadAllText(path), new CSharpParseOptions(LanguageVersion.Latest), path)))
                .GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
            Require(namesakeErrors.Length == 0,
                $"The generated bridge does not compile beside a type of its name:\n{string.Join('\n', namesakeErrors)}");

            // Toolkit commands have no result, so a result cardinality is a mistake.
            // A cancel-like name alone cannot establish a plain ICommand's
            // input contract, even when it wraps an async Toolkit command.
            await RejectAt("ManualToolkitCancel", """
                public sealed class ManualCancelViewModel : FixtureModel
                {
                    public IAsyncRelayCommand SaveCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                    public System.Windows.Input.ICommand SaveCancelCommand => SaveCommand.CreateCancelCommand();
                }
                public sealed partial class ManualCancelWindow : RunicWindow<ManualCancelViewModel>;
                """, "RUNICBRIDGE009", "public System.Windows.Input.ICommand SaveCancelCommand",
                "ManualCancelViewModel.SaveCancelCommand: unsupported command shape").ConfigureAwait(false);
            // GeneratedCode alone is insufficient: the source method must
            // opt into cancellation and have a matching cancellable async pair.
            foreach (var (name, flag, asyncType, parameters) in new[]
            {
                ("MissingCancelFlag", "false", "IAsyncRelayCommand", "System.Threading.CancellationToken token"),
                ("NonCancellableSource", "true", "IAsyncRelayCommand", ""),
                ("NonAsyncCancelPair", "true", "IRelayCommand", "System.Threading.CancellationToken token"),
                ("MismatchedCancelInput", "true", "IAsyncRelayCommand<string>", "int input, System.Threading.CancellationToken token"),
            })
                await RejectAt(name, $$"""
                    public sealed class InvalidCancelViewModel : FixtureModel
                    {
                        [RelayCommand(IncludeCancelCommand = {{flag}})]
                        private System.Threading.Tasks.Task SaveAsync({{parameters}}) => System.Threading.Tasks.Task.CompletedTask;
                        [System.CodeDom.Compiler.GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
                        public {{asyncType}} SaveCommand { get; } = null!;
                        [System.CodeDom.Compiler.GeneratedCode("CommunityToolkit.Mvvm.SourceGenerators.RelayCommandGenerator", "8.4.0.0")]
                        public System.Windows.Input.ICommand SaveCancelCommand { get; } = null!;
                    }
                    public sealed partial class InvalidCancelWindow : RunicWindow<InvalidCancelViewModel>;
                    """, "RUNICBRIDGE009", "public System.Windows.Input.ICommand SaveCancelCommand",
                    "InvalidCancelViewModel.SaveCancelCommand: unsupported command shape").ConfigureAwait(false);

            await RejectAt("ToolkitResult", """
                public sealed class ResultViewModel : FixtureModel
                {
                    [RunicCommandResult(RunicCommandResultCardinality.Last)]
                    public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                }
                public sealed partial class ResultWindow : RunicWindow<ResultViewModel>;
                """, "RUNICBRIDGE009", "public IAsyncRelayCommand LoadCommand",
                "LoadCommand: RunicCommandResult selects a ReactiveUI command's result cardinality").ConfigureAwait(false);

            // The contract fingerprint follows the generated wire shape, so a
            // nullable ReactiveUI argument is a contract change for hot reload.
            static string Fingerprint(string name, string commandType)
            {
                using var image = new MemoryStream();
                var emitted = Compile(name, Preamble + $$"""
                    public sealed class EchoViewModel : FixtureModel { public {{commandType}} EchoCommand { get; } = null!; }
                    public sealed partial class EchoWindow : RunicWindow<EchoViewModel>;
                    """).Emit(image);
                Require(emitted.Success, $"{name}: the fingerprint fixture did not compile.");
                var model = System.Reflection.Assembly.Load(image.ToArray()).GetType("Fixture.EchoViewModel")!;
                return Runic.Application.Views.Generated.BridgeContractShape.Compute(model);
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
                public sealed partial class PlainTitleWindow : RunicWindow<PlainTitleViewModel>;
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
                public sealed partial class AskWindow : RunicWindow<AskViewModel>;
                public sealed partial class PlainWindow : RunicWindow<PlainViewModel>;
                """, "--di-composition", "Fixture.Composition").ConfigureAwait(false);
            var registration = File.ReadAllText(Path.Combine(composition, "RunicBridgeComposition.g.cs"));
            Require(!registration.Contains("Func<global::Runic.Application.Views.IBridgeTransport, global::Fixture.AskViewModel, global::System.IDisposable>", StringComparison.Ordinal)
                && registration.Contains("Func<global::Runic.Application.Views.IBridgeTransport, global::Runic.Application.Views.WindowContentSession, global::Fixture.AskViewModel, global::System.IDisposable>", StringComparison.Ordinal)
                && registration.Contains("Func<global::Runic.Application.Views.IBridgeTransport, global::Fixture.PlainViewModel, global::System.IDisposable>", StringComparison.Ordinal),
                $"DI composition registered a transport-only factory for an interaction ViewModel.\n{registration}");

            // A ViewModel that a region, content property or content collection
            // admits but that has no View builds with a warning at its
            // declaration, and the generated failure names the slot and type.
            const string MissingViewSource = """
                public interface IPage : INotifyPropertyChanged { }
                public interface IWidget : INotifyPropertyChanged { }
                public abstract class ViewModelBase : FixtureModel { }
                public sealed class ShellViewModel : ViewModelBase
                {
                    public NavigationRegion<IPage> Main { get; } = null!;
                    public IPage? Detail { get; }
                    public IReadOnlyList<IPage> Tabs { get; } = [];
                    // Broad slots: an application-wide base that the shell shares,
                    // a framework interface, and a type admitting many ViewModels.
                    public ViewModelBase? Dialog { get; }
                    public INotifyPropertyChanged? Anything { get; }
                    public IReadOnlyList<IWidget> Widgets { get; } = [];
                }
                public sealed class HomeViewModel : ViewModelBase, IPage { public string Title => "home"; }
                public sealed class DialogViewModel : ViewModelBase { public string Title => "dialog"; }
                public sealed class WidgetViewModel : FixtureModel, IWidget { public int Number => 0; }
                public sealed partial class WidgetView : RunicView<WidgetViewModel>;
                public sealed class Widget1ViewModel : FixtureModel, IWidget { public int Number => 1; }
                public sealed class Widget2ViewModel : FixtureModel, IWidget { public int Number => 2; }
                public sealed class Widget3ViewModel : FixtureModel, IWidget { public int Number => 3; }
                public sealed class Widget4ViewModel : FixtureModel, IWidget { public int Number => 4; }
                public sealed class Widget5ViewModel : FixtureModel, IWidget { public int Number => 5; }
                public sealed class Widget6ViewModel : FixtureModel, IWidget { public int Number => 6; }
                public sealed class Widget7ViewModel : FixtureModel, IWidget { public int Number => 7; }
                public sealed class Widget8ViewModel : FixtureModel, IWidget { public int Number => 8; }
                public sealed class Widget9ViewModel : FixtureModel, IWidget { public int Number => 9; }
                public sealed class OrphanViewModel : FixtureModel, IPage { public string Title => "orphan"; }
                internal sealed class HiddenPageViewModel : FixtureModel, IPage { }
                public abstract class AbstractPageViewModel : FixtureModel, IPage { }
                public sealed partial class ShellWindow : RunicWindow<ShellViewModel>;
                public sealed partial class HomeView : RunicView<HomeViewModel>;
                """;
            var (missingViewExit, missingViewOutput, missingViewDirectory) = await RunGeneratorAsync(generator, temporaryRoot,
                "MissingView", Preamble + MissingViewSource).ConfigureAwait(false);
            var orphanLine = (Preamble + MissingViewSource)
                .Split('\n').ToList().FindIndex(text => text.Contains("class OrphanViewModel", StringComparison.Ordinal)) + 1;
            var warnings = missingViewOutput.Split('\n').Where(text => text.Contains("warning RUNICBRIDGE013:", StringComparison.Ordinal)).ToArray();
            Require(missingViewExit == 0 && warnings.Length == 3
                    && warnings.All(text => text.StartsWith($"{Path.Combine(missingViewDirectory, "Fixture.cs")}({orphanLine},", StringComparison.Ordinal)
                        && text.Contains("Fixture.OrphanViewModel is assignable to IPage but has no Runic View", StringComparison.Ordinal)
                        && text.Contains("'public sealed partial class OrphanView : RunicView<OrphanViewModel>;'", StringComparison.Ordinal))
                    && warnings.Any(text => text.Contains("ShellViewModel.Main:", StringComparison.Ordinal) && text.Contains("pushing it to this navigation region throws NotSupportedException", StringComparison.Ordinal))
                    && warnings.Any(text => text.Contains("ShellViewModel.Detail:", StringComparison.Ordinal))
                    && warnings.Any(text => text.Contains("ShellViewModel.Tabs:", StringComparison.Ordinal))
                    && !missingViewOutput.Contains("HiddenPageViewModel", StringComparison.Ordinal)
                    && !missingViewOutput.Contains("AbstractPageViewModel", StringComparison.Ordinal)
                    && !missingViewOutput.Contains("DialogViewModel", StringComparison.Ordinal)
                    && !missingViewOutput.Contains("Widget1ViewModel", StringComparison.Ordinal)
                    && !missingViewOutput.Contains("ShellViewModel.Anything", StringComparison.Ordinal)
                    && !missingViewOutput.Contains("HomeViewModel is assignable", StringComparison.Ordinal),
                $"A region ViewModel without a View was not reported once per slot as RUNICBRIDGE013.\n{missingViewOutput}");
            Require(warnings.All(text => text.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE013")}", StringComparison.Ordinal)),
                $"RUNICBRIDGE013 does not end with its help link {HelpLink("RUNICBRIDGE013")}.\n{missingViewOutput}");
            var shellBridge = File.ReadAllText(Path.Combine(missingViewDirectory, "cs", "Fixture.ShellViewModel.Bridge.g.cs"));
            Require(shellBridge.Contains("The navigation region ShellViewModel.Main cannot present {unregistered.GetType().FullName}", StringComparison.Ordinal)
                    && shellBridge.Contains("The ShellViewModel.Tabs cannot present {unregistered.GetType().FullName}", StringComparison.Ordinal),
                $"The generated content failure does not name the slot and the presented type.\n{shellBridge}");
            // Navigation is a supported API: a Bridge that observes a region slot needs no warning
            // suppression of its own.
            var regionBridges = Directory.GetFiles(Path.Combine(missingViewDirectory, "cs"), "*.g.cs")
                .Select(File.ReadAllLines).Where(lines => lines.Any(line => line.Contains("static vm => vm.", StringComparison.Ordinal))).ToArray();
            Require(regionBridges.Length > 0 && regionBridges.All(lines => !lines.Any(line => line.StartsWith("#pragma warning", StringComparison.Ordinal))),
                $"A generated Bridge with a navigation region slot suppresses a warning.\n{shellBridge}");
            // A Window's ViewModel has a Window, not a View: it is not reported
            // for a slot whose content type it shares.
            var (windowModelExit, windowModelOutput, _) = await RunGeneratorAsync(generator, temporaryRoot, "MissingViewWindowModel",
                Preamble + """
                public interface IPane : INotifyPropertyChanged { }
                public sealed class MainViewModel : FixtureModel { public IReadOnlyList<IPane> Panes { get; } = []; }
                public sealed class ListPaneViewModel : FixtureModel, IPane { public string Title => "list"; }
                public sealed class InspectorViewModel : FixtureModel, IPane { public string Title => "inspector"; }
                public sealed partial class MainWindow : RunicWindow<MainViewModel>;
                public sealed partial class InspectorWindow : RunicWindow<InspectorViewModel>;
                public sealed partial class ListPaneView : RunicView<ListPaneViewModel>;
                """).ConfigureAwait(false);
            Require(windowModelExit == 0 && !windowModelOutput.Contains("RUNICBRIDGE013", StringComparison.Ordinal),
                $"A Window's ViewModel was reported as a ViewModel without a View.\n{windowModelOutput}");
            // A ViewModel without a View of its own whose base class or interface has
            // one is presented by that View: the generated switch's type pattern for
            // the base matches it (#407). A ViewModel that no case matches still warns.
            const string BaseViewSource = """
                public abstract class ComponentLogViewModel : FixtureModel { public string Text => ""; }
                public sealed class ControllerLogViewModel : ComponentLogViewModel { }
                public sealed class InstallerLogViewModel : ComponentLogViewModel { }
                public sealed partial class ComponentLogView : RunicView<ComponentLogViewModel>;
                public interface ILogPage : INotifyPropertyChanged { }
                public class SummaryPageViewModel : FixtureModel, ILogPage { public string Title => ""; }
                public sealed class DetailedSummaryPageViewModel : SummaryPageViewModel { }
                public sealed class StrayPageViewModel : FixtureModel, ILogPage { public string Title => "stray"; }
                public sealed partial class SummaryPageView : RunicView<SummaryPageViewModel>;
                public sealed class LogViewModel : FixtureModel
                {
                    public ComponentLogViewModel? CurrentChild { get; }
                    public IReadOnlyList<ComponentLogViewModel> Children { get; } = [];
                    public NavigationRegion<ILogPage> Pages { get; } = null!;
                }
                public sealed partial class LogWindow : RunicWindow<LogViewModel>;
                """;
            var (baseViewExit, baseViewOutput, baseViewDirectory) = await RunGeneratorAsync(generator, temporaryRoot, "MissingViewBaseView",
                Preamble + BaseViewSource).ConfigureAwait(false);
            var baseViewWarnings = baseViewOutput.Split('\n').Where(text => text.Contains("warning RUNICBRIDGE013:", StringComparison.Ordinal)).ToArray();
            Require(baseViewExit == 0 && baseViewWarnings.Length == 1
                    && baseViewWarnings[0].Contains("LogViewModel.Pages: Fixture.StrayPageViewModel is assignable to ILogPage but has no Runic View", StringComparison.Ordinal)
                    && !baseViewOutput.Contains("ControllerLogViewModel", StringComparison.Ordinal)
                    && !baseViewOutput.Contains("InstallerLogViewModel", StringComparison.Ordinal)
                    && !baseViewOutput.Contains("DetailedSummaryPageViewModel", StringComparison.Ordinal),
                $"RUNICBRIDGE013 reported a ViewModel that a View of its base type presents, or missed one that no View presents.\n{baseViewOutput}");
            var logBridge = File.ReadAllText(Path.Combine(baseViewDirectory, "cs", "Fixture.LogViewModel.Bridge.g.cs"));
            Require(logBridge.Contains("case global::Fixture.ComponentLogViewModel page:", StringComparison.Ordinal)
                    && logBridge.Contains("case global::Fixture.SummaryPageViewModel page:", StringComparison.Ordinal)
                    && !logBridge.Contains("ControllerLogViewModel", StringComparison.Ordinal),
                $"The generated Bridge does not present the subclasses through their base type's case.\n{logBridge}");

            // Bridge generation that the build turned on by default is
            // optional: an assembly with no Window or View (a navigator-only
            // application) succeeds, writes nothing and leaves a marker the
            // targets use to skip the frontend steps. Without --optional the
            // project asked for generation, so the same assembly is an error.
            const string NoViews = "public sealed class PlainService { public int Value => 1; }";
            var skippedOutput = await GenerateValidAsync(generator, temporaryRoot, "OptionalNoViews", Preamble + NoViews,
                "--optional").ConfigureAwait(false);
            Require(File.Exists(Path.Combine(skippedOutput, "RunicBridge.NoViews.marker"))
                    && Directory.GetFiles(skippedOutput, "*.g.cs").Length == 0,
                "An optional generation without Windows or Views did not skip cleanly.");
            // Views that were removed leave no stale generated C# or TypeScript.
            var staleCs = await GenerateValidAsync(generator, temporaryRoot, "StaleOutput", Preamble + """
                public sealed class StaleViewModel : FixtureModel { public string Title { get; } = ""; }
                public sealed partial class StaleWindow : RunicWindow<StaleViewModel>;
                """).ConfigureAwait(false);
            var staleTs = Path.Combine(Path.GetDirectoryName(staleCs)!, "ts");
            Require(Directory.GetFiles(staleCs, "*.g.cs").Length > 0 && Directory.GetFiles(staleTs, "*.ts").Length > 0,
                "The stale-output fixture generated nothing to remove.");
            await GenerateValidAsync(generator, temporaryRoot, "StaleOutput", Preamble + NoViews, "--optional").ConfigureAwait(false);
            Require(Directory.GetFiles(staleCs, "*.g.cs").Length == 0 && Directory.GetFiles(staleTs, "*.ts").Length == 0
                    && File.Exists(Path.Combine(staleCs, "RunicBridge.NoViews.marker")),
                "Optional generation without Views left stale generated output.");
            // Optional does not hide a model assembly that cannot be loaded.
            var (optionalLoadExit, optionalLoadOutput) = await RunProcessAsync(generator, invalidAssembly, invalidDirectory, "--optional")
                .ConfigureAwait(false);
            Require(optionalLoadExit != 0 && optionalLoadOutput.Contains("error RUNICBRIDGE005:", StringComparison.Ordinal),
                $"Optional generation hid an unloadable model assembly.\n{optionalLoadOutput}");
            await Reject("RequiredNoViews", NoViews, "error RUNICBRIDGE006:", "no Runic Window/View classes found")
                .ConfigureAwait(false);
            // Optional only forgives a missing Window: an invalid View still fails.
            var (optionalViewExit, invalidViewOutput, _) = await RunGeneratorAsync(generator, temporaryRoot, "OptionalInvalidView", Preamble + """
                public sealed class HiddenViewModel : FixtureModel { public string Title { get; } = ""; }
                internal sealed partial class HiddenWindow : RunicWindow<HiddenViewModel>;
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
    // RUNICBRIDGE027 (#472): a misplaced RunicCommandKey, or a resolver that is missing or has an unsupported shape.
    // An unsupported key type stays RUNICBRIDGE003.
    private static async Task RejectKeyDiagnosticsAsync(Func<string, string, string, string, string[], Task> rejectAt)
    {
        const string Item = "public sealed class Item { public System.IO.Stream? Data { get; init; } }\n";
        Task Reject(string name, string members, string code, string declaration, params string[] expected) =>
            rejectAt(name, Item + members, code, declaration, expected);

        await Reject("KeyOnState", """
            public sealed class KeyStateViewModel : FixtureModel { [RunicCommandKey("Find")] public string Title { get; set; } = ""; }
            public sealed partial class KeyStateWindow : RunicWindow<KeyStateViewModel>;
            """, "RUNICBRIDGE027", "public string Title",
            "KeyStateViewModel.Title: RunicCommandKey applies only to a Bridge command property or its CommunityToolkit [RelayCommand] method.").ConfigureAwait(false);
        await Reject("KeyMissing", """
            public sealed class KeyMissingViewModel : FixtureModel
            {
                [RunicCommandKey("Find")] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
            }
            public sealed partial class KeyMissingWindow : RunicWindow<KeyMissingViewModel>;
            """, "RUNICBRIDGE027", "public IRelayCommand<Item> DeleteCommand",
            "KeyMissingViewModel.DeleteCommand: the RunicCommandKey resolver 'Find' is not a method of KeyMissingViewModel.").ConfigureAwait(false);
        await Reject("KeyOverloaded", """
            public sealed class KeyOverloadedViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
                public Item? Find(ulong id) => null;
                public Item? Find(string name) => null;
            }
            public sealed partial class KeyOverloadedWindow : RunicWindow<KeyOverloadedViewModel>;
            """, "RUNICBRIDGE027", "public Item? Find(string name)",
            "KeyOverloadedViewModel.DeleteCommand: the RunicCommandKey resolver KeyOverloadedViewModel.Find is overloaded.").ConfigureAwait(false);
        await Reject("KeyPrivate", """
            public sealed class KeyPrivateViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
                private Item? Find(ulong id) => null;
            }
            public sealed partial class KeyPrivateWindow : RunicWindow<KeyPrivateViewModel>;
            """, "RUNICBRIDGE027", "private Item? Find(ulong id)",
            "KeyPrivateViewModel.DeleteCommand: the RunicCommandKey resolver KeyPrivateViewModel.Find must be public or internal").ConfigureAwait(false);
        await Reject("KeyArity", """
            public sealed class KeyArityViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
                public Item? Find(ulong id, ulong version) => null;
            }
            public sealed partial class KeyArityWindow : RunicWindow<KeyArityViewModel>;
            """, "RUNICBRIDGE027", "public Item? Find(ulong id, ulong version)",
            "must take the key as its only parameter").ConfigureAwait(false);
        await Reject("KeyReturn", """
            public sealed class KeyReturnViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
                public System.Threading.Tasks.Task<string?> Find(ulong id) => System.Threading.Tasks.Task.FromResult<string?>(null);
            }
            public sealed partial class KeyReturnWindow : RunicWindow<KeyReturnViewModel>;
            """, "RUNICBRIDGE027", "public System.Threading.Tasks.Task<string?> Find(ulong id)",
            "returns System.Threading.Tasks.Task<System.String>; it must return the command's parameter Fixture.Item").ConfigureAwait(false);
        await Reject("KeyWithoutInput", """
            public sealed class KeyNoInputViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                public Item? Find(ulong id) => null;
            }
            public sealed partial class KeyNoInputWindow : RunicWindow<KeyNoInputViewModel>;
            """, "RUNICBRIDGE027", "public IRelayCommand SaveCommand",
            "KeyNoInputViewModel.SaveCommand: RunicCommandKey resolves a command's parameter, but this command takes none.").ConfigureAwait(false);
        await Reject("KeyWithInput", """
            public sealed class KeyAndInputViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find)), RunicCommandInput(typeof(ulong))]
                public System.Windows.Input.ICommand PickCommand { get; } = new RelayCommand<Item>(_ => { });
                public Item? Find(ulong id) => null;
            }
            public sealed partial class KeyAndInputWindow : RunicWindow<KeyAndInputViewModel>;
            """, "RUNICBRIDGE027", "public System.Windows.Input.ICommand PickCommand",
            "KeyAndInputViewModel.PickCommand: RunicCommandKey replaces RunicCommandInput").ConfigureAwait(false);
        await Reject("KeyOnBoth", """
            public sealed class KeyBothViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
                [RelayCommand, RunicCommandKey(nameof(Find))] private void Delete(Item item) { }
                public Item? Find(ulong id) => null;
            }
            public sealed partial class KeyBothWindow : RunicWindow<KeyBothViewModel>;
            """, "RUNICBRIDGE027", "[RelayCommand, RunicCommandKey(nameof(Find))] private void Delete(Item item)",
            "KeyBothViewModel.DeleteCommand: declare RunicCommandKey once").ConfigureAwait(false);
        await Reject("KeyType", """
            public sealed class KeyTypeViewModel : FixtureModel
            {
                [RunicCommandKey(nameof(Find))] public IRelayCommand<Item> DeleteCommand { get; } = new RelayCommand<Item>(_ => { });
                public Item? Find(object key) => null;
            }
            public sealed partial class KeyTypeWindow : RunicWindow<KeyTypeViewModel>;
            """, "RUNICBRIDGE003", "public Item? Find(object key)", "KeyTypeViewModel.DeleteCommand.key").ConfigureAwait(false);
    }

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
        => await RunGeneratorFixtureAsync(generator, temporaryRoot, name, source, null, options).ConfigureAwait(false);

    private static async Task<(int ExitCode, string Output, string Directory)> RunGeneratorFixtureAsync(string generator,
        string temporaryRoot, string name, string source, IEnumerable<MetadataReference>? additionalReferences,
        params string[] options)
    {
        var directory = Path.Combine(temporaryRoot, name);
        Directory.CreateDirectory(directory);
        var assembly = Path.Combine(directory, $"Fixture{name}.dll");
        // Emit a portable PDB over a source file on disk, as a project build
        // does, so diagnostics can report source locations.
        var sourcePath = Path.Combine(directory, "Fixture.cs");
        File.WriteAllText(sourcePath, source);
        var compilation = Compile(name, source, sourcePath, additionalReferences);
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
            if (reference.FilePath is { } path && (Path.GetDirectoryName(path) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)
                    || additionalReferences?.OfType<PortableExecutableReference>().Any(additional => additional.FilePath == path) == true))
                File.Copy(path, Path.Combine(directory, Path.GetFileName(path)), overwrite: true);
        var (exitCode, text) = await RunProcessAsync(generator, assembly, directory, options).ConfigureAwait(false);
        return (exitCode, text, directory);
    }

    private static CSharpCompilation Compile(string name, string source, string path = "",
        IEnumerable<MetadataReference>? additionalReferences = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(reference => MetadataReference.CreateFromFile(reference)).Concat(additionalReferences ?? []);
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
        var elapsed = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(GeneratorTimeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // #348: a generator that normally exits within half a second ran for more than 30 seconds on a Windows
            // runner. Keep a dump of it, then stop it and wait for its exit, so it no longer holds the fixture
            // directory and the timeout, not the cleanup, is the reported failure.
            var dump = TryWriteDump(process, Path.GetFileNameWithoutExtension(assembly));
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var partial = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
            throw new InvalidOperationException($"{assembly}: the generator did not exit within {GeneratorTimeout.TotalSeconds} seconds"
                + $" ({elapsed.Elapsed.TotalSeconds:F1} s). {dump}\nIts output so far:\n{partial}");
        }
        var text = await output.ConfigureAwait(false) + await error.ConfigureAwait(false);
        Require(!text.Contains("Unhandled exception", StringComparison.Ordinal),
            $"{assembly}: the generator crashed instead of reporting a diagnostic.\n{text}");
        return (process.ExitCode, text);
    }

    // A generator call normally takes well under a second. The one dump of a stall (#348) showed a cold run on a
    // loaded Windows runner still listing installed shared frameworks after 30 seconds, making steady progress;
    // the generator now looks up single files instead. The margin is for such a runner, and a hang still fails.
    private static readonly TimeSpan GeneratorTimeout = TimeSpan.FromSeconds(60);

    // A full-memory minidump of a stalled generator (dbghelp's MiniDumpWriteDump; dotnet-dump and Visual Studio read it
    // with managed stacks), written beside the test results, which the WPF job uploads when it fails. The stall was seen
    // on Windows; elsewhere the message says so.
    private static string TryWriteDump(Process process, string name)
    {
        if (!OperatingSystem.IsWindows()) return "No dump: dumps are written on Windows only.";
        try
        {
            var directory = Path.Combine(FindWorkspaceRoot(), "artifacts", "codegen-timeouts");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{name}-{process.Id}.dmp");
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                // MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithFullMemoryInfo | MiniDumpWithThreadInfo
                const uint DumpType = 0x2 | 0x4 | 0x800 | 0x1000;
                if (!MiniDumpWriteDump(process.Handle, (uint)process.Id, file.SafeFileHandle.DangerousGetHandle(), DumpType, 0, 0, 0))
                    return $"No dump: MiniDumpWriteDump failed with {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}.";
            }
            return $"Dump: {path}.";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException
            or DllNotFoundException or EntryPointNotFoundException)
        {
            return $"No dump: {failure.Message}";
        }
    }

    [System.Runtime.InteropServices.DllImport("dbghelp.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, nint file, uint dumpType, nint exception, nint userStream, nint callback);

    // The generator and this test assembly build with the same version.
    private static string HelpLink(string code)
    {
        var version = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
            typeof(CodegenDiagnosticsTests).Assembly)!.InformationalVersion.Split('+')[0];
        return $"https://github.com/Runic-Artifex/runic-sdk/blob/v{version}/docs/diagnostics.md#{code.ToLowerInvariant()}";
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
