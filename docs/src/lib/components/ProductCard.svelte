<script lang="ts">
  import { resolve } from '$app/paths';
  import * as Card from '$lib/components/ui/card';
  import type { Product } from '$lib/docs-data';

  let {
    product,
    label,
    headingLevel = 3,
  }: {
    product: Product;
    label: string;
    headingLevel?: 2 | 3;
  } = $props();
</script>

<a
  class="product-card-link"
  href={resolve('/products/[slug]', { slug: product.slug })}
>
  <Card.Root class="product-card">
    <Card.Header>
      <span
        class="product-mark"
        style:background-image={`url(${product.icon})`}
        aria-hidden="true"
      ></span>
      <p class="kicker">{product.kicker}</p>
      <Card.Title>
        {#if headingLevel === 2}
          <h2 class="font-serif text-2xl">{product.name}</h2>
        {:else}
          <h3 class="font-serif text-2xl">{product.name}</h3>
        {/if}
      </Card.Title>
      <Card.Description class="text-sm leading-relaxed">
        {product.summary}
      </Card.Description>
    </Card.Header>
    <Card.Footer class="mt-auto">
      <span class="card-link">{label} →</span>
    </Card.Footer>
  </Card.Root>
</a>
