import {
  Component,
  computed,
  signal,
} from "@angular/core";
import { injectAngularMvvmApplication } from "@runic-artifex/mvvm-angular";

import {
  type AdvancedTodoContract,
  type AdvancedTodoState,
} from "../../../shared/contracts";
import { AppHeaderComponent } from "../components/app-header.component";
import { injectAdvancedTodoContract } from "../todo-bindings.g";

const emptyState: AdvancedTodoState = {
  totalCount: 0,
  remainingCount: 0,
  completedCount: 0,
  isImporting: false,
  wizardStep: null,
  wizardIssues: [],
};

@Component({
  selector: "todo-app",
  standalone: true,
  imports: [AppHeaderComponent],
  templateUrl: "./advanced-todo.component.html",
})
export class AdvancedTodoComponent {
  private readonly application =
    injectAngularMvvmApplication<AdvancedTodoContract>();
  protected readonly bindings = injectAdvancedTodoContract();
  protected readonly todo = this.bindings.contract;
  protected readonly items = this.bindings.items;
  protected readonly diagnostics = this.bindings.diagnostics;
  protected readonly state = computed(() => this.bindings.state() ?? emptyState);
  protected readonly validation = computed(() =>
    this.bindings.newTitleErrors() ?? []);
  protected readonly wizardReview = computed(() =>
    this.state().wizardStep === "todo.create.review");
  protected readonly title = signal("");
  protected readonly notes = signal("");
  protected readonly priority = signal("Normal");
  protected readonly query = signal("");
  protected readonly filter = signal("All");
  protected readonly canAdd = computed(() =>
    this.application.store.snapshot().synchronized &&
    this.bindings.add.canExecute() &&
    !this.bindings.add.isRunning());

  protected setTitle(event: Event): void {
    this.title.set(inputValue(event));
  }

  protected setNotes(event: Event): void {
    this.notes.set(inputValue(event));
  }

  protected setPriority(event: Event): void {
    this.priority.set(inputValue(event));
  }

  protected setQuery(event: Event): void {
    this.query.set(inputValue(event));
  }

  protected setFilter(event: Event): void {
    this.filter.set(inputValue(event));
  }

  private async setDraft(): Promise<void> {
    await this.todo.newTitle.set(this.title());
    await this.todo.newNotes.set(this.notes());
    await this.todo.newPriority.set(this.priority());
  }

  protected async add(event: Event): Promise<void> {
    event.preventDefault();
    await this.setDraft();
    await this.bindings.add.execute().completion;
    if (this.todo.newTitle.validation.length === 0) {
      this.title.set("");
      this.notes.set("");
      this.priority.set("Normal");
    }
  }

  protected async applyFilter(event: Event): Promise<void> {
    event.preventDefault();
    await this.todo.query.set(this.query());
    await this.todo.filter.set(this.filter());
    await this.todo.applyFilter.execute().completion;
  }

  protected async wizard(
    command: typeof this.todo.wizardStart,
  ): Promise<void> {
    if (command === this.todo.wizardStart ||
        command === this.todo.wizardNext) {
      await this.setDraft();
    }
    await command.execute().completion;
  }

  protected formatTime(value: string): string {
    return new Date(value).toLocaleTimeString();
  }
}

function inputValue(event: Event): string {
  return (event.currentTarget as
    HTMLInputElement | HTMLTextAreaElement | HTMLSelectElement).value;
}
