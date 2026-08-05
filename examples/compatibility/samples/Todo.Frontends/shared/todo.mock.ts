import {
  MvvmMockFrameChannel,
  createMvvmMockChannelFactory,
  type JsonValue,
  type MvvmMockFixture,
  type MvvmMockMutation,
  type PatchChange,
  type PropertySnapshotMember,
  type CommandSnapshotMember,
} from "@runic-artifex/mvvm";

import {
  AdvancedTodoContract,
  SimpleTodoContract,
  type TodoDemo,
} from "./contracts";

export const TODO_MOCK_MARKER = "runic-toolkit.todo.mock/1";
const createSimpleTodoMockChannel = createMvvmMockChannelFactory(
  createSimpleTodoMockFixture(),
);
const createAdvancedTodoMockChannel = createMvvmMockChannelFactory(
  createAdvancedTodoMockFixture(),
);

export function createTodoMockChannel(demo: TodoDemo): MvvmMockFrameChannel {
  return demo === "simple"
    ? createSimpleTodoMockChannel()
    : createAdvancedTodoMockChannel();
}

export function markTodoMockMode(): void {
  document.body.dataset.runicToolkitMode = "mock";
  if (document.querySelector("[data-runic-toolkit-mock]") !== null) return;
  const badge = document.createElement("div");
  badge.dataset.runicToolkitMock = TODO_MOCK_MARKER;
  badge.className =
    "position-fixed bottom-0 start-50 translate-middle-x badge " +
    "rounded-top-pill rounded-bottom-0 text-bg-warning px-3 py-2";
  badge.style.zIndex = "1080";
  badge.textContent = "Frontend-only mock · no native process";
  document.body.append(badge);
}

function createSimpleTodoMockFixture(): MvvmMockFixture {
  let nextId = 3;
  let title = "";
  const items: JsonValue[] = [
    simpleItem("mock-simple-1", "Change this frontend-only task", false),
    simpleItem("mock-simple-2", "Verify the native app before release", true),
  ];

  return {
    contract: SimpleTodoContract.contractName,
    latencyMilliseconds: 80,
    initial: [
      property(1, title),
      { type: "validation", member: 1, errors: [] },
      { type: "collection", member: 3, items },
      command(10),
      command(11),
      command(12),
    ],
    setProperty(request) {
      if (request.payload.member !== 1) return unknownMember(request.payload.member);
      title = stringValue(request.payload.value);
      const errors = title.trim().length >= 2
        ? []
        : ["Title must contain at least two characters."];
      return {
        changes: [
          property(1, title),
          { type: "validation", member: 1, errors },
          command(10, errors.length === 0),
        ],
      };
    },
    execute(request) {
      switch (request.payload.member) {
        case 10: {
          const normalized = title.trim();
          if (normalized.length < 2) {
            return {
              changes: [{
                type: "validation",
                member: 1,
                errors: ["Title must contain at least two characters."],
              }],
            };
          }
          items.push(simpleItem(`mock-simple-${nextId++}`, normalized, false));
          title = "";
          return {
            changes: [
              resetCollection(3, items),
              property(1, title),
              { type: "validation", member: 1, errors: [] },
              command(10, false),
            ],
          };
        }
        case 11: {
          const id = stringValue(request.payload.argument);
          const index = itemIndex(items, id);
          if (index >= 0) {
            const item = objectValue(items[index]);
            items[index] = simpleItem(
              id,
              stringValue(item.title),
              !Boolean(item.isCompleted),
            );
          }
          return { changes: [resetCollection(3, items)] };
        }
        case 12: {
          const index = itemIndex(items, stringValue(request.payload.argument));
          if (index >= 0) items.splice(index, 1);
          return { changes: [resetCollection(3, items)] };
        }
        default:
          return unknownMember(request.payload.member);
      }
    },
  };
}

function createAdvancedTodoMockFixture(): MvvmMockFixture {
  let nextId = 4;
  let diagnosticTick = 1;
  let title = "";
  let notes = "";
  let priority = "Normal";
  let query = "";
  let filter = "All";
  let wizardStep: string | null = null;
  let wizardIssues: readonly string[] = [];
  let importing = false;
  const items: JsonValue[] = [
    advancedItem(
      "mock-advanced-1",
      "Inspect generated framework bindings",
      false,
      "This item came from the deterministic frontend-only fixture.",
      "High",
      1,
    ),
    advancedItem(
      "mock-advanced-2",
      "Try filtering and the guided workflow",
      false,
      "",
      "Normal",
      2,
    ),
    advancedItem(
      "mock-advanced-3",
      "Run the native integration gate",
      true,
      "Mock mode complements rather than replaces CsWebUi testing.",
      "Low",
      3,
    ),
  ];
  const diagnostics: JsonValue[] = [
    diagnostic(0, "Mock", "Frontend-only protocol fixture connected."),
  ];

  return {
    contract: AdvancedTodoContract.contractName,
    latencyMilliseconds: 100,
    initial: [
      property(1, title),
      { type: "validation", member: 1, errors: [] },
      property(2, notes),
      property(3, priority),
      property(4, query),
      property(5, filter),
      { type: "collection", member: 20, items: visibleItems() },
      { type: "collection", member: 21, items: diagnostics },
      property(22, state()),
      ...[40, 41, 42, 43, 44, 45, 46, 48, 49, 50, 51, 52].map((member) =>
        command(member)),
    ],
    setProperty(request) {
      const value = stringValue(request.payload.value);
      switch (request.payload.member) {
        case 1:
          title = value;
          return {
            changes: [
              property(1, title),
              {
                type: "validation",
                member: 1,
                errors: title.trim().length >= 2
                  ? []
                  : ["Title must contain at least two characters."],
              },
            ],
          };
        case 2:
          notes = value;
          return { changes: [property(2, notes)] };
        case 3:
          priority = ["Low", "Normal", "High"].includes(value) ? value : "Normal";
          return { changes: [property(3, priority)] };
        case 4:
          query = value;
          return { changes: [property(4, query)] };
        case 5:
          filter = ["All", "Active", "Completed"].includes(value) ? value : "All";
          return { changes: [property(5, filter)] };
        default:
          return unknownMember(request.payload.member);
      }
    },
    async execute(request, context) {
      switch (request.payload.member) {
        case 40:
          return addDraft("Added task through the mock protocol.");
        case 41:
          log("Filter", `Applied ${filter} filter for “${query || "all tasks"}”.`);
          return refreshed();
        case 42: {
          const index = itemIndex(items, stringValue(request.payload.argument));
          if (index >= 0) {
            const item = objectValue(items[index]);
            const completed = !Boolean(item.isCompleted);
            items[index] = advancedItem(
              stringValue(item.id),
              stringValue(item.title),
              completed,
              stringValue(item.notes),
              stringValue(item.priority),
              index + 1,
            );
            log("Task", completed ? "Marked task complete." : "Reopened task.");
          }
          return refreshed();
        }
        case 43: {
          const index = itemIndex(items, stringValue(request.payload.argument));
          if (index >= 0) items.splice(index, 1);
          log("Task", "Deleted task.");
          return refreshed();
        }
        case 44:
          for (let index = items.length - 1; index >= 0; index--) {
            if (Boolean(objectValue(items[index]).isCompleted)) items.splice(index, 1);
          }
          log("Task", "Cleared completed tasks.");
          return refreshed();
        case 45:
          importing = true;
          log("Import", "Started deterministic mock import.");
          await context.push(refreshed().changes ?? []);
          await delay(450, context.signal);
          if (!importing) return refreshed();
          items.push(
            advancedItem(
              `mock-advanced-${nextId++}`,
              "Imported frontend fixture task",
              false,
              "No filesystem or native process was used.",
              "Normal",
              nextId,
            ),
          );
          importing = false;
          log("Import", "Imported one mock task.");
          return refreshed();
        case 46:
          importing = false;
          log("Import", "Canceled mock import.");
          return refreshed();
        case 48:
          wizardStep = "todo.create.details";
          wizardIssues = [];
          log("Flow", "Started guided creation.");
          return refreshed();
        case 49:
          wizardIssues = title.trim().length >= 2
            ? []
            : ["Enter a title before continuing."];
          if (wizardIssues.length === 0) wizardStep = "todo.create.review";
          return refreshed();
        case 50:
          wizardStep = "todo.create.details";
          wizardIssues = [];
          return refreshed();
        case 51: {
          const result = addDraft("Finished guided creation.");
          const invalid = (result.changes ?? []).some((change) =>
            change.type === "validation" && change.errors.length > 0);
          if (invalid) {
            wizardIssues = ["Enter a title before saving."];
          } else {
            wizardStep = null;
            wizardIssues = [];
          }
          return {
            changes: [
              ...(result.changes ?? []),
              ...(refreshed().changes ?? []),
            ],
          };
        }
        case 52:
          wizardStep = null;
          wizardIssues = [];
          log("Flow", "Canceled guided creation.");
          return refreshed();
        default:
          return unknownMember(request.payload.member);
      }
    },
  };

  function addDraft(message: string): MvvmMockMutation {
    const normalized = title.trim();
    if (normalized.length < 2) {
      return {
        changes: [{
          type: "validation",
          member: 1,
          errors: ["Title must contain at least two characters."],
        }],
      };
    }
    items.push(
      advancedItem(
        `mock-advanced-${nextId++}`,
        normalized,
        false,
        notes.trim(),
        priority,
        nextId,
      ),
    );
    title = "";
    notes = "";
    priority = "Normal";
    log("Task", message);
    return {
      changes: [
        property(1, title),
        property(2, notes),
        property(3, priority),
        { type: "validation", member: 1, errors: [] },
        ...(refreshed().changes ?? []),
      ],
    };
  }

  function visibleItems(): JsonValue[] {
    const normalizedQuery = query.trim().toLocaleLowerCase();
    return items.filter((value) => {
      const item = objectValue(value);
      const completed = Boolean(item.isCompleted);
      const matchesFilter = filter === "All" ||
        (filter === "Active" && !completed) ||
        (filter === "Completed" && completed);
      const searchable =
        `${stringValue(item.title)} ${stringValue(item.notes)}`.toLocaleLowerCase();
      return matchesFilter &&
        (normalizedQuery.length === 0 || searchable.includes(normalizedQuery));
    });
  }

  function state(): JsonValue {
    const completed = items.filter((value) =>
      Boolean(objectValue(value).isCompleted)).length;
    return {
      totalCount: items.length,
      remainingCount: items.length - completed,
      completedCount: completed,
      isImporting: importing,
      wizardStep,
      wizardIssues,
    };
  }

  function refreshed(): MvvmMockMutation {
    return {
      changes: [
        resetCollection(20, visibleItems()),
        resetCollection(21, diagnostics),
        property(22, state()),
      ],
    };
  }

  function log(category: string, message: string): void {
    diagnostics.unshift(diagnostic(diagnosticTick++, category, `${message} · MOCK`));
    diagnostics.splice(12);
  }
}

function property(member: number, value: JsonValue): PropertySnapshotMember {
  return { type: "property", member, value };
}

function command(
  member: number,
  canExecute = true,
  isExecuting = false,
): CommandSnapshotMember {
  return { type: "command", member, canExecute, isExecuting };
}

function resetCollection(
  member: number,
  items: readonly JsonValue[],
): PatchChange {
  return { type: "collection", member, operation: "reset", index: 0, items };
}

function simpleItem(id: string, title: string, completed: boolean): JsonValue {
  return { id, title, isCompleted: completed };
}

function advancedItem(
  id: string,
  title: string,
  completed: boolean,
  notes: string,
  priority: string,
  day: number,
): JsonValue {
  return {
    id,
    title,
    isCompleted: completed,
    notes,
    priority,
    createdAt: `2026-01-${String(day).padStart(2, "0")}T09:00:00.000Z`,
    completedAt: completed
      ? `2026-01-${String(day).padStart(2, "0")}T10:00:00.000Z`
      : null,
  };
}

function diagnostic(tick: number, category: string, message: string): JsonValue {
  return {
    at: `2026-01-01T09:00:${String(tick).padStart(2, "0")}.000Z`,
    category,
    message,
  };
}

function itemIndex(items: readonly JsonValue[], id: string): number {
  return items.findIndex((value) => stringValue(objectValue(value).id) === id);
}

function objectValue(value: JsonValue | undefined): Readonly<Record<string, JsonValue>> {
  if (value === undefined ||
      value === null ||
      typeof value !== "object" ||
      Array.isArray(value)) {
    return {};
  }
  return value as Readonly<Record<string, JsonValue>>;
}

function stringValue(value: JsonValue | undefined): string {
  return typeof value === "string" ? value : "";
}

function unknownMember(member: number): MvvmMockMutation {
  return {
    fault: {
      code: "member.unknown",
      message: `The Todo mock does not define member ${member}.`,
    },
  };
}

async function delay(milliseconds: number, signal: AbortSignal): Promise<void> {
  await new Promise<void>((resolve, reject) => {
    const timer = setTimeout(resolve, milliseconds);
    signal.addEventListener("abort", () => {
      clearTimeout(timer);
      reject(signal.reason);
    }, { once: true });
  });
}
