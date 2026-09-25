# Runic Next Phase 2

Phase 1 is the Window/View SDK cutover in PR #28. Phase 2 makes that model the
useful default across the SDK's own desktop applications, while retaining the
host-neutral core and the lower-level Desktop API for applications that need it.
PRs #28 and #29 are merged into `main`; the preview release remains deferred.

## Package identity

Prefer existing, generic package identities where the replacement has the same
public purpose. Do not keep a package merely to avoid a new name when it would
misdescribe its contents.

| Phase 1 package | Phase 2 target | Reason |
| --- | --- | --- |
| `Runic.Application.Views` | `Runic.Application` | The old generic application package was removed; Views is now the application model. |
| `Runic.Application.Views.CsWebUi` and `.DependencyInjection` | `Runic.Application.CsWebUi` | The old generic host ID fits; its small DI owner should ship with the host instead of requiring another package. |
| `Runic.Application.Views.ReactiveUI` | `Runic.Application.ReactiveUI` | An optional ReactiveUI dependency must stay outside the core. This is the one justified new .NET ID. |
| Headless Window/View testing | `Runic.Application.Testing` | Reuse the retired generic testing ID for generated routes, snapshots, and View lifetimes. |
| Desktop Views adapter | `Runic.Application.Desktop` | Reuse the old generic application host ID; keep `Runic.Desktop` independently useful. |
| `@runic-artifex/views-svelte` | `@runic-artifex/svelte/views` | Add an export to the existing Svelte integration. |
| `@runic-artifex/views-angular` | `@runic-artifex/angular` | The old generic Angular integration ID fits its replacement. |

Keep C# namespaces explicit about Views even if the NuGet identity is generic.
Update workspace metadata, build targets, templates, locks, package consumers,
release tooling, and documentation together with each package change. The new
IDs from Phase 1 have not been published; the old generic IDs were preview
packages and may break before 1.0.

## Implemented in this branch

- `Runic.Application.Desktop` adapts removable Desktop capabilities to the
  generated client and owns a scoped native Window. A packaged consumer checks
  restore, client generation, browser commands and reload, and native close.
- The package identities above are applied across the shipping inventory,
  templates, release tools, and external Svelte and Angular consumers.
- Generated content dispatch prefers a derived ViewModel over its registered
  base class. Interface properties continue to select among registered
  concrete ViewModels. Duplicate View contracts fail at generation.
- The development coordinator rebuilds and reconnects after a generated
  contract edit. Svelte and Angular browser checks add and remove a contract
  field to verify this behavior.
- The Translations Editor's Window, routed feature and document ViewModels,
  and command results use ReactiveUI. The root JSON operation dispatcher has
  been removed; browser drafts stay local until a revision-checked save.

## Pre-release editor and selection gate

The next migration slice adds routed `IReadOnlyList<T>` ViewModel collections,
including derived-model dispatch, stable item identities through reorder,
route suspension on removal, and reconnection on restore. Default, Svelte,
Angular, and concurrent-client Reactive Notes journeys exercise those rules.

The editor publishes feature and document ViewModels from its root. Generated
routes handle workspace, review, interchange, diagnostics, local state, project
setup, document transformation, validation, and save. Browser-scoped drafts
remain local to the editor UI; the compiler-backed session retains revision
checking, atomic save, and conflict handling. The hosted browser journey runs once against
source references and once against locally packed `Runic.Application`,
`Runic.Application.CsWebUi`, and `Runic.Application.ReactiveUI` packages. The
package-consumer CI job repeats the packed journey and checks package origin.
Do not publish the preview until this migration PR passes the full SDK CI and
is merge ready.

## Design limitations and implementation order

1. Add a `Runic.Application.Desktop` adapter over `DesktopSurface`, with a
   generated-client browser bootstrap, per-window DI scope, content session,
   disconnect release, bounded close, and browser/embedded presentation checks.
   Desktop's removable capabilities should release routes with the View instead
   of retaining them to window close, as CS-WebUI must.
2. Consolidate package identities as above. Verify direct `dotnet build`, packed
   package consumers, all four templates, and Svelte/Angular imports. The
   generated client must remain ordinary TypeScript and host-neutral.
3. Define dynamic View selection: interface DataContext mappings, derived-model
   dispatch, ambiguity diagnostics, and routed collections. Keep selection
   explicit when more than one contract matches; test generated TypeScript and
   both framework outlets against the same rules.
4. Make generated-contract edits restart and reconnect automatically in the
   supported dev loop. Preserve existing in-process Hot Reload for safe method
   and getter edits; do not promise that .NET Hot Reload can change a generated
   contract in place.
5. The Translations Editor now routes named ReactiveUI commands through feature
   and document ViewModels. Typed results stay observable in their .NET owners;
   generated web clients carry correlated responses to the typed frontend
   bridge. The browser keeps ephemeral input and visual state. Session-backed
   saves retain revision checking and atomic writes.

Each slice uses focused checks while iterating. GitHub CI remains the full
merge gate. Native UI checks are scoped to affected host behavior.

## In-repo consumer migration inventory

| Consumer | Current state | Phase 2 disposition |
| --- | --- | --- |
| `examples/notes-reactive-views` | Already uses ReactiveUI ViewModels, routing, activation, and typed Views on CS-WebUI. | Make it the first Desktop adapter consumer; keep the existing CS-WebUI journey for host parity. |
| `examples/notes-view-first` | Toolkit MVVM composition and View location. | Retain as Toolkit coverage, and reuse its nested-content cases when extending ReactiveUI selection. |
| `examples/first-window` | Small Toolkit MVVM starter and package consumer. | Retain as the Toolkit entry point; add a parallel ReactiveUI starter or choose the Reactive Notes app for ReactiveUI guidance. |
| `apps/translations-editor` | Scoped ReactiveUI Window with routed feature and document ViewModels; compiler-backed session owns persisted data, while Svelte owns unsaved drafts and visual state. | Verify all feature owners in editor smoke and the document editing journey in the hosted browser, including the locally packed package consumer. |
| `examples/first-window-desktop` | ReactiveUI first window on the new Desktop adapter. | Exercise generated snapshot, command, browser reload, and scoped native close; extend to richer host capabilities after the first slice. |
| `tests/fixtures/desktop/samples/Runic.Desktop.Sample`, `tests/native/Runic.Desktop.WebViewSmoke`, and `tests/native/Runic.Desktop.Gtk4.Smoke` | Exercise low-level Desktop capabilities, native windows, dialogs, and accessibility. | Keep as low-level host tests; add separate Views-host integration journeys instead of replacing this coverage. |
| `tools/vscode-runic-translations` and `tools/visualstudio-runic-translations` | IDE-owned command UI and message preview over the shared translation language server. | Keep native IDE UI patterns; share the compiler/domain services with the editor. A Runic ReactiveUI ViewModel would be inappropriate inside the IDE host. |
| `packages/web/vite-plugin-runic` DevTools dock | Vite-owned development UI, fed by diagnostic and runtime state. | Keep Vite's dock and state interface; test it with a Views-hosted application. A desktop ViewModel would add an unnecessary runtime dependency to build tooling. |
| `docs` | SvelteKit documentation site with content navigation. | Keep web-native state and routing; update its examples and package catalog after the package cutover. It is not a Runic desktop app. |
| `examples/command-line`, `examples/dotnet/Runic.Administration.Console`, translation compiler/CLI | CLI programs with no GUI presentation. | Keep their existing architecture. |

This inventory covers every maintained in-repo GUI application, native UI
fixture, IDE preview, development dock, and documentation UI. The editor is the
principal application migrated to ReactiveUI. Reactive Notes remains the
reference for that pattern. The remaining GUI
tools are hosted by other frameworks or intentionally test lower-level APIs;
converting those would remove useful coverage or introduce a false desktop
dependency.
