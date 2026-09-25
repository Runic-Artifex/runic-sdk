import { Component, computed, effect, input, signal, viewChild, ViewContainerRef, type InputSignal, type Type } from "@angular/core";

export interface ViewReference { readonly kind: string; connect(): Promise<unknown>; }

/** A build-known map checks each generated reference against its component input. */
export type ViewRegistry<R extends ViewReference> = {
  readonly [K in R["kind"]]: Type<{ readonly page: InputSignal<Extract<R, { readonly kind: K }>> }>;
};

/** Headless host for a selected logical .NET View. A changed reference remounts its component. */
@Component({
  selector: "runic-view-outlet",
  template: `
    <ng-container #mount />
    @if (error(); as issue) { <p role="alert">{{ issue }}</p> }
  `,
})
export class RunicViewOutlet {
  readonly content = input<ViewReference | null | undefined>();
  readonly registry = input.required<Readonly<Record<string, Type<unknown>>>>();
  readonly error = signal<string | undefined>(undefined);
  private readonly mount = viewChild.required("mount", { read: ViewContainerRef });
  private readonly selected = computed(() => {
    const reference = this.content();
    return reference ? { reference, component: this.registry()[reference.kind] } : undefined;
  });

  constructor() {
    effect(() => {
      const container = this.mount();
      const selection = this.selected();
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
  }
}
