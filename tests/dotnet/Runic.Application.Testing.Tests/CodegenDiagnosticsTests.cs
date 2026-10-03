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

            await Reject("HashSetValue", """
                public sealed class SetViewModel : FixtureModel { public HashSet<string> Tags { get; } = []; }
                public sealed partial class SetWindow(SetViewModel model) : RunicWindow<SetViewModel>(model);
                """, "error RUNICBRIDGE003:", "System.Collections.Generic.HashSet`1", "is not a supported bridge collection").ConfigureAwait(false);
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
            await Reject("ShortReactiveCommand", """
                public sealed class GoViewModel : FixtureModel { public ReactiveUI.ReactiveCommand<ReactiveUI.Primitives.RxVoid, ReactiveUI.Primitives.RxVoid> Go { get; } = null!; }
                public sealed partial class GoWindow(GoViewModel model) : RunicWindow<GoViewModel>(model);
                """, "Go: Bridge commands must end with Command.").ConfigureAwait(false);
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
            await Reject("ToolkitResult", """
                public sealed class ResultViewModel : FixtureModel
                {
                    [RunicCommandResult(BridgeCommandResultCardinality.Last)]
                    public IAsyncRelayCommand LoadCommand { get; } = new AsyncRelayCommand(() => System.Threading.Tasks.Task.CompletedTask);
                }
                public sealed partial class ResultWindow(ResultViewModel model) : RunicWindow<ResultViewModel>(model);
                """, "LoadCommand: RunicCommandResult selects a ReactiveUI command's result cardinality").ConfigureAwait(false);

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
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create($"Fixture{name}",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        var emitted = compilation.Emit(assembly);
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

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string generator, string assembly,
        string directory, params string[] options)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                ArgumentList = { generator, "--generate", assembly, Path.Combine(directory, "cs"), Path.Combine(directory, "ts"), "--no-registry" },
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
