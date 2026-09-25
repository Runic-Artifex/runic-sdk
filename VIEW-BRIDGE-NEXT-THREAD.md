# Handoff for the Application Bridge removal thread

Use this file as the starting brief for a fresh thread. Read
[VIEW-BRIDGE-CUTOVER.md](VIEW-BRIDGE-CUTOVER.md) for the design and evidence.
The current implementation is on branch `codex/public-notes-consumer` in
`.worktrees/sdk-public-notes-consumer`, based on reviewed SDK Slice4 with
implementation commit `6af22967`. The worktree was clean at handoff. The separate
`runic-sdk` checkout contains a user-edited `AGENTS.md`; preserve it.

## Non-negotiable direction from the user

Replace the old Application Bridge. Do not retain an old bridge, compatibility
shim, automatic fallback, or two shipping application models for preview users.
Breaking changes are acceptable before v1.0. Keep the API close to the
`runic-next` Window/View prototype and its typed ordinary TypeScript clients.
The native host is presently CS-WebUI while host capabilities are evaluated.

The next thread should act as an orchestrator and delegate implementation
streams to Luna 6 xHigh and Sol 6 High agents. Assign bounded file ownership
or separate worktrees so parallel edits do not collide. Integrate the streams,
run the required checks, and follow build errors until the old Bridge graph is
gone. Do this in the new thread as the user requested.

## Starting graph

- `RunicSdk.Views.slnx` builds the new core, adapters, generator, and three
  examples. `OpenWindow<TWindow,TViewModel>` constructs the application Window
  before root Bridge attachment.
- `examples/first-window`: minimal Toolkit/TypeScript journey plus
  `package-smoke.mjs`, which packs core and CS-WebUI packages, restores a
  temporary external consumer, generates its contract, and runs Chromium.
- `examples/notes-view-first`: Toolkit Notes with nested panes, independent
  sidebar, modal, DI/Splat view location, two-window scope check, plain TS,
  Svelte, and Angular consumers.
- `examples/notes-reactive-views`: ReactiveUI routing, two simultaneous View
  contracts for one Editor ViewModel, activation lifetime, plain TS, Svelte,
  Angular, HMR and IDE host probes.
- `tools/Runic.Application.Views.Dev/runic-dev.mjs` coordinates frontend HMR
  and backend rebuild for the replacement examples.
- The new graph has no source reference to `Runic.Application.Bridge`, no
  `[RunicViewModel]` discovery fallback, and no endpoint-scoped `AttachView`.
  The Toolkit command inspector is build-time only; an unused Toolkit runtime
  package from the prototype was intentionally excluded.

## Checks completed on this branch

- `direnv exec . dotnet build RunicSdk.Views.slnx -c Release -m:1 -v:q`:
  zero warnings and errors.
- `direnv exec . node examples/first-window/package-smoke.mjs`:
  `FIRST_WINDOW_PACKAGE_OK|pack|restore|generate|browser`.
- Plain TypeScript browser journeys: `FIRST_WINDOW_OK`,
  `NOTES_COMPOSED_OK`, and `REACTIVE_NOTES_BROWSER_OK` after the Window factory
  change. Notes two-window check: `NOTES_WINDOWS_OK`.
- Svelte and Angular built and passed both Notes browser journeys before the
  Window factory change; the shared backend plain TypeScript journey passed
  after it. Reactive Svelte dev coordinator HMR passed again after the change:
  `SVELTE_REACTIVE_RUNIC_DEV_OK|framework-hmr|backend-rebuild|browser-reload`.
- Reactive Svelte/Angular direct HMR and coordinator HMR, plus IDE host smoke,
  passed earlier in this branch. The full old SDK release matrix, Native AOT,
  Windows GUI, and package graph beyond the first-window subset are not yet
  revalidated against this cutover.

## Removal trail

`eng/workspace.json` and generated `eng/build/shipping-projects.props` still
select `Runic.Application.Bridge` and old host packages. The old graph also
reaches `Runic.Application.Desktop`, `Hosting`, `CsWebUi`, `Platform`,
`dotnet-runic`, template content, old frontend packages, and migration
examples/tests. Replace these with the new Window/View graph and then delete
the obsolete source, generated fixtures, and metadata. Keep unrelated
platform, asset, translation, and command-line packages when independently
useful. Regenerate the shipping manifest from workspace metadata rather than
editing the generated file alone.

Run `direnv status` and read `.envrc`/`flake.nix` before builds. Use the locked
shell through `direnv exec .`, preserve stable caches, check disk before full
matrices, and run focused checks before a single required full verification.
Do not merge a half-cutover to a shipping branch. The cutover document lists
remaining design and release gates; treat them as concrete work, not reasons to
add compatibility paths.
