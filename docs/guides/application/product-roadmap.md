# Product roadmap

This roadmap turns the accepted product direction in
[ADR 0012](./adr/0012-native-html-and-frontend-direction.md) into an ordered
delivery plan. The primary outcome is a pleasant, end-to-end path for building
CsWebUi applications and porting WPF applications.

Status labels describe the re-centered product path, not the earlier wave
numbering. Phases 0–2 and the binary FrameChannel slice are implemented. A
checked-in project or historical gate is not, by itself, evidence that the
remaining WPF, framework, or asset-package outcomes are complete.

## Product test

Every major investment should answer at least one of these questions:

1. Does this make a real CsWebUi application simpler to build, debug, package,
   or maintain?
2. Does this let an existing WPF ViewModel, collection, command, validation,
   navigation, dialog, resource, or window concept move to a browser frontend
   with less application-specific glue?

Work that answers neither question should normally live outside this
repository or wait until the primary path is complete.

## Phase 0: Narrow the repository — complete

- Create a durable archival branch from the last revision containing the full
  DependencyNotices subsystem.
- Remove DependencyNotices source, tests, package projects, fixtures, release
  gates, ownership entries, and planning links from the primary branch.
- Retain only notices and license material required to build or distribute the
  remaining repository.
- Rebaseline the solution, package matrix, verification scripts, and reference
  application around the actual WebUIToolkit product.

## Phase 1: Native compiled-HTML transport — complete

- Introduce a CsWebUi adapter package for compiled HTML and HTMX.
- Register one private native endpoint binding per window rather than one
  manually named binding per command.
- Define bounded request and response envelopes for method, route, ordered form
  values, revision, status, supported HTMX headers, and UTF-8 fragment content.
- Prototype the HTMX 2 browser bridge and choose between a toolkit-route XHR
  shim and an event-driven transport.
- Map HTMX response behavior including `HX-Trigger`, retarget, reswap, refresh,
  redirects where appropriate, focus, and revision advancement.
- Support cancellation and deterministic teardown when a fragment, view, or
  window closes.
- Use the native CsWebUi connection boundary rather than reproducing HTTP
  cookies, Origin checks, or CSRF inside the process.

This acceptance condition is now covered by SimpleTodo's
`--browser-smoke-test`: a persistent Nix-pinned Chromium loads the real CsWebUi
server and shipped browser bridge, submits the compiled form, executes C#, and
observes HTMX replace the declared target without an ASP.NET Core server.

## Phase 2: Compiled C# golden path — complete

- Make `.cwhtml` generation produce the complete adapter surface required by
  the application: typed ViewModel access, snapshots, subscriptions,
  source-generated JSON obligations where needed, and closed routes.
- Integrate CommunityToolkit.MVVM properties, commands, asynchronous commands,
  validation, and `INotifyCollectionChanged`.
- Convert SimpleTodo to `.cwhtml` and the native HTMX bridge.
- Remove its manually named WebUi callbacks and hand-written full-state JSON
  projection.
- Add an end-to-end test that launches the real native host, performs a
  browser-to-C# round trip, and verifies the resulting DOM.
- Publish and run the same application with Native AOT on a matching host.

`eng/verify-cswebui-native-e2e.ps1` supplies the release-facing acceptance
evidence. It Native-AOT-publishes and executes both the binary FrameChannel
host and SimpleTodo against the pinned browser, while preserving portable lock
files.

## Phase 3: cwhtml developer experience and frontend pipeline — in progress

Turn the proven native transport into an authoring experience that is
competitive with modern web tooling. The detailed design and acceptance
criteria live in the
[cwhtml development-experience plan](./cwhtml-development-experience.md).

- `WebUIToolkit.Frontend.Sdk` now owns Node workspace install/build/watch,
  generated-contract verification, native bridge assets, source maps,
  production minification/hashing, manifests, and build/publish output for the
  four framework Todo projects and both cwhtml Todo projects. The cwhtml
  projects use shared compiler targets and the same optional Vite boundary,
  with a Node-free configuration still supported.
- The first generated registration slice now emits action, field, command,
  fragment, focus, and success-event metadata from compiler-only cwhtml
  declarations. Both Todo levels use it to build initial, success, invalid, and
  recovery render plans; conversion and validation remain explicit,
  reflection-free completion points with per-action overrides.
- A high-level CsWebUi HTMX application builder now owns registry, runtime,
  opened-view, transport, and session lifetime for both Todo levels. The first
  `dotnet webuitoolkit dev` milestone is implemented: it discovers SDK
  configuration, generates/verifies contracts, performs the initial Vite and
  .NET build, supervises asset and `dotnet watch` processes, starts CsWebUi,
  reports stable diagnostics, observes manifest changes, and shuts the process
  tree down cleanly. Native-window CSS/JavaScript HMR is now implemented
  through a supervised loopback Vite server. The compiler's versioned,
  source-mapped diagnostics now flow into Vite's browser overlay and clear
  after recovery without losing ViewModel state; state-preserving cwhtml
  renderer replacement remains.
- Vite is now the first-class optional pipeline for TypeScript, JavaScript, CSS,
  Sass/PostCSS, images, fonts, source maps, hashing, and minification. It is a
  development/build tool, not an ASP.NET Core dependency or a replacement for
  the native HTMX transport. The cwhtml Todo workspace proves npm-managed
  HTMX, Bootstrap 5.3, and Font Awesome with local production output.
- Continue tiered feedback beyond the implemented Vite asset HMR:
  state-preserving fragment refresh for compatible cwhtml-only changes, and a
  coordinated application restart when a C# change cannot be replaced safely.
- **Implemented for build, terminal, and browser:** surface the same stable
  cwhtml diagnostics through MSBuild and Vite's overlay. Next, reuse that
  contract in language-server features for completion, navigation, rename,
  formatting, and generated-source inspection.
- Make `dotnet publish` consume Vite's production manifest, package only
  minified and content-hashed local assets into the application VFS, and leave
  no development-server URL or Node.js runtime dependency in the result.

Both applications now contain no custom cwhtml build targets or hand-written
native-HTMX descriptor plumbing and use the shared development command. Phase 3
still requires the production asset and reload acceptance criteria in the
detailed plan.

## Phase 4: WPF migration proof — in progress

- AdvancedTodo's source is converted to compiled views, the single native HTMX
  transport, generated registration, the high-level application lifetime, and
  reusable Flow presenters. Its managed self-test covers persistence, workflow
  navigation, validation, and cancellation; its real Chromium gate also runs
  from a full-trim Native-AOT executable.
- Demonstrate ViewModel-first navigation, typed dialogs, guarded close,
  validation summaries, collection updates, asynchronous cancellation,
  persistence, localization, and host push.
- Provide a WPF-to-WebUIToolkit mapping guide covering DataContext, bindings,
  commands, collections, converters, templates, resources, navigation,
  dialogs, dispatcher behavior, and window services.
- Add native capability services for focus, size and position, minimum size,
  minimize/maximize, JavaScript execution, profiles, multi-window ownership,
  file dialogs, clipboard, drag/drop, and other migration-critical desktop
  behavior as demand establishes their contracts.

## Phase 5: Customer-aligned sample design system — in progress

- SimpleTodo and AdvancedTodo use locally pinned Bootstrap 5.3 and Font Awesome
  assets.
- Reuse Bootstrap's layout, form, validation, navigation, modal, toast, and
  accessibility patterns before adding sample-specific CSS.
- Provide small reusable cwhtml components for common Bootstrap structures
  without making Bootstrap a dependency of MVVM, Flow, Hosting, or protocol
  packages.
- Keep semantic component and rendering boundaries open so applications can
  replace the default with shadcn, Tailwind, raw CSS, or another system.
- Add at least one deliberately non-Bootstrap styling example after the primary
  migration samples are complete, proving that the styling baseline is not a
  framework lock-in.

## Phase 6: Framework browser alignment — product gaps complete, release alignment future

- The production CsWebUi `FrameChannel`, its C# counterpart, and the
  framework-neutral `04-NativeMvvmCounter` sample are implemented. The native
  E2E gate Native-AOT-publishes the binary host and verifies a C#-driven DOM
  update in real Chromium.
- React, Vue, Svelte, and Angular now each render both Todo applications over
  one shared C# model/ViewModel layer and native host. The product gaps found by
  that executable probe are implemented: one generated C#/TypeScript contract,
  read-only projections, typed command parameters, ordered host push, shared
  readiness/bootstrap, and SDK-owned Vite development and production builds.
  The generated C# surface now includes the complete direct CommunityToolkit
  adapter factory, eliminating duplicated host registration. Framework
  packages accept generated handles as typed React hooks, Vue computed refs,
  Svelte derived stores, and Angular signals. The
  [framework findings](./frontend-todo-findings.md) record the resulting design
  and remaining framework-specific convenience work.
- All eight framework/Todo combinations now have persistent-browser acceptance
  coverage. Each gate edits framework-rendered state, executes the C# command
  over the production CsWebUi binary channel, and verifies the resulting DOM.
- Extend those gates with full-trim Native-AOT publication, reconnect,
  validation, cancellation, accessibility, and leak coverage matching the
  counter and cwhtml tracks.
- Generate aggregate, named React hooks, a Vue contract composable, Svelte
  stores/runes, and an Angular injectable service from the shared contract
  without forking its identifiers or JSON model. Add rich command lifecycle
  façades, move Vue to SFC authoring, and move Angular to its supported
  application builder for release builds.

## Phase 7: Asset package and organization work — future

This phase includes coordinated work in the cs-webui repository:

- move cs-webui under the WebUIToolkit organization;
- specify the responsibilities and compatibility boundary of an extracted
  WebUIToolkit-namespaced asset/VFS package;
- support local development directories and deterministic embedded assets;
- retain traversal-safe path handling, bounded archive loading, content types,
  caching, single-file publication, and Native-AOT compatibility;
- adapt CsWebUi to consume the extracted package;
- avoid coupling the static asset API to HTMX, MVVM, Bootstrap, or any browser
  framework.

The extraction should follow a dedicated ADR after the package boundary and
migration requirements are proven in at least the SimpleTodo and AdvancedTodo
applications.
