import { createElement as h, useRef, useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import { Menu } from "@base-ui/react/menu";
import { Menubar } from "@base-ui/react/menubar";
import { splitAccessKey } from "@runic-artifex/views";
import { focusOpenMenu } from "../../Frontend/src/menu-focus.js";
import { useMenuBar, type MenuBarEntry, type MenuBarItem, type MenuBarMenu } from "../../../../packages/web/react/src/index.js";

// The window's in-page menu bar with Base UI's Menubar, the base of shadcn/ui's Base UI Menubar. Runic provides
// the state, invoke and the keyboard requests (useMenuBar); Base UI provides the WAI-ARIA menubar: roles, roving
// focus, arrow keys, typeahead and Escape. build.mjs swaps this module in for Frontend/src/menu-bar.ts.
export function mountMenuBar(host: HTMLElement): () => void {
  const root = createRoot(host);
  root.render(h(AppMenuBar));
  return () => root.unmount();
}

function AppMenuBar(): ReactNode {
  const bar = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState<string | null>(null);
  const menuBar = useMenuBar({
    // Alt+access key opens that menu; an Alt tap or F10 focuses the first one.
    onRequest: request => {
      if (request.kind === "open") {
        setOpen(request.menu.key);
        focusOpenMenu(bar.current?.querySelectorAll<HTMLElement>("[role=menuitem]")[menuBar.state.menus.indexOf(request.menu)]);
      } else if (request.kind === "focus") bar.current?.querySelector<HTMLElement>("[role=menuitem]")?.focus();
    },
  });
  const { placement, menus, accessKeyHints } = menuBar.state;
  // "global" on macOS (the application menu bar has the menus), "unavailable" on a host without them.
  if (placement !== "page") return null;
  const run = (item: MenuBarItem) => { menuBar.invoke(item).catch(error => console.error(error)); };
  const label = (entry: MenuBarMenu | MenuBarItem) => {
    const [before, key, after] = splitAccessKey(entry);
    return h("span", { className: "menu-label" }, before, key && (accessKeyHints ? h("u", null, key) : key), after);
  };

  const items = (entries: readonly MenuBarEntry[]): ReactNode[] => groups(entries).map(group => {
    if (Array.isArray(group))
      return h(Menu.RadioGroup, { key: group[0]!.key, value: group.find(item => item.checked)?.key ?? null },
        group.map(item => h(Menu.RadioItem, { ...itemProps(item), value: item.key },
          h("span", { className: "menu-mark", "aria-hidden": true }, h(Menu.RadioItemIndicator, null, "•")), label(item), shortcut(item))));
    const entry = group as MenuBarEntry;
    if (entry.kind === "separator") return h(Menu.Separator, { key: entry.key, className: "menu-separator" });
    if (entry.kind === "menu")
      return h(Menu.SubmenuRoot, { key: entry.key, disabled: !entry.enabled },
        h(Menu.SubmenuTrigger, { className: "menu-item" }, h("span", { className: "menu-mark" }), label(entry), h("span", { "aria-hidden": true }, "›")),
        h(Menu.Portal, null, h(Menu.Positioner, { alignOffset: -4 }, h(Menu.Popup, { className: "menu-popup" }, items(entry.entries)))));
    if (entry.checked !== undefined)
      return h(Menu.CheckboxItem, { ...itemProps(entry), checked: entry.checked },
        h("span", { className: "menu-mark", "aria-hidden": true }, h(Menu.CheckboxItemIndicator, null, "✓")), label(entry), shortcut(entry));
    return h(Menu.Item, itemProps(entry), h("span", { className: "menu-mark" }), label(entry), shortcut(entry));
  });
  const itemProps = (item: MenuBarItem) => ({
    key: item.key,
    className: "menu-item",
    disabled: !item.enabled,
    closeOnClick: true,
    onClick: () => run(item),
    ...(item.ariaKeyShortcuts ? { "aria-keyshortcuts": item.ariaKeyShortcuts } : {}),
  });

  return h(Menubar, { ref: bar, className: "menubar", "aria-label": "Application" },
    menus.map(menu => h(Menu.Root, {
      key: menu.key,
      disabled: !menu.enabled,
      open: open === menu.key,
      // Moving between menus opens the next before the previous closes.
      onOpenChange: (next: boolean) => setOpen(current => next ? menu.key : current === menu.key ? null : current),
    },
    h(Menu.Trigger, { className: "menubar-trigger" }, label(menu)),
    h(Menu.Portal, null, h(Menu.Positioner, { sideOffset: 4, align: "start" }, h(Menu.Popup, { className: "menu-popup" }, items(menu.entries)))))));
}

const shortcut = (item: MenuBarItem) => item.shortcutText ? h("kbd", { className: "menu-shortcut", "aria-hidden": true }, item.shortcutText) : null;

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
