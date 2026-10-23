// After an `open` request (Alt plus a menu's access key) focus the open menu's first item, as a native menu bar
// does. Base UI, Reka UI and the CDK focus the popup or keep the trigger when their open state is set from code,
// and render the popup a frame or two later; Bits UI already focuses the first item. The CDK's own key handling
// follows focus on the menu itself (`"menu"`), which then focuses its first item.
export function focusOpenMenu(trigger: HTMLElement | null | undefined, target: "first-item" | "menu" = "first-item", frames = 10): void {
  const id = trigger?.getAttribute("aria-controls");
  const menu = (id ? document.getElementById(id) : null) ?? document.querySelector<HTMLElement>("[role=menu]");
  const element = target === "menu" ? menu : menu?.querySelector<HTMLElement>("[role^=menuitem]");
  if (element) element.focus();
  else if (frames > 0) requestAnimationFrame(() => focusOpenMenu(trigger, target, frames - 1));
}
