import { Component, ElementRef, viewChild, viewChildren } from "@angular/core";
import { NgTemplateOutlet } from "@angular/common";
import { CdkMenu, CdkMenuBar, CdkMenuGroup, CdkMenuItem, CdkMenuItemCheckbox, CdkMenuItemRadio, CdkMenuTrigger } from "@angular/cdk/menu";
import { splitAccessKey, type MenuBarEntry, type MenuBarItem, type MenuBarMenu } from "@runic-artifex/views";
import { focusOpenMenu } from "../../../Frontend/src/menu-focus.js";
import { injectMenuBar } from "../../../../../packages/web/angular/src/inject-menu-bar";

// Consecutive items of one radio group render as a radio group.
type Group = { readonly key: string; readonly entry?: MenuBarEntry; readonly radios?: readonly MenuBarItem[] };
function groups(entries: readonly MenuBarEntry[]): Group[] {
  const result: Group[] = [];
  for (const entry of entries) {
    const last = result.at(-1);
    if (entry.kind === "item" && entry.radioGroup !== undefined) {
      if (last?.radios && last.radios[0]!.radioGroup === entry.radioGroup) result[result.length - 1] = { ...last, radios: [...last.radios, entry] };
      else result.push({ key: entry.key, radios: [entry] });
    } else result.push({ key: entry.key, entry });
  }
  return result;
}

// The window's in-page menu bar with the Angular CDK Menu. Runic provides the state, invoke and the keyboard requests
// (injectMenuBar); the CDK provides the WAI-ARIA menubar: roles, roving focus, arrow keys, typeahead and Escape.
@Component({
  selector: "notes-menu-bar",
  imports: [CdkMenuBar, CdkMenu, CdkMenuItem, CdkMenuTrigger, CdkMenuGroup, CdkMenuItemCheckbox, CdkMenuItemRadio, NgTemplateOutlet],
  template: `
    <!-- "global" on macOS (the application menu bar has the menus), "unavailable" on a host without them. -->
    @if (menuBar.state(); as state) {
      @if (state.placement === "page") {
        <div cdkMenuBar class="menubar" aria-label="Application">
          @for (menu of state.menus; track menu.key) {
            <button cdkMenuItem class="menubar-trigger" [cdkMenuTriggerFor]="menuTemplate" [cdkMenuTriggerData]="{ $implicit: menu.key }"
              [cdkMenuItemDisabled]="!menu.enabled" [attr.data-menu]="menu.key">
              <ng-container *ngTemplateOutlet="labelTemplate; context: { $implicit: menu }" />
            </button>
          }
        </div>
      }
    }

    <!-- The CDK keeps a menu's first trigger data, so the menu reads its entries from the current state by key. -->
    <ng-template #menuTemplate let-key>
      <div cdkMenu class="menu-popup">
        @for (group of groups(entriesOf(key)); track group.key) {
          @if (group.radios; as radios) {
            <div cdkMenuGroup>
              @for (item of radios; track item.key) {
                <button cdkMenuItemRadio class="menu-item" [cdkMenuItemChecked]="item.checked === true" [cdkMenuItemDisabled]="!item.enabled"
                  [attr.aria-keyshortcuts]="item.ariaKeyShortcuts ?? null" (cdkMenuItemTriggered)="run(item)">
                  <span class="menu-mark" aria-hidden="true">{{ item.checked ? "•" : "" }}</span>
                  <ng-container *ngTemplateOutlet="itemTemplate; context: { $implicit: item }" />
                </button>
              }
            </div>
          } @else if (group.entry; as entry) {
            @switch (entry.kind) {
              @case ("separator") { <div role="separator" class="menu-separator"></div> }
              @case ("menu") {
                @if (asMenu(entry); as submenu) {
                  <button cdkMenuItem class="menu-item" [cdkMenuTriggerFor]="menuTemplate" [cdkMenuTriggerData]="{ $implicit: submenu.key }"
                    [cdkMenuItemDisabled]="!submenu.enabled">
                    <span class="menu-mark"></span><ng-container *ngTemplateOutlet="labelTemplate; context: { $implicit: submenu }" /><span aria-hidden="true">›</span>
                  </button>
                }
              }
              @default {
                @if (asItem(entry); as item) {
                  @if (item.checked !== undefined) {
                    <button cdkMenuItemCheckbox class="menu-item" [cdkMenuItemChecked]="item.checked" [cdkMenuItemDisabled]="!item.enabled"
                      [attr.aria-keyshortcuts]="item.ariaKeyShortcuts ?? null" (cdkMenuItemTriggered)="run(item)">
                      <span class="menu-mark" aria-hidden="true">{{ item.checked ? "✓" : "" }}</span>
                      <ng-container *ngTemplateOutlet="itemTemplate; context: { $implicit: item }" />
                    </button>
                  } @else {
                    <button cdkMenuItem class="menu-item" [cdkMenuItemDisabled]="!item.enabled"
                      [attr.aria-keyshortcuts]="item.ariaKeyShortcuts ?? null" (cdkMenuItemTriggered)="run(item)">
                      <span class="menu-mark"></span><ng-container *ngTemplateOutlet="itemTemplate; context: { $implicit: item }" />
                    </button>
                  }
                }
              }
            }
          }
        }
      </div>
    </ng-template>

    <ng-template #itemTemplate let-item>
      <ng-container *ngTemplateOutlet="labelTemplate; context: { $implicit: item }" />
      @if (item.shortcutText) { <kbd class="menu-shortcut" aria-hidden="true">{{ item.shortcutText }}</kbd> }
    </ng-template>

    <ng-template #labelTemplate let-entry>
      @let parts = split(entry);
      <span class="menu-label">{{ parts[0] }}@if (parts[1] && menuBar.state().accessKeyHints) {<u>{{ parts[1] }}</u>} @else {{{ parts[1] }}}{{ parts[2] }}</span>
    </ng-template>
  `,
})
export class MenuBarComponent {
  private readonly bar = viewChild(CdkMenuBar);
  private readonly triggers = viewChildren(CdkMenuItem, { read: ElementRef });
  private readonly items = viewChildren(CdkMenuItem);
  readonly menuBar = injectMenuBar({
    // Alt+access key opens that menu; an Alt tap or F10 focuses the first one.
    onRequest: request => {
      const bar = this.bar();
      if (request.kind === "focus") bar?.focusFirstItem("keyboard");
      else if (request.kind === "open") {
        const index = this.triggers().findIndex(trigger => (trigger.nativeElement as HTMLElement).dataset["menu"] === request.menu.key);
        const item = this.items()[index];
        if (!bar || !item) return;
        bar.setActiveMenuItem(item);
        item.getMenuTrigger()?.open();
        focusOpenMenu(this.triggers()[index]!.nativeElement as HTMLElement, "menu");
      }
    },
  });
  readonly groups = groups;
  entriesOf(key: string): readonly MenuBarEntry[] {
    const find = (entries: readonly MenuBarEntry[]): MenuBarMenu | undefined => {
      for (const entry of entries) {
        if (entry.kind !== "menu") continue;
        if (entry.key === key) return entry;
        const found = find(entry.entries);
        if (found) return found;
      }
      return undefined;
    };
    return find(this.menuBar.state().menus)?.entries ?? [];
  }
  readonly split = (entry: MenuBarMenu | MenuBarItem) => splitAccessKey(entry);
  readonly asMenu = (entry: MenuBarEntry) => entry.kind === "menu" ? entry : undefined;
  readonly asItem = (entry: MenuBarEntry) => entry.kind === "item" ? entry : undefined;
  run(item: MenuBarItem): void { this.menuBar.invoke(item).catch(error => console.error(error)); }
}
