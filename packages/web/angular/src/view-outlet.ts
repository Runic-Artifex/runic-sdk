import { Component, computed, effect, input, signal, untracked, viewChild, ViewContainerRef, type InputSignal, type Type } from "@angular/core";
import type { PageReference } from "@runic-artifex/views";

/**
 * A build-known map checks each generated reference against its component input. A nullable
 * reference, such as a region slot's `State["main"]`, checks its non-null kinds.
 */
export type ViewRegistry<R extends PageReference | null | undefined> = {
  readonly [K in NonNullable<R>["kind"]]: Type<{ readonly page: InputSignal<Extract<NonNullable<R>, { readonly kind: K }>> }>;
};

const outletTemplate = `
    <ng-container #mount />
    @if (error(); as issue) { <p role="alert">{{ issue }}</p> }
  `;

/**
 * Headless host for a selected logical .NET View. A changed reference remounts its component.
 * The element keeps Angular's prefix convention: `<runic-view-outlet [content]="…" [registry]="…" />`.
 */
@Component({ selector: "runic-view-outlet", standalone: true, template: outletTemplate })
export class ViewOutlet<R extends PageReference = PageReference> {
  readonly content = input<R | null | undefined>();
  readonly registry = input.required<ViewRegistry<R>>();
  readonly error = signal<string | undefined>(undefined);
  private readonly mount = viewChild.required("mount", { read: ViewContainerRef });
  private readonly selected = computed(() => {
    const reference = this.content();
    if (!reference) return undefined;
    // A registry built without `satisfies ViewRegistry<R>` may miss a kind at run time.
    const component: Type<object> | undefined = this.registry()[reference.kind as R["kind"]];
    return { reference, component };
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
