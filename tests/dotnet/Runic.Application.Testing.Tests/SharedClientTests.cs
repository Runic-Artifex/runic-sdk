using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runic.Application.Views.Generated.Bridges;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// [RunicSharedClient] (#839): a test drives each result page through the shared interface, as the shared web component
// does, whichever ViewModel presents it.
internal sealed class SharedClientTests
{
    [Test]
    public async Task ResultPagesAreDrivenThroughTheirSharedInterfaceAsync()
    {
        var model = new SharedResultsViewModel();
        using var host = new RunicWindowTestHost<SharedResultsViewModel>(model,
            (transport, content, vm) => new SharedResultsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        var orders = new SharedOrdersViewModel();
        var invoices = new SharedInvoicesViewModel();
        var titles = new List<string>();
        foreach (var page in new ISharedResultPage[] { orders, invoices })
        {
            model.Show(page);
            var view = host.Root.View<ISharedResultPage>(vm => vm.Current);
            var id = view.Snapshot().Get(vm => vm.Page).GetProperty("elements")[0].GetProperty("id").GetString();
            view.Set(vm => vm.Filter, "1").ThrowIfFailed();
            (await view.ExecuteAsync<string>(vm => vm.RunActionCommand, id!)).ThrowIfFailed();
            titles.Add(view.Snapshot().Get(vm => vm.Page).GetProperty("title").GetString()!);
        }
        Require(titles.SequenceEqual(["Orders", "Invoices"]), $"The shared driver read {string.Join(", ", titles)}.");
        Require(orders.Filter == "1" && invoices.Filter == "1", "The shared driver did not set the filter of each page.");
        Require(orders.Runs.SequenceEqual(["o-1"]) && invoices.Runs.SequenceEqual(["i-1"]),
            $"The shared driver ran {string.Join(", ", orders.Runs.Concat(invoices.Runs))}.");
        Require(Throws<InvalidOperationException>(() => host.Root.View<INavPageViewModel>(vm => vm.Current)),
            "A content driver accepted an interface the presented ViewModel does not implement.");
    }

    // A library-split app: the shared interface in App.Contracts, the implementing ViewModel in App.Features and the
    // window with its test here. The driver finds the implementer in a loaded assembly that references the contract's.
    [Test]
    public async Task SharedInterfaceInAnotherAssemblyFindsImplementersInAThirdAsync()
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(path => MetadataReference.CreateFromFile(path)).ToList();
        var contracts = Emit("App.Contracts", "namespace App.Contracts; public interface IResultPage { string Title { get; } }", references);
        var features = Emit("App.Features", """
            namespace App.Features;
            // The kind 'sharedOrders' that the window presents is this ViewModel's name.
            public sealed class SharedOrdersViewModel : App.Contracts.IResultPage { public string Title => "Orders"; }
            """, [.. references, MetadataReference.CreateFromImage(contracts)]);
        var context = new SplitAppContext();
        var contract = context.Add(contracts).GetType("App.Contracts.IResultPage", throwOnError: true)!;
        var implementer = context.Add(features).GetType("App.Features.SharedOrdersViewModel", throwOnError: true)!;
        Require(contract.IsAssignableFrom(implementer), "The split app's ViewModel does not implement its contract.");

        var model = new SharedResultsViewModel();
        using var host = new RunicWindowTestHost<SharedResultsViewModel>(model,
            (transport, content, vm) => new SharedResultsBridge(transport, vm, content: content),
            new RunicWindowTestHostOptions { ViewLocator = new TestViewLocator() });
        model.Show(new SharedOrdersViewModel());
        var viewAsync = typeof(RunicViewDriver<SharedResultsViewModel>).GetMethod(nameof(RunicViewDriver<SharedResultsViewModel>.ViewAsync))!
            .MakeGenericMethod(contract);
        System.Linq.Expressions.Expression<Func<SharedResultsViewModel, object?>> current = vm => vm.Current;
        try { await ((Task)viewAsync.Invoke(host.Root, [current, CancellationToken.None])!).ConfigureAwait(false); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException($"The driver did not find the implementer in App.Features: {error.Message}", error);
        }
    }

    private static byte[] Emit(string name, string source, IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Require(emitted.Success, $"{name} did not compile.\n{string.Join('\n', emitted.Diagnostics)}");
        return image.ToArray();
    }

    // Loads the split app's libraries side by side, as an app's own load context would.
    private sealed class SplitAppContext() : AssemblyLoadContext("split-app")
    {
        private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.Ordinal);

        internal Assembly Add(byte[] image)
        {
            using var stream = new MemoryStream(image);
            var assembly = LoadFromStream(stream);
            _loaded[assembly.GetName().Name!] = assembly;
            return assembly;
        }

        protected override Assembly? Load(AssemblyName assemblyName) => _loaded.GetValueOrDefault(assemblyName.Name!);
    }

    private static bool Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return true; }
        return false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
