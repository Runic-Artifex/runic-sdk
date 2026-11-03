# Query collections

A .NET 10 window that shows 100,000 products from a `RunicQueryCollection`. The rows stay in .NET: the page loads only
the rows it shows, sorts and filters them in .NET with `setQuery`, scrolls to a product with `scrollToKey`, and lays
the rows out with their own heights (`createQueryViewportController`). **Restock** changes a row in place
(`Invalidate`), and **Add a product** adds one (`Refresh`).

Run it from the SDK root in its locked development environment:

```sh
bun install --frozen-lockfile
bun run --cwd packages/web/views build
dotnet run --project examples/query-collections
```

On NixOS use `direnv exec <sdk-root> <command>` when the SDK shell is not loaded. `RUNIC_APPLICATION_SERVE_ONLY=1`
serves the window to a browser without opening it.

- [`CatalogViewModel.cs`](CatalogViewModel.cs): the product store, the fields the page may sort and filter by
  (`RunicQueryFields`), and the collection over a `RunicQueryableRowSource`. A database would replace the store's
  `IQueryable` with a `DbSet`.
- [`Frontend/main.ts`](Frontend/main.ts): the generated client's `queries.products`, the variable-height viewport, the
  query form and the go-to box, without a framework. The framework packages wrap the same viewport as
  `useQueryCollection` (`injectQueryCollection` in Angular).

See [Query collections](../../packages/dotnet/Runic.Application.Views/README.md#query-collections) for the .NET side and
the [page runtime](../../packages/web/views/README.md#query-collections) for the page.
