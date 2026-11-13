using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Application.Views;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// RUNICBRIDGE038 (#916): Bridges that the 0.18 generator wrote derive from ViewModelBridge<T>, which 0.19 removed.
// Their composition makes no contract check, so the host reports the TypeLoadException of the first attach as the
// rebuild error. The case compiles such a Bridge against a stand-in of the 0.18 support API and runs it on this one.
internal sealed class StaleBridgeTests
{
    private const string StaleLibrary = "StaleViewModels";

    // The part of the 0.18 generated-code support API that a 0.18 Bridge needs to compile.
    private const string SupportApi018 = """
        namespace Runic.Application.Views
        {
            public interface IBridgeTransport { }
            public sealed class WindowContentSession { }
        }
        namespace Runic.Application.Views.Generated
        {
            public abstract class ViewModelBridge<TViewModel> : System.IDisposable where TViewModel : class
            {
                protected ViewModelBridge(Runic.Application.Views.IBridgeTransport transport, TViewModel vm, string name) { }
                public void Dispose() { }
            }
        }
        """;

    // What the 0.18 generator wrote for one ViewModel: a Bridge class and the composition's factory lambda.
    private const string Library018 = """
        using Runic.Application.Views;
        namespace Stale
        {
            public sealed class StaleViewModel { }
            internal sealed class StaleBridge : Runic.Application.Views.Generated.ViewModelBridge<StaleViewModel>
            {
                internal StaleBridge(IBridgeTransport transport, StaleViewModel vm) : base(transport, vm, "stale") { }
            }
            public static class RunicViewsComposition
            {
                public static System.Func<IBridgeTransport, WindowContentSession, StaleViewModel, System.IDisposable> Factory() =>
                    (transport, content, vm) => new StaleBridge(transport, vm);
            }
        }
        """;

    [Test]
    public void BridgeGeneratedByAnEarlierReleaseReportsRebuildAtAttach()
    {
        var context = new AssemblyLoadContext(StaleLibrary, isCollectible: true);
        try
        {
            var library = context.LoadFromStream(new MemoryStream(Emit(StaleLibrary, Library018,
                [MetadataReference.CreateFromImage(Emit("Runic.Application.Views", SupportApi018, []))])));
            var viewModelType = library.GetType("Stale.StaleViewModel", throwOnError: true)!;
            var factory = (Delegate)library.GetType("Stale.RunicViewsComposition", throwOnError: true)!
                .GetMethod("Factory")!.Invoke(null, null)!;
            var hostType = typeof(RunicWindowTestHost<>).MakeGenericType(viewModelType);
            Exception? thrown = null;
            try { Activator.CreateInstance(hostType, Activator.CreateInstance(viewModelType), factory); }
            catch (TargetInvocationException invocation) { thrown = invocation.InnerException; }
            var error = thrown as RunicBridgeContractException;
            Require(error is not null, $"A 0.18 Bridge did not report RunicBridgeContractException but {thrown}.");
            Require(error!.AssemblyName == StaleLibrary, $"The error names {error.AssemblyName}, not {StaleLibrary}.");
            Require(error.Message.StartsWith(
                    $"RUNICBRIDGE038: the generated Runic Bridge for Stale.StaleViewModel in {StaleLibrary} was built against another Runic release",
                    StringComparison.Ordinal)
                && error.Message.Contains($"Rebuild this library against Runic {RunicBridgeContractException.RuntimeVersion}.", StringComparison.Ordinal)
                && error.Message.EndsWith("/docs/diagnostics.md#runicbridge038", StringComparison.Ordinal),
                $"The message is not the rebuild error:\n{error.Message}");
            Require(error.InnerException is TypeLoadException load && load.TypeName.Contains("ViewModelBridge`1", StringComparison.Ordinal),
                $"The inner exception is not the Bridge's TypeLoadException: {error.InnerException}");
        }
        finally { context.Unload(); }
    }

    [Test]
    public void OtherFactoryFailuresStayAsTheyAre()
    {
        Func<object, object> factory = static model => model;
        Require(BridgeGeneratedCode.StaleBridge(new InvalidOperationException("Runic.Application.Views"), factory, typeof(object)) is null,
            "A failure that is no type load was reported as a stale Bridge.");
        Require(BridgeGeneratedCode.StaleBridge(new TypeLoadException("Could not load type 'Other.Type' from assembly 'Other'."),
                factory, typeof(object)) is null,
            "A type load failure of another assembly was reported as a stale Bridge.");
        Require(BridgeGeneratedCode.StaleBridge(new MissingMethodException("Method not found: 'Void Runic.Application.Views.Generated.PropertyDescriptor`1..ctor()'."),
                factory, typeof(object)) is { } stale && stale.AssemblyName == typeof(StaleBridgeTests).Assembly.GetName().Name,
            "A missing support API method was not reported against the factory's assembly.");
    }

    [Test]
    public void ContractCheckAcceptsOnlyTheContractsThisRuntimeRuns()
    {
        var assembly = typeof(StaleBridgeTests).Assembly;
        BridgeGeneratedCode.RequireContract(BridgeGeneratedCode.ContractVersion, assembly);
        foreach (var version in new[] { BridgeGeneratedCode.MinimumContractVersion - 1, BridgeGeneratedCode.ContractVersion + 1 })
        {
            RunicBridgeContractException? error = null;
            try { BridgeGeneratedCode.RequireContract(version, assembly); }
            catch (RunicBridgeContractException thrown) { error = thrown; }
            Require(error is not null && error.AssemblyName == assembly.GetName().Name
                && error.Message.Contains($"generated-code contract {version};", StringComparison.Ordinal),
                $"Contract {version} was not rejected with RUNICBRIDGE038: {error?.Message}");
        }
    }

    // Compiles against this process's assemblies, with the stand-ins in place of the real Runic.Application.Views.
    private static byte[] Emit(string name, string source, IEnumerable<MetadataReference> standIns)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => !Path.GetFileNameWithoutExtension(path).StartsWith("Runic.", StringComparison.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).Concat(standIns);
        var compilation = CSharpCompilation.Create(name,
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Require(emitted.Success, $"{name} did not compile:\n{string.Join('\n', emitted.Diagnostics)}");
        return image.ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
