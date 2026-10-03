# Reactive Notes with routed Views

A notes application built with ReactiveUI routing, commands, and
interactions. Its explicit [Window](NotesWindow.cs) and [Views](Views.cs)
select the generated client contracts; [ViewModels](ViewModels.cs) use
ReactiveUI routing and commands.

The scoped application composition binds the whole routed ViewModel graph to
one `IRunicModelContext`. The editor captures state and commits it on that
lane around asynchronous work. Its **Discard changes** command is a typed
`Interaction<DiscardNoteRequest, bool>`: a mounted browser confirms or
declines it, while the regular .NET fallback declines when no browser endpoint
is available. The browser smoke check covers both answers.
The Svelte and Angular variants also mount a second full editor for the same
page. The main editor owns the confirmation handler; the mirrored editor keeps
its own mount and subscription without advertising a competing handler. This
leaves one eligible interaction destination in the browser connection.

The shell routes Home or Document. Document routes Editor or Preview. It also
presents the same Editor ViewModel in a `compact` View contract. Full and compact
Views can be mounted together; leaving one does not deactivate the ViewModel
while the other remains. The frontend has plain TypeScript, Svelte, and Angular
variants, all using the same generated modules and the shared
`@runic-artifex/views` runtime; Svelte and Angular connect pages with
`useView` and `injectView()`. The `WhenActivated` counters
are observable in the browser check.

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

Build `Svelte` or `Angular` in this folder and set `RUNIC_WEB_ROOT` to the
resulting output directory to run the same browser check. Development server
HMR can be checked with `RUNIC_HMR_FRONTEND=svelte` or `angular` and
[hmr-smoke.mjs](hmr-smoke.mjs). `RUNIC_HMR_VIA_RUNIC_DEV=1` runs that check
through `runic-dev.mjs`, the SDK's internal runner for these multi-frontend
examples; applications use `dotnet runic dev` instead. The
[IDE host check](ide-host-smoke.mjs) covers development host startup and
shutdown. The dev server proxies `/webui.js` from the native host.

This example builds the Runic packages from source. To copy it out, replace the
`ProjectReference` and `Import` lines in `NotesReactiveViews.csproj` with
`Runic.Application.CsWebUi` and `Runic.Application.ReactiveUI` package
references, as described in the
[First Window README](../first-window/README.md#copy-it-into-your-own-project).
