<script lang="ts">
  interface Props {
    title: string;
    subtitle: string;
    connected: boolean;
    status: string;
  }

  let { title, subtitle, connected, status }: Props = $props();
</script>

<header class="mb-4">
  <div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
    <div>
      <span class="framework-badge badge text-bg-danger mb-2">Svelte</span>
      <h1 class="display-6 fw-semibold mb-1">{title}</h1>
      <p class="text-secondary mb-0">{subtitle}</p>
    </div>
    <span
      class:badge={true}
      class:text-bg-success={connected}
      class:text-bg-secondary={!connected}
    >{status}</span>
  </div>
</header>
