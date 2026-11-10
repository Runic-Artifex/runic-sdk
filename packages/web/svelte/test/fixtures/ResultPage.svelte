<script lang="ts">
  import type { ResultPage } from "./shared-pages.js";
  let { page }: { page: ResultPage } = $props();
</script>

<output>result:{page.kind}</output>
