using System.Collections.Specialized;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Runic.Application.Testing;
using Runic.Application.Views;
using Runic.Application.Views.ReactiveUI;
using Runic.Navigation;
using TUnit.Core;

namespace Runic.Application.Testing.Tests;

// query-collections/1: a RunicQueryCollection served through its generated Bridge (both flavors), the Testing driver,
// the collection's cache and cancellation, the LINQ mapping, and the WPF view list.
internal sealed class QueryCollectionTests
{
    [Test]
    public async Task EachConnectionReadsItsOwnRangeAndTheQueryIsSharedStateAsync()
    {
        var source = new RunicTestRowSource<CatalogRow, int>(CatalogRows.Create(100_000), CatalogRows.Fields(), row => row.Id);
        await using var services = Services<CatalogViewModel>((transport, content, vm) => new CatalogBridge(transport, vm, content: content));
        var model = new CatalogViewModel(source, services.GetRequiredService<IRunicModelContext>());
        using var host = RunicWindowTestHost.Create(services, model);

        var state = (await host.Root.SnapshotAsync()).Get(vm => vm.Rows);
        Require(state.GetProperty("total").ValueKind == JsonValueKind.Null && state.GetProperty("version").GetInt64() == 0
            && state.GetProperty("query").GetRawText() == """{"sort":[],"filters":[]}""",
            $"The query collection's state member is not {{query, total, version}}: {state.GetRawText()}");

        var first = await host.Root.QueryRowsAsync(vm => vm.Rows, 500, 20);
        Require(first.Total == 100_000 && first.Start == 500 && first.Keys.SequenceEqual(Enumerable.Range(500, 20).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            && first.Rows[0].GetProperty("label").GetString() == "row 500" && first.Rows[0].GetProperty("size").GetInt32() == 500 % 7,
            $"query-rows did not return the range: {JsonSerializer.Serialize(first.Keys)}");
        // Another page scrolls elsewhere without moving the first one's rows.
        var second = await host.Root.QueryRowsAsync(vm => vm.Rows, 90_000, 10, connectionKey: "second");
        Require(second.Keys[0] == "90000" && second.Keys.Count == 10, "The second connection did not read its own range.");
        Require(source.Loads.Select(load => load.Range.Start).Order().SequenceEqual([500, 90_000]),
            $"The loads were not one page per range: {string.Join(", ", source.Loads.Select(load => load.Range))}");
        // The count is published as state.
        await WaitAsync(async () => (await host.Root.SnapshotAsync()).Get(vm => vm.Rows).GetProperty("total").ValueKind == JsonValueKind.Number);

        var sorted = await host.Root.SetQueryAsync(vm => vm.Rows,
            new RunicQuery([new RunicSortKey("size", Descending: true)], [RunicFilter.Contains("label", "9")]));
        Require(sorted.Succeeded && sorted.State!.Get(vm => vm.Rows).GetProperty("version").GetInt64() == 1
            && sorted.State.Get(vm => vm.Rows).GetProperty("query").GetRawText()
                == """{"sort":[{"field":"size","descending":true}],"filters":[{"field":"label","op":"contains","value":"9"}]}""",
            $"query-set did not publish the query and a new version: {sorted.Json}");
        Require(model.Rows.Query.Sort.Single() == new RunicSortKey("size", true), "The ViewModel does not see the page's query.");
        var filtered = await host.Root.QueryRowsAsync(vm => vm.Rows, 0, 5);
        Require(filtered.Version == 1 && filtered.Rows.All(row => row.GetProperty("size").GetInt32() == 6
            && row.GetProperty("label").GetString()!.Contains('9', StringComparison.Ordinal)),
            "The rows do not follow the query.");
        Require(await host.Root.IndexOfAsync(vm => vm.Rows, filtered.Keys[3]) == 3, "query-index did not find a key in the query's order.");
        Require(await host.Root.IndexOfAsync(vm => vm.Rows, "100") == -1, "query-index found a row the filter excludes.");
        Require(await host.Root.IndexOfAsync(vm => vm.Rows, "not a number") == -1, "query-index did not answer -1 for a key that is not an Int32.");

        var unknown = await host.Root.SetQueryAsync(vm => vm.Rows, new RunicQuery([new RunicSortKey("owner")]));
        Require(!unknown.Succeeded && unknown.ErrorKind == "rejected" && unknown.ErrorReason == "invalid-input",
            $"A query with an unknown field was not rejected as invalid input: {unknown.Json}");
        // A .NET command changes the query as well, for every page.
        (await host.Root.ExecuteAsync(vm => vm.SortByIdCommand)).ThrowIfFailed();
        Require(model.Rows.Version == 2 && model.Rows.Query.Filters.Count == 1
            && (await host.Root.SnapshotAsync()).Get(vm => vm.Rows).GetProperty("version").GetInt64() == 2,
            "A .NET query change did not raise the version.");
    }

    [Test]
    public async Task ToolkitFlavorRefreshesAndCountsAgainAsync()
    {
        var source = new RunicTestRowSource<CatalogRow, int>(CatalogRows.Create(30), CatalogRows.Fields(), row => row.Id);
        await using var services = Services<ToolkitCatalogViewModel>((transport, content, vm) => new ToolkitCatalogBridge(transport, vm, content: content));
        var model = new ToolkitCatalogViewModel(source, services.GetRequiredService<IRunicModelContext>());
        using var host = RunicWindowTestHost.Create(services, model);
        var rows = await host.Root.QueryRowsAsync(vm => vm.Rows, 25, 10);
        Require(rows.Total == 30 && rows.Keys.Count == 5, "A range past the end did not end at the last row.");
        source.SetRows(CatalogRows.Create(40));
        (await host.Root.ExecuteAsync(vm => vm.RefreshCommand)).ThrowIfFailed();
        await WaitAsync(async () => (await host.Root.SnapshotAsync()).Get(vm => vm.Rows).GetProperty("total").GetInt32() == 40);
        var after = await host.Root.QueryRowsAsync(vm => vm.Rows, 25, 10);
        Require(after.Version == 1 && after.Keys.Count == 10 && source.Counts == 2, "Refresh did not reload the rows and count again.");
    }

    [Test]
    public async Task ScrollingHalfAMillionRowsKeepsMemoryAndTrafficBoundedAsync()
    {
        // Measured claim (PLAN-0.14): the collection keeps at most MaximumCachedPages pages, and each request sends only
        // its range, however long the list is and however far a page scrolls.
        const int total = 500_000;
        var source = new RunicTestRowSource<CatalogRow, int>(CatalogRows.Create(total), CatalogRows.Fields(), row => row.Id);
        using var collection = new RunicQueryCollection<CatalogRow, int>(source, row => row.Id,
            new RunicQueryCollectionOptions { PageSize = 100, MaximumCachedPages = 16 });
        await using var services = Services<CatalogHolder>((transport, content, vm) => new CatalogHolderBridge(transport, vm, content: content));
        using var host = RunicWindowTestHost.Create(services, new CatalogHolder(collection));
        long replyBytes = 0;
        var ranges = 0;
        for (var start = 0; start < total; start += 997)
        {
            var rows = await host.Root.QueryRowsAsync(vm => vm.Rows, start, 40);
            replyBytes += JsonSerializer.Serialize(rows.Rows).Length;
            ranges++;
            var statistics = collection.Statistics;
            Require(statistics.CachedPages <= 16 && statistics.CachedRows <= 1_600,
                $"The cache grew past its bound at row {start}: {statistics}");
        }
        var final = collection.Statistics;
        // Each range of 40 rows touches one or two pages of 100: the source never reads the whole list.
        Require(final.RowsLoaded <= ranges * 2 * 100 && final.RowsLoaded < total, $"The source read more than the ranges needed: {final}");
        Require(replyBytes / ranges < 40 * 64, $"A range reply carried more than its rows: {replyBytes / ranges} bytes per range.");
    }

    [Test]
    public async Task LoadsAreSharedCoalescedAndCancelledWhenNobodyWaitsAsync()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new RunicTestRowSource<CatalogRow, int>(CatalogRows.Create(10_000), CatalogRows.Fields(), row => row.Id, clock)
            { Latency = TimeSpan.FromSeconds(1) };
        using var collection = new RunicQueryCollection<CatalogRow, int>(source, row => row.Id,
            new RunicQueryCollectionOptions { PageSize = 100, MaximumConcurrentLoads = 2 });
        // Ten requests of one page share one load.
        var shared = Enumerable.Range(0, 10).Select(offset => collection.LoadAsync(new(offset, 10)).AsTask()).ToArray();
        await WaitAsync(() => Task.FromResult(source.Counts == 1));
        // The load waits for its latency once it starts; the clock moves until every request completed.
        await WaitAsync(() => { clock.Advance(TimeSpan.FromSeconds(1)); return Task.FromResult(shared.All(task => task.IsCompleted)); });
        Require(source.Loads.Count == 1 && shared.All(task => task.Result.Count == 10), "Requests of one page did not share its load.");

        // A request nobody waits for any more stops its load.
        using var cancel = new CancellationTokenSource();
        var abandoned = collection.LoadAsync(new(5_000, 50), cancel.Token).AsTask();
        await cancel.CancelAsync();
        try { await abandoned; Require(false, "A cancelled request completed."); }
        catch (OperationCanceledException) { }
        for (var round = 0; round < 5; round++) { clock.Advance(TimeSpan.FromSeconds(1)); await Task.Delay(10); }
        Require(source.Loads.All(load => load.Range.Start != 5_000), "A load nobody waited for still ran to completion.");

        // At most MaximumConcurrentLoads loads run at once.
        var many = Enumerable.Range(0, 8).Select(page => collection.LoadAsync(new(page * 100 + 1_000, 100)).AsTask()).ToArray();
        await WaitAsync(() => { clock.Advance(TimeSpan.FromSeconds(1)); return Task.FromResult(many.All(task => task.IsCompleted)); });
        await Task.WhenAll(many);
        Require(source.MaximumConcurrency <= 3, $"More loads ran at once than the bound: {source.MaximumConcurrency}.");
    }

    [Test]
    public async Task InvalidateReloadsOnlyThePageOfTheChangedRowAsync()
    {
        var rows = CatalogRows.Create(1_000);
        var source = new RunicTestRowSource<CatalogRow, int>(rows, CatalogRows.Fields(), row => row.Id);
        using var collection = new RunicQueryCollection<CatalogRow, int>(source, row => row.Id, new RunicQueryCollectionOptions { PageSize = 100 });
        await collection.LoadAsync(new(0, 300));
        Require(source.Loads.Count == 3 && collection.Version == 0, "The first range did not load three pages.");
        rows[150] = rows[150] with { Label = "changed" };
        source.SetRows(rows);
        collection.Invalidate(150);
        Require(collection.Version == 1, "Invalidate did not raise the version.");
        var reloaded = await collection.LoadAsync(new(0, 300));
        Require(source.Loads.Count == 4 && source.Loads[^1].Range.Start == 100 && reloaded[150].Label == "changed",
            $"Invalidate did not reload exactly the changed row's page: {string.Join(", ", source.Loads.Select(load => load.Range))}");
        Require(await collection.IndexOfAsync(999) == 999 && await collection.IndexOfAsync(5_000) == -1, "IndexOfAsync did not search the source.");
        source.FailNext(new InvalidOperationException("database offline"));
        collection.Refresh();
        try { await collection.LoadAsync(new(0, 10)); Require(false, "A failed load succeeded."); }
        catch (InvalidOperationException error) when (error.Message == "database offline") { }
        Require((await collection.LoadAsync(new(0, 10))).Count == 10, "A failed page was kept instead of loaded again.");
    }

    [Test]
    public async Task QueryFieldsMapToLinqAndRejectUnknownFields()
    {
        var fields = CatalogRows.Fields();
        var rows = CatalogRows.Create(100).AsQueryable();
        var query = new RunicQuery([new RunicSortKey("size"), new RunicSortKey("id", Descending: true)],
            [RunicFilter.Range("id", 10, 40), RunicFilter.Custom("even"), RunicFilter.Equal("size", 3)]);
        var result = fields.Apply(rows, query, row => row.Id).ToList();
        Require(result.Select(row => row.Id).SequenceEqual([38, 24, 10]), $"The query did not filter and sort: {string.Join(",", result.Select(row => row.Id))}");
        Require(fields.Apply(rows, new RunicQuery(filters: [RunicFilter.Contains("label", "ROW 9")]), row => row.Id).Count() == 11,
            "contains did not ignore case.");
        foreach (var invalid in new[]
        {
            new RunicQuery([new RunicSortKey("owner")]),
            new RunicQuery(filters: [RunicFilter.Contains("size", "3")]),
            new RunicQuery(filters: [RunicFilter.Range("label", 1, 2)]),
            new RunicQuery(filters: [RunicFilter.Equal("size", "three")]),
            new RunicQuery(filters: [RunicFilter.Custom("odd")]),
        })
        {
            try { fields.Validate(invalid); Require(false, $"{invalid} was accepted."); }
            catch (ArgumentException) { }
        }
        // The canonical wire form is what both sides compare.
        var copy = new RunicQuery([new RunicSortKey("size"), new RunicSortKey("id", true)],
            [RunicFilter.Range("id", 10, 40), RunicFilter.Custom("even"), RunicFilter.Equal("size", 3)]);
        Require(query.Equals(copy) && query.GetHashCode() == copy.GetHashCode(), "Equal queries are not equal.");
        await Task.CompletedTask;
    }

    [Test]
    public async Task ViewListLoadsTheItemsItsPanelReadsAsync()
    {
        var source = new RunicTestRowSource<CatalogRow, int>(CatalogRows.Create(1_000), CatalogRows.Fields(), row => row.Id);
        using var collection = new RunicQueryCollection<CatalogRow, int>(source, row => row.Id, new RunicQueryCollectionOptions { PageSize = 50 });
        var context = new QueueContext();
        using var view = collection.CreateView(context);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        view.CollectionChanged += (_, change) => changes.Add(change);
        // The total arrives with the first load.
        await WaitAsync(() => Task.FromResult(context.Run() && view.Count == 1_000));
        Require(view[120] is null, "An unloaded row is not a placeholder.");
        await WaitAsync(() => Task.FromResult(context.Run() && view[120] is not null));
        Require(view[120]!.Id == 120 && changes.Any(change => change.Action == NotifyCollectionChangedAction.Replace && change.NewStartingIndex == 120),
            "The loaded row did not replace its placeholder.");
        Require(source.Loads.Select(load => load.Range.Start).Contains(100), "The view did not load the page its panel read.");
        collection.SetQuery(new RunicQuery([new RunicSortKey("id", Descending: true)]));
        await WaitAsync(() => Task.FromResult(context.Run() && changes[^1].Action == NotifyCollectionChangedAction.Reset));
        await WaitAsync(() => Task.FromResult(context.Run() && view[0] is { Id: 999 }));
    }

    // A ViewModel that only holds a collection the test configured.
    internal static ServiceProvider Services<TModel>(Func<IBridgeTransport, WindowContentSession, TModel, IDisposable> attach) where TModel : class
    {
        var services = new ServiceCollection();
        services.AddRunicReactiveModelContext();
        services.AddScoped(_ => attach);
        return services.BuildServiceProvider();
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(10);
        }
        Require(false, "The condition did not hold within 5 seconds.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // A UI thread's context for the view list: callbacks run when the test pumps it.
    private sealed class QueueContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        // Runs what was posted; returns true so it can lead a condition.
        public bool Run()
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try { while (_queue.TryDequeue(out var item)) item.Callback(item.State); }
            finally { SetSynchronizationContext(previous); }
            return true;
        }
    }
}
