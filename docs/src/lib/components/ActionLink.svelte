<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import type { Snippet } from 'svelte';

  let {
    href,
    variant = 'default',
    children,
  }: {
    href: string;
    variant?: 'default' | 'outline' | 'secondary' | 'ghost' | 'link';
    children: Snippet;
  } = $props();
</script>

<Button {href} {variant} size="lg">{@render children()}</Button>
