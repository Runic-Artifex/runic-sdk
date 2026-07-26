# Frontend Todo findings

The React, Vue, Svelte, and Angular versions of SimpleTodo and AdvancedTodo are
an executable design probe for the browser-framework track. All eight variants
use the same C# model/ViewModel layer, native CsWebUi frame channel, protocol
contract, Bootstrap 5.3 baseline, and Font Awesome assets.

The initial probe found six cross-framework gaps. They are now implemented in
the shared runtime and samples. A second DX pass also removed handwritten C#
adapter registration and added typed framework-native reads; the remaining work
is higher-level generated framework façades and release hardening.

## Implemented product changes

### One generated, typed contract

`samples/Todo.Frontends/todo.frontend.json` is the single symbol model for the
two demos. The frontend SDK's contract tool emits the C# member vocabulary and
closed CommunityToolkit adapter factory used by `Todo.FrontendHost`, plus the
TypeScript contract used by every frontend. The workspace and MSBuild builds
verify generated-file drift before compiling.

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

### Frontend SDK and Vite builds

`WebUIToolkit.Frontend.Sdk` owns npm install/build/watch integration, generated
contract verification, bridge publication, and copying the produced asset
graph into build and publish output. Sample `.csproj` files select a workspace;
they do not contain custom npm or asset-copy targets.

All four samples use the same Vite build helper:

- development builds are readable and include source maps;
- production builds use minification and content-hashed filenames;
- Vite's manifest and `webuitoolkit.assets.json` are emitted together;
- the toolkit manifest records byte sizes and SHA-256 hashes; and
- stale hashed output is removed before the current graph is copied.

Svelte uses the official Vite plugin, a Svelte config with Vite preprocessing,
and `svelte-check`. This replaces the minimal custom compiler transform that
helped expose the original startup problem.

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

## Remaining framework-specific improvements

Generated handles now compose with each framework's native reactive primitive.
The next layer should generate an aggregate façade so application authors do
not have to wire each handle individually:

- React: named contract hooks plus result, error, cancellation, and transition
  composition around the existing typed command-state hook.
- Vue: a generated contract composable with effect-scope ownership, and
  ordinary `.vue` single-file-component authoring through the official Vite
  plugin.
- Svelte: named stores and Svelte 5 rune-friendly helpers around the existing
  typed derived readables.
- Angular: a generated injectable contract service and standalone provider.
  Release builds should move from the compact sample JIT entry to Angular's
  supported application builder, which encapsulates its production compiler
  and optimizer.

These are adapter ergonomics and compiler alignment, not protocol gaps. They
must preserve the one generated symbol model and framework-neutral runtime.

## Verification and remaining release gates

Each project provides managed smoke modes and real pinned-Chromium modes for
both Todo levels. `eng/verify-todo-frontends.ps1` runs the eight browser cases
serially and is included by `eng/verify.ps1` when the Nix-provided native
library and browser are available.

Every browser case starts the real CsWebUi server, loads the built framework
bundle, edits framework-rendered input, executes a typed C# command through the
binary channel, and verifies the framework-rendered result. Advanced cases
also start the asynchronous import, observe its pushed busy-state transition,
and verify the host-pushed imported tasks without polling.

Before the framework-alignment phase is release-complete, add full-trim
Native-AOT publication, reconnect, validation, cancellation, accessibility,
and leak coverage matching the native cwhtml track. Add deterministic
production-output checks from clean roots and freeze compressed-size budgets
only after a reference machine and measurement method are declared.
