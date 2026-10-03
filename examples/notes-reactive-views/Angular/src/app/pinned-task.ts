import { Component, input } from "@angular/core";
import type { PinnedTaskPageReference } from "../../../Frontend/src/generated/pinnedTask.js";
import { injectView } from "../../../../../packages/web/angular/src/inject-view";

@Component({
  selector: "notes-pinned-task",
  template: `@if (task.state(); as state) { <span>Task: {{ state.priority }}</span> } @else { <span>Connecting…</span> }`,
})
export class PinnedTaskComponent {
  readonly page = input.required<PinnedTaskPageReference>();
  readonly task = injectView(this.page);
}
