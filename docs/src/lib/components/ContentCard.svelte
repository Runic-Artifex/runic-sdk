<script lang="ts">
  import * as Card from '$lib/components/ui/card';
  import { cn } from '$lib/utils';
  import type { Snippet } from 'svelte';

  let {
    eyebrow,
    title,
    full = false,
    children,
  }: {
    eyebrow: string;
    title: string;
    full?: boolean;
    children: Snippet;
  } = $props();
</script>

<Card.Root class={cn('info-card', full && 'full')}>
  <Card.Header>
    <p class="eyebrow">{eyebrow}</p>
    <Card.Title>
      <h2 class="font-serif text-2xl sm:text-3xl">{title}</h2>
    </Card.Title>
  </Card.Header>
  <Card.Content class="grid gap-4">{@render children()}</Card.Content>
</Card.Root>
