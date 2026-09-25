# Reactive Notes with routed Views

This SDK example exercises the replacement View bridge with ReactiveUI. It
depends on no old Application Bridge package. Its explicit
[Window](NotesWindow.cs) and [Views](Views.cs) select the generated bridge
contracts; [ViewModels](ViewModels.cs) use ReactiveUI routing and commands.

The shell routes Home or Document. Document routes Editor or Preview. It also
presents the same Editor ViewModel in a `compact` View contract. Full and compact
Views can be mounted together; leaving one does not deactivate the ViewModel
while the other remains. The frontend has plain TypeScript, Svelte, and Angular
variants, all using the same generated modules. The `WhenActivated` counters
are observable in the browser check.

```csharp
[RunicViewContract("compact")]
public sealed partial class CompactEditorView : ReactiveRunicView<EditorViewModel>;
```

Build and check from this SDK worktree:

```sh
direnv exec . dotnet build examples/notes-reactive-views/NotesReactiveViews.csproj -c Release
direnv exec . node examples/notes-reactive-views/browser-smoke.mjs
RUNIC_VERIFY_PENDING_MOUNT=1 direnv exec . node examples/notes-reactive-views/browser-smoke.mjs
RUNIC_VERIFY_CLIENT_DISCONNECT=1 direnv exec . node examples/notes-reactive-views/browser-smoke.mjs
```

Build `Svelte` or `Angular` in this folder and set `RUNIC_WEB_ROOT` to the
resulting output directory to run the same browser check. Development server
HMR can be checked with `RUNIC_HMR_FRONTEND=svelte` or `angular` and
[hmr-smoke.mjs](hmr-smoke.mjs); add `RUNIC_HMR_VIA_RUNIC_DEV=1` to check the
replacement [dev coordinator](../../tools/Runic.Application.Views.Dev/runic-dev.mjs).
The [IDE host check](ide-host-smoke.mjs) covers development host startup and
shutdown. The dev server proxies `/webui.js` from the native host.

This example deliberately tests a fixed View map. Dynamic View location from
a routed collection, interface based binding contracts, automatic restart when
a generated contract changes, and the final native Window close policy are
still open design work. See
[the cutover plan](../../VIEW-BRIDGE-CUTOVER.md).
