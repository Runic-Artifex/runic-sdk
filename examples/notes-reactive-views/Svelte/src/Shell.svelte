<script lang="ts">
  import type { ShellClient, ShellState } from "../../Frontend/src/generated/shell.js";
  import { useView } from "../../../../packages/web/svelte/src/views/use-view.svelte.js";
  import { useCommand } from "../../../../packages/web/svelte/src/views/use-command.svelte.js";
  import Home from "./Home.svelte";
  import Document from "./Document.svelte";
  import PinnedNote from "./PinnedNote.svelte";
  import PinnedTask from "./PinnedTask.svelte";
  import ViewOutlet from "../../../../packages/web/svelte/src/views/ViewOutlet.svelte";
  import type { ViewRegistry } from "../../../../packages/web/svelte/src/views/view-registry.js";

  const mainViews = { home: Home, document: Document } satisfies ViewRegistry<ShellState["main"]>;
  const pinnedViews = { pinnedNote: PinnedNote, pinnedTask: PinnedTask } satisfies ViewRegistry<ShellState["pinned"][number]>;
  let { shell }: { shell: ShellClient } = $props();
  const source = useView(() => shell);
  let shellState = $derived(source.state!);
  const command = useCommand((name: "openHome" | "openDocument" | "swapPinned" | "removePinned" | "restorePinned") => shell[name]());
</script>

<header><strong>Reactive Notes</strong><span>Svelte · two View contracts</span></header>
<div class="layout">
  <nav aria-label="Main navigation">
    <button data-go="home" aria-current={shellState.main.kind === "home" ? "page" : "false"} onclick={() => command.run("openHome")}>Home</button>
    <button data-go="document" aria-current={shellState.main.kind === "document" ? "page" : "false"} onclick={() => command.run("openDocument")}>Document</button>
  </nav>
  <main id="main"><ViewOutlet content={shellState.main} registry={mainViews} /></main>
</div>
<section id="pinned" aria-label="Pinned Views">
  <button data-pinned-action="swap" onclick={() => command.run("swapPinned")}>Reorder pinned</button>
  <button data-pinned-action="remove" onclick={() => command.run("removePinned")}>Remove pinned note</button>
  <button data-pinned-action="restore" onclick={() => command.run("restorePinned")}>Restore pinned note</button>
  {#each shellState.pinned as pinned (pinned)}
    <span data-pin={pinned.kind}><ViewOutlet content={pinned} registry={pinnedViews} /></span>
  {/each}
</section>
<p id="status" role="status">{command.error === undefined ? "Connected." : String(command.error)}</p>
