# Document tabs

Document tabs that the user opens and closes at run time, with
CommunityToolkit.Mvvm ViewModels and a React page. Each tab is a navigation
region with its own back stack.

| File | What it does |
| --- | --- |
| [`ViewModels.cs`](ViewModels.cs) | `DocumentsViewModel` holds the tabs in a `[RunicRegionCollection]` `NavigationSelection`; `NewDocument` adds a region with a new `DocumentViewModel` and selects it. `ShowDetails` pushes `DocumentInfoViewModel` into the same tab, and its `Back` returns to the document. |
| [`Views.cs`](Views.cs) | The Window and the Views of the two pages a tab presents. |
| [`Program.cs`](Program.cs) | Calls `AddDocumentTabs()`, which registers the navigator and the ViewModel, and `AddRunicViews()`, then opens the Window on CS-WebUI. |
| [`Frontend/src/app.tsx`](Frontend/src/app.tsx) | A tab bar from `regionEntries(state.documents)`, with `selectDocuments(key)` and `closeDocuments(key)`, and a panel per tab that renders its page with `ViewOutlet`. |
| [`Tests/DocumentTabsTests.cs`](Tests/DocumentTabsTests.cs) | Opens, switches, pushes and closes tabs through the generated Bridges with `RunicWindowTestHost`. |

The generated client lists the tabs as `state.documents`: each region's `key`
and current page, and the `selected` index. The page selects and closes a tab
by its key. A close resolves with the .NET `NavigationOutcome`, such as
`{ kind: "Rejected", rejection: "Guard" }`, and the tab bar highlights
`selected`, which changes only when a switch or close commits.

`DocumentViewModel` is a departure guard. Switching tabs and showing the
details keep the document, so they are never blocked. Closing a tab retires
every page in it, also a document under its details page, and the document
refuses while it has unsaved changes. The page then selects that tab and says
why. While the document has unsaved changes it shows **Discard and close**,
which runs `Discard` and closes the tab again.

The page keeps every tab's panel mounted and hides all but the selected one,
so a tab keeps its DOM. A push in a tab replaces only that tab's page. Each
panel provides its region key through a React context, and **Discard and
close** closes the tab by that key. `<RegionOutlets>` renders the same panels
when the pages need no key. Each tab
label connects to its page with `useView` to show the title.

```sh
dotnet run --project examples/document-tabs
dotnet run --project examples/document-tabs/Tests
```

The example builds the Runic packages from source, and the frontend bundles
`@runic-artifex/react` from its workspace source. To copy it out, replace the
`ProjectReference` and `Import` lines with a `Runic.Application.Views.CsWebUi`
package reference, install `@runic-artifex/views`, `@runic-artifex/react`,
`react` and `react-dom`, and remove the `paths` in `tsconfig.json` and the
plug-in in `build.mjs`.
