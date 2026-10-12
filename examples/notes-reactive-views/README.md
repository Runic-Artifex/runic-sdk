# Reactive Notes with navigated Views

A notes application built with ReactiveUI commands and interactions on
`RunicNavigator` regions, through the `Runic.Navigation.ReactiveUI` adapter. Its
explicit [Window](NotesWindow.cs) and [Views](Views.cs) select the generated
client contracts; [ViewModels](ViewModels.cs) hold the navigation, and no
ViewModel takes a scheduler.

The window binds its ViewModels to its `IRunicModelContext` (namespace
`Runic.Navigation`) as it presents them. `AddRunicReactiveModelContext()` makes
ReactiveUI deliver command notifications on that context, so commands are
created without a scheduler argument. The bridge starts each command in a turn
of that context, and the async commands resume there after `await`, so they set
state directly without `InvokeAsync`.

## Navigation

- **Master/detail.** The shell's `Main` is a `NavigationRegion<IMainPage>` that
  starts at the window-scoped Home. **Document** pushes a fresh
  `DocumentViewModel` with `PushForResult<DocumentViewModel, string>()`; the
  navigator constructs it from the window's services and disposes it when the
  visit ends. Home shows the result: the saved title, or that the document was
  closed. **Home** is the region's `CreateBackCommand()`.
- **Entry context.** `DocumentViewModel` takes its `NavigationEntryContext` as
  a constructor parameter. `entry.CreateCompleteCommand(...)` is **Save and
  close**: it saves, then completes the request with the title.
  `entry.CreateCloseCommand()` is **Close**: a Back from the entry that its guard
  can veto. `entry.WhenNavigatedTo(...)` follows the draft's unsaved state while
  the document is current, and disposes that subscription when it is left.
- **Leave guard.** `ReactiveLeaveConfirmation.WithInteraction` asks through the
  document's `Interaction<DiscardNoteRequest, bool> ConfirmLeave` before unsaved
  edits are lost, and reverts them only when the departure commits. The web View
  answers it with `window.confirm` for its own **Close** and for the shell's
  **Home** (Back) in the same window. Navigation that .NET code starts, or a
  departure while no View is mounted, gets the ViewModel's fallback handler,
  which keeps the edits.
- **Child region.** The document owns a `NavigationRegion<IDocumentPane>` that
  swaps the borrowed, window-scoped Editor and Preview with `ReplaceAsync`. It
  closes with the document. Because the editor is borrowed, the draft and its
  Views' routes outlive each visit.

The generator presents each get-only region property as a content slot, so the
generated `main` and `currentPane` are nullable references.

## Commands, failures and activation

The editor's **Discard changes** command is a typed
`Interaction<DiscardNoteRequest, bool>`: a mounted browser confirms or
declines it, while the regular .NET fallback declines when no browser endpoint
is available. The browser smoke check covers both answers.
**Save** declares `[RunicFailure(typeof(SaveFailure))]` on `SaveCommand`, and
**Save and close** declares it on `SaveAndCloseCommand`: a missing or overlong
title is a `SaveFailure` that every frontend shows with `matchCase`
([save-failure.ts](Frontend/src/save-failure.ts)) instead of a rejected call.
A `ReactiveCommand` also reports that exception on `ThrownExceptions`, so each
ViewModel attaches `ObserveBridgeExceptions(logger)` to its commands and
disposes the subscriptions with the ViewModel.
The Svelte and Angular variants also mount a second full editor for the same
page. The main editor owns the confirmation handler; the mirrored editor keeps
its own mount and subscription without advertising a competing handler. This
leaves one eligible interaction destination in the browser connection.

The document presents the same Editor ViewModel in its pane and in a `compact`
View contract. Full and compact Views can be mounted together; leaving one does
not deactivate the ViewModel while the other remains. ReactiveUI activation
follows mounted Views, not the navigation entry: the editor's `WhenActivated`
counters, observable in the browser check, stop and restart as the document is
left and opened again. The frontend has plain TypeScript, Svelte, and Angular
variants, all using the same generated modules and the shared
`@runic-artifex/views` runtime; Svelte and Angular connect pages with
`useView` and `injectView()`.

```csharp
[RunicViewContract("compact")]
public sealed partial class CompactEditorView : ReactiveRunicView<EditorViewModel>;
```

This example uses a fixed map of View types known at generation time. It covers
interface-based content, polymorphic ViewModel collections, stable reorder, and
route removal and restoration. View types discovered only at runtime are not
part of that generated map.

## Build and check

From the repository root (prefix commands with `direnv exec .` on Linux with
Nix):

```sh
dotnet build examples/notes-reactive-views/NotesReactiveViews.csproj -c Release
node examples/notes-reactive-views/browser-smoke.mjs
RUNIC_VERIFY_PENDING_MOUNT=1 node examples/notes-reactive-views/browser-smoke.mjs
RUNIC_VERIFY_CLIENT_DISCONNECT=1 node examples/notes-reactive-views/browser-smoke.mjs
```

The default check also reads 300 large (over ~2 KB) Bridge replies and requires
each one to parse exactly. `RUNIC_VERIFY_CALL_BURST=1` also sends 2,000
concurrent Bridge calls and requires every reply. It takes about 80 seconds.

Build `Svelte` or `Angular` in this folder and set `RUNIC_WEB_ROOT` to the
resulting output directory to run the same browser check. Development server
HMR can be checked with `RUNIC_HMR_FRONTEND=svelte` or `angular` and
[hmr-smoke.mjs](hmr-smoke.mjs). `RUNIC_HMR_VIA_RUNIC_DEV=1` runs that check
through `runic-dev.mjs`, the SDK's internal runner for these multi-frontend
examples; applications use `dotnet runic dev` instead. The
[IDE host check](ide-host-smoke.mjs) covers development host startup and
shutdown. [dotnet-hot-reload-smoke.mjs](dotnet-hot-reload-smoke.mjs) runs the
host under `dotnet watch` and edits `ViewModels.cs`: Save's message by default,
or the Home greeting with `RUNIC_HOT_RELOAD_SCENARIO=getter`. The edit must apply
without restarting the View's activation. The check fails before it starts when
the line it edits is missing. The dev server proxies `/webui.js` from the native host.

This example builds the Runic packages from source. To copy it out, replace the
`ProjectReference` and `Import` lines in `NotesReactiveViews.csproj` with
`Runic.Application.Views.CsWebUi`, `Runic.Application.Views.ReactiveUI` and
`Runic.Navigation.ReactiveUI` package references, as described in the
[First Window README](../first-window/README.md#copy-it-into-your-own-project).
