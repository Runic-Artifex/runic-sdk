# `@runic-artifex/svelte`

Svelte 5 Application Views helpers. `@runic-artifex/svelte` and
`@runic-artifex/svelte/views` export `useView`, `ViewOutlet`, `ViewRegistry`,
`useCommand` and `useCollectionViewport` for generated Window/View clients.

Generated View clients import `@runic-artifex/views`, which this package also
builds on, so a Views application installs both packages:

```sh
npm install @runic-artifex/svelte@preview @runic-artifex/views@preview
pnpm add @runic-artifex/svelte@preview @runic-artifex/views@preview
bun add @runic-artifex/svelte@preview @runic-artifex/views@preview
```

Previews are published under the `preview` dist-tag. Install the versions that
match your Runic SDK packages.

```svelte
<script lang="ts">
  import { useCommand, useView } from "@runic-artifex/svelte/views";
  import type { CounterPageReference } from "./generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
  const increment = useCommand(() => counter.client?.increment());
</script>

<button disabled={!counter.client || increment.pending} onclick={() => increment.run()}>
  {counter.state?.count ?? "…"}
</button>
{#if increment.error ?? counter.error}<p role="alert">{String(increment.error ?? counter.error)}</p>{/if}
```

## `useView(source)`

Call `useView` during component initialization. It connects a generated page
reference, or `{ connect: connect<Name> }` for a root ViewModel, while the
component is mounted and disposes it on destroy or when the getter returns a
reference with a different `connect` function. An already connected client is
observed and left to its owner. The result exposes reactive `state`, `client`,
`error` and `pending` properties and `retry()`.

## `ViewOutlet`

Renders the component registered for a generated reference's `kind` and passes
the reference as its `page` prop:

```svelte
<script lang="ts">
  import { useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/svelte/views";
  import { connectWorkspace, type WorkspaceState } from "./generated/workspace.js";
  import CounterPage from "./pages/CounterPage.svelte";
  import WelcomePage from "./pages/WelcomePage.svelte";

  const pages = { counter: CounterPage, welcome: WelcomePage } satisfies ViewRegistry<WorkspaceState["main"]>;
  const workspace = useView(() => ({ connect: connectWorkspace }));
</script>

<ViewOutlet content={workspace.state?.main} registry={pages}>
  {#snippet fallback()}<p>Connecting…</p>{/snippet}
</ViewOutlet>
```

The outlet is generic over the reference union, so the registry must have a
component for every kind with a matching `page` prop. A different reference
remounts the component, a kind without a component renders an alert, and the
optional `fallback` snippet renders while `content` is empty.

## `useCommand(command)`

Returns `{ run, pending, error, failure, reset }` with reactive `pending`, `error`
and `failure`.
`run(...args)` resolves to the command's result, or to `undefined` after a
failure, which `error` then holds until the next run. It never rejects, so
handlers need no `try`/`catch`. One command can serve several buttons:
`useCommand((name: "showWelcome" | "showCounter") => workspace.client?.[name]())`
and `navigate.run("showWelcome")`.

For a command that declares a failure (`[RunicFailure]` in .NET), `run` resolves the
`BridgeOutcome`, and `failure` holds the declared failure of the latest run, typed
from the outcome, while `error` keeps unexpected failures. Starting a run and
`reset()` clear both, and a run superseded by a later run or by `reset()` sets
neither.

## `useCollectionViewport(options)`

Follows a fixed-row-height scroll container and returns `{ viewport, attach }`.
`viewport` is the reactive [`collectionViewport`](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#collectionviewportoptions)
range; `attach` is an attachment for the container. Call it during component
initialization and send the range to the ViewModel from an effect:

```svelte
<script lang="ts">
  const rows = useView(() => page);
  const list = useCollectionViewport(() => ({ totalCount: rows.state?.totalCount ?? 0, rowHeight: 32 }));
  $effect(() => {
    const { start, size } = list.viewport;
    if (size !== 0) void rows.client?.setViewport({ start, size });
  });
</script>

<div {@attach list.attach} style="height: 480px; overflow: auto">
  <div style:height="{list.viewport.totalSize}px"><!-- rows.state.rows --></div>
</div>
```

Translation helpers moved to
[`@runic-artifex/translations-svelte`](https://github.com/Runic-Artifex/runic-translations-sdk/tree/main/packages/web/translations-svelte).
Use a prior SDK preview until application imports have been migrated.
