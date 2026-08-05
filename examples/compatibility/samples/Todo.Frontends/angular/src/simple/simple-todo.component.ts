import {
  Component,
  computed,
  signal,
} from "@angular/core";
import { injectAngularMvvmApplication } from "@runic-artifex/mvvm-angular";

import type { SimpleTodoContract } from "../../../shared/contracts";
import { AppHeaderComponent } from "../components/app-header.component";
import { injectSimpleTodoContract } from "../todo-bindings.g";

@Component({
  selector: "todo-app",
  standalone: true,
  imports: [AppHeaderComponent],
  templateUrl: "./simple-todo.component.html",
})
export class SimpleTodoComponent {
  private readonly application =
    injectAngularMvvmApplication<SimpleTodoContract>();
  protected readonly bindings = injectSimpleTodoContract();
  protected readonly todo = this.bindings.contract;
  protected readonly items = this.bindings.items;
  protected readonly title = signal("");
  protected readonly pending = signal(false);
  protected readonly completed = computed(() =>
    this.items().filter((item) => item.isCompleted).length);
  protected readonly canAdd = computed(() =>
    this.application.store.snapshot().synchronized &&
    this.bindings.add.canExecute() &&
    !this.bindings.add.isRunning() &&
    !this.pending() &&
    this.title().trim().length >= 2);

  protected setTitle(event: Event): void {
    this.title.set((event.currentTarget as HTMLInputElement).value);
  }

  protected async add(event: Event): Promise<void> {
    event.preventDefault();
    if (this.title().trim().length < 2) return;
    this.pending.set(true);
    try {
      await this.todo.newTitle.set(this.title());
      await this.bindings.add.execute().completion;
      this.title.set("");
    } finally {
      this.pending.set(false);
    }
  }
}
