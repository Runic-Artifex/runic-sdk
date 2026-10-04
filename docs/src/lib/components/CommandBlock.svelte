<script lang="ts">
  import { Button } from '#lib/components/ui/button/index.js';
  import { cn } from '#lib/utils.js';
  import CheckIcon from '@lucide/svelte/icons/check';
  import CopyIcon from '@lucide/svelte/icons/copy';
  import type { Snippet } from 'svelte';

  let {
    command,
    label = 'Copy command',
    class: className,
    leading,
  }: {
    command: string;
    label?: string;
    class?: string;
    leading?: Snippet;
  } = $props();

  let copied = $state(false);
  let reset: ReturnType<typeof setTimeout> | undefined;

  async function copy() {
    await navigator.clipboard.writeText(command);
    copied = true;
    clearTimeout(reset);
    reset = setTimeout(() => (copied = false), 1600);
  }
</script>

<div class={cn('command-block', className)}>
  {#if leading}
    <div class="command-leading">{@render leading()}</div>
  {/if}
  <pre><code>{command}</code></pre>
  <Button
    class="command-copy"
    variant="ghost"
    size="icon-sm"
    aria-label={copied ? 'Copied' : label}
    title={copied ? 'Copied' : label}
    onclick={copy}
  >
    {#if copied}<CheckIcon aria-hidden="true" />{:else}<CopyIcon
        aria-hidden="true"
      />{/if}
  </Button>
  <span class="sr-only" role="status"
    >{copied ? 'Copied to clipboard' : ''}</span
  >
</div>

<style>
  .command-block {
    position: relative;
    display: flex;
    align-items: stretch;
    border: 1px solid var(--border);
    border-radius: calc(var(--radius) * 1.4);
    background: color-mix(in oklch, var(--muted) 65%, var(--background));
  }

  .command-leading {
    display: flex;
    align-items: center;
    padding-left: 8px;
    border-right: 1px solid var(--border);
  }

  pre {
    flex: 1;
    min-width: 0;
    margin: 0;
    padding: 14px 52px 14px 18px;
    font-size: 0.86rem;
    line-height: 1.7;
    white-space: pre-wrap;
    overflow-wrap: break-word;
  }

  pre code {
    overflow-wrap: break-word;
  }

  .command-block :global(.command-copy) {
    position: absolute;
    top: 8px;
    right: 8px;
  }
</style>
