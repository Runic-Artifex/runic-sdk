<script lang="ts">
  import * as Alert from '$lib/components/ui/alert';
  import CircleAlertIcon from '@lucide/svelte/icons/circle-alert';
  import type { Snippet } from 'svelte';

  let { title, children }: { title: string; children: Snippet } = $props();
</script>

<Alert.Root class="notice border-primary/30 bg-primary/5">
  <CircleAlertIcon />
  <Alert.Title class="text-primary">{title}</Alert.Title>
  <Alert.Description>{@render children()}</Alert.Description>
</Alert.Root>
