# Invoice grid

An invoice with a billing address and editable lines, where every address member
and every cell of a line is a field of its own: it keeps the user's draft, writes
it after a pause or on blur, shows the validation messages .NET reports for it,
and reports a conflict when another writer changed the same cell meanwhile. No
command is written per cell.

- The [ViewModel](InvoiceViewModel.cs) uses CommunityToolkit.Mvvm. `Address` is
  an immutable record with a public setter, so the page can write
  `address.street` alone. `Lines` is a `[RunicCollection]` of immutable records,
  and the `[RunicRowEditor]` method `UpdateLine(int id, InvoiceLine line)` is
  the one place that accepts a changed line: it normalizes it, replaces it and
  updates the total.
- A line implements `INotifyDataErrorInfo`, so its messages travel at the
  validation path `["lines", index, "quantity"]` and the cell's field shows them.
- The [page](Frontend/src/app.ts) binds each input with `bindField` to the
  generated descriptors `InvoiceFields.address.street` and
  `InvoiceFields.lines.at(id).quantity`. A conflict offers "Keep mine"
  (`retry()`) and "Take theirs" (`reset()`).

The protocol side is the feature `field-paths/1`
([field paths](../../specs/application/README.md#field-paths)); the .NET side is
described in [Editable rows and nested fields](../../packages/dotnet/Runic.Application.Views/README.md#editable-rows-and-nested-fields).

## Run it from this repository

```sh
bun run bootstrap
dotnet run --project examples/invoice-grid/InvoiceGrid.csproj
```

Open the window twice (or edit the same cell from a test with
`RunicViewDriver.WritePathAsync`) to see a conflict.
