# Document tabs

Document tabs that the user opens and closes at run time, with
CommunityToolkit.Mvvm ViewModels and a React page. Each tab is a navigation
region with its own back stack.

| File | What it does |
| --- | --- |
| [`ViewModels.cs`](ViewModels.cs) | `DocumentsViewModel` holds the tabs in a `[RunicRegionCollection]` `NavigationSelection`; `NewDocument` opens `documents/{id}` in a new tab and selects it. `ShowDetails` pushes `DocumentInfoViewModel` (`documents/{id}/info`) into the same tab, and its `Back` returns to the document. `SaveNavigation`, `RestoreNavigationAsync` and `OpenLinkAsync` save the tabs, reopen them and open deep links. `AddDocumentTabs` registers the navigator, the route map and the ViewModels. |
| [`Views.cs`](Views.cs) | The Window and the Views of the two pages a tab presents. |
| [`Program.cs`](Program.cs) | Claims the running instance, opens the Window on CS-WebUI, reopens the saved tabs, and routes `document-tabs://` links from this launch and later ones to the Window. |
| [`Frontend/src/app.tsx`](Frontend/src/app.tsx) | A tab bar from `regionEntries(state.documents)`, with `selectDocuments(key)` and `closeDocuments(key)`, and a panel per tab that renders its page with `ViewOutlet`. |
| [`Tests/DocumentTabsTests.cs`](Tests/DocumentTabsTests.cs) | Opens, switches, pushes and closes tabs through the generated Bridges with `RunicWindowTestHost`, reopens them in a new window scope as after a restart, and opens links as a second launch and a notification deliver them. |

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

## Reopening tabs and deep links

The pages have routes, registered with `AddRunicNavigationRoutes`:
`documents/{id:int}` for a document and `documents/{id:int}/info` for its
details. See
[Routes, saved paths and deep links](../../packages/dotnet/Runic.Navigation/README.md#routes-saved-paths-and-deep-links).

- **Restart.** After each change of the tabs or of a tab's page,
  `DocumentsViewModel` saves `navigator.GetPath()` as JSON in the user's local
  application data, shortly after and once for a burst of changes. Saving on
  change works however the window ends: CS-WebUI can't ask the application
  before a browser window closes. At the next start, `RestoreNavigationAsync` reopens every tab with its back
  stack and the selected tab. The selection's `RestoreRegion` creates the empty
  tabs the path restores into. The example keeps documents in memory, so a
  reopened document starts empty; an application reads it from its own store.
- **Deep links.** `OpenLinkAsync(document-tabs://documents/2/info)` selects the
  tab of document 2, or opens one, and shows its details with
  `NavigationRouteOpen.Stack`, which puts the document below the details page.
  A link to an unknown document or route opens nothing. Program.cs passes it
  the links of this launch and of later launches that a second instance
  forwards (`ApplicationInstance.CreateOpenRequests`).
  `OpenNotificationAsync` opens a notification's `ActivationUri` the same way.
  To open links from the desktop, register the `document-tabs` scheme for the
  built executable, as the
  [opening files and URLs design](../../specs/platform/open-activation.md)
  describes.
- **Back.** The page calls `installBackNavigation()`, so the mouse's Back
  button, Alt+Left and Cmd+[ go back in the selected tab: from a details page to
  its document, through the pages' guards (`navigation-path/1`).

```sh
dotnet run --project examples/document-tabs
dotnet run --project examples/document-tabs -- document-tabs://documents/1/info   # while it runs: forwarded to it
dotnet run --project examples/document-tabs/Tests
```

The example builds the Runic packages from source, and the frontend bundles
`@runic-artifex/react` from its workspace source. To copy it out, replace the
`ProjectReference` and `Import` lines with a `Runic.Application.Views.CsWebUi`
package reference, add a `Runic.Platform.Runtime` package reference, install `@runic-artifex/views`, `@runic-artifex/react`,
`react` and `react-dom`, and remove the `paths` in `tsconfig.json` and the
plug-in in `build.mjs`.
