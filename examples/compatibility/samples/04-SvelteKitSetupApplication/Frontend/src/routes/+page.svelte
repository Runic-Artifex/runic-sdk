<script lang="ts">
  import { initialSnapshot, setupBridge } from "$lib/setup-bridge.svelte";
  import { setupBridgeContext } from "$lib/setup-context.svelte";
  import type { FeatureId, SetupViewId } from "$lib/setup-contract";

  const bridge = setupBridgeContext.provide(setupBridge);
  let snapshot = $derived(bridge.snapshot ?? initialSnapshot());
  let features = $state<FeatureId[]>(["core"]);
  let busy = $state(false);
  let localError = $state<string>();
  let progress = $derived(
    bridge.lastEvent?._tag === "OperationProgress"
      ? bridge.lastEvent.completed
      : snapshot.viewId === "Complete" ? 5 : 0,
  );
  let error = $derived(
    localError ?? (bridge.error instanceof Error ? bridge.error.message : undefined),
  );

  const next: Record<SetupViewId, SetupViewId> = {
    Welcome: "Destination",
    Destination: "Features",
    Features: "Installing",
    Installing: "Installing",
    Complete: "Complete",
  };
  const back: Record<SetupViewId, SetupViewId> = {
    Welcome: "Welcome",
    Destination: "Welcome",
    Features: "Destination",
    Installing: "Installing",
    Complete: "Complete",
  };

  async function run(action: () => Promise<void>): Promise<void> {
    busy = true;
    localError = undefined;
    bridge.clearError();
    try {
      await action();
    } catch (failure) {
      localError = failure instanceof Error ? failure.message : "The command failed.";
    } finally {
      busy = false;
    }
  }

  function navigate(target: SetupViewId): Promise<void> {
    return run(async () => {
      const receipt = await bridge.dispatch({
        _tag: "Navigate",
        target,
        expectedRevision: snapshot.revision,
      });
      if (receipt._tag === "NavigationAccepted") bridge.snapshot = receipt.snapshot;
    });
  }

  function selectDestination(): Promise<void> {
    return run(async () => {
      const receipt = await bridge.dispatch({
        _tag: "SelectDestination",
        ...(snapshot.destination === undefined
          ? {}
          : { currentSelectionId: snapshot.destination.selectionId }),
      });
      if (receipt._tag === "DestinationSelected") {
        bridge.snapshot = {
          ...snapshot,
          destination: receipt.destination,
          revision: receipt.revision,
          canNavigateNext: true,
        };
      }
    });
  }

  function startInstallation(): Promise<void> {
    return run(async () => {
      if (snapshot.destination === undefined) {
        throw new Error("Select a destination before installing.");
      }
      const receipt = await bridge.dispatch({
        _tag: "StartInstallation",
        destinationSelectionId: snapshot.destination.selectionId,
        selectedFeatures: features,
      });
      if (receipt._tag === "InstallationStarted" && bridge.snapshot?.viewId !== "Complete") {
        bridge.snapshot = {
          ...snapshot,
          viewId: "Installing",
          revision: receipt.revision,
          selectedFeatures: features,
          activeOperationId: receipt.operationId,
          canNavigateBack: false,
          canNavigateNext: false,
        };
      }
    });
  }

  function toggleFeature(feature: FeatureId, checked: boolean): void {
    features = checked
      ? [...features, feature]
      : features.filter((candidate) => candidate !== feature);
  }
</script>

<svelte:head><title>Runic Toolkit · SvelteKit Setup</title></svelte:head>
<svelte:window onpagehide={() => void bridge.dispose()} />

<main
  class="shell"
  data-e2e-view={snapshot.viewId}
  data-e2e-progress={progress}
  data-e2e-status={bridge.status}
>
  <header>
    <div>
      <p class="eyebrow">Svelte 5 · SvelteKit · native CsWebUi</p>
      <h1>Setup Application</h1>
    </div>
    <span class="status">{bridge.status} · revision {snapshot.revision}</span>
  </header>

  <nav aria-label="Setup progress">
    {#each ["Welcome", "Destination", "Features", "Installing", "Complete"] as view (view)}
      <span class:active={snapshot.viewId === view}>{view}</span>
    {/each}
  </nav>

  <section>
    {#if snapshot.viewId === "Welcome"}
      <h2>One protocol runtime, native Svelte lifecycle</h2>
      <p>The Svelte integration projects authoritative bridge state into runes. It does not duplicate transport or reconnect logic.</p>
    {:else if snapshot.viewId === "Destination"}
      <h2>Choose a native destination</h2>
      <p>{snapshot.destination?.displayName ?? "No destination selected"}</p>
      <button data-e2e="select" onclick={() => void selectDestination()} disabled={busy}>Select in native host</button>
    {:else if snapshot.viewId === "Features"}
      <h2>Select features</h2>
      {#each ["core", "desktop-shortcut", "examples"] as feature (feature)}
        <label>
          <input
            type="checkbox"
            checked={features.includes(feature as FeatureId)}
            disabled={feature === "core" || busy}
            onchange={(event) => toggleFeature(feature as FeatureId, event.currentTarget.checked)}
          />
          {feature}
        </label>
      {/each}
    {:else if snapshot.viewId === "Installing"}
      <h2>Installing</h2>
      <progress max="5" value={progress}>{progress}/5</progress>
    {:else}
      <h2>Installation complete</h2>
      <p>The native host completed the operation and published the authoritative final revision.</p>
    {/if}
    {#if error}<p class="error">{error}</p>{/if}
  </section>

  <footer>
    <button onclick={() => void bridge.reconnect()} disabled={busy}>Reconnect</button>
    <div>
      <button data-e2e="back" onclick={() => void navigate(back[snapshot.viewId])} disabled={busy || !snapshot.canNavigateBack}>Back</button>
      {#if snapshot.viewId === "Features"}
        <button class="primary" data-e2e="install" onclick={() => void startInstallation()} disabled={busy}>Install</button>
      {:else if snapshot.viewId !== "Installing" && snapshot.viewId !== "Complete"}
        <button class="primary" data-e2e="next" onclick={() => void navigate(next[snapshot.viewId])} disabled={busy || !snapshot.canNavigateNext}>Next</button>
      {/if}
    </div>
  </footer>
</main>

<style>
  :global(*) { box-sizing: border-box; }
  :global(body) { margin: 0; background: #0b1110; color: #ebe5d5; font-family: Inter, system-ui, sans-serif; }
  button, input { font: inherit; }
  .shell { width: min(880px, calc(100% - 2rem)); margin: 2rem auto; border: 1px solid #6f623d; border-radius: 1rem; background: #111a17; box-shadow: 0 1.5rem 5rem #0008; overflow: hidden; }
  header, footer { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding: 1.5rem 2rem; }
  header { border-bottom: 1px solid #393725; }
  h1, h2, p { margin-top: 0; }
  h1 { margin-bottom: 0; font-family: Georgia, serif; font-size: clamp(2rem, 5vw, 3.5rem); font-weight: 500; }
  .eyebrow, .status { color: #c4a45e; font-size: .8rem; letter-spacing: .12em; text-transform: uppercase; }
  nav { display: grid; grid-template-columns: repeat(5, 1fr); gap: .5rem; padding: 1rem 2rem; border-bottom: 1px solid #393725; }
  nav span { color: #817e70; font-size: .78rem; text-align: center; }
  nav span.active { color: #e8c77b; }
  section { min-height: 300px; padding: 3rem 2rem; }
  section label { display: block; margin: .75rem 0; }
  section input { margin-right: .65rem; }
  progress { width: 100%; accent-color: #b88b44; }
  footer { border-top: 1px solid #393725; }
  footer div { display: flex; gap: .75rem; }
  button { border: 1px solid #6f623d; border-radius: .45rem; padding: .65rem 1rem; background: transparent; color: inherit; cursor: pointer; }
  button.primary { background: #b07437; border-color: #d09a59; color: #130e08; font-weight: 700; }
  button:disabled { cursor: not-allowed; opacity: .45; }
  .error { margin-top: 1rem; color: #ff9f91; }
  @media (max-width: 650px) { header { align-items: flex-start; flex-direction: column; } nav { grid-template-columns: 1fr; text-align: left; } }
</style>
