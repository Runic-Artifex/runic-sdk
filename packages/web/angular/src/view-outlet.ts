import { Component, computed, effect, input, signal, untracked, viewChild, ViewContainerRef, type InputSignal, type TemplateRef, type Type } from "@angular/core";
import type { PageReference } from "@runic-artifex/views";

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
const outletTemplate = `
    <ng-container #mount />
    @if (!content()) { <ng-content /> }
    @if (error(); as issue) { <p role="alert">{{ issue }}</p> }
  `;

/**
 * Headless host for a selected logical .NET View. A changed reference remounts its component, and
 * the projected content renders while there is no reference. A kind without a component renders
 * the `missing` template, for example localized text, or an English alert without it:
 *
 * ```html
 * <runic-view-outlet [content]="state()?.main" [registry]="pages" [missing]="missingView" />
 * <ng-template #missingView let-page><p role="alert">{{ missingText(page.kind) }}</p></ng-template>
 * ```
 *
 * The element keeps Angular's prefix convention: `<runic-view-outlet [content]="…" [registry]="…" />`.
 */
@Component({ selector: "runic-view-outlet", standalone: true, template: outletTemplate })
export class ViewOutlet<R extends PageReference = PageReference> {
  readonly content = input<R | null | undefined>();
  readonly registry = input.required<ViewRegistry<R>>();
  /** Renders a reference whose kind has no registered component instead of the English alert. */
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
          this.error.set(`No web component is registered for ${selection.reference.kind}.`);
          return;
        }
        const component = container.createComponent(selection.component);
        component.setInput("page", selection.reference);
      });
    });
  }
}

/**
 * The name of {@link ViewOutlet} before 0.7.0-preview.7, where React, Vue and Svelte already used
 * `ViewOutlet`. It renders the same `<runic-view-outlet>` element.
 * @deprecated Import `ViewOutlet` instead; this alias is removed in the next preview.
 */
@Component({ selector: "runic-view-outlet", standalone: true, template: outletTemplate })
export class RunicViewOutlet<R extends PageReference = PageReference> extends ViewOutlet<R> {}
