import { createApp, defineComponent, h, ref, type VNode } from "vue";
import {
  MenubarCheckboxItem, MenubarContent, MenubarItem, MenubarItemIndicator, MenubarMenu, MenubarPortal, MenubarRadioGroup, MenubarRadioItem,
  MenubarRoot, MenubarSeparator, MenubarSub, MenubarSubContent, MenubarSubTrigger, MenubarTrigger,
} from "reka-ui";
import { splitAccessKey } from "@runic-artifex/views";
import { focusOpenMenu } from "../../Frontend/src/menu-focus.js";
import { useMenuBar, type MenuBarEntry, type MenuBarItem, type MenuBarMenu } from "../../../../packages/web/vue/src/index.js";

// The window's in-page menu bar with Reka UI's Menubar. Runic provides the state, invoke and the keyboard requests
// (useMenuBar); Reka UI provides the WAI-ARIA menubar: roles, roving focus, arrow keys, typeahead and Escape.
// build.mjs swaps this module in for Frontend/src/menu-bar.ts.
export function mountMenuBar(host: HTMLElement): () => void {
  const app = createApp(AppMenuBar);
  app.mount(host);
  return () => app.unmount();
}

const AppMenuBar = defineComponent({
  setup() {
    const bar = ref<{ $el: HTMLElement }>();
    // The open menu's key, MenubarRoot's v-model.
    const open = ref("");
    const { state, invoke } = useMenuBar({
      // Alt+access key opens that menu; an Alt tap or F10 focuses the first one.
      onRequest: request => {
        if (request.kind === "open") {
          open.value = request.menu.key;
          focusOpenMenu(bar.value?.$el.querySelectorAll<HTMLElement>("[role=menuitem]")[state.value.menus.indexOf(request.menu)]);
        } else if (request.kind === "focus") bar.value?.$el.querySelector<HTMLElement>("[role=menuitem]")?.focus();
      },
    });
    const run = (item: MenuBarItem) => { invoke(item).catch(error => console.error(error)); };
    const label = (entry: MenuBarMenu | MenuBarItem) => {
      const [before, key, after] = splitAccessKey(entry);
      return h("span", { class: "menu-label" }, [before, key && (state.value.accessKeyHints ? h("u", key) : key), after]);
    };
    const shortcut = (item: MenuBarItem) => item.shortcutText ? h("kbd", { class: "menu-shortcut", "aria-hidden": "true" }, item.shortcutText) : null;
    const itemProps = (item: MenuBarItem) => ({
      key: item.key,
      class: "menu-item",
      disabled: !item.enabled,
      onSelect: () => run(item),
      ...(item.ariaKeyShortcuts ? { "aria-keyshortcuts": item.ariaKeyShortcuts } : {}),
    });
    const mark = (indicator: string) => h("span", { class: "menu-mark", "aria-hidden": "true" }, [h(MenubarItemIndicator, () => indicator)]);

    const items = (entries: readonly MenuBarEntry[]): VNode[] => groups(entries).map(group => {
      if (Array.isArray(group))
        return h(MenubarRadioGroup, { key: group[0]!.key, modelValue: group.find(item => item.checked)?.key ?? "" }, () =>
          group.map(item => h(MenubarRadioItem, { ...itemProps(item), value: item.key }, () => [mark("•"), label(item), shortcut(item)])));
      const entry = group as MenuBarEntry;
      if (entry.kind === "separator") return h(MenubarSeparator, { key: entry.key, class: "menu-separator" });
      if (entry.kind === "menu")
        return h(MenubarSub, { key: entry.key }, () => [
          h(MenubarSubTrigger, { class: "menu-item", disabled: !entry.enabled }, () => [h("span", { class: "menu-mark" }), label(entry), h("span", { "aria-hidden": "true" }, "›")]),
          h(MenubarPortal, () => h(MenubarSubContent, { class: "menu-popup", alignOffset: -4 }, () => items(entry.entries))),
        ]);
      if (entry.checked !== undefined)
        return h(MenubarCheckboxItem, { ...itemProps(entry), modelValue: entry.checked }, () => [mark("✓"), label(entry), shortcut(entry)]);
      return h(MenubarItem, itemProps(entry), () => [h("span", { class: "menu-mark" }), label(entry), shortcut(entry)]);
    });

    return () => {
      // "global" on macOS (the application menu bar has the menus), "unavailable" on a host without them.
      if (state.value.placement !== "page") return null;
      return h(MenubarRoot, { ref: bar, class: "menubar", "aria-label": "Application", modelValue: open.value, "onUpdate:modelValue": (value: unknown) => { open.value = typeof value === "string" ? value : ""; } }, () =>
        state.value.menus.map(menu => h(MenubarMenu, { key: menu.key, value: menu.key }, () => [
          h(MenubarTrigger, { class: "menubar-trigger", disabled: !menu.enabled }, () => label(menu)),
          h(MenubarPortal, () => h(MenubarContent, { class: "menu-popup", align: "start", sideOffset: 4 }, () => items(menu.entries))),
        ])));
    };
  },
});

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
