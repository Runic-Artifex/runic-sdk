import {
  Component,
  computed,
  input,
} from "@angular/core";
import { injectAngularMvvmApplication } from "@webuitoolkit/mvvm-angular";

@Component({
  selector: "todo-app-header",
  standalone: true,
  template: `
    <header class="mb-4">
      <div class="d-flex flex-wrap gap-2 justify-content-between align-items-start">
        <div>
          <span class="framework-badge badge text-bg-danger mb-2">Angular</span>
          <h1 class="display-6 fw-semibold mb-1">{{ title() }}</h1>
          <p class="text-secondary mb-0">{{ subtitle() }}</p>
        </div>
        <span
          class="badge"
          [class.text-bg-success]="connected()"
          [class.text-bg-secondary]="!connected()"
        >{{ status() }}</span>
      </div>
    </header>
  `,
})
export class AppHeaderComponent {
  public readonly title = input.required<string>();
  public readonly subtitle = input.required<string>();
  private readonly application = injectAngularMvvmApplication<unknown>();
  private readonly snapshot = this.application.store.snapshot;
  protected readonly connected = computed(() => this.snapshot().synchronized);
  protected readonly status = computed(() => this.snapshot().synchronized
    ? `Connected · r${this.snapshot().revision}`
    : this.snapshot().phase);
}
