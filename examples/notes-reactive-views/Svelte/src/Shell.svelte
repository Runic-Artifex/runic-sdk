<script lang="ts">
  import type { ShellView, ShellState } from "../../Frontend/src/generated/shell.js";
  import { bridgeState } from "./bridge-state.js";
  import Home from "./Home.svelte";
  import Document from "./Document.svelte";
  import ViewOutlet from "../../../../packages/web/svelte/src/views/ViewOutlet.svelte";
  import type { ViewRegistry } from "../../../../packages/web/svelte/src/views/view-registry.js";

  const mainViews = { home: Home, document: Document } satisfies ViewRegistry<ShellState["main"]>;
  let { shell }: { shell: ShellView } = $props();
  let source = $derived(bridgeState(shell));
  let shellState = $derived(source.current);
  let error = $state<string | undefined>();

  async function run(command: () => Promise<unknown>) {
    try { await command(); error = undefined; }
    catch (cause) { error = String(cause); }
  }
</script>

<header><strong>Reactive Notes</strong><span>Svelte · two View contracts</span></header>
<div class="layout">
  <nav aria-label="Main navigation">
    <button data-go="home" aria-current={shellState.main.kind === "home" ? "page" : "false"} onclick={() => run(() => shell.openHome())}>Home</button>
    <button data-go="document" aria-current={shellState.main.kind === "document" ? "page" : "false"} onclick={() => run(() => shell.openDocument())}>Document</button>
  </nav>
  <main id="main"><ViewOutlet content={shellState.main} registry={mainViews} /></main>
</div>
<p id="status" role="status">{error ?? "Connected."}</p>
