<script lang="ts">
  import { resolve } from '$app/paths';
  import ActionLink from '$lib/components/ActionLink.svelte';
  import * as Empty from '$lib/components/ui/empty';
  import FileQuestionIcon from '@lucide/svelte/icons/file-question';

  let {
    status,
    title,
    description,
  }: { status: number | string; title: string; description: string } = $props();
</script>

<div>
  <section class="page-hero shell">
    <Empty.Root class="min-h-[28rem] border border-border bg-card/60">
      <Empty.Media variant="icon" class="text-primary">
        <FileQuestionIcon />
      </Empty.Media>
      <Empty.Header>
        <p class="eyebrow">{status}</p>
        <Empty.Title>
          <h1 class="font-serif text-4xl sm:text-6xl">{title}</h1>
        </Empty.Title>
        <Empty.Description class="max-w-xl text-base">
          {description}
        </Empty.Description>
      </Empty.Header>
      <Empty.Content class="actions mt-2">
        <ActionLink href={resolve('/')}>Return home</ActionLink>
        <ActionLink href={resolve('/products')} variant="outline"
          >Browse products</ActionLink
        >
      </Empty.Content>
    </Empty.Root>
  </section>
</div>
