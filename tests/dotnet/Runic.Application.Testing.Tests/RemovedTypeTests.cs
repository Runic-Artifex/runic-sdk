using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// A1: removed types that upgrading applications still name are compile errors that name the replacement (CS0619), not
// CS0246. The tombstones stay for one release.
internal sealed class RemovedTypeTests
{
    [Test]
    [Arguments("[assembly: Runic.Application.Views.RunicBridgeJsonIgnore]", "RunicIncludeJsonIgnored")]
    [Arguments("public sealed class T : Runic.Application.Views.IAsyncBridgeTransport { }", "Serve(IBridgeRequestHandler)")]
    [Arguments("public sealed class T : Runic.Application.Views.IBridgeUtf8Transport { }", "BroadcastAsync")]
    [Arguments("public static class T { public static void M(Runic.Application.Views.IBridgeArguments a) { } }", "Rebuild")]
    [Arguments("public static class T { public static object M() => typeof(Runic.Application.Testing.BridgeTestArguments); }", "SetAsync")]
    [Arguments("public static class T { public static bool M() => Runic.Application.Views.ReactiveUI.RunicReactiveSchedulerProvider.InstallMainThreadScheduler(); }", "ReactiveSchedulers.InstallMainThreadScheduler")]
    [Arguments("public sealed class T : Runic.Application.Views.ReactiveUI.IRunicReactiveSchedulerProvider { }", "IReactiveSchedulerProvider")]
    [Arguments("public static class T { public static void M(Runic.Platform.Runtime.INativePickerOwner o) { } }", "INativeWindowOwner")]
    public void RemovedTypesNameTheirReplacement(string source, string replacement)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Concat([typeof(Runic.Application.Views.RunicWindowOptions).Assembly.Location, typeof(RunicWindowTestHost).Assembly.Location,
                typeof(Runic.Application.Views.ReactiveUI.ReactiveSchedulers).Assembly.Location,
                typeof(Runic.Platform.Runtime.INativeWindowOwner).Assembly.Location])
            .Distinct(StringComparer.Ordinal)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Upgrade", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var errors = compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        if (!errors.Any(error => error.Id == "CS0619" && error.GetMessage(System.Globalization.CultureInfo.InvariantCulture).Contains(replacement, StringComparison.Ordinal))
            || errors.Any(error => error.Id == "CS0246"))
            throw new InvalidOperationException($"{source}: {string.Join("; ", errors.Select(error => error.ToString()))}");
    }
}
