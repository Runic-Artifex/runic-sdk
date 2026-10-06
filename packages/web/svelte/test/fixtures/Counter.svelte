<script lang="ts">
  import type { CounterPage } from "./pages.js";
  let { page }: { page: CounterPage } = $props();
</script>

<output>counter:{page.kind}</output>
