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
