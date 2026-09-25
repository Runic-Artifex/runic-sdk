# Replace the preview Application Bridge with Views

Status: source integration and runnable SDK examples, 2026-09-25. This branch
is a replacement experiment, not a compatibility layer or a second public
application model. The existing preview packages are still in the current
shipping graph because their CLI, templates, host adapters, and tests have not
yet been moved. The cutover should remove them from that graph and delete their
obsolete source and generated fixtures in one reviewable change. Preview APIs
may break; no old Bridge behavior is a preservation requirement.

## Target developer experience

A first project declares a ViewModel and a partial
[`CounterWindow`](examples/first-window/CounterWindow.cs). It composes a window
through Microsoft DI and uses the generated
[ordinary TypeScript client](examples/first-window/Frontend/src/app.ts) in the
browser. It needs no `[RunicViewModel]`, route strings, transport setup, or
manual JSON code. The current native host is CS-WebUI; the logical View model
is host-neutral.

A larger app declares explicit Window and View classes. A View has a typed
`DataContext`; a View locator constructs one logical View for each mounted
presentation. The browser framework owns its visual tree and chooses a
component from the generated page kind. A ViewModel can survive View changes
and can be shown through two contracts at once. This resembles Avalonia's
Window/View, DataContext, ViewLocation, and routed content concepts while
leaving DOM ownership with the web framework. The bridge is the generated
code-behind boundary between the logical and visual trees.

The target package split is host-neutral `Runic.Application.Views`, a
build-time CommunityToolkit command inspector, a ReactiveUI integration, and
host adapters such as `CsWebUi` and its Microsoft DI window owner. The
unused CommunityToolkit runtime helper package from the prototype was left
out of this branch; generated Toolkit commands already work without it.
Svelte and Angular receive small
outlet helpers. The generated ES modules remain usable directly from ordinary
TypeScript. Effect is not required for the application API.

## Evidence in this branch

| Journey | Source | Checked behavior |
| --- | --- | --- |
| First window | [`examples/first-window`](examples/first-window/README.md) | Counter snapshot, command, writable property, reload in Chromium |
| Composed Toolkit Notes | [`examples/notes-view-first`](examples/notes-view-first/README.md) | Nested pane, independent sidebar, modal, two window scopes, DI and Splat view location, mount lifetime, TypeScript/Svelte/Angular browser journeys |
| Reactive Notes | [`examples/notes-reactive-views`](examples/notes-reactive-views/README.md) | Nested routing, alternate compact View of the same Editor ViewModel, shared activation, route cleanup, TypeScript/Svelte/Angular browser journeys, framework HMR and dev coordinator rebuild |

The source projects build together through [`RunicSdk.Views.slnx`](RunicSdk.Views.slnx).
The new examples reference only the new View packages and CS-WebUI, not
`Runic.Application.Bridge` or its old host packages. The imported source is
based on the measured `runic-next` prototype; the migration removed the
marked-ViewModel discovery fallback and endpoint-scoped `AttachView` helper
because neither belongs to the Window/View API. No bridge adapter was added.
The `OpenWindow<TWindow,TViewModel>` factory now constructs the application
Window with its scoped ViewModel before attaching the generated root Bridge;
the Window owns its host adapter lifetime.
The core and CS-WebUI/DI packages were packed at the SDK preview version; a
first-window consumer under `/tmp` restored those packages, generated its
contract, built, and passed the Chromium browser journey. This checks the
packaged build targets, but does not replace the full release matrix.

## Cutover sequence

1. **Finish Window ownership.** The new factory creates the application
   `RunicWindow<T>` before attachment and checks its scoped `DataContext`.
   Two-window isolation passes. Add a focused failed-construction check and
   settle how a custom Window closes and releases native resources.
2. **Select the public packages and build owner.** Replace the current
   `Runic.Application.Bridge` implementation and generated contracts with the
   View implementation. Move post-MVVM discovery, TypeScript generation, and
   frontend build coordination to one MSBuild owner; ensure `runic dev`, IDE
   builds, and direct `dotnet build` produce the same contract. Finish the
   package graph and release checks using the first-window package consumer
   as a starting gate. Do not publish old and new application bridges as
   parallel choices.
3. **Move templates and tooling.** Change `dotnet new`, `dotnet-runic`, their
   workspace metadata, and the IDE launch tasks to create and run a
   Window/View project. The first-window app is the concrete target for a
   starter template; the Notes apps are the pressure tests for real routing
   and framework outlets. Regenerate checked-in contracts from the new build.
4. **Replace host integrations.** The View core stays independent of hosts.
   Make the CS-WebUI adapter usable through the Window factory, then evaluate
   a Runic Desktop adapter against the same contract. Native dialogs, menus,
   tray icons, and close policy belong to the host/application layer, with
   explicit ownership and observability. CS-WebUI's inability to unregister
   native routes must be measured under distinct page identities and treated
   as a host limit, not hidden by the bridge.
5. **Remove the old application graph.** Delete old Bridge source generators,
   runtime, frontend tooling, obsolete examples/tests, package entries, CLI
   inspection paths, compatibility metadata, and old template assets once
   their replacement checks pass. Update `eng/workspace.json` and regenerate
   the shipping manifest. Keep shared lower-level platform and asset packages
   only when they have an independent purpose. No compatibility shims or
   automatic fallback to old contracts.
6. **Run release gates once against the selected graph.** Verify Toolkit and
   ReactiveUI build ordering, generated TypeScript and frontend builds, Linux
   and Windows IDE launch/HMR, framework reload and stale work cleanup,
   Native AOT, pack/restore, and the SDK's selected CI jobs. A generated
   contract edit still needs an automatic restart/reconnect path; in-process
   .NET Hot Reload covers supported method-body/getter edits only.

## Design decisions still open

- **View selection:** the current generator accepts fixed concrete View
  contracts. Interface based DataContext contracts, dynamic routed lists,
  derived model dispatch, and ambiguity diagnostics need a deliberate rule.
  DI constructs a selected View; it does not decide which View a region wants.
- **Write ordering:** frontend form helpers must flush pending field writes
  before Save. A command completion should not silently race UI input events.
  Keep the field-write receipt and conflict semantics visible in the typed
  client and test the edit-then-Save journey in every frontend.
- **Ownership and lifetime:** View disposal must follow the mounted
  presentation; ViewModel disposal must follow its DI/application scope. Two
  Views of one model must not detach each other. Closing a window should stop
  new commands, drain accepted work according to an explicit policy, and then
  release content and native resources.
- **Host capability:** a new desktop host may offer richer window, tray,
  dialog, and route lifecycle support than CS-WebUI. The View contract should
  express only guarantees both hosts can implement or expose host capability
  differences explicitly.

The replacement is ready to discuss through running projects. It is not yet
ready to replace the shipping manifest: final Window ownership, build owner,
full package graph, templates/CLI, host migration, and release checks above
remain required. Until that atomic cutover, this branch keeps new packages out
of the shipping list; it does not propose retaining the old Bridge after it.
