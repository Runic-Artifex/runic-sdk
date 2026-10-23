<script lang="ts">
  // The window's in-page menu bar with Bits UI's Menubar. Runic provides the state, invoke and the keyboard requests
  // (useMenuBar); Bits UI provides the WAI-ARIA menubar: roles, roving focus, arrow keys, typeahead and Escape.
  import { Menubar } from "bits-ui";
  import { SvelteMap } from "svelte/reactivity";
  import { splitAccessKey, type MenuBarEntry, type MenuBarItem, type MenuBarMenu } from "@runic-artifex/views";
  import { useMenuBar } from "../../../../packages/web/svelte/src/views/use-menu-bar.svelte.js";

  let bar = $state<HTMLElement | null>(null);
  // The open menu's key.
  let open = $state("");
  const menuBar = useMenuBar({
    // Alt+access key opens that menu; an Alt tap or F10 focuses the first one.
    onRequest: request => {
      if (request.kind === "open") open = request.menu.key;
      else if (request.kind === "focus") bar?.querySelector<HTMLElement>("[role=menuitem]")?.focus();
    },
  });
  const run = (item: MenuBarItem) => { menuBar.invoke(item).catch(error => console.error(error)); };
  const keyShortcuts = (item: MenuBarItem) => item.ariaKeyShortcuts ? { "aria-keyshortcuts": item.ariaKeyShortcuts } : {};

  // Bits UI 2.20 runs a menu's open auto-focus again when the menu's props change after it opened (its open transition
  // ends a frame later), and that moves focus from an item already reached with the arrow keys back to the first item.
  // An open menu that has focus keeps it.
  const contents = new SvelteMap<string, HTMLElement>();
  const content = (key: string, element: HTMLElement | null) => { if (element) contents.set(key, element); else contents.delete(key); };
  const keepFocus = (key: string) => (event: Event) => {
    if (contents.get(key)?.contains(document.activeElement)) event.preventDefault();
  };

  // Consecutive items of one radio group render as a radio group.
  function groups(entries: readonly MenuBarEntry[]): (MenuBarEntry | MenuBarItem[])[] {
    const result: (MenuBarEntry | MenuBarItem[])[] = [];
    for (const entry of entries) {
      const last = result.at(-1);
      if (entry.kind === "item" && entry.radioGroup !== undefined) {
        if (Array.isArray(last) && last[0]!.radioGroup === entry.radioGroup) last.push(entry);
        else result.push([entry]);
      } else result.push(entry);
    }
    return result;
  }
</script>

{#snippet label(entry: MenuBarMenu | MenuBarItem)}
  {@const [before, key, after] = splitAccessKey(entry)}
  <span class="menu-label">{before}{#if key && menuBar.state.accessKeyHints}<u>{key}</u>{:else}{key}{/if}{after}</span>
{/snippet}

{#snippet shortcut(item: MenuBarItem)}
  {#if item.shortcutText}<kbd class="menu-shortcut" aria-hidden="true">{item.shortcutText}</kbd>{/if}
{/snippet}

{#snippet entries(list: readonly MenuBarEntry[])}
  {#each groups(list) as group ((Array.isArray(group) ? group[0] : group)!.key)}
    {#if Array.isArray(group)}
      <!-- The host owns the state: the bindings read it and ignore the component's own changes. -->
      <Menubar.RadioGroup bind:value={() => group.find(item => item.checked)?.key ?? "", () => {}}>
        {#each group as item (item.key)}
          <Menubar.RadioItem class="menu-item" value={item.key} disabled={!item.enabled} onSelect={() => run(item)} {...keyShortcuts(item)}>
            <span class="menu-mark" aria-hidden="true">{item.checked ? "•" : ""}</span>{@render label(item)}{@render shortcut(item)}
          </Menubar.RadioItem>
        {/each}
      </Menubar.RadioGroup>
    {:else if group.kind === "separator"}
      <Menubar.Separator class="menu-separator" />
    {:else if group.kind === "menu"}
      <Menubar.Sub>
        <Menubar.SubTrigger class="menu-item" disabled={!group.enabled}>
          <span class="menu-mark"></span>{@render label(group)}<span aria-hidden="true">›</span>
        </Menubar.SubTrigger>
        <Menubar.SubContent class="menu-popup" alignOffset={-4} bind:ref={() => contents.get(group.key) ?? null, element => content(group.key, element)}
          onOpenAutoFocus={keepFocus(group.key)}>{@render entries(group.entries)}</Menubar.SubContent>
      </Menubar.Sub>
    {:else if group.checked !== undefined}
      {@const item = group}
      <Menubar.CheckboxItem class="menu-item" bind:checked={() => item.checked ?? false, () => {}} disabled={!item.enabled} onSelect={() => run(item)} {...keyShortcuts(item)}>
        <span class="menu-mark" aria-hidden="true">{item.checked ? "✓" : ""}</span>{@render label(item)}{@render shortcut(item)}
      </Menubar.CheckboxItem>
    {:else}
      {@const item = group}
      <Menubar.Item class="menu-item" disabled={!item.enabled} onSelect={() => run(item)} {...keyShortcuts(item)}>
        <span class="menu-mark"></span>{@render label(item)}{@render shortcut(item)}
      </Menubar.Item>
    {/if}
  {/each}
{/snippet}

<!-- "global" on macOS (the application menu bar has the menus), "unavailable" on a host without them. -->
{#if menuBar.state.placement === "page"}
  <Menubar.Root class="menubar" aria-label="Application" bind:ref={bar} bind:value={open}>
    {#each menuBar.state.menus as menu (menu.key)}
      <Menubar.Menu value={menu.key}>
        <Menubar.Trigger class="menubar-trigger" disabled={!menu.enabled}>{@render label(menu)}</Menubar.Trigger>
        <Menubar.Portal>
          <Menubar.Content class="menu-popup" align="start" sideOffset={4} bind:ref={() => contents.get(menu.key) ?? null, element => content(menu.key, element)}
            onOpenAutoFocus={keepFocus(menu.key)}>{@render entries(menu.entries)}</Menubar.Content>
        </Menubar.Portal>
      </Menubar.Menu>
    {/each}
  </Menubar.Root>
{/if}
