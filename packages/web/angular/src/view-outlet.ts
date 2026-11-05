import {
  Component, computed, Directive, effect, ElementRef, inject, input, isDevMode, Renderer2, signal, untracked, viewChild, ViewContainerRef,
  type InputSignal,
  type TemplateRef, type Type,
} from "@angular/core";
import { regionEntries, type PageReference, type RegionCollection, type RegionEntry } from "@runic-artifex/views";
import { missingViewMessage, reportMissingView } from "@runic-artifex/views/bindings";

/**
 * A build-known map checks each generated reference against its component input. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds.
 */
export type ViewRegistry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: Type<{ readonly page: InputSignal<Extract<NonNullable<R>, { readonly kind: K }>> }>;
};

/** The context of a `missing` template: the reference without a component, as `$implicit` (`let-page`) and `content`. */
export interface ViewOutletMissingContext<R extends PageReference = PageReference> {
  readonly $implicit: R;
  readonly content: R;
}

// Projected content renders while there is no reference, like React's `fallback`, Vue's default slot
// and Svelte's `fallback` snippet: `<runic-view-outlet [content]="…" [registry]="…">Loading…</runic-view-outlet>`.

/**
 * Headless host for a selected logical .NET View. A changed reference remounts its component, and
 * the projected content renders while there is no reference. A kind without a component renders
 * the `missing` template, for example localized text. Without it, a development build (`isDevMode()`) renders an
 * English alert, and a production build renders nothing and reports the kind (`installErrorReporting`,
 * `onBridgeDiagnostic`):
 *
 * ```html
 * <runic-view-outlet [content]="state()?.main" [registry]="pages" [missing]="missingView" />
 * <ng-template #missingView let-page><p role="alert">{{ missingText(page.kind) }}</p></ng-template>
 * ```
 *
 * The element keeps Angular's prefix convention: `<runic-view-outlet [content]="…" [registry]="…" />`.
 */
@Component({ selector: "runic-view-outlet", standalone: true, template: `
    <ng-container #mount />
    @if (!content()) { <ng-content /> }
    @if (error(); as issue) { <p role="alert">{{ issue }}</p> }
  ` })
export class ViewOutlet<R extends PageReference = PageReference> {
  readonly content = input<R | null | undefined>();
  readonly registry = input.required<ViewRegistry<R>>();
  /** Renders a reference whose kind has no registered component instead of the development alert. */
  readonly missing = input<TemplateRef<ViewOutletMissingContext<R>> | undefined>();
  readonly error = signal<string | undefined>(undefined);
  private readonly mount = viewChild.required("mount", { read: ViewContainerRef });
  private readonly selected = computed(() => {
    const reference = this.content();
    if (!reference) return undefined;
    // A registry built without `satisfies ViewRegistry<R>` may miss a kind at run time.
    const component: Type<object> | undefined = this.registry()[reference.kind as R["kind"]];
    // `missing` is a dependency only while the kind has no component, so changing it never remounts one.
    return { reference, component, missing: component ? undefined : this.missing() };
  });

  constructor() {
    effect(() => {
      const container = this.mount();
      const selection = this.selected();
      // Creating the component runs its constructor and inputs; their signal
      // reads must not become dependencies of this effect.
      untracked(() => {
        container.clear();
        this.error.set(undefined);
        if (!selection) return;
        if (!selection.component) {
          if (selection.missing) {
            container.createEmbeddedView(selection.missing, { $implicit: selection.reference, content: selection.reference });
            return;
          }
          if (isDevMode()) this.error.set(missingViewMessage(selection.reference.kind));
          else reportMissingView(selection.reference.kind);
          return;
        }
        const component = container.createComponent(selection.component);
        component.setInput("page", selection.reference);
      });
    });
  }
}

/** How {@link RegionOutlets} renders the regions of a collection. */
export type RegionOutletsMode = "keep-mounted" | "selected";

/**
 * Attributes that {@link RegionOutlets} sets on a region's wrapper, such as `id`, `role` and `aria-labelledby`. A
 * `null` or `undefined` value leaves the attribute out. `hidden`, `style` and `data-region-key` stay controlled by
 * the outlet and are ignored here.
 */
export type RegionPanelAttributes = Readonly<Record<string, string | null | undefined>>;

const outletAttributes = new Set(["hidden", "style", "data-region-key"]);

// Sets the attributes of the latest panelProps result on its element and removes the ones it no longer returns.
@Directive({ selector: "[runicRegionPanel]", standalone: true })
export class RegionPanel {
  readonly attributes = input<RegionPanelAttributes | undefined>(undefined, { alias: "runicRegionPanel" });

  constructor() {
    const element = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    const renderer = inject(Renderer2);
    let applied = new Set<string>();
    effect(() => {
      const set = new Set<string>();
      for (const [name, value] of Object.entries(this.attributes() ?? {})) {
        if (value === null || value === undefined || outletAttributes.has(name.toLowerCase())) continue;
        renderer.setAttribute(element, name, value);
        set.add(name);
      }
      for (const name of applied) if (!set.has(name)) renderer.removeAttribute(element, name);
      applied = set;
    });
  }
}

/**
 * Renders a {@link ViewOutlet} for the regions of a region collection, each in a `<div data-region-key>` tracked by
 * the region's key. A region that is not selected is rendered with `hidden` and an inline `display: none`, which
 * author CSS without `!important` cannot override. A new content reference in a region remounts only that region's
 * component. The projected content renders while the collection has no region, and `missing` is passed to each
 * outlet. `panelProps` returns attributes for each wrapper, so a tab can point `aria-controls` at its panel:
 *
 * ```html
 * <runic-region-outlets [collection]="state()?.documents" [registry]="pages" [panelProps]="panel">No open documents</runic-region-outlets>
 * ```
 *
 * `mode` `"keep-mounted"` (the default) renders every region and hides all but the selected one, so each region
 * keeps its component and DOM. `"selected"` renders only the selected region.
 */
@Component({ selector: "runic-region-outlets", standalone: true, imports: [ViewOutlet, RegionPanel], template: `
    @for (panel of shown(); track panel.region.key) {
      <div [runicRegionPanel]="panel.attributes" [hidden]="!panel.region.selected" [style.display]="panel.region.selected ? null : 'none'"
        [attr.data-region-key]="panel.region.key">
        <runic-view-outlet [content]="panel.region.content" [registry]="registry()" [missing]="missing()" />
      </div>
    } @empty { <ng-content /> }
  ` })
export class RegionOutlets<R extends PageReference = PageReference> {
  readonly collection = input<RegionCollection<R> | null | undefined>();
  readonly registry = input.required<ViewRegistry<R>>();
  /** Renders a reference whose kind has no registered component instead of the English alert. */
  readonly missing = input<TemplateRef<ViewOutletMissingContext<R>> | undefined>();
  readonly mode = input<RegionOutletsMode>("keep-mounted");
  /** Returns attributes for a region's wrapper, for example `{ id, role: "tabpanel", "aria-labelledby" }`. */
  readonly panelProps = input<((entry: RegionEntry<R>) => RegionPanelAttributes) | undefined>();
  protected readonly shown = computed(() => {
    const keepMounted = this.mode() === "keep-mounted";
    const panelProps = this.panelProps();
    return regionEntries(this.collection())
      .filter(region => keepMounted || region.selected)
      .map(region => ({ region, attributes: panelProps?.(region) }));
  });
}

/**
 * The name of {@link ViewOutlet} before 0.7.0-preview.7, where React, Vue and Svelte already used
 * `ViewOutlet`. It is the same component class, so it renders the same `<runic-view-outlet>` element
 * without a second component definition for that selector (NG0912).
 * @deprecated Import `ViewOutlet` instead; this alias is removed in the next major release.
 */
export const RunicViewOutlet = ViewOutlet;
/**
 * The name of {@link ViewOutlet} before 0.7.0-preview.7.
 * @deprecated Import `ViewOutlet` instead; this alias is removed in the next major release.
 */
export type RunicViewOutlet<R extends PageReference = PageReference> = ViewOutlet<R>;
