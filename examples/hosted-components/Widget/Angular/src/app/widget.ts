import { ChangeDetectionStrategy, Component, effect, inject, InjectionToken } from "@angular/core";
import { Title } from "@angular/platform-browser";
import { injectView } from "@runic-artifex/angular";
import { connectWidget } from "../../../Frontend/src/generated/widget";

/** True on the widget's own page (main.ts), which names the window after the service. */
export const WIDGET_PAGE = new InjectionToken<boolean>("WIDGET_PAGE", { factory: () => false });

// The widget's view, for its own page (main.ts) and its component (component.ts), with the default frontend's client
// (../Frontend/src/generated). Its styles are the component's: Angular scopes them to the component, and in a component
// they are stylesheets of the element's shadow root, where the host page's custom properties inherit (the dashboard
// sets --dashboard-accent).
@Component({
  selector: "service-widget",
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="widget">
      <h2>{{ widget.state()?.name ?? "Connecting…" }}</h2>
      @if (widget.state(); as state) {
        <p>Process {{ state.processId }}, running for {{ state.seconds }} s</p>
        <button type="button" [disabled]="!state.canPing" (click)="ping()">Ping ({{ state.pings }})</button>
      }
    </section>
  `,
  styles: `
    :host { display: block; font-family: system-ui, sans-serif; color: CanvasText; }
    .widget { border-inline-start: 4px solid var(--dashboard-accent, crimson); padding: .5rem .75rem; }
    h2 { margin: 0 0 .25rem; font-size: 1rem; }
    p { margin: 0 0 .5rem; font-size: .9rem; }
  `,
})
export class Widget {
  protected readonly widget = injectView({ connect: connectWidget });

  constructor() {
    // On its own page, the window's title names the service; a component shows no title of its own.
    if (inject(WIDGET_PAGE)) {
      const title = inject(Title);
      effect(() => {
        const name = this.widget.state()?.name;
        if (name !== undefined) title.setTitle(`${name} widget`);
      });
    }
  }

  protected async ping(): Promise<void> {
    await this.widget.client()?.ping();
  }
}
