# `@runic-artifex/svelte`

Svelte 5 Application Views helpers. `@runic-artifex/svelte` exports `useView`,
`ViewOutlet`, `ViewRegistry`, `useCommand`, `useOperation`, `useLatestOperation`
and `useCollectionViewport` for generated Window/View clients. The
`@runic-artifex/svelte/views` entry of earlier previews and the
`@runic-artifex/svelte/experimental` entry of 0.9 are gone: import from
`@runic-artifex/svelte`.

Generated View clients import `@runic-artifex/views`, which this package also
builds on, so a Views application installs both packages:

```sh
npm install @runic-artifex/svelte@preview @runic-artifex/views@preview
pnpm add @runic-artifex/svelte@preview @runic-artifex/views@preview
bun add @runic-artifex/svelte@preview @runic-artifex/views@preview
```

Previews are published under the `preview` dist-tag. Install the versions that
match your Runic SDK packages. Svelte 5.57.1 or a later 5.x release is supported,
the same floor as SvelteKit 3 and `@runic-artifex/sveltekit`.

```svelte
<script lang="ts">
  import { useCommand, useView } from "@runic-artifex/svelte";
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

## `useView(source, options?)`

Call `useView` during component initialization. `options.release(client)`
replaces `client.dispose()` for a client the helper connected. It connects a generated page
reference, or `{ connect: connect<Name> }` for a root ViewModel, while the
component is mounted. An already connected client is observed and left to its
owner. The result exposes reactive `state`, `client`, `error` and `pending`
properties and `retry()`.

When the component is destroyed or the getter returns a reference with a
different `connect` function, the client stays connected for
`options.keepAlive` milliseconds (2000 by default), and the next `useView` of
the same reference takes it over with its state, before its first effect runs:
an `{#if}` that toggles, HMR and A→B→A navigation show the View at once instead
of "Connecting…" and do not open the route again. `keepAlive: 0` disposes at
once.

### Awaiting the connection: `useSuspenseView(source, options?)` and `{#await}`

`useSuspenseView(page)` connects like `useView` during component
initialization and returns a promise that resolves once the View connected,
with reactive `state` and `client` that are never `undefined`, or rejects with
the failure. Await it with `{#await}`:

```svelte
<script lang="ts">
  import { describeBridgeError } from "@runic-artifex/views";
  import { useSuspenseView } from "@runic-artifex/svelte";
  import type { CounterPageReference } from "../generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useSuspenseView(page);
</script>

{#await counter}
  <p>Connecting…</p>
{:then view}
  <button onclick={() => view.client.increment()}>{view.state.count}</button>
{:catch error}
  <p role="alert">{describeBridgeError(error)}</p>
{/await}
```

The source is read once: wrap the component in `{#key page}` to follow another
reference. Do not `{#await page.connect()}` directly: nothing would dispose
that client. In SvelteKit, connect in a `load` function instead
([`@runic-artifex/sveltekit`](../sveltekit/README.md#connecting-in-load)).

### Selecting state: `$derived`

The handle's `state` changes with every change of the View. A `$derived` that
selects a member updates its dependents only when its value changed, so markup
or an `$effect` that reads `title` does not run again when another member
changes:

```ts
const editor = useView(() => page);
const title = $derived(editor.state?.title);
```

Members the View did not change keep their identity, so selecting a member,
also an object or a list, is enough. A derived value that builds a new object
each time, such as `items.filter(…)`, changes every time.

### Read-only state and `bind:value`

As in any MVVM View, the ViewModel's C# setters decide what the page may set.
Read state one way from `state`. A property with a public C# setter also gets a
`set<Property>` client method; pass a getter and setter pair to `bind:value`
(Svelte 5.9 or later) to bind it two way. Status with a `private set`, such as
`IsDirty` or `Error`, has no setter and is only rendered:

```svelte
<script lang="ts">
  // C#: public partial int Count { get; private set; }  public partial int Step { get; set; }
  import { useCommand, useView } from "@runic-artifex/svelte";
  import type { CounterPageReference } from "./generated/counter.js";

  let { page }: { page: CounterPageReference } = $props();
  const counter = useView(() => page);
  const setStep = useCommand((step: number) => counter.client?.setStep(step));
</script>

<p>{counter.state?.count ?? "…"}</p>
<select bind:value={() => counter.state?.step ?? 1, step => setStep.run(step)}>
  <option value={1}>1</option>
  <option value={5}>5</option>
</select>
```

### Fields: `useField(view, field, options?)`

Call during component initialization to bind an element to a member. Pass the generated `{View}Fields` descriptor; it
knows the member's kind, setter, checked write and validation. `{@attach field.attach}` wires the element:

```svelte
<script lang="ts">
  import { useField, useView } from "@runic-artifex/svelte";
  import { EditorFields, type EditorPageReference } from "./generated/editor.js";

  let { page }: { page: EditorPageReference } = $props();
  const editor = useView(() => page);
  const title = useField(() => editor, EditorFields.title, { commit: "pause", checked: true });
  const step = useField(() => editor, EditorFields.step);
  const done = useField(() => editor, EditorFields.done);
</script>

<input {@attach title.attach}>
{#each title.errors as message}<p role="alert">{message}</p>{/each}
{#if title.conflict}
  <p role="alert">
    Changed elsewhere to “{title.conflict.value}”.
    <button onclick={title.retry}>Keep mine</button> <button onclick={title.reset}>Take theirs</button>
  </p>
{/if}
<input type="number" {@attach step.attach}>
<input type="checkbox" {@attach done.attach}>
```

The attachment writes what the user types (`input`; `change` of a `<select>` or checkbox), writes nothing during IME
composition, commits on blur, keeps the element's text (a checkbox's `checked`) equal to the field's without moving
the caret, and sets `aria-invalid` while the field has `errors` or an `error`. Number inputs keep `-` and `1.` while
the user types: a text that does not parse stays the draft with an `invalid-text` error and nothing is written. The
handle also has `value` and `text`, which `bind:value={title.value}` reads and sets, the reactive `editing`, `error`,
`errors` and `conflict`, and `input(value)`, `commit()`, `retry()` and `reset()`.

`options` are read once, when the field is created:

- `commit: "pause"` writes the draft after `delay` milliseconds without input (700 by default) and at once on
  `commit()`, which blur calls; use it where each write is an undo step or starts work in .NET. The default,
  `commit: "input"`, writes each input. `commit()` resolves with the field's state once its writes settled, so a
  command that depends on the field can follow them and skip a failed write:
  `if ((await title.commit()).error === undefined) await editor.save()`.
- `checked: true` writes with the member's checked write, so a change by another writer while the user edits is a
  conflict instead of being overwritten. A `rejected` receipt keeps the draft and sets `error` to a `FieldError` with
  the setter's message; a `conflict` keeps the draft, sets `conflict` to the producer's `{ value, version }` and
  writes nothing until `retry()` writes the draft over it or `reset()` takes it.
- `coalesce` and `keepDraftOnError` as for text inputs below.

`errors` holds the member's validation messages from the descriptor (`[]` when valid), `error` why the latest write
or text failed (a `BridgeError` or a `FieldError`, whose `kind` is `rejected`, `conflict`, `committed-with-error` or
`invalid-text`). `FieldError`, `FieldOptions`, `FieldState` and `BridgeField` are exported from this package. See the
[field controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#fields) for the receipts
and text forms.

### Text inputs: `useTextField(view, read, write, options?)`

Call during component initialization to bind a text input to a member through the
framework-neutral
[text field controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#text-inputs):

```svelte
<script lang="ts">
  import { useTextField, useView } from "@runic-artifex/svelte";
  import type { PersonPageReference } from "./generated/person.js";

  let { page }: { page: PersonPageReference } = $props();
  const person = useView(() => page);
  const name = useTextField(() => person, state => state.name, (client, value) => client.setName(value));
</script>

<input bind:value={name.value} oncompositionstart={name.compositionStart} oncompositionend={name.compositionEnd}>
{#if name.error !== undefined}<p role="alert">{String(name.error)}</p>{/if}
```

It returns `value`, which `bind:value` reads and sets, the reactive `editing` and
`error`, and `compositionStart()`, `compositionEnd(event | text)` and `reset()`.
`view` returns a `useView` handle or any `{ state, client }`. The options are
`coalesce` and `keepDraftOnError`.
The field shows what the user typed while its writes are in flight, so a reply or
frame that answers an older keystroke never replaces newer text. At most one write is
in flight; text typed meanwhile is written next as one value (`coalesce: false` writes
every change). Nothing is written during IME composition. Once the last write settled,
the field shows the View's value again, including any normalization the ViewModel
applied (trimmed, upper-cased). `write` may also be a command or a checked write,
whose receipt the field reads: `rejected` and `conflict` keep the draft and set `error`
to a `FieldError` of that kind, `committed-with-error` drops the draft and sets it too,
and `applied.validation` is `errors`. A failed write keeps the draft and sets `error`
(`keepDraftOnError: false` shows the View's value instead); `reset()` drops the draft. `editing` is true while a draft
exists. Don't disable the input on the View's busy state (`isSaving`, `can…`) while
the user types: the field's own write causes it, and keys typed meanwhile would be
lost. Test such fields by typing key by key (Playwright `pressSequentially`, Testing
Library `user.type`) against the mock with `latency: "slow"`; `fill()` sets the whole
text at once and hides the race.

## `ViewOutlet`

Renders the component registered for a generated reference's `kind` and passes
the reference as its `page` prop:

```svelte
<script lang="ts">
  import { useView, ViewOutlet, type ViewRegistry } from "@runic-artifex/svelte";
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
remounts the component, a kind without a component renders an English
`<p role="alert">`, and the optional `fallback` snippet renders while `content` is empty.

The optional `missing` snippet replaces that alert, for example with localized
text. It receives the reference whose kind has no component, and its markup
(including any `role`) is yours:

```svelte
<ViewOutlet content={workspace.state?.main} registry={pages}>
  {#snippet missing(page)}<p role="alert">{t("missingView", { kind: page.kind })}</p>{/snippet}
</ViewOutlet>
```

## `useCommand(command)`

Returns `{ run, pending, error, failure, reset }` with reactive `pending`, `error`
and `failure`.
`run(...args)` resolves to the command's result, or to `undefined` after a
failure, which `error` then holds until the next run. It never rejects, so
handlers need no `try`/`catch`. One command can serve several buttons:
`useCommand((name: "showWelcome" | "showCounter") => workspace.client?.[name]())`
and `navigate.run("showWelcome")`. Called during component initialization, it
stops publishing state when the component is destroyed, even before its first
effect flush; a run in flight is not cancelled and its promise still settles.

`pending` covers only this component's run. Whether .NET can run the command is
the View's state: a command without an argument publishes `can<Name>` (such as
`state.canSave`), so disable a button while either says no. A command with an
argument has the `can<Name>(input)` query on the client instead.

For a command that declares a failure (`[RunicFailure]` in .NET), `run` resolves the
`BridgeOutcome`, and `failure` holds the declared failure of the latest run, typed
from the outcome, while `error` keeps unexpected failures. Starting a run and
`reset()` clear both, and a run superseded by a later run or by `reset()` sets
neither.

## `useOperation(start, options?)`

Call during component initialization to observe a generated operation's admission,
pending state, progress, terminal outcome and cancellation:

```svelte
<script lang="ts">
  import { useOperation, useView } from "@runic-artifex/svelte";
  import { connectWorkspace } from "./generated/workspace.js";

  const workspace = useView(() => ({ connect: connectWorkspace }));
  const push = useOperation((remote: string) => workspace.client?.startPush(remote));
</script>

<button disabled={!workspace.client || push.pending} onclick={() => push.run("origin")}>Push</button>
{#if push.pending}
  <progress value={push.progress}></progress>
  <button disabled={push.cancellationRequested} onclick={() => push.cancel()}>Cancel</button>
{/if}
{#if push.cancelled}<p role="status">Push cancelled.</p>{/if}
{#if push.error ?? push.cancelError}<p role="alert">{String(push.error ?? push.cancelError)}</p>{/if}
```

The result exposes reactive `pending`, `admitting`, `operation`, `status`, `progress`, `outcome`,
`failure`, `error`, `cancelled`, `cancellationRequested`, `cancelling`, `cancellation`
and `cancelError`, with `run(...args)`, `cancel()` and `reset()`. `progress` is how far
the running operation got, from 0 to 1, when its command reports progress
(`RunicOperationProgress.Report` in .NET); it is `undefined` while unknown and after the run. Both async methods never
reject. A run that ends with a `cancelled` status sets `cancelled` and leaves `error`
undefined; `failed` and `timed-out` statuses are still `error`. Cancel before a delayed Start receipt is retained for that captured run;
its response does not release pending state. `reset()` clears feedback while
preserving busy state and cancellation identity. Component destruction settles UI
observation promises and detaches feedback without cancelling accepted mutations.
The host still owns accepted-work completion and shutdown drain.

The optional typed `cancel(operation)` callback can use a short application control
command. `waitForCompletion(operation, terminalStatus)` can await application drain
or command availability that outlives the invocation wrapper. By default the helper
uses the operation's public `wait()` and `outcome()`; the desktop transport keeps
Cancel and other callbacks responsive during observation.

## `useLatestOperation()`

For rapid read selection, `useLatestOperation()` wraps the framework-neutral
[latest-operation controller](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#operation-feedback-and-latest-selection)
for the component's lifetime. It coalesces the latest intent across delayed receipts
and orders the next admission after terminal observation and any application drain
barrier:

```svelte
<script lang="ts">
  import { useLatestOperation, useView } from "@runic-artifex/svelte";
  import { connectCatalog } from "./generated/catalog.js";

  const catalog = useView(() => ({ connect: connectCatalog }));
  const load = useLatestOperation<Item>();
  function select(id: string) {
    const client = catalog.client;
    if (client) void load.run({ start: () => client.startLoad(id), isCurrent: () => catalog.client === client });
  }
</script>

{#if load.outcome?.ok}<ItemView item={load.outcome.value} />{/if}
{#if load.blocked}<p role="alert">Selection completion is uncertain. Reconnect or replace the session before retrying.</p>{/if}
```

The result has the `useOperation` properties plus `blocked`, with `run(intent)` and
`clear(mode?)`, which never reject. Capture the original client/session in each intent
(`start`, and optionally `cancel`, `waitForCompletion` and `isCurrent`).
`clear()` drops queued input and cancels the captured read; `clear("replace")`
detaches it without cancellation and permits fresh admission. The application must
establish that replacement boundary and own the old work's drain. Lost terminal
observation blocks further Starts instead of retrying blindly, with its reason in
`error`; ordinary Cancel does not clear this block. Component destruction settles
observation without cancelling accepted work. The
[operations package consumer](https://github.com/Runic-Artifex/runic-sdk/tree/main/tests/fixtures/application/operations-consumer)
runs this hook against the packed packages and the desktop transport.

## `useBridgeConnection(options?)`

Follows the page's connection to its .NET host. It wraps `onBridgeConnection`
from `@runic-artifex/views` (re-exported here since 0.11, with the
`BridgeConnectionState` and `BridgeConnectionOptions` types) and returns `{ state }`, a reactive
`BridgeConnectionState`: `connecting`, `connected`, `reconnecting`, `lost` or `closed` (the
application closed the window, since 0.15; a closed set, see
[Connection state](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#connection-state)):

```svelte
<script lang="ts">
  import { useBridgeConnection } from "@runic-artifex/svelte";

  const connection = useBridgeConnection({ lostAfter: 5_000 });
</script>

{#if connection.state === "lost"}<p role="alert">The application stopped responding.</p>{/if}
```

Call it during component initialization (or in `$effect.root`). `state` holds
the current state at once; observing starts with the owner's effects, so a
server render does not observe, and destroying the owner unsubscribes. An invalid
`lostAfter` throws a `RangeError`.

## `useMenuBar(options?)`

Follows the declared menu bar of a Runic Desktop window that leaves it to the
page (`DesktopMenuBarPresentation.Page`) while its owner lives. It wraps
`createMenuBarController` from `@runic-artifex/views` (see
[In-page menu bar](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#in-page-menu-bar)) and returns `{ state, invoke, handleKeyDown }`, where `state` is a
reactive `MenuBarState` property. The bar is rendered by the menubar component
of the application's own UI library. Runic ships no menubar component.

```svelte
<script lang="ts">
  import { useMenuBar } from "@runic-artifex/svelte";

  let open = $state("");
  const menuBar = useMenuBar({
    onRequest: request => { if (request.kind === "open") open = request.menu.key; else focusFirstTrigger(); },
  });
</script>

<!-- On macOS the application menu bar keeps the menus ("global"): render nothing. -->
{#if menuBar.state.placement === "page"}<!-- the menubar component; an item's select handler calls menuBar.invoke(item) -->{/if}
```

Options are those of `createMenuBarController` (`shortcuts`, `keyboard`)
without `signal`, plus `onRequest`, which receives the `focus` and `open`
requests. Call it during component initialization (or in `$effect.root`). The
controller starts with the owner's effects, so server rendering creates none
and `state` stays `connecting`; `invoke` rejects with a `BridgeError`
(`disconnected`) until then. Owner destruction disposes it, which also returns
the `shortcuts: "app"` lease.

### With Bits UI

Bits UI's `Menubar.Root bind:value`; the
[Notes example](https://github.com/Runic-Artifex/runic-sdk/tree/main/examples/notes-view-first#in-page-menu-bar) adds submenus, check and radio
items and is tested with real keys:

```svelte
<script lang="ts">
  import { Menubar } from "bits-ui";
  import { splitAccessKey, type MenuBarItem, type MenuBarMenu } from "@runic-artifex/views";
  import { useMenuBar } from "@runic-artifex/svelte";

  let bar = $state<HTMLElement | null>(null);
  let open = $state("");
  const menuBar = useMenuBar({
    onRequest: request => {
      if (request.kind === "open") open = request.menu.key;
      else bar?.querySelector<HTMLElement>("[role=menuitem]")?.focus();
    },
  });
</script>

{#snippet label(entry: MenuBarMenu | MenuBarItem)}
  {@const [before, key, after] = splitAccessKey(entry)}
  {before}{#if menuBar.state.accessKeyHints}<u>{key}</u>{:else}{key}{/if}{after}
{/snippet}

{#if menuBar.state.placement === "page"}
  <Menubar.Root aria-label="Application" bind:ref={bar} bind:value={open}>
    {#each menuBar.state.menus as menu (menu.key)}
      <Menubar.Menu value={menu.key}>
        <Menubar.Trigger disabled={!menu.enabled}>{@render label(menu)}</Menubar.Trigger>
        <Menubar.Portal>
          <Menubar.Content align="start">
            {#each menu.entries as entry (entry.key)}
              {#if entry.kind === "separator"}<Menubar.Separator />
              {:else if entry.kind === "item"}
                <Menubar.Item disabled={!entry.enabled} aria-keyshortcuts={entry.ariaKeyShortcuts} onSelect={() => menuBar.invoke(entry)}>
                  {@render label(entry)} <kbd aria-hidden="true">{entry.shortcutText}</kbd>
                </Menubar.Item>
              {/if}<!-- a submenu: Menubar.Sub; check and radio items: Menubar.CheckboxItem, Menubar.RadioGroup -->
            {/each}
          </Menubar.Content>
        </Menubar.Portal>
      </Menubar.Menu>
    {/each}
  </Menubar.Root>
{/if}
```

The host owns `checked`: bind it one way, as
`bind:checked={() => item.checked ?? false, () => {}}`.

## Errors

The `error` of `useView` and of a command or operation helper is typed
`unknown`: it holds whatever the connection or the call rejected with. A Bridge
failure is a `BridgeError` from `@runic-artifex/views`. Show it with
`describeBridgeError(error)`, one English sentence, instead of `String(error)`,
and narrow it with `isBridgeError` (or `instanceof`) before reading `kind`,
`route` or `detail`:

```ts
import { describeBridgeError, isBridgeError } from "@runic-artifex/views";

const offline = $derived(isBridgeError(view.error, "disconnected"));
const message = $derived(describeBridgeError(view.error));
```

`instanceof` holds for errors from every copy of the package on the page.
[Errors and diagnostics](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#errors-and-diagnostics)
lists the kinds. Any other value, such as an exception that your own connect
function or command callback throws, arrives unchanged.

## Interactions

A ReactiveUI `Interaction<TInput, TOutput>` on the ViewModel, such as the
question of a `ReactiveLeaveConfirmation.WithInteraction` leave guard, is
`client.interactions.<name>` on the generated client. Register the page's answer
in `$effect`: `handle((prompt, { signal }) => …)` returns the function that
removes it, so return that from the effect, and Svelte removes the handler when
the client changes or the component is destroyed:

```svelte
<script lang="ts">
  import { useView } from "@runic-artifex/svelte";
  import type { EditPageReference } from "./generated/edit.js";

  let { page }: { page: EditPageReference } = $props();
  const view = useView(() => page);
  $effect(() => view.client?.interactions.confirmDiscard.handle(
    async (prompt, { signal }) => confirmDialog(`Discard the changes to ${prompt.itemName}?`, signal),
  ));
</script>
```

`prompt` and the answer are typed from the ViewModel's interaction. The handler
may return the answer or a promise of it; `confirmDialog` stands for the
application's own dialog.

- The page is asked for a departure that its own window starts: its Close, the
  shell's Back in the same window, or a guarded window close.
- `signal` aborts when .NET no longer needs the answer, for example when the
  departure is superseded or the window closes. Close the dialog then; a late
  answer is ignored.
- The handler exists only after the client connected and the effect ran. A
  departure before that is answered by the ViewModel's .NET fallback handler, or
  fails closed without one.

The [ReactiveUI leave confirmation](https://github.com/Runic-Artifex/runic-sdk/blob/main/packages/dotnet/Runic.Application.Views.ReactiveUI/README.md#leave-confirmation-in-a-web-view)
has the .NET side and which View answers which departure. In a frontend test,
the generated mock's `interactions.<name>.request(input)` resolves with the
registered handler's answer; see the [Testing guide](https://github.com/Runic-Artifex/runic-sdk/tree/main/packages/web/views#testing).

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
[`@runic-artifex/translations-svelte`](https://www.npmjs.com/package/@runic-artifex/translations-svelte).
Import them from there instead of `@runic-artifex/svelte`.
