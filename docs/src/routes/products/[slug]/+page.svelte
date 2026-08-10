<script lang="ts">
  import ProductPage from '$lib/components/ProductPage.svelte';
  import type { PageProps } from './$types';

  let { data }: PageProps = $props();
</script>

<ProductPage product={data.product} />
