<script lang="ts">
  import {
    isThemeMode,
    isThemePalette,
    type ThemeMode,
    type ThemePalette,
  } from '$lib/appearance';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import ChevronsUpDownIcon from '@lucide/svelte/icons/chevrons-up-down';
  import MonitorIcon from '@lucide/svelte/icons/monitor';
  import MoonIcon from '@lucide/svelte/icons/moon';
  import PaletteIcon from '@lucide/svelte/icons/palette';
  import SunIcon from '@lucide/svelte/icons/sun';

  let {
    mode,
    palette,
    compact = false,
    onmodechange,
    onpalettechange,
  }: {
    mode: ThemeMode;
    palette: ThemePalette;
    compact?: boolean;
    onmodechange: (mode: ThemeMode) => void;
    onpalettechange: (palette: ThemePalette) => void;
  } = $props();

  const modeNames: Record<ThemeMode, string> = {
    system: 'System',
    light: 'Light',
    dark: 'Dark',
  };
  const paletteNames: Record<ThemePalette, string> = {
    runic: 'Runic Gold',
    moss: 'Moss',
    fjord: 'Fjord',
    ember: 'Ember',
  };

  let appearanceName = $derived(
    `${paletteNames[palette]} · ${modeNames[mode]}`,
  );
  let accessibleLabel = $derived(`Appearance, ${appearanceName}`);

  function changeMode(value: string): void {
    if (isThemeMode(value)) onmodechange(value);
  }

  function changePalette(value: string): void {
    if (isThemePalette(value)) onpalettechange(value);
  }
</script>

<DropdownMenu.Root>
  <DropdownMenu.Trigger>
    {#snippet child({ props })}
      <Button
        {...props}
        class={compact ? '' : 'w-full justify-between'}
        variant={compact ? 'ghost' : 'outline'}
        size={compact ? 'icon' : 'default'}
        aria-label={accessibleLabel}
        title={compact ? 'Appearance' : undefined}
        data-appearance-trigger={compact ? 'compact' : 'full'}
      >
        <PaletteIcon aria-hidden="true" />
        {#if !compact}
          <span class="min-w-0 flex-1 truncate text-left">{appearanceName}</span
          >
          <ChevronsUpDownIcon class="ml-auto" aria-hidden="true" />
        {/if}
      </Button>
    {/snippet}
  </DropdownMenu.Trigger>
  <DropdownMenu.Content class="min-w-56" align={compact ? 'end' : 'start'}>
    <DropdownMenu.Label>Appearance</DropdownMenu.Label>
    <DropdownMenu.RadioGroup value={mode} onValueChange={changeMode}>
      <DropdownMenu.RadioItem value="system"
        ><MonitorIcon />System</DropdownMenu.RadioItem
      >
      <DropdownMenu.RadioItem value="light"
        ><SunIcon />Light</DropdownMenu.RadioItem
      >
      <DropdownMenu.RadioItem value="dark"
        ><MoonIcon />Dark</DropdownMenu.RadioItem
      >
    </DropdownMenu.RadioGroup>
    <DropdownMenu.Separator />
    <DropdownMenu.Label>Color theme</DropdownMenu.Label>
    <DropdownMenu.RadioGroup value={palette} onValueChange={changePalette}>
      <DropdownMenu.RadioItem value="runic"
        ><PaletteIcon />Runic Gold</DropdownMenu.RadioItem
      >
      <DropdownMenu.RadioItem value="moss"
        ><PaletteIcon />Moss</DropdownMenu.RadioItem
      >
      <DropdownMenu.RadioItem value="fjord"
        ><PaletteIcon />Fjord</DropdownMenu.RadioItem
      >
      <DropdownMenu.RadioItem value="ember"
        ><PaletteIcon />Ember</DropdownMenu.RadioItem
      >
    </DropdownMenu.RadioGroup>
  </DropdownMenu.Content>
</DropdownMenu.Root>
