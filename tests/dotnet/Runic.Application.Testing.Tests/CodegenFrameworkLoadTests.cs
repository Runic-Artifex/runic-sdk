using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

/// <summary>
/// The generator runs on Microsoft.NETCore.App only, while the application
/// assembly may need other shared frameworks: Microsoft.WindowsDesktop.App in a
/// single-project WPF application (#269) and Microsoft.AspNetCore.App, which
/// every Runic Desktop application references through Runic.Desktop (#270).
/// WPF is simulated with stand-in PresentationFramework, PresentationCore and
/// System.Xaml assemblies that the fixture compiles against but the generator
/// cannot find, as on a build without the Windows Desktop framework.
/// </summary>
internal sealed partial class CodegenDiagnosticsTests
{
    private const string WpfStandInPresentationFramework = """
        namespace System.Windows;
        public enum ResourceDictionaryLocation { None, SourceAssembly, ExternalAssembly }
        [AttributeUsage(AttributeTargets.Assembly)]
        public sealed class ThemeInfoAttribute(ResourceDictionaryLocation themeDictionaryLocation, ResourceDictionaryLocation genericDictionaryLocation) : Attribute
        {
            public ResourceDictionaryLocation ThemeDictionaryLocation { get; } = themeDictionaryLocation;
            public ResourceDictionaryLocation GenericDictionaryLocation { get; } = genericDictionaryLocation;
        }
        public class Window { }
        """;

    private const string WpfStandInPresentationCore = """
        namespace System.Windows.Media;
        public abstract class ImageSource { }
        public abstract class Brush { }
        """;

    private const string WpfStandInSystemXaml = """
        namespace System.Windows.Markup;
        [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
        public sealed class XmlnsDefinitionAttribute(string xmlNamespace, string clrNamespace) : Attribute
        {
            public string XmlNamespace { get; } = xmlNamespace;
            public string ClrNamespace { get; } = clrNamespace;
        }
        """;

    // A single-project WPF application: WPF assembly attributes, a WPF window,
    // and a ViewModel whose bridged surface has no WPF type, beside WPF-typed
    // [RunicIgnore] properties (one inherited by an override) and a private
    // iterator over WPF objects, whose state machine is a nested type that
    // implements IEnumerable<ImageSource>.
    private const string WpfApplication = """
        using System.Collections.Generic;
        using System.ComponentModel;
        using System.Windows.Media;
        using CommunityToolkit.Mvvm.Input;
        using Runic.Application.Views;

        [assembly: System.Windows.ThemeInfo(System.Windows.ResourceDictionaryLocation.None, System.Windows.ResourceDictionaryLocation.SourceAssembly)]
        [assembly: System.Windows.Markup.XmlnsDefinition("urn:fixture", "Fixture")]

        namespace Fixture;

        public abstract class FixtureModel : INotifyPropertyChanged
        {
            public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        }

        public sealed class EditorWindow : System.Windows.Window { }

        public abstract class ImageViewModelBase : FixtureModel
        {
            [RunicIgnore] public virtual ImageSource? Icon => null;
        }

        public sealed class EditorViewModel : ImageViewModelBase
        {
            public string Title => "editor";
            [RunicIgnore] public ImageSource? Preview => null;
            public override ImageSource? Icon => null;
            public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
            private IEnumerable<ImageSource> Previews() { if (Preview is { } preview) yield return preview; }
            public int Count() { var count = 0; foreach (var _ in Previews()) count++; return count; }
        }

        public sealed partial class EditorView : RunicView<EditorViewModel>;
        """;

    [Test]
    public async Task GeneratorInspectsViewModelsBesideUnloadableFrameworkTypesAsync()
    {
        var root = FindWorkspaceRoot();
        var configuration = typeof(CodegenDiagnosticsTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0", "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-frameworks-{Guid.NewGuid():N}");
        try
        {
            // Image references have no file, so the fixture directory gets no
            // copy: the generator cannot load them.
            var wpf = new[]
            {
                StandIn("PresentationFramework", WpfStandInPresentationFramework),
                StandIn("PresentationCore", WpfStandInPresentationCore),
                StandIn("System.Xaml", WpfStandInSystemXaml),
            };

            // #269: the ViewModels of a WPF application assembly generate.
            var (wpfExit, wpfOutput, wpfDirectory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "WpfApplication",
                WpfApplication, wpf).ConfigureAwait(false);
            Require(wpfExit == 0, $"A WPF application's ViewModel without WPF types in its Bridge did not generate.\n{wpfOutput}");
            // The window and the iterator's state machine do not load.
            Require(wpfOutput.Contains("skipped 2 types that depend on PresentationCore, PresentationFramework.", StringComparison.Ordinal),
                $"The WPF window and iterator were not reported as skipped.\n{wpfOutput}");
            var editorBridge = File.ReadAllText(Path.Combine(wpfDirectory, "cs", "Fixture.EditorViewModel.Bridge.g.cs"));
            Require(editorBridge.Contains("vm.Title", StringComparison.Ordinal) && editorBridge.Contains("SaveCommand", StringComparison.Ordinal)
                    && !editorBridge.Contains("Preview", StringComparison.Ordinal) && !editorBridge.Contains("Icon", StringComparison.Ordinal),
                $"The Bridge does not expose exactly the ViewModel's non-ignored members.\n{editorBridge}");
            // Hot Reload computes the same contract in a process without WPF.
            var fixture = Assembly.LoadFrom(Path.Combine(wpfDirectory, "FixtureWpfApplication.dll"));
            var fingerprint = BridgeContractShape.Compute(fixture.GetType("Fixture.EditorViewModel", throwOnError: true)!);
            Require(editorBridge.Contains($"\"{fingerprint}\"", StringComparison.Ordinal),
                $"The contract computed without WPF differs from the generated one ({fingerprint}).\n{editorBridge}");

            // A ViewModel that does expose a WPF type is reported at that member,
            // also inside a DTO; the application's other ViewModels are not.
            const string Exposed = """
                public sealed class CanvasViewModel : FixtureModel
                {
                    public string Title => "";
                    public System.Windows.Media.ImageSource? Cover => null;
                }
                public sealed partial class CanvasView : RunicView<CanvasViewModel>;
                public sealed class Row
                {
                    public string Name { get; init; } = "";
                    public System.Windows.Media.Brush? Fill { get; init; }
                }
                public sealed class RowsViewModel : FixtureModel { public IReadOnlyList<Row> Rows { get; } = []; }
                public sealed partial class RowsView : RunicView<RowsViewModel>;
                public sealed class NotesViewModel : FixtureModel { public string Text => ""; }
                public sealed partial class NotesView : RunicView<NotesViewModel>;
                // A union case crosses the Bridge; a custom codec's members do not.
                [RunicUnion(typeof(Circle))] public abstract class Shape { }
                [RunicUnionCase("circle")] public sealed class Circle : Shape { public System.Windows.Media.Brush? Stroke { get; init; } }
                public sealed class ShapesViewModel : FixtureModel { public Shape? Current { get; } }
                public sealed partial class ShapesView : RunicView<ShapesViewModel>;
                public sealed class SwatchCodec { }
                [RunicBridgeCodec(typeof(SwatchCodec))] public sealed class Swatch { public System.Windows.Media.Brush? Paint { get; init; } }
                public sealed class SwatchViewModel : FixtureModel { public Swatch Current { get; } = new(); }
                public sealed partial class SwatchView : RunicView<SwatchViewModel>;
                // A command's failure value crosses the Bridge.
                public sealed class SaveError { public System.Windows.Media.Brush? Highlight { get; init; } }
                public sealed class SaveViewModel : FixtureModel
                {
                    [RunicFailure(typeof(SaveError))] public IRelayCommand SaveCommand { get; } = new RelayCommand(() => { });
                }
                public sealed partial class SaveView : RunicView<SaveViewModel>;
                """;
            var exposedSource = Preamble + Exposed;
            var (exposedExit, exposedOutput, exposedDirectory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "WpfExposed",
                exposedSource, wpf).ConfigureAwait(false);
            var errors = exposedOutput.Split('\n').Where(line => line.Contains(": error ", StringComparison.Ordinal)).ToArray();
            string At(string declaration) =>
                $"{Path.Combine(exposedDirectory, "Fixture.cs")}({exposedSource[..exposedSource.IndexOf(declaration, StringComparison.Ordinal)].Count(character => character == '\n') + 1},";
            Require(exposedExit != 0 && errors.Length == 4
                    && errors.Any(line => line.StartsWith(At("public System.Windows.Media.ImageSource? Cover"), StringComparison.Ordinal)
                        && line.Contains("error RUNICBRIDGE005: CanvasViewModel.Cover: its type needs the assembly PresentationCore", StringComparison.Ordinal)
                        && line.Contains("cannot depend on Windows Desktop (WPF or Windows Forms) types", StringComparison.Ordinal)
                        && line.Contains("[RunicIgnore]", StringComparison.Ordinal))
                    && errors.Any(line => line.StartsWith(At("public System.Windows.Media.Brush? Fill"), StringComparison.Ordinal)
                        && line.Contains("error RUNICBRIDGE005: RowsViewModel.Rows.Fill: its type needs the assembly PresentationCore", StringComparison.Ordinal))
                    && errors.Any(line => line.StartsWith(At("public System.Windows.Media.Brush? Stroke"), StringComparison.Ordinal)
                        && line.Contains("error RUNICBRIDGE005: ShapesViewModel.Current.Stroke:", StringComparison.Ordinal))
                    && errors.Any(line => line.StartsWith(At("public System.Windows.Media.Brush? Highlight"), StringComparison.Ordinal)
                        && line.Contains("error RUNICBRIDGE005: SaveViewModel.SaveCommand.failure.Highlight:", StringComparison.Ordinal))
                    && !exposedOutput.Contains("NotesViewModel", StringComparison.Ordinal)
                    && !exposedOutput.Contains("SwatchViewModel", StringComparison.Ordinal),
                $"Expected one RUNICBRIDGE005 at each WPF-typed member that crosses the Bridge, and none for NotesViewModel or the custom codec.\n{exposedOutput}");

            // A View that does not load because of a WPF type is reported by name.
            var (viewExit, viewOutput, _) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "WpfView", Preamble + """
                public class DialogViewModel : System.Windows.Window, INotifyPropertyChanged
                {
                    public string Title => "";
                    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
                }
                public sealed partial class DialogView : RunicView<DialogViewModel>;
                public sealed class NotesViewModel : FixtureModel { public string Text => ""; }
                public sealed partial class NotesView : RunicView<NotesViewModel>;
                """, wpf).ConfigureAwait(false);
            Require(viewExit != 0 && viewOutput.Contains("error RUNICBRIDGE005: FixtureWpfView: the Runic View Fixture.DialogView could not be loaded because the assembly PresentationFramework could not be loaded.", StringComparison.Ordinal),
                $"A View that needs WPF was not reported by name.\n{viewOutput}");

            // A type whose base class is in a missing assembly that may declare a
            // View base (not a Windows Desktop or shared framework assembly) may be
            // a View: it is reported instead of skipped.
            var shared = StandIn("Fixture.Shared", "namespace Shared; public abstract class PageBase { }");
            var (possibleExit, possibleOutput, _) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "PossibleView", Preamble + """
                public sealed class SettingsPage : Shared.PageBase { }
                public sealed class NotesViewModel : FixtureModel { public string Text => ""; }
                public sealed partial class NotesView : RunicView<NotesViewModel>;
                """, [shared]).ConfigureAwait(false);
            Require(possibleExit != 0 && possibleOutput.Contains("error RUNICBRIDGE005: FixturePossibleView: Fixture.SettingsPage could not be loaded because the assembly Fixture.Shared could not be loaded", StringComparison.Ordinal)
                    && possibleOutput.Contains("cannot tell whether it is a Runic View", StringComparison.Ordinal),
                $"A type whose base class could not be loaded was skipped.\n{possibleOutput}");

        }
        finally
        {
            // This process loaded FixtureWpfApplication.dll, which Windows does
            // not let it delete; the system cleans the temporary directory then.
            try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    [Test]
    public async Task GeneratorLoadsTheApplicationsSharedFrameworksAsync()
    {
        if (SharedFramework("Microsoft.AspNetCore.App") is not { } aspNetCoreDirectory)
        {
            Skip.Test("The ASP.NET Core runtime (Microsoft.AspNetCore.App) is not installed beside this .NET runtime.");
            return;
        }
        var root = FindWorkspaceRoot();
        var configuration = typeof(CodegenDiagnosticsTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var generator = Path.Combine(root, "tools", "Runic.Application.Views.Codegen", "bin", configuration, "net10.0", "BridgeCodegen.dll");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"runic-codegen-aspnetcore-{Guid.NewGuid():N}");
        try
        {
            // #270: a hosted service derives from BackgroundService, which an
            // application on Microsoft.AspNetCore.App gets from that framework.
            var hosting = Path.Combine(aspNetCoreDirectory, "Microsoft.Extensions.Hosting.Abstractions.dll");
            var aspNetCore = new[] { MetadataReference.CreateFromImage(File.ReadAllBytes(hosting)) };
            const string Hosted = """
                public sealed class Heartbeat : Microsoft.Extensions.Hosting.BackgroundService
                {
                    protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
                public sealed class StatusViewModel : FixtureModel { public string Text => ""; }
                public sealed partial class StatusView : RunicView<StatusViewModel>;
                """;
            // The build passes the application's shared frameworks; the type loads.
            var (hostedExit, hostedOutput, hostedDirectory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "HostedService",
                Preamble + Hosted, aspNetCore, "--framework", "Microsoft.NETCore.App", "--framework", "Microsoft.AspNetCore.App").ConfigureAwait(false);
            Require(hostedExit == 0 && !hostedOutput.Contains("skipped", StringComparison.Ordinal)
                    && File.Exists(Path.Combine(hostedDirectory, "cs", "Fixture.StatusViewModel.Bridge.g.cs")),
                $"An application with a BackgroundService on Microsoft.AspNetCore.App did not generate.\n{hostedOutput}");
            // Without the framework, the hosted service is skipped, not the generation.
            var (unresolvedExit, unresolvedOutput, unresolvedDirectory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "HostedServiceWithoutFramework",
                Preamble + Hosted, aspNetCore).ConfigureAwait(false);
            // It is a warning: the assembly may be missing at run time too.
            Require(unresolvedExit == 0
                    && unresolvedOutput.Split('\n').Any(line => line.StartsWith("warning RUNICBRIDGE020: FixtureHostedServiceWithoutFramework: skipped 1 types that depend on Microsoft.Extensions.Hosting.Abstractions, which the Bridge generator could not load. None of them is a Runic View", StringComparison.Ordinal)
                        && line.TrimEnd().EndsWith($" See {HelpLink("RUNICBRIDGE020")}", StringComparison.Ordinal))
                    && File.Exists(Path.Combine(unresolvedDirectory, "cs", "Fixture.StatusViewModel.Bridge.g.cs")),
                $"A type that needs a framework the generator lacks failed the generation or was not reported as RUNICBRIDGE020.\n{unresolvedOutput}");
            // Optional generation without Views keeps the previous output when
            // types were skipped: they may have been the Views.
            const string HostedOnly = """
                public sealed class Heartbeat : Microsoft.Extensions.Hosting.BackgroundService
                {
                    protected override System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken stoppingToken) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
                """;
            var (optionalExit, optionalOutput, optionalDirectory) = await RunGeneratorFixtureAsync(generator, temporaryRoot, "HostedServiceWithoutFramework",
                Preamble + HostedOnly, aspNetCore, "--optional").ConfigureAwait(false);
            Require(optionalExit != 0 && optionalOutput.Contains("error RUNICBRIDGE005: FixtureHostedServiceWithoutFramework: no Runic Window/View classes were found, but types were skipped because Microsoft.Extensions.Hosting.Abstractions could not be loaded", StringComparison.Ordinal)
                    && File.Exists(Path.Combine(optionalDirectory, "cs", "Fixture.StatusViewModel.Bridge.g.cs"))
                    && !File.Exists(Path.Combine(optionalDirectory, "cs", "RunicBridge.NoViews.marker")),
                $"Optional generation removed the output of an assembly whose types were skipped.\n{optionalOutput}");
        }
        finally
        {
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    /// <summary>A fixture compilation against this process's assemblies, named <paramref name="name"/>.</summary>
    internal static CSharpCompilation CompileFixture(string name, string source, IEnumerable<MetadataReference> references) =>
        Compile(name, source, additionalReferences: references).WithAssemblyName(name);

    private static PortableExecutableReference StandIn(string name, string source)
    {
        using var image = new MemoryStream();
        var emitted = CompileFixture(name, source, []).Emit(image);
        Require(emitted.Success, $"The {name} stand-in did not compile.\n{string.Join('\n', emitted.Diagnostics)}");
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    // The installed shared framework of this runtime's version, else its newest
    // one, or null when it is not installed.
    private static string? SharedFramework(string name)
    {
        var runtime = new DirectoryInfo(Path.GetDirectoryName(typeof(object).Assembly.Location)!);
        var framework = Path.Combine(runtime.Parent!.Parent!.FullName, name);
        if (!Directory.Exists(framework)) return null;
        var exact = Path.Combine(framework, runtime.Name);
        return Directory.Exists(exact) ? exact
            : Directory.GetDirectories(framework).OrderByDescending(path => Version.TryParse(Path.GetFileName(path).Split('-')[0], out var version) ? version : null).FirstOrDefault();
    }
}
