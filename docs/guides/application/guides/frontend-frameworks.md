# Frontend framework integration

The React, Vue, Svelte, and Angular versions of SimpleTodo and AdvancedTodo are
an executable design probe for the browser-framework track. All eight variants
use the same C# model/ViewModel layer, native CsWebUi frame channel, protocol
contract, Bootstrap 5.3 baseline, and Font Awesome assets.

The initial probe found six cross-framework gaps. They are now implemented in
the shared runtime and samples. A second DX pass also removed handwritten C#
adapter registration and added typed framework-native reads. Native-AOT,
reconnect, validation, cancellation, accessibility, and leak gates now cover
all eight variants. Generated aggregate framework façades and package-release
alignment now sit on top of that shared contract.

## Implemented product changes

### One generated, typed contract

`samples/Todo.Frontends/todo.frontend.json` remains the JSON-first single
symbol model for the two shared reference demos. New framework templates use
the C#-first attributes described in
[Frontend contracts](./frontend-contracts.md). Both paths produce the same
canonical artifact consumed by the frontend SDK, and both emit the closed
CommunityToolkit adapter plus the TypeScript contract used by each frontend.

The model records each ViewModel source member, synchronous or asynchronous
command shape, validation participation, and source-generated `JsonTypeInfo`.
Generated C# binds properties, read-only properties, collections, validation,
and typed commands with direct lambdas. The native host now creates an adapter
with one generated `CreateAdapter(model)` call rather than repeating the whole
wire contract in registration code.

Generated TypeScript exposes:

- named writable and read-only property handles;
- typed collection handles;
- parameterless and typed-argument command handles;
- exact structural JSON types; and
- the contract name required during session open.

Frontend code no longer repeats numeric member identifiers or casts values out
of untyped maps. The framework-neutral handles remain usable from React hooks,
Vue computed refs, Svelte stores, Angular signals, or direct TypeScript.

### Typed framework-native reads

The framework packages accept generated handles directly while retaining their
numeric-ID APIs for compatibility:

- React hooks infer property, collection, command, and validation types;
- Vue exposes typed computed-ref helpers and injected composables;
- Svelte exposes lazy typed derived readables; and
- Angular signal accessors infer their result from the generated handle.

The Todo frontends exercise these APIs rather than reading raw projection maps.
Writes and command execution remain on the generated handles, preserving typed
arguments and results without framework-specific protocol code.

### Read-only and derived projections

The CommunityToolkit adapter now binds read-only properties directly. Derived
counts, import status, workflow state, diagnostics, and validation state can be
projected as their real JSON types instead of synthetic one-item collections.
The adapter observes property, collection, validation, and command-state
notifications and produces an authoritative projection transaction.

### Typed command parameters

Commands can declare their JSON argument type. Item actions now execute
`toggle(item.id)`, `remove(item.id)`, or `delete(item.id)` as one protocol
mutation. The browser no longer stages an ID in a writable `SelectedId`
property before executing a parameterless command.

### Ordered host push

An adapter may implement `IMvvmBindingChangeSource`. Its external
notifications are coalesced and serialized through the same per-session queue
as browser mutations. Each non-empty projection transaction advances the
session revision and is pushed through the existing CsWebUi binary binding.

AdvancedTodo's background import therefore updates all four frameworks without
a refresh command or application polling. If an unsolicited frame cannot be
delivered, the existing revision mismatch and snapshot recovery path remains
authoritative.

### Shared application bootstrap and readiness

`startMvvmApplication` owns transport creation, client/projection startup,
contract open, diagnostics, and disposal. The CsWebUi bridge exports
`waitForCsWebUiBinding`, so applications wait on one readiness promise rather
than implementing their own binding poll. Framework code now supplies only its
generated contract and adapts the accepted projection into native reactive
primitives.

The native owner is also exposed in ecosystem-native forms:

- `startReactMvvmApplication` owns the external store and provider lifetime;
- `startVueMvvmApplication` plus `createVueMvvmApplicationPlugin` owns the
  adapter and application unmount;
- `startSvelteMvvmApplication` plus the context helpers owns the readable store
  and component teardown; and
- `startAngularMvvmApplication` plus environment providers owns the signal
  store and application destruction.

Normal entrypoints now select a generated contract, mount a root, and register
that root's cleanup. They do not poll the bridge, create channels, recover
revisions, or reproduce reconnect logic.

### Frontend SDK and development servers

`WebUIToolkit.Frontend.Sdk` owns npm install/build/watch integration, generated
contract verification, bridge publication, and copying the produced asset
graph into build and publish output. Sample `.csproj` files select a workspace;
they do not contain custom npm or asset-copy targets.

Installs are cached by package manager and lock-file SHA-256 identity. The
coordinated development command restores a changed workspace automatically and
does not run a production asset build before starting Vite.

Start a new application through the local template pack:

```console
dotnet new webuitoolkit-react -n MyReactApp
cd MyReactApp
dotnet tool restore
dotnet webuitoolkit dev
```

The equivalent short names are `webuitoolkit-vue`, `webuitoolkit-svelte`, and
`webuitoolkit-angular`. Each generated project uses published package
references and carries its own local tool manifest and reproducible frontend
lock file.

The framework samples use the shared production build conventions:

- development builds are readable and include source maps;
- production builds use minification and content-hashed filenames;
- Vite's manifest and `webuitoolkit.assets.json` are emitted together;
- the toolkit manifest records byte sizes and SHA-256 hashes; and
- stale hashed output is removed before the current graph is copied.

Svelte uses the official Vite plugin, a Svelte config with Vite preprocessing,
and `svelte-check`. This replaces the minimal custom compiler transform that
helped expose the original startup problem.

Vue uses ordinary `.vue` single-file components through the official Vite
plugin. Angular production output uses the supported application builder and
its AOT compiler/optimizer rather than the earlier compact sample-only JIT
entry.

The Todo projects now use recognizable ecosystem structure rather than
protocol-probe entrypoints: React has focused features and custom hooks,
Svelte has separate Svelte 5 Simple/Advanced components with feature-owned
stores, Angular has standalone signal components and external templates, and
Vue retains its ordinary Simple/Advanced SFCs. Application roots select and
mount the generated owner; they do not contain feature presentation.

The coordinated development path uses real development servers for every
framework. React, Vue, and Svelte use Vite and their native HMR plugins;
Angular uses `ng serve` and the Angular application development builder. The
coordinator writes both `simple/index.html` and `advanced/index.html` native
bootstraps, retains `/webui.js` and MVVM commands on CsWebUi, and serves only
the framework asset graph from loopback HTTP. The .NET process, window,
document, and ViewModel therefore survive compatible component and style
updates.

`eng/verify-todo-frontend-hmr.ps1` proves that claim against pinned Chromium:
it creates native C# state, edits the shared CSS source, observes the
framework's live update, and requires the same ViewModel-backed Todo to remain
in the existing native document for React, Vue, Svelte, and Angular.

For debugging without HTTP controllers or Swagger, applications may opt into
the shared sanitized private-binding inspector. Generated contracts expose
member metadata so correlated operations can point back to their C# authoring
member without retaining arguments, values, validation text, or raw frames.
For presentation-only work,
`MvvmMockFrameChannel` drives the production client and generated framework
bindings from deterministic fixtures; it is a protocol mock, not a replacement
framework store. Generated React, Vue, Svelte, and Angular projects expose that
path as `cd Frontend && npm run dev:mock`; normal production builds use a
separate entry graph and template acceptance rejects leaked fixture code.
The reference SimpleTodo and AdvancedTodo applications now use one shared
fixture through each ecosystem's conventional `npm run dev:mock` command as
well. The fixture visibly marks the document, implements validation,
collections, filtering, commands, guided workflow, latency, reconnect, and
deterministic disposal, and the production-size gate rejects its marker from
native application entrypoints.
For generated C#-first starters, inspector member metadata also includes the
authoring file, line, and column. During `dotnet webuitoolkit dev`, the
coordinator injects a random loopback diagnostic endpoint: the native overlay
shows clickable/copyable source coordinates and the terminal prints the same
payload-free correlated event with a project-contained absolute path.
cwhtml deliberately has no frontend-only equivalent because its renderer and
actions are compiled C#—its retained native development host is the meaningful
fast loop. The checked-in parity policy and gate mapping live in
`eng/frontend-support-matrix.json`.

## What the implementation confirmed

- `MvvmProjection` is a credible common boundary. Framework adapters do not
  decode frames, own revision recovery, or reproduce command semantics.
- React external stores, Vue computed refs, Svelte readable stores, and Angular
  signals all update correctly from immutable accepted snapshots and pushed
  revisions.
- CommunityToolkit properties, collections, typed commands, asynchronous
  commands, validation, derived state, and host-originated changes cover
  realistic shared ViewModels.
- The C# application layer is genuinely reusable. The cwhtml applications and
  all four browser frameworks share Todo models, ViewModels, persistence,
  validation, and workflow code.
- One frontend project can contain both teaching levels without duplicating its
  native host.

## Generated framework façades

The frontend contract generator emits four optional framework views alongside
the direct TypeScript handles:

- React aggregate hooks with named values, validation, collections, and command
  façades;
- Vue contract composables whose command lifetimes follow the active effect
  scope;
- Svelte named store groups plus the Svelte 5-only
  `@webuitoolkit/mvvm-svelte/runes` getter adapter; and
- Angular injectable contract services with standalone provider helpers.

Every command façade presents the same idle/running/succeeded/failed/canceled
transition model, last result or error, cancellation state, and the host's
projected `canExecute`/running state. The underlying generated command handle
remains available, so direct TypeScript consumers do not have to adopt a
framework adapter.

## Verification and release alignment

Each project provides managed smoke modes and real pinned-Chromium modes for
both Todo levels. `eng/verify-todo-frontends.ps1` runs the eight browser cases
serially and is included by `eng/verify.ps1` when the Nix-provided native
library and browser are available.

Every browser case starts the real CsWebUi server, loads the built framework
bundle, edits framework-rendered input, executes a typed C# command through the
binary channel, and verifies the framework-rendered result. Advanced cases
also start the asynchronous import, observe its pushed busy-state transition,
and verify the host-pushed imported tasks without polling.

`eng/verify-todo-frontends-native-aot.ps1` full-trim Native-AOT-publishes all
four native hosts and runs both Todo levels. The shared gates cover
authoritative reconnect snapshots, validation, asynchronous cancellation,
accessibility structure, and managed/browser/process leak behavior.

G5 and G6 install packed SDK/adapter tarballs into isolated consumers at both
ends of every supported framework range. Those consumers compile and bundle
the command façades as well as the original primitive APIs.

`npm run verify:frontend-production` performs two clean production builds for
each Todo frontend, compares the complete asset graph by SHA-256, and enforces
the checked-in raw, gzip-9, and Brotli-11 entrypoint budgets. The runtime and
compression method are frozen in `eng/frontend-production-budgets.json`.
