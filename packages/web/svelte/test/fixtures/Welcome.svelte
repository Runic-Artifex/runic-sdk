<script lang="ts">
  import type { WelcomePage } from "./pages.js";
  let { page }: { page: WelcomePage } = $props();
</script>

<output>welcome:{page.kind}</output>
